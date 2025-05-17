using System.Net;
using System.Security.Policy;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;


namespace gbelenky.AsyncJob;

// Define the state of the JobEntity
public record JobEntityState(string ThirdPartyJobId, string JobName);


public static class AsyncJob
{
    [Function(nameof(JobStartOrchestrator))] // Renamed
    public static async Task<List<string>> JobStartOrchestrator( // Renamed
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

    public record StatusPayload(string? JobStatus);

    [Function(nameof(GetJobStatusActivity))]
    public static async Task<string> GetJobStatusActivity(
        [ActivityTrigger] string targetInstanceId,
        [DurableClient] DurableTaskClient durableTaskClient, // Inject DurableTaskClient
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(GetJobStatusActivity));
        logger.LogInformation("Activity: Attempting to fetch status for instance ID: {TargetInstanceId}", targetInstanceId);

        OrchestrationMetadata? instanceMetadata = await durableTaskClient.GetInstanceAsync(targetInstanceId, getInputsAndOutputs: true);

        if (instanceMetadata == null)
        {
            logger.LogWarning("Activity: Instance not found: {TargetInstanceId}", targetInstanceId);
            return "NotFound";
        }

        // The custom status is a JToken, deserialize it to string.
        // Corrected access to custom status via SerializedCustomStatus and ensuring it's not null before deserializing.
        string? customStatus = null;
        if (instanceMetadata.SerializedCustomStatus != null)
        {
            customStatus = System.Text.Json.JsonSerializer.Deserialize<string>(instanceMetadata.SerializedCustomStatus);
        }

        logger.LogInformation("Activity: Successfully fetched status for {TargetInstanceId}. Status: '{CustomStatus}'", targetInstanceId, customStatus ?? "null");
        return customStatus;
    }


    [Function(nameof(JobStatusOrchestrator))]
    public static async Task<string> JobStatusOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        string instanceId = context.GetInput<string>() ?? string.Empty;
        var customStatus = await context.CallActivityAsync<string>(nameof(GetJobStatusActivity), instanceId);
        return customStatus ?? "Unknown";
    }

    // add a function to retuen the status of the job
    [Function(nameof(JobStatusTrigger))] // Renamed
    public static async Task<HttpResponseData> JobStatusTrigger( // Renamed
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "job-status/{jobName}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client, // This is the DurableTaskClient
        FunctionContext executionContext,
        string jobName)
    {
        ILogger logger = executionContext.GetLogger(nameof(JobStatusTrigger));
        string targetJobInstanceId = $"job-{jobName}"; // Instance ID of the job we want to get status for

        logger.LogInformation("Trigger: Received request to get status for job: {JobName} (Instance ID: {TargetJobInstanceId})", jobName, targetJobInstanceId);

        // Start the orchestrator that will fetch the status.
        // The input to JobStatusOrchestrator is the instance ID of the job whose status is being queried.
        string statusQueryOrchestrationId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(JobStatusOrchestrator),
            targetJobInstanceId);

        logger.LogInformation("Trigger: Started status query orchestration with ID = '{StatusQueryOrchestrationId}' to get status for job '{JobName}'.", statusQueryOrchestrationId, jobName);

        // Wait for the orchestration to complete (timeout after 30 seconds)
        var timeout = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(timeout);
        var orchestrationStatus = await client.WaitForInstanceCompletionAsync(statusQueryOrchestrationId, true, cts.Token);

        var response = req.CreateResponse(HttpStatusCode.OK);
        if (orchestrationStatus == null)
        {
            await response.WriteAsJsonAsync(new { status = "Timeout while retrieving job status" });
            return response;
        }

        var statusPayload = orchestrationStatus.ReadOutputAs<string>();
        await response.WriteAsJsonAsync(new {
            status = statusPayload ?? "Unknown"
        });
        return response;
    }

    [Function("JobStartTrigger")] // Renamed from AsyncJobTrigger
    public static async Task<HttpResponseData> JobStartTrigger( // Renamed from AsyncJobTrigger
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "job-start/{jobName}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        FunctionContext executionContext, string jobName)
    {
        // The function input comes from the request content.
        ILogger logger = executionContext.GetLogger("JobStartTrigger"); // Updated logger category
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
                nameof(JobStartOrchestrator), // Updated to new name
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