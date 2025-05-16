using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace gbelenky.AsyncJob;

public static class Delay
{
    [Function("Delay")]
    public static async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "delay")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("Delay");
        double delaySec = double.TryParse(
            Environment.GetEnvironmentVariable("DELAY_SEC"),
            out var delay) ? delay : 1d;

        logger.LogInformation($"Delaying for {delaySec} seconds...");
        await Task.Delay(TimeSpan.FromSeconds(delaySec));
        logger.LogInformation("Delay complete.");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync($"Delayed for {delaySec} seconds.");
        return response;
    }
}
