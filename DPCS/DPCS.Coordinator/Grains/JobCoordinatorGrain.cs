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
    private readonly Dictionary<PID, AgentStatus> _agentStatuses = [];

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
                _agentStatuses.Remove(pid);
                foreach (var reqId in workerState.AssignedChunks)
                {
                    _jobStrategy?.FailChunk(reqId);
                    Console.WriteLine($"{_clusterIdentity.Identity}: Agent {pid.Address}/{pid.Id} timed out. Re-queuing chunk {reqId}.");
                }
            }
        }
    }

    public override async Task JobInit(JobSpecsEnvelope request)
    {
        _chunkAttackSeconds = request.ChunkTimeSeconds > 0 ? request.ChunkTimeSeconds : Constants.DefaultChunkTimeSeconds;
        
        _jobStartTime = DateTime.UtcNow;
        var registratrion = new JobRegistration
        {
            JobId = _clusterIdentity.Identity,
            HashType = request.HashType,
            StartTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(_jobStartTime)
        };

        switch (request.PayloadCase)
        {
        case JobSpecsEnvelope.PayloadOneofCase.MaskJobSpecs:
            _jobStrategy = new MaskJobStrategy(_clusterIdentity.Identity, request, _hashcatWrapper);
            registratrion.AttackMode = (int)AttackMode.Mask;
            break;

        case JobSpecsEnvelope.PayloadOneofCase.DictionaryJobSpecs:
            _jobStrategy = new DictionaryJobStrategy(_clusterIdentity.Identity, request, _serverBaseUrl);
            registratrion.AttackMode = (int)AttackMode.Dictionary;
            break;

        case JobSpecsEnvelope.PayloadOneofCase.CombinatorJobSpecs:
            _jobStrategy = new CombinatorJobStrategy(_clusterIdentity.Identity, request, _serverBaseUrl);
            registratrion.AttackMode = (int)AttackMode.Combinator;
            break;

        default:
            throw new InvalidOperationException("Invalid job specs payload");
        }
        await _jobStrategy.InitializeAsync();

        var collector = Context.Cluster().GetResultCollectorGrain(_clusterIdentity.Identity);
        await collector.RegisterJob(registratrion, CancellationToken.None);
    }

    public override async Task<WorkAssignmentEnvelope> WorkRequest(WorkRequest request)
    {
        if (_jobStrategy is null)
        {
            return new WorkAssignmentEnvelope();
        }

        var workerPid = new PID(request.AgentId.Address, request.AgentId.Id);
        WorkAssignmentEnvelope? nextChunk;
        try
        {
            var agentKey = $"{request.AgentId.Address}/{request.AgentId.Id}";
            nextChunk = await _jobStrategy.NextChunkAsync(request.CurrentHashrate, agentKey);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{_clusterIdentity.Identity}: Failed to generate next chunk. Cancelling job. Error: {ex.Message}");
            var manager = System.Cluster().GetJobManagerGrain("root");
            await manager.FinishAck(new JobId { Id = request.JobId }, CancellationToken.None);
            await CancelJob();
            return new WorkAssignmentEnvelope();
        }

        if (nextChunk is null)
        {
            return new WorkAssignmentEnvelope();
        }

        lock (_workersLock)
        {
            if (!_activeWorkers.TryGetValue(workerPid, out var state))
            {
                state = new WorkerState { LastSeen = DateTime.UtcNow };
                _activeWorkers[workerPid] = state;
            }

            var requestId = nextChunk.RequestId;
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                state.AssignedChunks.Add(requestId);
            }

            if (_agentStatuses.TryGetValue(workerPid, out AgentStatus? value))
            {
                value.AssignedChunks.Add(CreateAssignedChunk(nextChunk));
            }
            else
            {
                _agentStatuses[workerPid] = new AgentStatus
                {
                    Telemetry = new AgentTelemetry
                    {
                        AgentId = request.AgentId,
                        CurrentHashrate = -1,
                        Temperature = -1,
                        FanSpeed = -1,
                        GpuUtilization = -1,
                        RejectRate = float.NaN
                    },
                    AssignedChunks = { CreateAssignedChunk(nextChunk) }
                };
            }
        }

        return nextChunk;
    }

    private AssignedChunk CreateAssignedChunk(WorkAssignmentEnvelope assignment)
    {
        switch (assignment.PayloadCase)
        {
            case WorkAssignmentEnvelope.PayloadOneofCase.MaskAssignment:
                return new AssignedChunk
                {
                    RequestId = assignment.RequestId,
                    Mask = assignment.MaskAssignment.Mask,
                    KeyspaceStart = assignment.MaskAssignment.KeyspaceStart,
                    KeyspaceEnd = assignment.MaskAssignment.KeyspaceStart + assignment.MaskAssignment.KeyspaceLength - 1,
                    TotalKeyspace = (_jobStrategy as MaskJobStrategy)?.GetStoredKeyspaceForMask(assignment.MaskAssignment.Mask) ?? 0
                };
            case WorkAssignmentEnvelope.PayloadOneofCase.DictionaryAssignment:
            {
                var dictUri = new Uri(assignment.DictionaryAssignment.DictionaryChunkUrl);
                var parsedQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(dictUri.Query);
                return new AssignedChunk
                {
                    RequestId = assignment.RequestId,
                    WordlistName = dictUri.Segments.LastOrDefault() ?? "unknown",
                    ByteStart = dictUri.Query.Contains("startByte=")
                        ? long.TryParse(parsedQuery["startByte"].FirstOrDefault() ?? "0", out var startByte)
                            ? startByte : 0
                        : 0,
                    ByteEnd = dictUri.Query.Contains("endByte=")
                        ? long.TryParse(parsedQuery["endByte"].FirstOrDefault() ?? "0", out var endByte)
                            ? endByte : 0
                        : 0,
                };
            }
            default:
                return new AssignedChunk();
        }
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
                    _agentStatuses.Remove(workerPid);
                }
                else if (_agentStatuses.TryGetValue(workerPid, out var agentStatus))
                {
                    _agentStatuses[workerPid] = new AgentStatus
                    {
                        Telemetry = agentStatus.Telemetry,
                        AssignedChunks = { agentStatus.AssignedChunks.Where(c => c.RequestId != requestId) }
                    };
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
                AttackMode = (int)(_jobStrategy?.Mode ?? AttackMode.Invalid),
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

            if (_jobStrategy is not null)
                await _jobStrategy.CleanupAsync();
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
                _agentStatuses[workerPid].Telemetry = request;
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
            Agents = { _agentStatuses.Values },
            ChunkAttackSeconds = _chunkAttackSeconds,
            RecoveredPasswords = { _recoveredPasswords },
        };
        return await Task.FromResult(jobStatus);
    }

    public override async Task CancelJob()
    {
        // Placeholder implementation for job cancellation
        Console.WriteLine($"{_clusterIdentity.Identity}: received job cancellation request");
        float finalProgress = 0;
        if (_jobStrategy is not null)
        {
            finalProgress = _jobStrategy.GetProgress();
            await _jobStrategy.CleanupAsync();
            _jobStrategy = null;
        }
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
            _agentStatuses.Clear();
        }
        foreach (var worker in workersToStop)
        {
            Context.Send(worker, new StopWork());
            // We don't need to FailChunk here because the job is being cancelled entirely.
        }

        Context.Stop(Context.Self);
    }

    public override async Task<JobSpecsEnvelope> GetJobSpecs()
    {
        return await Task.FromResult(_jobStrategy?.Specs ?? new JobSpecsEnvelope());
    }
}