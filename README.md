# AsyncJob
lightweight async job implementation

---

### Project Description: AsyncJob

**AsyncJob** is a lightweight implementation of asynchronous job orchestration, leveraging Microsoft Azure Functions and Durable Tasks. It is designed to handle job lifecycles with states such as `Queued`, `InProgress`, and `Completed`. The project is particularly suited for applications requiring reliable, stateful, and distributed task management.

### Key Features:
1. **Azure Functions Integration**: AsyncJob uses Azure Functions to define and execute orchestrators and triggers.
2. **Durable Task Framework**: The project employs the Durable Task framework for managing long-running workflows and state persistence.
3. **Customizable Durations**: Job states such as `Queued` and `InProgress` can be customized using environment variables.
4. **State Management**: Provides detailed logging and state transitions for each job instance.
5. **HTTP Trigger Support**: The `AsyncJobTrigger` function allows initiating jobs via HTTP requests.

### Environment Variables:
The project uses the following environment variables for configuration:
- `ASYNC_JOB_QUEUED_DURATION_SEC`: Specifies the duration (in seconds) for which a job remains in the `Queued` state.
- `ASYNC_JOB_INPROGRESS_DURATION_SEC`: Specifies the duration (in seconds) for which a job remains in the `InProgress` state.

### Example Workflow:
1. **Job Initialization**:
   - A job is started via an HTTP GET request to the `job-start/{jobName}` route.
   - The job parameters, including durations, are extracted from the environment variables.
2. **State Transitions**:
   - The job transitions through `Queued` and `InProgress` states, with timers set for each phase.
   - Custom status messages are logged at each step.
3. **Completion**:
   - The job concludes with a `Completed` state, and an HTTP response is returned to the client.

### Fetching Custom Status

You can query the current custom status (customStatus below) of a job instance via the Durable Functions HTTP API:

```http
/runtime/webhooks/durabletask/instances/{jobName}
```

with the response similar to:

```json
{
  "name": "AsyncJobOrchestrator",
  "instanceId": "job-gb4711",
  "runtimeStatus": "Running",
  "input": {
    "ThirdPartyJobId": "d349c562-53ea-41ef-a1b9-8f0d4be22e4f",
    "QueuedDuration": 20,
    "InProgressDuration": 30
  },
  "customStatus": "Queued",
  "output": null,
  "createdTime": "2025-05-15T14:41:12Z",
  "lastUpdatedTime": "2025-05-15T14:41:12Z"
}
```

AsyncJob is licensed under the MIT License, allowing for flexible use and modification in both personal and commercial projects.