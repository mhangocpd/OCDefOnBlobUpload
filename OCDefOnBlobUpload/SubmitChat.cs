using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using System.ClientModel;
using System.Net;


namespace OCDefOnBlobUpload;

// NOTE: Although search embedding can be done here, Azure AI search does this automatically.
// Without that enabled all this function does is connect to gpt-4o and feed the results of the vector search into it.
public class SubmitChat
{
    private readonly ILogger<SubmitChat> _logger;
    private string openAiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY")!;
    private string openAiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!;
    private readonly string searchEndpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT")!;
    private readonly string searchIndex = Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX")!;
    private readonly string searchKey = Environment.GetEnvironmentVariable("AZURE_SEARCH_KEY")!;
    private const string systemPrompt = "You are the “W&A Assistant”. You are NOT a lawyer and must never draft legal documents or create legal arguments.\r\n \r\nSCOPE\r\n- Only: (a) summarize provided transcripts and case files; (b) answer questions using those same files; (c) extract entities, dates, events, and citations to the exact passages.\r\n- Never: generate original legal content, draft appeals/motions/briefs/letters/emails, write recommendations, or speculate about law or case strategy.\r\n \r\nGROUNDING\r\n- Answer ONLY with information grounded in the retrieved documents. For every non-trivial answer, include inline citations [DocName, page/section] to the specific passages you used.\r\n- If the answer cannot be found in the provided materials, reply:\r\n  “I can’t find that in the case materials. Please upload a source or point me to the relevant document.”\r\n \r\nSTYLE & NAMING\r\n- Refer to yourself only as “Assistant”. Do not use the words AI, Copilot, Agent, or model.\r\n- Be neutral, concise, and factual; no creative rewriting or embellishment.\r\n \r\nSAFETY\r\n- If asked to create, rewrite, or improve any legal content (appeals, motions, briefs, letters, emails), respond with the refusal template.\r\n- If asked for legal interpretation or advice beyond the documents, refuse and suggest consulting counsel.\r\n- Follow content safety: do not output hateful, sexual, self-harm content; handle violent content factually and neutrally when it appears in source materials.\r\n \r\nREFUSAL TEMPLATE\r\n“I can’t do that. I’m limited to summarizing and answering questions directly from the uploaded case materials. I can help you find the relevant passages or produce a factual summary with citations.”\r\n \r\nOUTPUT LIMITS\r\n- Temperature ≤ 0.2; max 512 output tokens unless summarizing multi-document bundles.\r\n- No links to the open web; no tools other than court‑approved indexes.";


    public SubmitChat(ILogger<SubmitChat> logger)
    {
        _logger = logger;
    }

    [Microsoft.Azure.Functions.Worker.Function(nameof(SubmitChat))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
    {
        _logger.LogInformation("Sending user query to AI Search...");
        string userQuery = await new StreamReader(req.Body).ReadToEndAsync();
        var client = new AzureOpenAIClient(new Uri(openAiEndpoint), new AzureKeyCredential(openAiKey));
        // Send embeddings to AI search
        /**
        var embedding = await client.GetEmbeddingClient("text-embedding-3-large").GenerateEmbeddingAsync(userQuery);
        ReadOnlyMemory<float> vector = embedding.Value.ToFloats();
        var vectorQuery = new VectorizedQuery(vector)
        {
            Fields = { "text_vector" }
        };

        _logger.LogInformation("Successfully retrieved embeddings, searching with Azure AI Search...");
        **/

        var searchOptions = new SearchOptions
        {
            Size = 5,
            Select = { "chunk" }
        };
        var searchClient = new SearchClient(new Uri(searchEndpoint), searchIndex, new AzureKeyCredential(searchKey));
        var searchResults = await searchClient.SearchAsync<SearchDocument>(userQuery, searchOptions);

        var relevantChunks = searchResults.Value.GetResults()
            .Select(r => r.Document["chunk"]?.ToString())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        // Put relevant chunks together and send to OpenAI chat
        _logger.LogInformation("Successfully searched and found relevant chunks, plugging context into OpenAI chat...");
        var userPrompt = $"Question: {userQuery}";

        // Return the answer to the frontend
        var messages = new ChatMessage[]
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };
        foreach (var relevantChunk in relevantChunks)
        {
            messages = messages.Append(new AssistantChatMessage($"Context: {relevantChunk}")).ToArray();
        }

        string answer;
        try
        {
            ChatClient chatClient = client.GetChatClient("gpt-4o");
            ClientResult<ChatCompletion> chatResult = chatClient.CompleteChatAsync(messages).Result;
            answer = chatResult.Value.Content.FirstOrDefault()?.Text ?? "I'm sorry, I couldn't find an answer to your question.";
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError($"OpenAI request failed: {ex.Message}");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Error communicating with OpenAI service.");
            return errorResponse;
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
}
