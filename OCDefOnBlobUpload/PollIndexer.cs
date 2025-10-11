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

namespace OCDefOnBlobUpload;

public class PollIndexer
{
    private readonly ILogger<PollIndexer> _logger;
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

            // Poll indexer execution status
            var maxWaitTime = TimeSpan.FromSeconds(pollRequest.TimeoutSeconds);
            var pollInterval = TimeSpan.FromSeconds(pollRequest.PollIntervalSeconds);
            var startTime = DateTime.UtcNow;

            _logger.LogInformation($"Starting to poll indexer execution status. Timeout: {maxWaitTime}, Poll interval: {pollInterval}");

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

                // Get indexer execution status
                try
                {
                    var indexerStatus = await indexerClient.GetIndexerStatusAsync(blobIndexerName);
                    lastExecution = indexerStatus.Value.LastResult;

                    _logger.LogInformation($"Current execution status: {lastExecution.Status}");

                    // Error status
                    if (lastExecution.Status == IndexerExecutionStatus.TransientFailure)
                    {
                        var errorMessage = lastExecution.ErrorMessage ?? "Unknown error occurred";
                        _logger.LogError($"Indexer execution failed: {errorMessage}");
                        
                        var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                        var errorResult = new PollIndexerResponse
                        {
                            Status = lastExecution.Status.ToString(),
                            Message = $"Indexer encountered an error: {errorMessage}",
                            IsComplete = true,
                            ElapsedTime = DateTime.UtcNow - startTime,
                            ItemsProcessed = lastExecution.ItemCount,
                            ItemsFailed = lastExecution.FailedItemCount
                        };
                        await errorResponse.WriteStringAsync(JsonSerializer.Serialize(errorResult, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = true
                        }));
                        return errorResponse;
                    }
                    else if (lastExecution.Status == IndexerExecutionStatus.Reset)
                    {
                        _logger.LogInformation("Indexer status is Reset. Returning reset status.");
                        
                        var resetResponse = req.CreateResponse(HttpStatusCode.OK);
                        resetResponse.Headers.Add("Content-Type", "application/json");
                        
                        var resetResult = new PollIndexerResponse
                        {
                            Status = lastExecution.Status.ToString(),
                            Message = "Indexer is in Reset state. It may not have started processing yet or has been reset.",
                            IsComplete = true,
                            ElapsedTime = DateTime.UtcNow - startTime,
                            ItemsProcessed = lastExecution.ItemCount,
                            ItemsFailed = lastExecution.FailedItemCount
                        };

                        await resetResponse.WriteStringAsync(JsonSerializer.Serialize(resetResult, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = true
                        }));
                        return resetResponse;
                    }
                    else if (lastExecution.Status == IndexerExecutionStatus.Success)
                    {
                        _logger.LogInformation("Indexer execution completed successfully!");
                        
                        var successResponse = req.CreateResponse(HttpStatusCode.OK);
                        successResponse.Headers.Add("Content-Type", "application/json");
                        
                        var successResult = new PollIndexerResponse
                        {
                            Status = lastExecution.Status.ToString(),
                            Message = "Indexer execution completed successfully!",
                            IsComplete = true,
                            ElapsedTime = DateTime.UtcNow - startTime,
                            ItemsProcessed = lastExecution.ItemCount,
                            ItemsFailed = lastExecution.FailedItemCount
                        };

                        await successResponse.WriteStringAsync(JsonSerializer.Serialize(successResult, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = true
                        }));
                        return successResponse;
                    }
                }
                catch (RequestFailedException ex)
                {
                    _logger.LogError($"Failed to get indexer status: {ex.Message}");
                    var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                    await errorResponse.WriteStringAsync($"Failed to get indexer status: {ex.Message}");
                    return errorResponse;
                }

                // Only continue polling if execution is actually in progress
                if (lastExecution?.Status == IndexerExecutionStatus.InProgress)
                {
                    await Task.Delay(pollInterval);
                }
                else
                {
                    // If no execution is in progress, we're done
                    break;
                }

            } while (lastExecution?.Status == IndexerExecutionStatus.InProgress);

            // If we exit the loop without a success or error, return current status (in progress)
            _logger.LogInformation($"Polling loop exited with status: {lastExecution?.Status}");
            
            var inProgressResponse = req.CreateResponse(HttpStatusCode.OK);
            inProgressResponse.Headers.Add("Content-Type", "application/json");
            
            var inProgressResult = new PollIndexerResponse
            {
                Status = lastExecution?.Status.ToString() ?? "Unknown",
                Message = $"Indexer is in {lastExecution?.Status} state. Polling completed without final resolution.",
                IsComplete = false,
                ElapsedTime = DateTime.UtcNow - startTime,
                ItemsProcessed = lastExecution?.ItemCount,
                ItemsFailed = lastExecution?.FailedItemCount
            };

            await inProgressResponse.WriteStringAsync(JsonSerializer.Serialize(inProgressResult, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
            return inProgressResponse;
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