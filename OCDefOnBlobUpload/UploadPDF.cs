using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using HttpMultipartParser;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Threading.Tasks;

namespace OCDefOnBlobUpload;

// NOTE: Although chunking and OCR can be done in here, Azure AI search has it built in so it's disabled.
// Without them enabled all this function does is upload the PDF and call the AI Search indexer.
public class UploadPDF
{
    private readonly ILogger<UploadPDF> _logger;
    private const int CHUNK_SIZE = 2000, CHUNK_OVERLAP = 200;
    private const string accountName = "ocdefstorage";
    private readonly string searchEndpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT")!;
    private readonly string adminSearchKey = Environment.GetEnvironmentVariable("AZURE_SEARCH_ADMIN_KEY")!;
    private readonly string blobIndexerName = Environment.GetEnvironmentVariable("AZURE_BLOB_INDEXER_NAME")!;
    private readonly bool RUN_OCR_AND_CHUNKING = Environment.GetEnvironmentVariable("RUN_OCR_AND_CHUNKING") == "true";

    public UploadPDF(ILogger<UploadPDF> logger)
    {
        _logger = logger;
    }

    [Function(nameof(UploadPDF))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
    {
        _logger.LogInformation("HTTP trigger function processed a request.");
        var cred = new VisualStudioCredential();
        var pdfUri = new Uri($"https://{accountName}.blob.core.windows.net/pdfs");
        var ocrUri = new Uri($"https://{accountName}.blob.core.windows.net/pdfs-ocr");
        _logger.LogInformation("Successfully authenticated function");

        // Validate request
        if (!req.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            !req.Headers.TryGetValues("Content-Type", out var contentTypes) ||
            !contentTypes.First().StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteStringAsync("Request must be multipart/form-data POST with a file.");
            return badResponse;
        }


        // Make sure all files are valid before proceeding
        var parser = await MultipartFormDataParser.ParseAsync(req.Body);
        foreach (var file in parser.Files)
        {
            if (file == null || file.Data == null || file.Data.Length == 0)
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                if (file != null && file.FileName != null)
                    await badResponse.WriteStringAsync($"File {file.FileName} is empty.");
                else
                    await badResponse.WriteStringAsync("One or more files are null.");
                return badResponse;
            }

            else if (!file.FileName.EndsWith("pdf", StringComparison.OrdinalIgnoreCase))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync($"File {file.FileName} is not a PDF.");
                return badResponse;
            }
        }

        BlobContainerClient container = new BlobContainerClient(pdfUri, cred);
        DocumentIntelligenceClient docintel = new DocumentIntelligenceClient(
            new Uri("https://ocdef-docintelpro.cognitiveservices.azure.com/"),
            cred);
        _logger.LogInformation("Successfully connected to blob container " + container.AccountName + "/" + container.Name +
            " and document intelligence");
        foreach (var file in parser.Files)
        {
            // Upload the file to blob storage
            var fileName = file.FileName ?? "uploaded_file.pdf";
            BlobClient blob = container.GetBlobClient(fileName);
            _logger.LogInformation("Uploading {fileName} for archiving...", fileName);
            using var memoryStream = new MemoryStream();
            await file.Data.CopyToAsync(memoryStream);
            memoryStream.Position = 0;
            await blob.UploadAsync(memoryStream, overwrite: true);

            // Perform OCR using Document Intelligence
            if (RUN_OCR_AND_CHUNKING)
            {
                _logger.LogInformation("Performing OCR on {fileName}...", fileName);
                memoryStream.Position = 0;
                var operation = await docintel.AnalyzeDocumentAsync(
                    WaitUntil.Completed,
                    new AnalyzeDocumentOptions("prebuilt-read", BinaryData.FromStream(memoryStream)));

                AnalyzeResult result = operation.Value;
                var allText = string.Join(" ", result.Pages.SelectMany(p => p.Lines).Select(l => l.Content));

                // Chunk the text into smaller pieces
                _logger.LogInformation("Chunking {fileName} and uploading to blob...", fileName);
                var total = (allText.Length + CHUNK_SIZE - 1) / (CHUNK_SIZE - CHUNK_OVERLAP);
                container = new BlobContainerClient(ocrUri, cred);
                for (int i = 0; i < allText.Length; i += (CHUNK_SIZE - CHUNK_OVERLAP))
                {
                    string chunk;
                    int chunkNum = i / (CHUNK_SIZE - CHUNK_OVERLAP) + 1;
                    if (i + CHUNK_SIZE <= allText.Length)
                        chunk = allText.Substring(i, CHUNK_SIZE);
                    else
                        chunk = allText.Substring(i);
                    string chunkName = fileName.Substring(0, fileName.Length - 4) + $"_chunk_{chunkNum}.txt";
                    var chunkBlob = container.GetBlobClient(chunkName);
                    using (var ocrStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(chunk)))
                    {
                        ocrStream.Position = 0;
                        await chunkBlob.UploadAsync(ocrStream, overwrite: true);
                    }

                    if (chunkNum % 10 == 0)
                    {
                        _logger.LogInformation("Uploaded chunk {chunkNum} / {total}", chunkNum, total);
                    }
                }
            }
            _logger.LogInformation("Completed upload, calling Azure AI Search indexer...");
            SearchIndexerClient indexerClient = new SearchIndexerClient(new Uri(searchEndpoint), new AzureKeyCredential(adminSearchKey));
            await indexerClient.RunIndexerAsync(blobIndexerName);
            try
            {
                await indexerClient.RunIndexerAsync(blobIndexerName);
            }
            catch (RequestFailedException ex) when (ex.Status == 429)
            {
                Console.WriteLine("Failed to run indexer: {0}", ex.Message);
            }
            _logger.LogInformation("Upload and indexing complete.");
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync("Upload and indexing complete!");
        return response;
    }
}
