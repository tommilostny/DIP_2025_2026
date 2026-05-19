using DPCS.Coordinator.Strategies;

namespace DPCS.Coordinator.Grains;

public class JobCoordinatorGrain : JobCoordinatorGrainBase
{
    private readonly ClusterIdentity _clusterIdentity;

    // Maps a Worker PID to the RequestId of the chunk they are currently processing.
    private readonly Lock _workersLock = new();
    private sealed class WorkerState
    {
        public DateTime LastSeen { get; set; }
        public HashSet<string> AssignedChunks { get; } = [];
    }
    private readonly Dictionary<PID, WorkerState> _activeWorkers = [];
    private readonly Timer? _timeoutTimer;

    private IJobStrategy? _jobStrategy;

    private DateTime _jobStartTime;

    private readonly IHashcatWrapper _hashcatWrapper;

    private ulong _chunkAttackSeconds;
    private readonly string _serverBaseUrl;

    private readonly HashSet<RecoveredPassword> _recoveredPasswords = [];
    private readonly Dictionary<PID, AgentTelemetry> _agentTelemetries = [];

    private readonly TimeSpan _livenessTimeout;

    public JobCoordinatorGrain(IContext context, ClusterIdentity clusterIdentity, IHashcatWrapper hashcatWrapper, string serverBaseUrl, TimeSpan? livenessTimeout = null) : base(context)
    {
        _clusterIdentity = clusterIdentity;
        _hashcatWrapper = hashcatWrapper;
        _serverBaseUrl = serverBaseUrl;
        _livenessTimeout = livenessTimeout ?? TimeSpan.FromSeconds(45);
        var checkInterval = livenessTimeout.HasValue ? TimeSpan.FromSeconds(_livenessTimeout.TotalSeconds / 3.0) : TimeSpan.FromSeconds(30);
        
        // Setup the scanner to look for dead agents
        _timeoutTimer = new Timer(CheckTimeouts, null, checkInterval, checkInterval);
        Console.WriteLine($"{_clusterIdentity.Identity}: created");
    }

    private void CheckTimeouts(object? state)
    {
        lock (_workersLock)
        {
            var now = DateTime.UtcNow;
            var deadWorkers = _activeWorkers.Where(kvp => now - kvp.Value.LastSeen > _livenessTimeout).ToList();
            
            foreach (var (pid, workerState) in deadWorkers)
            {
                _activeWorkers.Remove(pid);
                _agentTelemetries.Remove(pid);
                foreach (var reqId in workerState.AssignedChunks)
                {
                    _jobStrategy?.FailChunk(reqId);
                    Console.WriteLine($"{_clusterIdentity.Identity}: Agent {pid.Address}/{pid.Id} timed out. Re-queuing chunk {reqId}.");
                }
            }
        }
    }

    public override async Task MaskJobInit(HashcatMaskJobSpecs request)
    {
        _chunkAttackSeconds = request.ChunkTimeSeconds > 0 ? request.ChunkTimeSeconds : Constants.DefaultChunkTimeSeconds;
        _jobStrategy = new MaskJobStrategy(_clusterIdentity.Identity, request, _hashcatWrapper, _chunkAttackSeconds);
        _jobStartTime = DateTime.UtcNow;

        var collector = Context.Cluster().GetResultCollectorGrain(_clusterIdentity.Identity);
        await collector.RegisterJob(new JobRegistration
        {
            JobId = _clusterIdentity.Identity,
            AttackMode = (int)AttackMode.Mask,
            HashType = request.HashType,
            StartTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(_jobStartTime)
        }, CancellationToken.None);
    }

    public override async Task DictionaryJobInit(HashcatDictionaryJobSpecs request)
    {
        _chunkAttackSeconds = request.ChunkTimeSeconds > 0 ? request.ChunkTimeSeconds : Constants.DefaultChunkTimeSeconds;
        _jobStrategy = new DictionaryJobStrategy(_clusterIdentity.Identity, request, _chunkAttackSeconds, _serverBaseUrl);
        _jobStartTime = DateTime.UtcNow;

        var collector = Context.Cluster().GetResultCollectorGrain(_clusterIdentity.Identity);
        await collector.RegisterJob(new JobRegistration
        {
            JobId = _clusterIdentity.Identity,
            AttackMode = (int)AttackMode.Dictionary,
            HashType = request.HashType,
            StartTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(_jobStartTime)
        }, CancellationToken.None);
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
            // We have reached the end of the keyspace, but other workers might still be computing.
            // Do NOT call FinishAck here. It is handled in WorkResultSubmission when Progress == 100.
            return new MaskWorkAssignment();
        }

        // Track the worker
        lock (_workersLock)
        {
            if (!_activeWorkers.TryGetValue(workerPid, out var state))
            {
                state = new WorkerState { LastSeen = DateTime.UtcNow };
                _activeWorkers[workerPid] = state;
            }
            state.AssignedChunks.Add(nextChunk.RequestId);
            if (!_agentTelemetries.ContainsKey(workerPid))
            {
                _agentTelemetries[workerPid] = new AgentTelemetry
                {
                    AgentId = request.AgentId,
                    CurrentHashrate = -1,
                    Temperature = -1,
                    FanSpeed = -1,
                    GpuUtilization = -1,
                    RejectRate = float.NaN
                };
            }
        }

        return nextChunk;
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
            // We have reached the end of the wordlists, but other workers might still be computing.
            return new DictionaryWorkAssignment();
        }

        // Track the worker
        lock (_workersLock)
        {
            if (!_activeWorkers.TryGetValue(workerPid, out var state))
            {
                state = new WorkerState { LastSeen = DateTime.UtcNow };
                _activeWorkers[workerPid] = state;
            }
            state.AssignedChunks.Add(nextChunk.RequestId);
            if (!_agentTelemetries.ContainsKey(workerPid))
            {
                _agentTelemetries[workerPid] = new AgentTelemetry
                {
                    AgentId = request.AgentId,
                    CurrentHashrate = -1,
                    Temperature = -1,
                    FanSpeed = -1,
                    GpuUtilization = -1,
                    RejectRate = float.NaN
                };
            }
        }

        return nextChunk;
    }

    public override async Task WorkResultSubmission(WorkResult request)
    {
        var workerPid = new PID(request.AgentId.Address, request.AgentId.Id);
        string? requestId = null;
        lock (_workersLock)
        {
            if (_activeWorkers.TryGetValue(workerPid, out var state))
            {
                requestId = request.RequestId;
                state.AssignedChunks.Remove(requestId);
                state.LastSeen = DateTime.UtcNow;
                if (state.AssignedChunks.Count == 0)
                {
                    _activeWorkers.Remove(workerPid);
                    _agentTelemetries.Remove(workerPid);
                }
            }
        }

        if (requestId is null)
            return;

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
            _timeoutTimer?.Dispose();
            // Notify the JobManagerGrain that the job is complete
            var cluster = System.Cluster();
            var manager = cluster.GetJobManagerGrain("root");
            await manager.FinishAck(new JobId { Id = _clusterIdentity.Identity }, CancellationToken.None);

            var collector = cluster.GetResultCollectorGrain(_clusterIdentity.Identity);
            await collector.UpdateJobProgress(new JobProgressUpdate
            {
                JobId = _clusterIdentity.Identity,
                ProgressPercentage = 100,
                Status = "Completed"
            }, CancellationToken.None);
        }
    }

    public override Task Heartbeat(AgentTelemetry request)
    {
        var workerPid = new PID(request.AgentId.Address, request.AgentId.Id);
        lock (_workersLock)
        {
            if (_activeWorkers.TryGetValue(workerPid, out var state))
            {
                state.LastSeen = DateTime.UtcNow;
                _agentTelemetries[workerPid] = request;
            }
        }
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
                JobId = _clusterIdentity.Identity,
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
            Agents = { _agentTelemetries.Values },
            ChunkAttackSeconds = _chunkAttackSeconds,
            RecoveredPasswords = { _recoveredPasswords },
        };
        return await Task.FromResult(jobStatus);
    }

    public override async Task CancelJob()
    {
        // Placeholder implementation for job cancellation
        Console.WriteLine($"{_clusterIdentity.Identity}: received job cancellation request");
        var finalProgress = _jobStrategy?.GetProgress() ?? 0;
        _jobStrategy = null;
        _timeoutTimer?.Dispose();

        var collector = Context.Cluster().GetResultCollectorGrain(_clusterIdentity.Identity);
        await collector.UpdateJobProgress(new JobProgressUpdate 
        {
            JobId = _clusterIdentity.Identity,
            ProgressPercentage = finalProgress,
            Status = "Cancelled"
        }, CancellationToken.None);

        // Send cancellation signal to all active worker actors
        List<PID> workersToStop;
        lock (_workersLock)
        {
            workersToStop = [.. _activeWorkers.Keys];
            _activeWorkers.Clear();
            _agentTelemetries.Clear();
        }
        foreach (var worker in workersToStop)
        {
            Context.Send(worker, new StopWork());
            // We don't need to FailChunk here because the job is being cancelled entirely.
        }

        Context.Stop(Context.Self);
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