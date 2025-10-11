using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Web;
using IndexerExecutionResult = Azure.Search.Documents.Indexes.Models.IndexerExecutionResult;
using IndexerExecutionStatus = Azure.Search.Documents.Indexes.Models.IndexerExecutionStatus;
using IndexerStatus = Azure.Search.Documents.Indexes.Models.IndexerStatus;

namespace OCDefOnBlobUpload;

public class PollIndexer
{
    private readonly ILogger<PollIndexer> _logger;
    private readonly string? managedIdentity = Environment.GetEnvironmentVariable("MANAGED_IDENTITY_CLIENT_ID");
    private readonly string searchEndpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT")!;
    private readonly string adminSearchKey = Environment.GetEnvironmentVariable("AZURE_SEARCH_ADMIN_KEY")!;
    private readonly string blobIndexerName = Environment.GetEnvironmentVariable("AZURE_BLOB_INDEXER_NAME")!;

    public PollIndexer(ILogger<PollIndexer> logger)
    {
        _logger = logger;
    }

    [Function(nameof(PollIndexer))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req)
    {
        _logger.LogInformation("Indexer polling function triggered.");

        try
        {
            // Parse query parameters
            var query = HttpUtility.ParseQueryString(req.Url.Query);
            
            var pollRequest = new PollIndexerRequest
            {
                TimeoutSeconds = int.TryParse(query["timeoutSeconds"], out var timeout) ? timeout : 300,
                PollIntervalSeconds = int.TryParse(query["pollIntervalSeconds"], out var interval) ? interval : 5
            };

            _logger.LogInformation($"Poll request parameters - StartIndexer: {pollRequest.StartIndexer}, Timeout: {pollRequest.TimeoutSeconds}s, Interval: {pollRequest.PollIntervalSeconds}s");

            // Create indexer client
            SearchIndexerClient indexerClient = new SearchIndexerClient(
                new Uri(searchEndpoint), 
                new AzureKeyCredential(adminSearchKey));

            // Poll indexer status
            var maxWaitTime = TimeSpan.FromSeconds(pollRequest.TimeoutSeconds);
            var pollInterval = TimeSpan.FromSeconds(pollRequest.PollIntervalSeconds);
            var startTime = DateTime.UtcNow;

            _logger.LogInformation($"Starting to poll indexer status. Timeout: {maxWaitTime}, Poll interval: {pollInterval}");

            IndexerStatus status;
            IndexerExecutionResult? lastExecution = null;

            do
            {
                // Check for timeout
                if (DateTime.UtcNow - startTime > maxWaitTime)
                {
                    _logger.LogWarning($"Indexer polling timed out after {maxWaitTime}");
                    var timeoutResponse = req.CreateResponse(HttpStatusCode.RequestTimeout);
                    var timeoutResult = new PollIndexerResponse
                    {
                        Status = "Timeout",
                        Message = $"Indexer operation timed out after {maxWaitTime}. Processing may still be in progress.",
                        IsComplete = false,
                        ElapsedTime = DateTime.UtcNow - startTime
                    };
                    await timeoutResponse.WriteStringAsync(JsonSerializer.Serialize(timeoutResult, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }));
                    return timeoutResponse;
                }

                // Get indexer status
                try
                {
                    var indexerStatus = await indexerClient.GetIndexerStatusAsync(blobIndexerName);
                    status = indexerStatus.Value.Status;
                    lastExecution = indexerStatus.Value.LastResult;

                    _logger.LogInformation($"Indexer status: {status}, Last execution status: {lastExecution?.Status}");

                    // Check if indexer is in error state
                    if (status == IndexerStatus.Error)
                    {
                        var errorMessage = lastExecution?.ErrorMessage ?? "Unknown error occurred";
                        _logger.LogError($"Indexer is in error state: {errorMessage}");
                        
                        var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                        var errorResult = new PollIndexerResponse
                        {
                            Status = "Error",
                            Message = $"Indexer encountered an error: {errorMessage}",
                            IsComplete = true,
                            ElapsedTime = DateTime.UtcNow - startTime,
                            ItemsProcessed = lastExecution?.ItemCount,
                            ItemsFailed = lastExecution?.FailedItemCount
                        };
                        await errorResponse.WriteStringAsync(JsonSerializer.Serialize(errorResult, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = true
                        }));
                        return errorResponse;
                    }
                }
                catch (RequestFailedException ex)
                {
                    _logger.LogError($"Failed to get indexer status: {ex.Message}");
                    var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                    await errorResponse.WriteStringAsync($"Failed to get indexer status: {ex.Message}");
                    return errorResponse;
                }

                // If still running, wait before next poll
                if (status == IndexerStatus.Running || 
                    (lastExecution?.Status == IndexerExecutionStatus.InProgress))
                {
                    await Task.Delay(pollInterval);
                }

            } while (status == IndexerStatus.Running || 
                     (lastExecution?.Status == IndexerExecutionStatus.InProgress));

            // Indexer completed successfully
            _logger.LogInformation("Indexer completed successfully!");
            
            var successResponse = req.CreateResponse(HttpStatusCode.OK);
            successResponse.Headers.Add("Content-Type", "application/json");
            
            var successResult = new PollIndexerResponse
            {
                Status = lastExecution?.Status.ToString() ?? "Unknown",
                Message = "Indexer completed successfully!",
                IsComplete = true,
                ElapsedTime = DateTime.UtcNow - startTime,
                ItemsProcessed = lastExecution?.ItemCount,
                ItemsFailed = lastExecution?.FailedItemCount
            };

            await successResponse.WriteStringAsync(JsonSerializer.Serialize(successResult, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
            return successResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Unexpected error in PollIndexer: {ex.Message}");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Internal server error: {ex.Message}");
            return errorResponse;
        }
    }
}

// Request model for the poll indexer function
public class PollIndexerRequest
{
    public bool StartIndexer { get; set; } = false;
    public int TimeoutSeconds { get; set; } = 300; // 5 minutes default
    public int PollIntervalSeconds { get; set; } = 2; // 2 seconds default
}

// Response model for the poll indexer function
public class PollIndexerResponse
{
    public required string Status { get; set; }
    public required string Message { get; set; }
    public bool IsComplete { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public int? ItemsProcessed { get; set; }
    public int? ItemsFailed { get; set; }
}