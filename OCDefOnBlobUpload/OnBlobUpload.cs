using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace OCDefOnBlobUpload;

public class OnBlobUpload
{
    private readonly ILogger<OnBlobUpload> _logger;

    public OnBlobUpload(ILogger<OnBlobUpload> logger)
    {
        _logger = logger;
    }

    [Function(nameof(OnBlobUpload))]
    public async Task Run([BlobTrigger("pdfs/{name}")] Stream stream, string name)
    {
        using var blobStreamReader = new StreamReader(stream);
        var content = await blobStreamReader.ReadToEndAsync();
        _logger.LogInformation("C# Blob Trigger (using Event Grid) processed blob\n Name: {name} \n Data: {content}", name, content);
    }
}