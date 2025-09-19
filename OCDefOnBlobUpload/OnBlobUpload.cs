using System.IO;
using System.Threading.Tasks;
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
    [BlobOutput("test-samples-output/{name}-output.txt")]
    public static string Run(
        [BlobTrigger("pdf/{name}")] string myTriggerItem,
        FunctionContext context)
    {
        var logger = context.GetLogger("OnBlobUpload");
        logger.LogInformation("Triggered Item = {myTriggerItem}", myTriggerItem);

        // Blob Output
        return "blob-output content";
    }
}
