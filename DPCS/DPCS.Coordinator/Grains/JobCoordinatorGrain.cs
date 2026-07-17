using DPCS.Coordinator.Strategies;
using JsonSerializer = System.Text.Json.JsonSerializer;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DPCS.Coordinator.Grains;

public class JobCoordinatorGrain : JobCoordinatorGrainBase
{
    private static readonly ActivitySource WorkUnitActivitySource = new("DPCS.Coordinator");
    private static readonly Meter CoordinatorMeter = new("DPCS.Coordinator");
    private static readonly Counter<long> WorkUnitAssignedCounter = CoordinatorMeter.CreateCounter<long>("dpcs.coordinator.wu.assigned.total", description: "Number of work units assigned by coordinator.");
    private static readonly Counter<long> WorkUnitCompletedCounter = CoordinatorMeter.CreateCounter<long>("dpcs.coordinator.wu.completed.total", description: "Number of work units completed and submitted.");
    private static readonly Counter<long> WorkUnitRetriedCounter = CoordinatorMeter.CreateCounter<long>("dpcs.coordinator.wu.retried.total", description: "Number of work units queued for retry.");
    private static readonly Counter<long> WorkUnitTimeoutCounter = CoordinatorMeter.CreateCounter<long>("dpcs.coordinator.wu.timeout.total", description: "Number of work units that timed out due to worker liveness expiration.");
    private static readonly UpDownCounter<long> WorkUnitActiveCounter = CoordinatorMeter.CreateUpDownCounter<long>("dpcs.coordinator.wu.active", description: "Number of work units currently assigned and awaiting result.");
    private static readonly Histogram<double> WorkUnitProcessingDurationSeconds = CoordinatorMeter.CreateHistogram<double>("dpcs.coordinator.wu.processing.duration.seconds", unit: "s", description: "Time from work assignment to completion or timeout.");

    private readonly ClusterIdentity _clusterIdentity;
    private const int MaxLifecycleRecords = 10000;
    private const int MaxGpuTelemetrySamples = 50000;

    private sealed class WorkUnitLifecycleState
    {
        public string RequestId { get; set; } = string.Empty;
        public string JobId { get; set; } = string.Empty;
        public string Mode { get; set; } = "unknown";
        public string AgentKey { get; set; } = string.Empty;
        public string ChunkSummary { get; set; } = string.Empty;
        public DateTime AssignedAtUtc { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public DateTime? TimedOutAtUtc { get; set; }
        public string Outcome { get; set; } = "assigned";
        public double? ProcessingDurationSeconds { get; set; }
        public int RecoveredCount { get; set; }
    }

    // Maps a Worker PID to the RequestId of the chunk they are currently processing.
    private readonly Lock _workersLock = new();
    private sealed class WorkerState
    {
        public DateTime LastSeen { get; set; }
        public HashSet<string> AssignedChunks { get; } = [];
        public Dictionary<string, DateTime> AssignedAtUtc { get; } = [];
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
    private readonly Dictionary<string, AgentStatus> _participatingAgentStatuses = [];
    private readonly Dictionary<string, WorkUnitLifecycleState> _workUnitLifecycle = [];
    private readonly Queue<string> _workUnitLifecycleOrder = [];
    private readonly List<AgentGpuTelemetrySample> _agentGpuTelemetryHistory = [];

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
                    using var timeoutActivity = WorkUnitActivitySource.StartActivity("wu.timeout", ActivityKind.Internal);
                    timeoutActivity?.SetTag("job.id", _clusterIdentity.Identity);
                    timeoutActivity?.SetTag("wu.request_id", reqId);
                    timeoutActivity?.SetTag("agent.id", pid.Id);
                    timeoutActivity?.SetTag("agent.address", pid.Address);
                    timeoutActivity?.SetTag("wu.mode", GetModeTag());

                    _jobStrategy?.FailChunk(reqId);
                    RecordChunkCompletionMetrics(workerState, reqId, timeout: true, recoveredCount: 0);
                    UpdateWorkUnitLifecycleOnTimeout(reqId, $"{pid.Address}/{pid.Id}");
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

        case JobSpecsEnvelope.PayloadOneofCase.HybridJobSpecs:
            _jobStrategy = new HybridJobStrategy(_clusterIdentity.Identity, request, _hashcatWrapper, _serverBaseUrl);
            registratrion.AttackMode = request.HybridJobSpecs.AttackMode switch
            {
                (int)AttackMode.Hybrid_WordlistMask or (int)AttackMode.Hybrid_MaskWordlist
                    => request.HybridJobSpecs.AttackMode,
                _ => throw new InvalidOperationException($"HybridJobStrategy can only be used with hybrid attack modes, but received {request.HybridJobSpecs.AttackMode}.")
            };
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
        using var assignActivity = WorkUnitActivitySource.StartActivity("wu.assign", ActivityKind.Producer);
        assignActivity?.SetTag("job.id", _clusterIdentity.Identity);
        assignActivity?.SetTag("agent.id", request.AgentId.Id);
        assignActivity?.SetTag("agent.address", request.AgentId.Address);
        assignActivity?.SetTag("wu.mode", GetModeTag());

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

        var assignedChunk = CreateAssignedChunk(nextChunk);
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
                state.AssignedAtUtc[requestId] = DateTime.UtcNow;
                WorkUnitAssignedCounter.Add(1, new KeyValuePair<string, object?>("mode", GetModeTag()));
                WorkUnitActiveCounter.Add(1, new KeyValuePair<string, object?>("mode", GetModeTag()));
                UpsertWorkUnitLifecycleOnAssigned(requestId, request.AgentId, assignedChunk);
            }

            if (_agentStatuses.TryGetValue(workerPid, out AgentStatus? value))
            {
                value.AssignedChunks.Add(assignedChunk);
                UpsertParticipatingStatus(value.Telemetry, value.AssignedChunks);
            }
            else
            {
                var status = new AgentStatus
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
                    AssignedChunks = { assignedChunk }
                };
                _agentStatuses[workerPid] = status;
                UpsertParticipatingStatus(status.Telemetry, status.AssignedChunks);
            }
        }

        assignActivity?.SetTag("wu.request_id", nextChunk.RequestId);
        assignActivity?.SetTag("wu.chunk", SummarizeChunk(assignedChunk));
        EmitWorkUnitLifecycleEvent("assigned", nextChunk.RequestId, request.AgentId, assignedChunk);

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
                    MaskChunk = new MaskAssignedChunk
                    {
                        Mask = assignment.MaskAssignment.Mask,
                        KeyspaceStart = assignment.MaskAssignment.KeyspaceStart,
                        KeyspaceEnd = assignment.MaskAssignment.KeyspaceStart + assignment.MaskAssignment.KeyspaceLength - 1,
                        TotalKeyspace = (_jobStrategy as MaskJobStrategy)?.GetStoredKeyspaceForMask(assignment.MaskAssignment.Mask) ?? 0
                    }
                };

            case WorkAssignmentEnvelope.PayloadOneofCase.DictionaryAssignment:
                return new AssignedChunk
                {
                    RequestId = assignment.RequestId,
                    DictionaryChunk = new DictionaryAssignedChunk
                    {
                        WordlistName = assignment.DictionaryAssignment.WordlistName,
                        ByteStart = assignment.DictionaryAssignment.StartByte,
                        ByteEnd = assignment.DictionaryAssignment.EndByte,
                    }
                };

            case WorkAssignmentEnvelope.PayloadOneofCase.CombinatorAssignment:
                return new AssignedChunk
                {
                    RequestId = assignment.RequestId,
                    CombinatorChunk = new CombinatorAssignedChunk
                    {
                        LeftWordlistName = assignment.CombinatorAssignment.LeftWordlistName,
                        LeftByteStart = assignment.CombinatorAssignment.LeftStartByte,
                        LeftByteEnd = assignment.CombinatorAssignment.LeftEndByte,
                        RightWordlistName = assignment.CombinatorAssignment.RightWordlistName,
                        RightByteStart = assignment.CombinatorAssignment.RightStartByte,
                        RightByteEnd = assignment.CombinatorAssignment.RightEndByte
                    }
                };

            case WorkAssignmentEnvelope.PayloadOneofCase.HybridAssignment:
                return new AssignedChunk
                {
                    RequestId = assignment.RequestId,
                    HybridChunk = new HybridAssignedChunk
                    {
                        WordlistName = assignment.HybridAssignment.WordlistName,
                        ByteStart = assignment.HybridAssignment.StartByte,
                        ByteEnd = assignment.HybridAssignment.EndByte,
                        Mask = assignment.HybridAssignment.Mask,
                        KeyspaceStart = assignment.HybridAssignment.KeyspaceStart,
                        KeyspaceEnd = assignment.HybridAssignment.KeyspaceStart + assignment.HybridAssignment.KeyspaceLength - 1,
                        TotalKeyspace = (_jobStrategy as HybridJobStrategy)?.GetStoredKeyspaceForMask(assignment.HybridAssignment.Mask) ?? 0,
                        CustomCharset1 = assignment.HybridAssignment.CustomCharset1,
                        CustomCharset2 = assignment.HybridAssignment.CustomCharset2,
                        CustomCharset3 = assignment.HybridAssignment.CustomCharset3,
                        CustomCharset4 = assignment.HybridAssignment.CustomCharset4,
                    }
                };
            default:
                return new AssignedChunk();
        }
    }

    public override async Task WorkResultSubmission(WorkResult request)
    {
        using var resultActivity = WorkUnitActivitySource.StartActivity("wu.result", ActivityKind.Consumer);
        resultActivity?.SetTag("job.id", _clusterIdentity.Identity);
        resultActivity?.SetTag("wu.request_id", request.RequestId);
        resultActivity?.SetTag("agent.id", request.AgentId.Id);
        resultActivity?.SetTag("agent.address", request.AgentId.Address);
        resultActivity?.SetTag("wu.mode", GetModeTag());

        var workerPid = new PID(request.AgentId.Address, request.AgentId.Id);
        string? requestId = null;
        lock (_workersLock)
        {
            if (_activeWorkers.TryGetValue(workerPid, out var state))
            {
                requestId = request.RequestId;
                if (!string.IsNullOrWhiteSpace(requestId))
                {
                    state.AssignedChunks.Remove(requestId);
                    RecordChunkCompletionMetrics(state, requestId, timeout: false, recoveredCount: request.RecoveredPasswords.Count);
                }
                state.LastSeen = DateTime.UtcNow;
                if (state.AssignedChunks.Count == 0)
                {
                    _activeWorkers.Remove(workerPid);
                    _agentStatuses.Remove(workerPid);
                }
                else if (_agentStatuses.TryGetValue(workerPid, out var agentStatus))
                {
                    var updatedStatus = new AgentStatus
                    {
                        Telemetry = agentStatus.Telemetry,
                        AssignedChunks = { agentStatus.AssignedChunks.Where(c => c.RequestId != requestId) }
                    };
                    _agentStatuses[workerPid] = updatedStatus;
                    UpsertParticipatingStatus(updatedStatus.Telemetry, updatedStatus.AssignedChunks);
                }
            }
        }

        if (requestId is null)
            return;

        UpdateWorkUnitLifecycleOnCompleted(requestId, request.RecoveredPasswords.Count, request.Success);
        EmitWorkUnitLifecycleEvent(
            "completed",
            requestId,
            request.AgentId,
            chunk: null,
            extras: new Dictionary<string, object?>
            {
                ["success"] = request.Success,
                ["recovered_count"] = request.RecoveredPasswords.Count
            });

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
            AppendGpuTelemetrySample(request);
            UpsertParticipatingStatus(request, assignedChunks: null);
            if (_activeWorkers.TryGetValue(workerPid, out var state))
            {
                state.LastSeen = DateTime.UtcNow;
                _agentStatuses[workerPid].Telemetry = request;
                UpsertParticipatingStatus(request, _agentStatuses[workerPid].AssignedChunks);
            }
        }
        return Task.CompletedTask;
    }

    public override Task<AgentGpuTelemetryExport> GetAgentGpuTelemetryHistory()
    {
        var export = new AgentGpuTelemetryExport
        {
            JobId = _clusterIdentity.Identity
        };

        lock (_workersLock)
        {
            export.Samples.Add(_agentGpuTelemetryHistory);
        }

        return Task.FromResult(export);
    }

    public override async Task<JobStatus> GetJobStatus()
    {
        if (_jobStrategy is null)
        {
            Console.WriteLine($"{_clusterIdentity.Identity}: Reporting status of cancelled or invalid job...");
            return new JobStatus
            {
                JobId = _clusterIdentity.Identity,
                Status = "Cancelled",
                ProgressPercentage = 100,
                ChunkAttackSeconds = _chunkAttackSeconds,
                RecoveredPasswords = { _recoveredPasswords },
                ParticipatingAgents = { _participatingAgentStatuses.Values },
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
            ParticipatingAgents = { _participatingAgentStatuses.Values },
        };
        return await Task.FromResult(jobStatus);
    }

    public override Task<WorkUnitLifecycleExport> GetWorkUnitLifecycle(WorkUnitLifecycleFilter request)
    {
        var export = new WorkUnitLifecycleExport
        {
            JobId = _clusterIdentity.Identity
        };

        DateTime? fromUtc = request.FromUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(request.FromUnixMs).UtcDateTime
            : null;
        DateTime? toUtc = request.ToUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(request.ToUnixMs).UtcDateTime
            : null;
        var outcomeFilter = request.Outcome?.Trim();

        lock (_workersLock)
        {
            var orderedRecords = _workUnitLifecycleOrder
                .Select(id => _workUnitLifecycle.TryGetValue(id, out var record) ? record : null)
                .Where(record => record is not null)
                .Cast<WorkUnitLifecycleState>();

            foreach (var record in orderedRecords)
            {
                if (!string.IsNullOrWhiteSpace(outcomeFilter)
                    && !string.Equals(record.Outcome, outcomeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (fromUtc.HasValue && record.AssignedAtUtc < fromUtc.Value)
                {
                    continue;
                }

                if (toUtc.HasValue && record.AssignedAtUtc > toUtc.Value)
                {
                    continue;
                }

                export.Records.Add(new WorkUnitLifecycleRecord
                {
                    RequestId = record.RequestId,
                    JobId = record.JobId,
                    Mode = record.Mode,
                    AgentKey = record.AgentKey,
                    ChunkSummary = record.ChunkSummary,
                    AssignedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(record.AssignedAtUtc),
                    CompletedAt = record.CompletedAtUtc.HasValue
                        ? Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(record.CompletedAtUtc.Value)
                        : null,
                    TimedOutAt = record.TimedOutAtUtc.HasValue
                        ? Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(record.TimedOutAtUtc.Value)
                        : null,
                    Outcome = record.Outcome,
                    ProcessingDurationSeconds = record.ProcessingDurationSeconds ?? -1,
                    RecoveredCount = record.RecoveredCount
                });
            }
        }

        return Task.FromResult(export);
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
            var modeTag = GetModeTag();
            var remainingAssigned = _activeWorkers.Values.Sum(state => state.AssignedChunks.Count);
            workersToStop = [.. _activeWorkers.Keys];
            _activeWorkers.Clear();
            _agentStatuses.Clear();
            if (remainingAssigned > 0)
            {
                WorkUnitActiveCounter.Add(-remainingAssigned, new KeyValuePair<string, object?>("mode", modeTag));
            }
        }
        foreach (var worker in workersToStop)
        {
            Context.Send(worker, new StopWork());
            // We don't need to FailChunk here because the job is being cancelled entirely.
        }
    }

    private void UpsertParticipatingStatus(AgentTelemetry telemetry, IEnumerable<AssignedChunk>? assignedChunks)
    {
        var key = $"{telemetry.AgentId.Address}/{telemetry.AgentId.Id}";
        var snapshot = new AgentStatus
        {
            Telemetry = telemetry
        };

        if (assignedChunks is not null)
        {
            snapshot.AssignedChunks.Add(assignedChunks);
        }

        _participatingAgentStatuses[key] = snapshot;
    }

    private void AppendGpuTelemetrySample(AgentTelemetry telemetry)
    {
        var sample = new AgentGpuTelemetrySample
        {
            AgentKey = $"{telemetry.AgentId.Address}/{telemetry.AgentId.Id}",
            CapturedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            CurrentHashrate = telemetry.CurrentHashrate,
            Temperature = telemetry.Temperature,
            FanSpeed = telemetry.FanSpeed,
            GpuUtilization = telemetry.GpuUtilization
        };
        sample.GpuDevices.Add(telemetry.GpuDevices);

        _agentGpuTelemetryHistory.Add(sample);
        if (_agentGpuTelemetryHistory.Count > MaxGpuTelemetrySamples)
        {
            var overflow = _agentGpuTelemetryHistory.Count - MaxGpuTelemetrySamples;
            _agentGpuTelemetryHistory.RemoveRange(0, overflow);
        }
    }

    public override async Task<JobSpecsEnvelope> GetJobSpecs()
    {
        return await Task.FromResult(_jobStrategy?.Specs ?? new JobSpecsEnvelope());
    }

    private void RecordChunkCompletionMetrics(WorkerState workerState, string requestId, bool timeout)
    {
        var modeTag = GetModeTag();
        WorkUnitActiveCounter.Add(-1, new KeyValuePair<string, object?>("mode", modeTag));

        if (workerState.AssignedAtUtc.Remove(requestId, out var assignedAtUtc))
        {
            var durationSeconds = (DateTime.UtcNow - assignedAtUtc).TotalSeconds;
            if (durationSeconds >= 0)
            {
                WorkUnitProcessingDurationSeconds.Record(
                    durationSeconds,
                    new KeyValuePair<string, object?>("mode", modeTag),
                    new KeyValuePair<string, object?>("outcome", timeout ? "timeout" : "completed"));
            }
        }

        if (timeout)
        {
            WorkUnitTimeoutCounter.Add(1, new KeyValuePair<string, object?>("mode", modeTag));
            WorkUnitRetriedCounter.Add(1, new KeyValuePair<string, object?>("mode", modeTag));
            return;
        }

        WorkUnitCompletedCounter.Add(1, new KeyValuePair<string, object?>("mode", modeTag));
    }

    private void RecordChunkCompletionMetrics(WorkerState workerState, string requestId, bool timeout, int recoveredCount)
    {
        RecordChunkCompletionMetrics(workerState, requestId, timeout);
        if (_workUnitLifecycle.TryGetValue(requestId, out var record))
        {
            record.RecoveredCount = recoveredCount;
        }
    }

    private void UpsertWorkUnitLifecycleOnAssigned(string requestId, AgentId agentId, AssignedChunk chunk)
    {
        var now = DateTime.UtcNow;
        _workUnitLifecycle[requestId] = new WorkUnitLifecycleState
        {
            RequestId = requestId,
            JobId = _clusterIdentity.Identity,
            Mode = GetModeTag(),
            AgentKey = $"{agentId.Address}/{agentId.Id}",
            ChunkSummary = SummarizeChunk(chunk),
            AssignedAtUtc = now,
            LastUpdatedUtc = now,
            Outcome = "assigned"
        };

        _workUnitLifecycleOrder.Enqueue(requestId);
        TrimWorkUnitLifecycleRecords();
    }

    private void UpdateWorkUnitLifecycleOnCompleted(string requestId, int recoveredCount, bool success)
    {
        if (!_workUnitLifecycle.TryGetValue(requestId, out var record))
        {
            return;
        }

        var now = DateTime.UtcNow;
        record.CompletedAtUtc = now;
        record.LastUpdatedUtc = now;
        record.RecoveredCount = recoveredCount;
        record.Outcome = success ? "completed_with_result" : "completed_no_result";
        record.ProcessingDurationSeconds = Math.Max(0, (now - record.AssignedAtUtc).TotalSeconds);
    }

    private void UpdateWorkUnitLifecycleOnTimeout(string requestId, string agentKey)
    {
        if (!_workUnitLifecycle.TryGetValue(requestId, out var record))
        {
            return;
        }

        var now = DateTime.UtcNow;
        record.AgentKey = agentKey;
        record.TimedOutAtUtc = now;
        record.LastUpdatedUtc = now;
        record.Outcome = "timed_out_requeued";
        record.ProcessingDurationSeconds = Math.Max(0, (now - record.AssignedAtUtc).TotalSeconds);

        EmitWorkUnitLifecycleEvent(
            "timed_out",
            requestId,
            new AgentId { Address = agentKey.Split('/').FirstOrDefault() ?? string.Empty, Id = agentKey.Split('/').LastOrDefault() ?? string.Empty },
            chunk: null,
            extras: new Dictionary<string, object?>
            {
                ["outcome"] = "timed_out_requeued"
            });
    }

    private void EmitWorkUnitLifecycleEvent(string eventName, string requestId, AgentId agentId, AssignedChunk? chunk, Dictionary<string, object?>? extras = null)
    {
        _workUnitLifecycle.TryGetValue(requestId, out var record);
        var payload = new Dictionary<string, object?>
        {
            ["event"] = eventName,
            ["timestamp_utc"] = DateTime.UtcNow,
            ["job_id"] = _clusterIdentity.Identity,
            ["request_id"] = requestId,
            ["mode"] = record?.Mode ?? GetModeTag(),
            ["agent_id"] = agentId.Id,
            ["agent_address"] = agentId.Address,
            ["agent_key"] = string.IsNullOrWhiteSpace(agentId.Address) && string.IsNullOrWhiteSpace(agentId.Id) ? record?.AgentKey : $"{agentId.Address}/{agentId.Id}",
            ["outcome"] = record?.Outcome,
            ["processing_duration_seconds"] = record?.ProcessingDurationSeconds,
            ["chunk"] = chunk is null ? record?.ChunkSummary : SummarizeChunk(chunk)
        };

        if (extras is not null)
        {
            foreach (var (key, value) in extras)
            {
                payload[key] = value;
            }
        }

        Console.WriteLine($"WU_LIFECYCLE {JsonSerializer.Serialize(payload)}");
    }

    private static string SummarizeChunk(AssignedChunk chunk)
    {
        return chunk.PayloadCase switch
        {
            AssignedChunk.PayloadOneofCase.MaskChunk =>
                $"mask={chunk.MaskChunk.Mask};range={chunk.MaskChunk.KeyspaceStart}-{chunk.MaskChunk.KeyspaceEnd}",
            AssignedChunk.PayloadOneofCase.DictionaryChunk =>
                $"wordlist={chunk.DictionaryChunk.WordlistName};bytes={chunk.DictionaryChunk.ByteStart}-{(chunk.DictionaryChunk.ByteEnd == -1 ? "EOF" : chunk.DictionaryChunk.ByteEnd.ToString())}",
            AssignedChunk.PayloadOneofCase.CombinatorChunk =>
                $"left={chunk.CombinatorChunk.LeftWordlistName}[{chunk.CombinatorChunk.LeftByteStart}-{(chunk.CombinatorChunk.LeftByteEnd == -1 ? "EOF" : chunk.CombinatorChunk.LeftByteEnd.ToString())}];right={chunk.CombinatorChunk.RightWordlistName}[{chunk.CombinatorChunk.RightByteStart}-{(chunk.CombinatorChunk.RightByteEnd == -1 ? "EOF" : chunk.CombinatorChunk.RightByteEnd.ToString())}]",
            AssignedChunk.PayloadOneofCase.HybridChunk =>
                $"wordlist={chunk.HybridChunk.WordlistName};bytes={chunk.HybridChunk.ByteStart}-{(chunk.HybridChunk.ByteEnd == -1 ? "EOF" : chunk.HybridChunk.ByteEnd.ToString())};mask={chunk.HybridChunk.Mask};range={chunk.HybridChunk.KeyspaceStart}-{chunk.HybridChunk.KeyspaceEnd}",
            _ => "unknown"
        };
    }

    private static string SummarizeChunk(WorkAssignmentEnvelope chunk)
    {
        return chunk.PayloadCase switch
        {
            WorkAssignmentEnvelope.PayloadOneofCase.MaskAssignment =>
                $"mask={chunk.MaskAssignment.Mask};range={chunk.MaskAssignment.KeyspaceStart}-{chunk.MaskAssignment.KeyspaceStart + chunk.MaskAssignment.KeyspaceLength - 1}",
            WorkAssignmentEnvelope.PayloadOneofCase.DictionaryAssignment =>
                $"dictionary_url={chunk.DictionaryAssignment.WordlistUrl}",
            WorkAssignmentEnvelope.PayloadOneofCase.CombinatorAssignment =>
                $"left={chunk.CombinatorAssignment.LeftWordlistName}[{chunk.CombinatorAssignment.LeftStartByte}-{(chunk.CombinatorAssignment.LeftEndByte == -1 ? "EOF" : chunk.CombinatorAssignment.LeftEndByte.ToString())}];right={chunk.CombinatorAssignment.RightWordlistName}[{chunk.CombinatorAssignment.RightStartByte}-{(chunk.CombinatorAssignment.RightEndByte == -1 ? "EOF" : chunk.CombinatorAssignment.RightEndByte.ToString())}]",
            WorkAssignmentEnvelope.PayloadOneofCase.HybridAssignment =>
                $"wordlist={chunk.HybridAssignment.WordlistUrl};mask={chunk.HybridAssignment.Mask};custom_charset1={chunk.HybridAssignment.CustomCharset1};custom_charset2={chunk.HybridAssignment.CustomCharset2};custom_charset3={chunk.HybridAssignment.CustomCharset3};custom_charset4={chunk.HybridAssignment.CustomCharset4};range={chunk.HybridAssignment.KeyspaceStart}-{chunk.HybridAssignment.KeyspaceStart + chunk.HybridAssignment.KeyspaceLength - 1}",
            _ => "unknown"
        };
    }

    private void TrimWorkUnitLifecycleRecords()
    {
        while (_workUnitLifecycleOrder.Count > MaxLifecycleRecords)
        {
            var oldestRequestId = _workUnitLifecycleOrder.Dequeue();
            _workUnitLifecycle.Remove(oldestRequestId);
        }
    }

    private string GetModeTag()
    {
        return _jobStrategy?.Mode switch
        {
            AttackMode.Mask => "mask",
            AttackMode.Dictionary => "dictionary",
            AttackMode.Combinator => "combinator",
            AttackMode.Hybrid_WordlistMask => "hybrid_wordlist_mask",
            AttackMode.Hybrid_MaskWordlist => "hybrid_mask_wordlist",
            AttackMode.Association => "association",
            _ => "unknown"
        };
    }
}