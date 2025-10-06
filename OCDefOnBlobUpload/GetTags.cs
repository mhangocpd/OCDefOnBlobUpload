using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Markdig;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using System.ClientModel;
using System.Net;
using System.Numerics;
using System.Text.Json;
using static System.Runtime.InteropServices.JavaScript.JSType;


namespace OCDefOnBlobUpload;

// NOTE: Although search embedding can be done here, Azure AI search does this automatically.
// Without that enabled all this function does is connect to gpt-4o and feed the results of the vector search into it.
public class GetTags
{
    private readonly ILogger<GetTags> _logger;

    private readonly string openAiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY")!;
    private readonly string openAiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!;
    private readonly string accountName = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME")!;
    private readonly string searchEndpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT")!;
    private readonly string searchIndex = Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX")!;
    private readonly string searchKey = Environment.GetEnvironmentVariable("AZURE_SEARCH_KEY")!;


    public GetTags(ILogger<GetTags> logger)
    {
        _logger = logger;
    }

    [Microsoft.Azure.Functions.Worker.Function(nameof(GetTags))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
    {
        // Read blob URI from request body
        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(requestBody);

        if (!data.TryGetProperty("blobUri", out var blobUriElement))
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await errorResponse.WriteStringAsync("Please provide blobUri in the request body.");
            return errorResponse;
        }

        string blobUri = blobUriElement.GetString()!;
        // var cred = new DefaultAzureCredential();
        var cred = new ManagedIdentityCredential(clientId: "20238ad9-abb5-4ca6-a9ad-c468b21d0b3d");

        if (string.IsNullOrEmpty(blobUri))
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await errorResponse.WriteStringAsync("Please provide blobUri in the request body.");
            return errorResponse;
        }

        // Parse blob URI
        var blobClient = new BlobClient(new Uri(blobUri), cred);

        // Get tags
        IDictionary<string, string> tags;
        try
        {
            var resp = await blobClient.GetTagsAsync();
            tags = resp.Value.Tags;
            _logger.LogInformation($"Successfully retrieved {tags.Count} tags from blob");
        }
        catch (System.Exception ex)
        {
            _logger.LogError($"Error fetching tags: {ex.Message}");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Error fetching tags: {ex.Message}");
            return errorResponse;
        }

        // Build response object
        var responseData = new Dictionary<string, object>
        {
            ["blobUri"] = blobUri,
            ["tags"] = tags,
            ["tagCount"] = tags.Count
        };

        // Return the tags as JSON
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");

        var jsonResponse = JsonSerializer.Serialize(responseData, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await response.WriteStringAsync(jsonResponse);
        return response;
    }
}