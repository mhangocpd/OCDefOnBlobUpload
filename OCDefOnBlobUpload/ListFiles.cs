using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Web;

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
        [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req)
    {
        try
        {
            // Extract CaseNumber from query parameters
            var query = HttpUtility.ParseQueryString(req.Url.Query);
            string? caseNumber = query["aseNumber"];

            if (string.IsNullOrWhiteSpace(caseNumber))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("CaseNumber query parameter is required. Example: ?caseNumber=ABC");
                return badResponse;
            }

            _logger.LogInformation($"Listing files for case number: {caseNumber}");

            // Set up authentication
            TokenCredential cred = managedIdentity != null 
                ? new ManagedIdentityCredential(clientId: managedIdentity) 
                : new VisualStudioCredential();

            // Connect to blob service
            var serviceUri = new Uri($"https://{accountName}.blob.core.windows.net");
            var blobServiceClient = new BlobServiceClient(serviceUri, cred);
            var matchingFiles = new List<string>();
            string tagFilter = $"CaseNumber = '{caseNumber}'";
            
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

            _logger.LogInformation($"Found {matchingFiles.Count} files for case number {caseNumber}");

            // Create response
            var responseData = new ListFilesResponse
            {
                CaseNumber = caseNumber,
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

// Response model (Request model no longer needed)
public class ListFilesResponse
{
    public required string CaseNumber { get; set; }
    public int FileCount { get; set; }
    public required List<string> FileNames { get; set; }
}