using DPCS.Coordinator.Strategies;

namespace DPCS.Coordinator.Grains;

public sealed class JobCoordinatorGrain : JobCoordinatorGrainBase
{
    private readonly ClusterIdentity _clusterIdentity;

    // Maps a Worker PID to the RequestId of the chunk they are currently processing.
    private readonly Dictionary<PID, string> _activeWorkers = [];

    private IJobStrategy? _jobStrategy;

    public JobCoordinatorGrain(IContext context, ClusterIdentity clusterIdentity) : base(context)
    {
        _clusterIdentity = clusterIdentity;
        Console.WriteLine($"{_clusterIdentity.Identity}: created");
    }

    public override Task MaskJobInit(HashcatMaskJobSpecs request)
    {
        _jobStrategy = new MaskJobStrategy(request);
        return Task.CompletedTask;
    }

    public override Task DictionaryJobInit(HashcatDictionaryJobSpecs request)
    {
        _jobStrategy = new DictionaryJobStrategy(request);
        return Task.CompletedTask;
    }

    public override async Task<MaskWorkAssignment> MaskWorkRequest(WorkRequest request)
    {
        if (_jobStrategy is not MaskJobStrategy)
        {
            return new MaskWorkAssignment(); // Or handle error
        }

        var workerPid = new PID(request.AgentId.Address, request.AgentId.Id);
        var nextChunk = _jobStrategy.NextMaskChunk(_clusterIdentity.Identity, request.CurrentHashrate);

        if (nextChunk == null) return new MaskWorkAssignment(); // Job finished

        // Track the worker
        _activeWorkers[workerPid] = nextChunk.Meta.RequestId;

        return await Task.FromResult(nextChunk);
    }

    public override async Task<DictionaryWorkAssignment> DictionaryWorkRequest(WorkRequest request)
    {
        if (_jobStrategy is not DictionaryJobStrategy)
        {
            return new DictionaryWorkAssignment(); // Or handle error
        }

        var workerPid = new PID(request.AgentId.Address, request.AgentId.Id);
        var nextChunk = _jobStrategy.NextDictionaryChunk(_clusterIdentity.Identity, request.CurrentHashrate);

        if (nextChunk == null) return new DictionaryWorkAssignment();

        // Track the worker
        _activeWorkers[workerPid] = nextChunk.Meta.RequestId;

        return await Task.FromResult(nextChunk);
    }

    public override /*async*/ Task WorkResultSubmission(WorkResult request)
    {
        if (Context.Sender != null && _activeWorkers.Remove(Context.Sender, out var requestId))
        {
            // Notify strategy that this specific chunk is done.
            // (If the result indicates the password was found, we would handle that here too)
            _jobStrategy?.CompleteChunk(requestId);
        }
        return Task.CompletedTask;
    }

    public override /*async*/ Task Heartbeat(AgentTelemetry request)
    {
        return Task.CompletedTask;
    }

    public override async Task<JobStatus> GetJobStatus()
    {
        if (_jobStrategy is null)
        {
            Context.Stop(Context.Self);
            return new JobStatus { Status = "NotFound" };
        }

        var jobStatus = new JobStatus
        {
            JobId = _clusterIdentity.Identity,
            Status = "In Progress",
            ProgressPercentage = _jobStrategy.GetProgress()
        };
        return await Task.FromResult(jobStatus);
    }

    public override async Task CancelJob()
    {
        // Placeholder implementation for job cancellation
        Console.WriteLine($"{_clusterIdentity.Identity}: received job cancellation request");
        _jobStrategy = null;

        // Send cancellation signal to all active worker actors
        foreach (var worker in _activeWorkers.Keys)
        {
            Context.Send(worker, new StopWork());
            // We don't need to FailChunk here because the job is being cancelled entirely.
        }

        _activeWorkers.Clear();

        Context.Stop(Context.Self);
        await Task.CompletedTask;
    }
}