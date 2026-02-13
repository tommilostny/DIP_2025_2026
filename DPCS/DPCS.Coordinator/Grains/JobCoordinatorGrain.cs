using DPCS.Coordinator.Enums;

namespace DPCS.Coordinator.Grains;

public sealed class JobCoordinatorGrain : JobCoordinatorGrainBase
{
    private abstract record WorkDetailsUnion;
    private sealed record MaskWorkDetails(MaskWorkAssignment Assignment) : WorkDetailsUnion;
    private sealed record DictionaryWorkDetails(DictionaryWorkAssignment Assignment) : WorkDetailsUnion;

    private sealed class WorkChunk
    {
        public required PID WorkerPid { get; set; }

        public WorkChunkState State { get; set; } = WorkChunkState.Pending;

        public required WorkDetailsUnion WorkDetails { get; set; }
    }

    private readonly ClusterIdentity _clusterIdentity;

    private ulong _globalCursor = 0;

    private readonly Dictionary<PID, WorkChunk> _workChunks = [];

    private AttackMode _attackMode = AttackMode.Invalid;

    public JobCoordinatorGrain(IContext context, ClusterIdentity clusterIdentity) : base(context)
    {
        _clusterIdentity = clusterIdentity;
        Console.WriteLine($"{_clusterIdentity.Identity}: created");
    }

    public override Task MaskJobInit(HashcatMaskJobSpecs request)
    {
        _attackMode = AttackMode.Mask;
        return Task.CompletedTask;
    }

    public override Task DictionaryJobInit(HashcatDictionaryJobSpecs request)
    {
        _attackMode = AttackMode.Dictionary;
        return Task.CompletedTask;
    }

    public override async Task<MaskWorkAssignment> MaskWorkRequest(WorkRequest request)
    {
        var workerPid = new PID(request.AgentId.Address, request.AgentId.Id);
        // Track the worker
        if (!_workChunks.ContainsKey(workerPid))
        {
            _workChunks[workerPid] = new WorkChunk 
            { 
                WorkerPid = workerPid,
                WorkDetails = new MaskWorkDetails(new MaskWorkAssignment()) // Placeholder init
            };
        }

        return await Task.FromResult(new MaskWorkAssignment());
    }

    public override async Task<DictionaryWorkAssignment> DictionaryWorkRequest(WorkRequest request)
    {
        var workerPid = new PID(request.AgentId.Address, request.AgentId.Id);
        // Track the worker
        if (!_workChunks.ContainsKey(workerPid))
        {
            _workChunks[workerPid] = new WorkChunk 
            { 
                WorkerPid = workerPid,
                WorkDetails = new DictionaryWorkDetails(new DictionaryWorkAssignment()) // Placeholder init
            };
        }

        return await Task.FromResult(new DictionaryWorkAssignment());
    }

    public override /*async*/ Task WorkResultSubmission(WorkResult request)
    {
        return Task.CompletedTask;
    }

    public override /*async*/ Task Heartbeat(AgentTelemetry request)
    {
        return Task.CompletedTask;
    }

    public override async Task<JobStatus> GetJobStatus()
    {
        if (_attackMode == AttackMode.Invalid)
        {
            Context.Stop(Context.Self);
            return new JobStatus { Status = "NotFound" };
        }

        // Placeholder implementation, returning dummy job status
        var jobStatus = new JobStatus
        {
            JobId = _clusterIdentity.Identity,
            Status = "In Progress",
            ProgressPercentage = 42.0
        };
        return await Task.FromResult(jobStatus);
    }

    public override async Task CancelJob()
    {
        // Placeholder implementation for job cancellation
        Console.WriteLine($"{_clusterIdentity.Identity}: received job cancellation request");
        _attackMode = AttackMode.Invalid;

        // Send cancellation signal to all active worker actors
        foreach (var chunk in _workChunks.Values)
        {
            Context.Send(chunk.WorkerPid, new StopWork());
        }

        _workChunks.Clear();

        Context.Stop(Context.Self);
        await Task.CompletedTask;
    }
}