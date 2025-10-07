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

public class GetTags
{
    private readonly ILogger<GetTags> _logger;

    public GetTags(ILogger<GetTags> logger)
    {
        _logger = logger;
    }

    [Microsoft.Azure.Functions.Worker.Function(nameof(GetTags))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        var cred = new ManagedIdentityCredential(clientId: "20238ad9-abb5-4ca6-a9ad-c468b21d0b3d");

        try
        {
            // Read the Azure AI Search skill request
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var skillRequest = JsonSerializer.Deserialize<SkillRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (skillRequest?.Values == null || !skillRequest.Values.Any())
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await errorResponse.WriteStringAsync("No values provided in skill request.");
                return errorResponse;
            }

            var responseRecords = new List<SkillResponseRecord>();

            // Process each record in the batch
            foreach (var record in skillRequest.Values)
            {
                try
                {
                    var responseRecord = new SkillResponseRecord
                    {
                        RecordId = record.RecordId,
                        Data = new Dictionary<string, object>()
                    };

                    // Extract the blob URI from the record data
                    if (record.Data.TryGetValue("metadata_storage_path", out var storagePathObj) && 
                        storagePathObj is JsonElement storagePathElement)
                    {
                        string blobUri = storagePathElement.GetString()!;
                        
                        // Get blob tags
                        var blobClient = new BlobClient(new Uri(blobUri), cred);
                        var tagsResponse = await blobClient.GetTagsAsync();
                        var tags = tagsResponse.Value.Tags;

                        // Add tags to the response data
                        responseRecord.Data["tags"] = tags;
                        responseRecord.Data["tagCount"] = tags.Count;

                        // Add individual tag values as separate fields (optional)
                        foreach (var tag in tags)
                        {
                            responseRecord.Data[$"tag_{tag.Key}"] = tag.Value;
                        }

                        _logger.LogInformation($"Successfully retrieved {tags.Count} tags for record {record.RecordId}");
                    }
                    else
                    {
                        responseRecord.Errors = new List<SkillError>
                        {
                            new SkillError { Message = "metadata_storage_path not found in record data" }
                        };
                    }

                    responseRecords.Add(responseRecord);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error processing record {record.RecordId}: {ex.Message}");
                    responseRecords.Add(new SkillResponseRecord
                    {
                        RecordId = record.RecordId,
                        Data = new Dictionary<string, object>(),
                        Errors = new List<SkillError>
                        {
                            new SkillError { Message = $"Error retrieving tags: {ex.Message}" }
                        }
                    });
                }
            }

            // Create the skill response
            var skillResponse = new SkillResponse
            {
                Values = responseRecords
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");

            var jsonResponse = JsonSerializer.Serialize(skillResponse, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            await response.WriteStringAsync(jsonResponse);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Unexpected error in GetTags skill: {ex.Message}");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Internal server error: {ex.Message}");
            return errorResponse;
        }
    }
}

// Data models for Azure AI Search custom skills
public class SkillRequest
{
    public List<SkillRequestRecord> Values { get; set; } = new();
}

public class SkillRequestRecord
{
    public string RecordId { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
}

public class SkillResponse
{
    public List<SkillResponseRecord> Values { get; set; } = new();
}

public class SkillResponseRecord
{
    public string RecordId { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public List<SkillError>? Errors { get; set; }
    public List<SkillWarning>? Warnings { get; set; }
}

public class SkillError
{
    public string Message { get; set; } = string.Empty;
}

public class SkillWarning
{
    public string Message { get; set; } = string.Empty;
}