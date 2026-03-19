using DPCS.Coordinator.Strategies;

namespace DPCS.Coordinator.Grains;

public sealed class JobCoordinatorGrain : JobCoordinatorGrainBase
{
    private readonly ClusterIdentity _clusterIdentity;

    // Maps a Worker PID to the RequestId of the chunk they are currently processing.
    private readonly Dictionary<PID, string> _activeWorkers = [];

    private IJobStrategy? _jobStrategy;

    private DateTime _jobStartTime;

    private readonly HashcatWrapper _hashcatWrapper;

    private readonly ulong _chunkAttackSeconds;

    private readonly HashSet<RecoveredPassword> _recoveredPasswords = [];

    public JobCoordinatorGrain(IContext context, ClusterIdentity clusterIdentity, HashcatWrapper hashcatWrapper, ulong chunkAttackSeconds) : base(context)
    {
        _clusterIdentity = clusterIdentity;
        _hashcatWrapper = hashcatWrapper;
        _chunkAttackSeconds = chunkAttackSeconds;
        Console.WriteLine($"{_clusterIdentity.Identity}: created");
    }

    public override Task MaskJobInit(HashcatMaskJobSpecs request)
    {
        _jobStrategy = new MaskJobStrategy(_clusterIdentity.Identity, request, _hashcatWrapper, _chunkAttackSeconds);
        _jobStartTime = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public override Task DictionaryJobInit(HashcatDictionaryJobSpecs request)
    {
        _jobStrategy = new DictionaryJobStrategy(_clusterIdentity.Identity, request, _hashcatWrapper, _chunkAttackSeconds);
        _jobStartTime = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public override async Task<MaskWorkAssignment> MaskWorkRequest(WorkRequest request)
    {
        if (_jobStrategy is not MaskJobStrategy)
        {
            return new MaskWorkAssignment(); // Or handle error
        }

        var workerPid = new PID(request.AgentId.Address, request.AgentId.Id);
        MaskWorkAssignment? nextChunk;
        try
        {
            nextChunk = await _jobStrategy.NextMaskChunkAsync(request.CurrentHashrate);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{_clusterIdentity.Identity}: Failed to generate next mask chunk. Cancelling job. Error: {ex.Message}");
            var manager = System.Cluster().GetJobManagerGrain("root");
            await manager.FinishAck(new JobId { Id = request.JobId }, CancellationToken.None);
            await CancelJob();
            return new MaskWorkAssignment();
        }
        if (nextChunk is null)
        {
            // Job is complete, notify JobManagerGrain to stop advertising this job and mark it as finished.
            var manager = System.Cluster().GetJobManagerGrain("root");
            await manager.FinishAck(new JobId { Id = request.JobId }, CancellationToken.None);
            return new MaskWorkAssignment();
        }

        // Track the worker
        _activeWorkers[workerPid] = nextChunk.RequestId;

        return await Task.FromResult(nextChunk);
    }

    public override async Task<DictionaryWorkAssignment> DictionaryWorkRequest(WorkRequest request)
    {
        if (_jobStrategy is not DictionaryJobStrategy)
        {
            return new DictionaryWorkAssignment(); // Or handle error
        }

        var workerPid = new PID(request.AgentId.Address, request.AgentId.Id);
        DictionaryWorkAssignment? nextChunk;
        try
        {
            nextChunk = await _jobStrategy.NextDictionaryChunkAsync(request.CurrentHashrate);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{_clusterIdentity.Identity}: Failed to generate next dictionary chunk. Cancelling job. Error: {ex.Message}");
            var manager = System.Cluster().GetJobManagerGrain("root");
            await manager.FinishAck(new JobId { Id = request.JobId }, CancellationToken.None);
            await CancelJob();
            return new DictionaryWorkAssignment();
        }

        if (nextChunk is null)
        {
            // Job is complete, notify JobManagerGrain to stop advertising this job and mark it as finished.
            var manager = System.Cluster().GetJobManagerGrain("root");
            await manager.FinishAck(new JobId { Id = _clusterIdentity.Identity }, CancellationToken.None);
            return new DictionaryWorkAssignment();
        }

        // Track the worker
        _activeWorkers[workerPid] = nextChunk.RequestId;

        return await Task.FromResult(nextChunk);
    }

    public override async Task WorkResultSubmission(WorkResult request)
    {
        var workerPid = new PID(request.AgentId.Address, request.AgentId.Id);
        if (_activeWorkers.Remove(workerPid, out var requestId))
        {
            // Mark the chunk as complete in the strategy.
            _jobStrategy?.CompleteChunk(requestId);

            // If the result indicates the password was found, we handle that here.
            if (request.Success && request.RecoveredPasswords.Count > 0)
            {
                _jobStrategy?.HandleRecoveredPasswords(request.RecoveredPasswords);

                Console.BackgroundColor = ConsoleColor.Green;
                Console.ForegroundColor = ConsoleColor.Black;
                Console.WriteLine($"{_clusterIdentity.Identity}: Received {request.RecoveredPasswords.Count} recovered passwords from an agent.");
                foreach (var recovered in request.RecoveredPasswords)
                {
                    Console.WriteLine($"  - Hash: {recovered.Hash}, Plaintext: {recovered.Plaintext}");
                    _recoveredPasswords.Add(recovered);
                }
                Console.ResetColor();

                var now = DateTime.UtcNow;
                var jobResult = new JobResult
                {
                    JobId = _clusterIdentity.Identity,
                    RecoveredPasswords = { request.RecoveredPasswords },
                    AttackMode = _jobStrategy switch
                    {
                        MaskJobStrategy => (int)AttackMode.Mask,
                        DictionaryJobStrategy => (int)AttackMode.Dictionary,
                        _ => (int)AttackMode.Invalid
                    },
                    CrackedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(now),
                    TimeTaken = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(now - _jobStartTime)
                };

                await Context.Cluster()
                    .GetResultCollectorGrain(_clusterIdentity.Identity)
                    .StoreResult(jobResult, CancellationToken.None);
            }

            var progress = _jobStrategy?.GetProgress() ?? 0;
            Console.WriteLine($"{_clusterIdentity.Identity}: updated progress for job: {progress}%");
            if (progress >= 100)
            {
                // Notify the JobManagerGrain that the job is complete
                var cluster = System.Cluster();
                var manager = cluster.GetJobManagerGrain("root");
                await manager.FinishAck(new JobId { Id = _clusterIdentity.Identity }, CancellationToken.None);
            }
        }
    }

    public override /*async*/ Task Heartbeat(AgentTelemetry request)
    {
        return Task.CompletedTask;
    }

    public override async Task<JobStatus> GetJobStatus()
    {
        if (_jobStrategy is null)
        {
            Console.WriteLine($"{_clusterIdentity.Identity}: Reporting status of cancelled or invalid job...");
            Context.Stop(Context.Self);
            return new JobStatus
            {
                Status = "Cancelled",
                ProgressPercentage = 100,
                ChunkAttackSeconds = _chunkAttackSeconds,
                RecoveredPasswords = { _recoveredPasswords },
            };
        }

        var progress = _jobStrategy.GetProgress();
        var jobStatus = new JobStatus
        {
            JobId = _clusterIdentity.Identity,
            Status = progress < 100 ? "In Progress" : "Completed",
            ProgressPercentage = progress,
            AgentIds = { _activeWorkers.Keys.Select(pid => pid.ToString()) },
            ChunkAttackSeconds = _chunkAttackSeconds,
            RecoveredPasswords = { _recoveredPasswords },
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

    public override async Task<HashcatMaskJobSpecs> GetMaskJobSpecs()
    {
        if (_jobStrategy is MaskJobStrategy maskJobStrategy)
        {
            return await Task.FromResult(maskJobStrategy.Specs);
        }
        return new HashcatMaskJobSpecs();
    }

    public override async Task<HashcatDictionaryJobSpecs> GetDictionaryJobSpecs()
    {
        if (_jobStrategy is DictionaryJobStrategy dictionaryJobStrategy)
        {
            return await Task.FromResult(dictionaryJobStrategy.Specs);
        }
        return new HashcatDictionaryJobSpecs();
    }
}