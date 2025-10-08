using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace OCDefOnBlobUpload;

public class ListFiles
{
    private readonly ILogger<ListFiles> _logger;
    private readonly string? managedIdentity = Environment.GetEnvironmentVariable("MANAGED_IDENTITY_CLIENT_ID");
    private readonly string accountName = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME")!;
    private readonly string filesContainer = Environment.GetEnvironmentVariable("FILES_CONTAINER")!;

    public ListFiles(ILogger<ListFiles> logger)
    {
        _logger = logger;
    }

    [Microsoft.Azure.Functions.Worker.Function(nameof(ListFiles))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        try
        {
            // Read the JSON request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var listRequest = JsonSerializer.Deserialize<ListFilesRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (listRequest == null || string.IsNullOrWhiteSpace(listRequest.CaseNumber))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("CaseNumber is required in the request body.");
                return badResponse;
            }

            _logger.LogInformation($"Listing files for case number: {listRequest.CaseNumber}");

            // Set up authentication
            TokenCredential cred = managedIdentity != null 
                ? new ManagedIdentityCredential(clientId: managedIdentity) 
                : new VisualStudioCredential();

            // Connect to blob servicehttps://ocdefstorage.blob.core.windows.net/pdfs/deposition_CNABC.pdf
            var serviceUri = new Uri($"https://{accountName}.blob.core.windows.net");
            var blobServiceClient = new BlobServiceClient(serviceUri, cred);
            var matchingFiles = new List<string>();
            string tagFilter = $"CaseNumber";
            // string tagFilter = $"CaseNumber = '{listRequest.CaseNumber}'";
            
            await foreach (var taggedBlobItem in blobServiceClient.FindBlobsByTagsAsync(tagFilter))
            {
                // Extract container name and blob name from the blob name
                // taggedBlobItem.BlobName format: "container/blobname"
                var blobParts = taggedBlobItem.BlobName.Split('/', 2);
                if (blobParts.Length == 2 && blobParts[0] == filesContainer)
                {
                    matchingFiles.Add(blobParts[1]);
                    _logger.LogInformation($"Found matching file: {blobParts[1]}");
                }
            }

            _logger.LogInformation($"Found {matchingFiles.Count} files for case number {listRequest.CaseNumber}");

            // Create response
            var responseData = new ListFilesResponse
            {
                CaseNumber = listRequest.CaseNumber,
                FileCount = matchingFiles.Count,
                FileNames = matchingFiles
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");

            var jsonResponse = JsonSerializer.Serialize(responseData, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            await response.WriteStringAsync(jsonResponse);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error listing files: {ex.Message}");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Internal server error: {ex.Message}");
            return errorResponse;
        }
    }
}

// Request model
public class ListFilesRequest
{
    public required string CaseNumber { get; set; }
}

// Response model
public class ListFilesResponse
{
    public required string CaseNumber { get; set; }
    public int FileCount { get; set; }
    public required List<string> FileNames { get; set; }
}