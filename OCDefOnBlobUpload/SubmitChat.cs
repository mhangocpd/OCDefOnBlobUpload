using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using System.ClientModel;
using System.Net;
using System.Text.Json;


namespace OCDefOnBlobUpload;

// NOTE: Although search embedding can be done here, Azure AI search does this automatically.
// Without that enabled all this function does is connect to gpt-4o and feed the results of the vector search into it.
public class SubmitChat
{
    private readonly ILogger<SubmitChat> _logger;
    private readonly string openAiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY")!;
    private readonly string openAiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!;
    private readonly string accountName = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME")!;
    private readonly string searchEndpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT")!;
    private readonly string searchIndex = Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX")!;
    private readonly string searchKey = Environment.GetEnvironmentVariable("AZURE_SEARCH_KEY")!;
    private const string systemPrompt = "You are the “W&A Assistant”. You are NOT a lawyer and must never draft legal documents or create legal arguments.\r\n \r\nSCOPE\r\n- Only: (a) summarize provided transcripts and case files; (b) answer questions using those same files; (c) extract entities, dates, events, and citations to the exact passages.\r\n- Never: generate original legal content, draft appeals/motions/briefs/letters/emails, write recommendations, or speculate about law or case strategy.\r\n \r\nGROUNDING\r\n- Answer ONLY with information grounded in the retrieved documents. For every non-trivial answer, include inline citations [DocName, page/section] to the specific passages you used.\r\n- If the answer cannot be found in the provided materials, reply:\r\n  “I can’t find that in the case materials. Please upload a source or point me to the relevant document.”\r\n \r\nSTYLE & NAMING\r\n- Refer to yourself only as “Assistant”. Do not use the words AI, Copilot, Agent, or model.\r\nYour responses should be formatted in HTML, not markdown.\r\n- Be neutral, concise, and factual; no creative rewriting or embellishment.\r\n \r\nSAFETY\r\n- If asked to create, rewrite, or improve any legal content (appeals, motions, briefs, letters, emails), respond with the refusal template.\r\n- If asked for legal interpretation or advice beyond the documents, refuse and suggest consulting counsel.\r\n- Follow content safety: do not output hateful, sexual, self-harm content; handle violent content factually and neutrally when it appears in source materials.\r\n \r\nREFUSAL TEMPLATE\r\n“I can’t do that. I’m limited to summarizing and answering questions directly from the uploaded case materials. I can help you find the relevant passages or produce a factual summary with citations.”\r\n \r\nOUTPUT LIMITS\r\n- Temperature ≤ 0.2; max 512 output tokens unless summarizing multi-document bundles.\r\n- No links to the open web; no tools other than court‑approved indexes.";


    public SubmitChat(ILogger<SubmitChat> logger)
    {
        _logger = logger;
    }

    [Microsoft.Azure.Functions.Worker.Function(nameof(SubmitChat))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
    {
        // Read in JSON request
        string body = await new StreamReader(req.Body).ReadToEndAsync();
        ChatRequest chatRequest;
        try
        {
            chatRequest = JsonSerializer.Deserialize<ChatRequest>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!;
            _logger.LogInformation($"Received chat request: {chatRequest.Message}");
        }
        catch
        {
            // Fallback for legacy string-only requests
            chatRequest = new ChatRequest
            {
                SessionId = Guid.NewGuid().ToString(),
                Message = body
            };
            _logger.LogInformation($"Failed to parse chat request, using fallback to simple string request (no chat history): {chatRequest.Message}");
        }

        // Search for user query with AI Search
        _logger.LogInformation("Sending user query to AI Search...");
        var client = new AzureOpenAIClient(new Uri(openAiEndpoint), new AzureKeyCredential(openAiKey));
        var searchOptions = new SearchOptions
        {
            Size = 5,
            Select = { "chunk" }
        };
        var searchClient = new SearchClient(new Uri(searchEndpoint), searchIndex, new AzureKeyCredential(searchKey));
        var searchResults = await searchClient.SearchAsync<SearchDocument>(chatRequest.Message, searchOptions);
        var relevantChunks = searchResults.Value.GetResults()
            .Select(r => r.Document["chunk"]?.ToString())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        // Search for previous chat history
        var historyUri = new Uri($"https://{accountName}.blob.core.windows.net/chathistory");
        var cred = new VisualStudioCredential();
        BlobContainerClient container = new BlobContainerClient(historyUri, cred);
        BlobClient blobClient = container.GetBlobClient(chatRequest.SessionId + ".json");
        ChatMessage[] messages = GetFreshMessages();
        ChatHistory chatHistory = GetFreshHistory();
        if (blobClient.Exists())
        {
            _logger.LogInformation("Found existing chat history for session " + chatRequest.SessionId);
            // Add previous chat history as context
            blobClient.DownloadContentAsync().ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    // Read in JSON chat history
                    try
                    {
                        chatHistory = JsonSerializer.Deserialize<ChatHistory>(task.Result.Value.Content.ToString(), new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        })!;
                        messages = chatHistory.GetMessages();
                        _logger.LogInformation($"Successfully received chat history for session: {chatRequest.SessionId}");
                        foreach (var msg in chatHistory.Messages)
                        {
                            _logger.LogInformation($"History message - Role: {msg.Role}, Message: {msg.Message}");
                        }
                    }
                    catch
                    {
                        _logger.LogWarning($"Failed to parse chat history for session: {chatRequest.SessionId}");
                    }
                }
                else
                {
                    _logger.LogWarning("Failed to download chat history, starting fresh.");
                }
            }).Wait();
        }
        else
        {
            _logger.LogInformation("No previous chat history found, starting fresh.");
        }

        // Add AI search results as context, then add user query
        foreach (var relevantChunk in relevantChunks)
        {
            messages = messages.Append(new AssistantChatMessage($"Context: {relevantChunk}")).ToArray();
        }
        chatHistory.AddMessage(ChatHistory.Role.USER, chatRequest.Message);
        messages = messages.Append(new UserChatMessage($"User Question: {chatRequest.Message}")).ToArray();


        // Send in messages
        string answer;
        try
        {
            ChatClient chatClient = client.GetChatClient("gpt-4o");
            ClientResult<ChatCompletion> chatResult = chatClient.CompleteChatAsync(messages).Result;
            answer = chatResult.Value.Content.FirstOrDefault()?.Text ?? "I'm sorry, I couldn't find an answer to your question.";
            
            // Add to chat history and save to blob
            chatHistory.AddMessage(ChatHistory.Role.BOT, answer);
            blobClient.UploadAsync(BinaryData.FromString(JsonSerializer.Serialize(chatHistory)), overwrite: true).Wait();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Unexpected error: {ex.Message}");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("An unexpected error occurred.");
            return errorResponse;
        }
        _logger.LogInformation("Completed and returned response.");
        // Return the answer to the frontend
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync(answer); // Replace with answer
        return response;
    }

    private ChatMessage[] GetFreshMessages() => new ChatMessage[]
    {
        new SystemChatMessage(systemPrompt)
    };

    private ChatHistory GetFreshHistory()
    {

        return new ChatHistory
        {
            Messages = new ChatHistory.HistoryMessage[]
            {
                new ChatHistory.HistoryMessage
                {
                    Role = ChatHistory.Role.SYSTEM,
                    Message = systemPrompt
                },
            }
        };
    }
}
