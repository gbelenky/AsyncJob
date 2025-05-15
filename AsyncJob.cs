using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;


namespace gbelenky.AsyncJob;

public static class AsyncJob
{
    [Function(nameof(AsyncJobOrchestrator))]
    public static async Task<List<string>> AsyncJobOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(AsyncJob));
        string instanceId = context.InstanceId;
        logger.LogInformation("Orchestration started with Instance ID: {InstanceId}", instanceId);
        var outputs = new List<string>();

        // extract orchestrator parameters
        OrchParams orchParams = context.GetInput<OrchParams>()
            ?? throw new ArgumentNullException(nameof(orchParams));

        string thirdPartyJobId = orchParams.ThirdPartyJobId;
        double queuedDuration = orchParams.QueuedDuration;
        double inProgressDuration = orchParams.InProgressDuration;

        context.SetCustomStatus("Queued");

        DateTime queuedTime = context.CurrentUtcDateTime.AddSeconds(queuedDuration);
        await context.CreateTimer(queuedTime, CancellationToken.None);
        logger.LogInformation("Job {JobId} is queued.", instanceId);

        // Transition to InProgress
        context.SetCustomStatus("InProgress");
        DateTime inProgressTime = context.CurrentUtcDateTime.AddSeconds(inProgressDuration);
        await context.CreateTimer(inProgressTime, CancellationToken.None);
        logger.LogInformation("Job {JobId} is inProgress.", instanceId);

        // Transition to Completed
        context.SetCustomStatus("Completed");

        return outputs;
    }

    [Function("AsyncJobTrigger")]
    public static async Task<HttpResponseData> AsyncJobTrigger(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "job-start/{jobName}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        FunctionContext executionContext, string jobName)
    {
        // The function input comes from the request content.
        ILogger logger = executionContext.GetLogger("AsyncJobTrigger");
        // extract ASYNC_JOB_QUEUED_DURATION_SEC from the environment variable
        double queuedDuration = double.TryParse(
            Environment.GetEnvironmentVariable("ASYNC_JOB_QUEUED_DURATION_SEC"),
            out var queued) ? queued : 1d;

        double inProgressDuration = double.TryParse(
            Environment.GetEnvironmentVariable("ASYNC_JOB_INPROGRESS_DURATION_SEC"),
            out var inProgress) ? inProgress : 1d;

        string instanceId = $"job-{jobName}";
        // create mock thirdPartyJobId
        string thirdPartyJobId = Guid.NewGuid().ToString();

        // Check if an orchestration with this ID exists and is not running
        var state = await client.GetInstanceAsync(instanceId);
        if (state == null || state.RuntimeStatus == OrchestrationRuntimeStatus.Completed
            || state.RuntimeStatus == OrchestrationRuntimeStatus.Failed
            || state.RuntimeStatus == OrchestrationRuntimeStatus.Terminated)
        {
            // Pack parameters into one object to avoid non-deterministic environment calls inside orchestrator
            var orchParams = new OrchParams(thirdPartyJobId, queuedDuration, inProgressDuration);

            // Start new orchestration instance, specifying the desired instance ID via options
            await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(AsyncJobOrchestrator),
                orchParams,
                new StartOrchestrationOptions { InstanceId = instanceId });
            logger.LogInformation("Started orchestration with ID = '{InstanceId}' for job '{JobName}'.", instanceId, jobName);
        }
        else
        {
            logger.LogInformation("Orchestration '{InstanceId}' for job '{JobName}' already exists with status {Status}.", instanceId, jobName, state.RuntimeStatus);
        }

        // Returns an HTTP 202 response with an instance management payload.
        // See https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-http-api#start-orchestration
        return await client.CreateCheckStatusResponseAsync(req, instanceId);
    }

    // Immutable parameter object with auto-implemented properties
    private record OrchParams(
        string ThirdPartyJobId,
        double QueuedDuration,
        double InProgressDuration);
}