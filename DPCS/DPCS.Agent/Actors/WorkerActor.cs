using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DPCS.Agent.Actors;

/// <summary>
/// Worker actor that discovers jobs, prefetches work assignments, executes Hashcat chunks,
/// and submits results back to the coordinator.
/// </summary>
/// <remarks>
/// The actor keeps prefetching and processing decoupled so network/download latency can be
/// hidden behind chunk execution when queue size is greater than zero.
/// </remarks>
public sealed class WorkerActor(Cluster cluster, IHashcatWrapper hashcatWrapper, int maxPrefetchQueueSize = 1, TimeSpan? heartbeatInterval = null) : IActor
{
    private static readonly ActivitySource WorkUnitActivitySource = new("DPCS.Agent");
    private static readonly Meter AgentMeter = new("DPCS.Agent");
    private static readonly Counter<long> WorkUnitStartedCounter = AgentMeter.CreateCounter<long>("dpcs.agent.wu.started.total", description: "Number of work-unit executions started by agent.");
    private static readonly Counter<long> WorkUnitResultCounter = AgentMeter.CreateCounter<long>("dpcs.agent.wu.result.total", description: "Number of finished work-unit executions by outcome.");
    private static readonly UpDownCounter<long> WorkUnitActiveCounter = AgentMeter.CreateUpDownCounter<long>("dpcs.agent.wu.active", description: "Number of work units currently executing on this agent.");
    private static readonly Histogram<double> WorkUnitComputeDurationSeconds = AgentMeter.CreateHistogram<double>("dpcs.agent.wu.compute.duration.seconds", unit: "s", description: "Time spent in hashcat compute for one work-unit execution.");
    private static readonly Histogram<double> WorkUnitSubmissionDurationSeconds = AgentMeter.CreateHistogram<double>("dpcs.agent.wu.submission.duration.seconds", unit: "s", description: "Time spent submitting one work-unit result back to coordinator.");
    private static readonly Histogram<double> WorkUnitTotalDurationSeconds = AgentMeter.CreateHistogram<double>("dpcs.agent.wu.total.duration.seconds", unit: "s", description: "Total work-unit execution time including compute and submission.");

    private CancellationTokenSource? _currentWorkCts;
    private JobAssignment? _currentJob;
    private readonly HashFileStore _hashFileStore = new();
    private readonly RuleFileStore _ruleFileStore = new();
    private readonly WorkAssignmentMaterializer _workAssignmentMaterializer = new();

    private readonly Queue<WorkAssignmentEnvelope> _workQueue = new();
    private bool _isPrefetching;
    private bool _isCracking;
    private bool _noMoreWork;
    private Timer? _heartbeatTimer;
    private readonly TimeSpan _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(5);

    /// <summary>
    /// Main actor message dispatcher.
    /// </summary>
    public Task ReceiveAsync(IContext context) => context.Message switch
    {
        Started => OnStarted(context),
        Stopped => OnStopped(),
        StartLoop => FindJob(context),
        StopWork => OnStopWork(context),
        PrefetchWork => OnPrefetchWork(context),
        ProcessWork => OnProcessWork(context),
        HeartbeatTick => SendHeartbeatAsync(context),
        _ => Task.CompletedTask
    };

    /// <summary>
    /// Initializes periodic heartbeat and starts job discovery loop.
    /// </summary>
    private Task OnStarted(IContext context)
    {
        Console.WriteLine($"WorkerActor {context.Self} started.");
        _heartbeatTimer = new Timer(_ => context.Send(context.Self, new HeartbeatTick()), null, _heartbeatInterval, _heartbeatInterval);
        context.Send(context.Self, new StartLoop());
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops periodic heartbeat timer.
    /// </summary>
    private Task OnStopped()
    {
        _heartbeatTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels current processing, clears prefetched work, and returns actor to discovery mode.
    /// </summary>
    private Task OnStopWork(IContext context)
    {
        Console.WriteLine("WorkerActor received StopWork signal.");
        _currentWorkCts?.Cancel();
        
        // Cleanup any prefetched temporary files hanging out in the queue.
        foreach (var chunk in _workQueue)
        {
            _workAssignmentMaterializer.CleanupAssignmentFiles(chunk);
        }
        _workQueue.Clear();
        _isPrefetching = false;
        _isCracking = false;
        _noMoreWork = false;

        if (_currentJob is not null)
        {
            CleanupJobData(_currentJob.JobId);
            _currentJob = null;
        }

        // Start looking for a new job immediately after stopping current work.
        context.Send(context.Self, new StartLoop());
        return Task.CompletedTask;
    }

    /// <summary>
    /// Attempts to discover a new job assignment and initialize local state for processing.
    /// </summary>
    private async Task FindJob(IContext context)
    {
        // Reset metrics when entering discovery so heartbeat telemetry starts from a clean state.
        hashcatWrapper.ResetMetrics();
        try
        {
            var assignment = await cluster
                .GetJobManagerGrain("root")
                .JobDiscovery(new AgentId
                {
                    Address = context.Self.Address,
                    Id = context.Self.Id,
                }, CancellationToken.None);

            if (assignment is null or { ModeId: (int)AttackMode.Invalid })
            {
                // No job, retry later
                await Task.Delay(5000);
                context.Send(context.Self, new StartLoop());
                return;
            }

            // If we are switching to a new job, clean up the old one's data.
            if (_currentJob is not null && _currentJob.JobId != assignment.JobId)
            {
                CleanupJobData(_currentJob.JobId);
            }

            Console.WriteLine($"Job found: {assignment.JobId}");
            _currentJob = assignment;
            await _ruleFileStore.InitializeJobRulesAsync(assignment.JobId, assignment.RuleFileContent, CancellationToken.None);
            _noMoreWork = false;
            _isPrefetching = false;
            _isCracking = false;
            _workQueue.Clear();

            // Start prefetch loop; processing is triggered as soon as first chunk arrives.
            context.Send(context.Self, new PrefetchWork());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Discovery error: {ex.Message}");
            await Task.Delay(5000);
            context.Send(context.Self, new StartLoop());
        }
    }

    /// <summary>
    /// Prefetches the next chunk when queue capacity allows and schedules follow-up prefetch/process ticks.
    /// </summary>
    private Task OnPrefetchWork(IContext context)
    {
        if (_currentJob is null || _isPrefetching || _noMoreWork)
        {
            return Task.CompletedTask;
        }

        if (maxPrefetchQueueSize == 0 && (_isCracking || _workQueue.Count >= 1))
        {
            return Task.CompletedTask;
        }

        if (maxPrefetchQueueSize > 0 && _workQueue.Count >= maxPrefetchQueueSize)
        {
            return Task.CompletedTask;
        }

        _isPrefetching = true;
        var fetchTask = FetchNextChunkAsync(context);

        // ReenterAfter keeps mailbox responsive while asynchronous fetch is in-flight.
        context.ReenterAfter(fetchTask, task =>
        {
            _isPrefetching = false;

            if (task.IsFaulted)
            {
                Console.Error.WriteLine($"Error fetching chunk: {task.Exception?.GetBaseException().Message}");
                Task.Delay(5000).ContinueWith(_ => context.Send(context.Self, new PrefetchWork()));
                return;
            }

            var chunk = task.Result;
            if (chunk is null)
            {
                _noMoreWork = true;
                CheckJobCompletion(context);
                return;
            }

            //Console.WriteLine("Chunk prefetched and added to queue.");
            _workQueue.Enqueue(chunk);
            
            // Keep queue warm and nudge processor in case it is currently idle.
            context.Send(context.Self, new PrefetchWork());
            context.Send(context.Self, new ProcessWork());
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Dequeues and executes one chunk if worker is idle, then continues processing loop.
    /// </summary>
    private Task OnProcessWork(IContext context)
    {
        // Skip when no work is available or worker is already processing.
        if (_currentJob is null || _isCracking || _workQueue.Count == 0)
        {
            return Task.CompletedTask;
        }

        _isCracking = true;
        var chunk = _workQueue.Dequeue();

        // Refill queue slot immediately when one item is consumed.
        context.Send(context.Self, new PrefetchWork());

        _currentWorkCts = new CancellationTokenSource();
        var processTask = ProcessAndSubmitAsync(chunk, context);
        
        context.ReenterAfter(processTask, task =>
        {
            _isCracking = false;
            CheckJobCompletion(context);

            // In polling mode there is no background queue fill; trigger fetch explicitly.
            if (maxPrefetchQueueSize == 0)
            {
                context.Send(context.Self, new PrefetchWork());
            }

            // Continue draining queued chunks.
            context.Send(context.Self, new ProcessWork()); 
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Executes chunk, reports result, and cleans up chunk-local temporary files.
    /// </summary>
    private async Task ProcessAndSubmitAsync(WorkAssignmentEnvelope chunk, IContext context)
    {
        var modeTag = GetWorkUnitModeTag(chunk);
        using var workUnitActivity = WorkUnitActivitySource.StartActivity("wu.execute", ActivityKind.Internal);
        workUnitActivity?.SetTag("job.id", _currentJob?.JobId);
        workUnitActivity?.SetTag("wu.request_id", chunk.RequestId);
        workUnitActivity?.SetTag("wu.mode", modeTag);
        workUnitActivity?.SetTag("agent.id", context.Self.Id);
        workUnitActivity?.SetTag("agent.address", context.Self.Address);

        EmitWorkUnitLifecycleLog("wu_started", chunk, context, modeTag);
        WorkUnitStartedCounter.Add(1, new KeyValuePair<string, object?>("mode", modeTag));
        WorkUnitActiveCounter.Add(1, new KeyValuePair<string, object?>("mode", modeTag));
        await SendHeartbeatAsync(context);

        var totalStopwatch = Stopwatch.StartNew();
        var computeStopwatch = Stopwatch.StartNew();
        using var computeActivity = WorkUnitActivitySource.StartActivity("wu.compute", ActivityKind.Internal);
        computeActivity?.SetTag("wu.request_id", chunk.RequestId);
        computeActivity?.SetTag("wu.mode", modeTag);
        List<RecoveredPassword> recovered = [];
        bool isFaulted = false;
        bool submitFailed = false;
        try
        {
            recovered = await ProcessChunkAsync(chunk);
        }
        catch (Exception ex)
        {
            isFaulted = true;
            computeActivity?.SetStatus(ActivityStatusCode.Error, ex.GetBaseException().Message);
            workUnitActivity?.SetStatus(ActivityStatusCode.Error, ex.GetBaseException().Message);
            Console.Error.WriteLine($"Hashcat task failed: {ex.GetBaseException().Message}");
        }
        finally
        {
            computeStopwatch.Stop();
            computeActivity?.SetTag("wu.compute.duration_seconds", computeStopwatch.Elapsed.TotalSeconds);
            EmitWorkUnitLifecycleLog("wu_compute_finished", chunk, context, modeTag, new()
            {
                ["compute_duration_seconds"] = computeStopwatch.Elapsed.TotalSeconds,
                ["faulted"] = isFaulted
            });
            WorkUnitComputeDurationSeconds.Record(
                computeStopwatch.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("mode", modeTag));
            _workAssignmentMaterializer.CleanupAssignmentFiles(chunk);
        }

        var workResult = new WorkResult
        {
            AgentId = new AgentId { Address = context.Self.Address, Id = context.Self.Id },
            Success = !isFaulted && recovered.Count > 0,
            RecoveredPasswords = { recovered },
            RequestId = chunk.RequestId,
        };

        var submissionStopwatch = Stopwatch.StartNew();
        using var submissionActivity = WorkUnitActivitySource.StartActivity("wu.submit", ActivityKind.Client);
        submissionActivity?.SetTag("wu.request_id", chunk.RequestId);
        submissionActivity?.SetTag("wu.mode", modeTag);
        try
        {
            var coordinator = cluster.GetJobCoordinatorGrain(_currentJob!.JobId);
            await coordinator.WorkResultSubmission(workResult, CancellationToken.None);

            if (recovered.Count > 0)
            {
                try
                {
                    await _hashFileStore.UpdateHashFileAsync(_currentJob!.JobId, _currentJob!.Hashes, recovered);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error updating hash file for job {_currentJob!.JobId}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            submitFailed = true;
            submissionActivity?.SetStatus(ActivityStatusCode.Error, ex.GetBaseException().Message);
            workUnitActivity?.SetStatus(ActivityStatusCode.Error, ex.GetBaseException().Message);
            Console.WriteLine($"Failed to submit work result: {ex.Message}");
        }
        finally
        {
            submissionStopwatch.Stop();
            totalStopwatch.Stop();
            submissionActivity?.SetTag("wu.submission.duration_seconds", submissionStopwatch.Elapsed.TotalSeconds);
            workUnitActivity?.SetTag("wu.total.duration_seconds", totalStopwatch.Elapsed.TotalSeconds);

            WorkUnitSubmissionDurationSeconds.Record(
                submissionStopwatch.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("mode", modeTag));
            WorkUnitTotalDurationSeconds.Record(
                totalStopwatch.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("mode", modeTag));

            var outcome = isFaulted ? "faulted" : submitFailed ? "submit_failed" : "completed";
            WorkUnitResultCounter.Add(
                1,
                new KeyValuePair<string, object?>("mode", modeTag),
                new KeyValuePair<string, object?>("outcome", outcome));
            WorkUnitActiveCounter.Add(-1, new KeyValuePair<string, object?>("mode", modeTag));

            EmitWorkUnitLifecycleLog("wu_finished", chunk, context, modeTag, new()
            {
                ["outcome"] = outcome,
                ["compute_duration_seconds"] = computeStopwatch.Elapsed.TotalSeconds,
                ["submission_duration_seconds"] = submissionStopwatch.Elapsed.TotalSeconds,
                ["total_duration_seconds"] = totalStopwatch.Elapsed.TotalSeconds,
                ["recovered_count"] = recovered.Count
            });

            await SendHeartbeatAsync(context);
        }
    }

    private static string GetWorkUnitModeTag(WorkAssignmentEnvelope chunk)
    {
        return chunk.PayloadCase switch
        {
            WorkAssignmentEnvelope.PayloadOneofCase.MaskAssignment => "mask",
            WorkAssignmentEnvelope.PayloadOneofCase.DictionaryAssignment => "dictionary",
            WorkAssignmentEnvelope.PayloadOneofCase.AssociationAssignment => "association",
            WorkAssignmentEnvelope.PayloadOneofCase.CombinatorAssignment => "combinator",
            _ => "unknown"
        };
    }

    private void EmitWorkUnitLifecycleLog(
        string eventName,
        WorkAssignmentEnvelope chunk,
        IContext context,
        string modeTag,
        Dictionary<string, object?>? extras = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["event"] = eventName,
            ["timestamp_utc"] = DateTime.UtcNow,
            ["job_id"] = _currentJob?.JobId,
            ["request_id"] = chunk.RequestId,
            ["mode"] = modeTag,
            ["agent_id"] = context.Self.Id,
            ["agent_address"] = context.Self.Address,
            ["chunk"] = DescribeChunk(chunk)
        };

        if (extras is not null)
        {
            foreach (var (key, value) in extras)
            {
                payload[key] = value;
            }
        }

        Console.WriteLine($"WU_LIFECYCLE {System.Text.Json.JsonSerializer.Serialize(payload)}");
    }

    private static object DescribeChunk(WorkAssignmentEnvelope chunk)
    {
        return chunk.PayloadCase switch
        {
            WorkAssignmentEnvelope.PayloadOneofCase.MaskAssignment => new
            {
                type = "mask",
                mask = chunk.MaskAssignment.Mask,
                keyspace_start = chunk.MaskAssignment.KeyspaceStart,
                keyspace_length = chunk.MaskAssignment.KeyspaceLength
            },
            WorkAssignmentEnvelope.PayloadOneofCase.DictionaryAssignment => new
            {
                type = "dictionary",
                dictionary_chunk_url = chunk.DictionaryAssignment.WordlistUrl
            },
            WorkAssignmentEnvelope.PayloadOneofCase.CombinatorAssignment => new
            {
                type = "combinator",
                left_dictionary_chunk_url = chunk.CombinatorAssignment.LeftWordlistUrl,
                right_dictionary_chunk_url = chunk.CombinatorAssignment.RightWordlistUrl
            },
            WorkAssignmentEnvelope.PayloadOneofCase.HybridAssignment => new
            {
                type = "hybrid",
                mask = chunk.HybridAssignment.Mask,
                keyspace_start = chunk.HybridAssignment.KeyspaceStart,
                keyspace_length = chunk.HybridAssignment.KeyspaceLength,
                dictionary_chunk_url = chunk.HybridAssignment.WordlistUrl
            },
            _ => new { type = "unknown" }
        };
    }

    /// <summary>
    /// Requests next assignment from coordinator and materializes any required local files.
    /// </summary>
    private async Task<WorkAssignmentEnvelope?> FetchNextChunkAsync(IContext context)
    {
        var hashrate = await hashcatWrapper.GetBenchmarkHashrateAsync(_currentJob!.HashType);
        var workRequest = new WorkRequest
        {
            AgentId = new AgentId { Address = context.Self.Address, Id = context.Self.Id },
            JobId = _currentJob!.JobId,
            CurrentHashrate = hashrate,
        };

        var coordinator = cluster.GetJobCoordinatorGrain(_currentJob!.JobId);
        var envelope = await coordinator.WorkRequest(workRequest, CancellationToken.None);
        return await _workAssignmentMaterializer.MaterializeAsync(envelope);
    }

    /// <summary>
    /// Runs the assignment through the matching Hashcat attack mode implementation.
    /// </summary>
    private async Task<List<RecoveredPassword>> ProcessChunkAsync(WorkAssignmentEnvelope chunk)
    {
        var hashFilePath = await _hashFileStore.GetOrCreateHashFileAsync(_currentJob!.JobId, _currentJob!.Hashes);

        switch (chunk.PayloadCase)
        {
            case WorkAssignmentEnvelope.PayloadOneofCase.MaskAssignment:
                Console.WriteLine($"Cracking mask chunk: {chunk.RequestId} ({chunk.MaskAssignment.KeyspaceStart} - {chunk.MaskAssignment.KeyspaceStart + chunk.MaskAssignment.KeyspaceLength})");
                return await hashcatWrapper.RunHashcatMaskAttackAsync(chunk.MaskAssignment, _currentJob.HashType, hashFilePath, _currentWorkCts!.Token);

            case WorkAssignmentEnvelope.PayloadOneofCase.DictionaryAssignment:
                Console.WriteLine($"Cracking dictionary chunk: {chunk.RequestId}");
                return await hashcatWrapper.RunHashcatDictionaryAttackAsync(
                    chunk.DictionaryAssignment,
                    _currentJob.HashType,
                    hashFilePath,
                    _ruleFileStore.GetRuleFilePath(_currentJob.JobId),
                    _currentWorkCts!.Token);

            case WorkAssignmentEnvelope.PayloadOneofCase.CombinatorAssignment:
                Console.WriteLine($"Cracking combinator chunk: {chunk.RequestId}");
                return await hashcatWrapper.RunHashcatCombinatorAttackAsync(
                    chunk.CombinatorAssignment,
                    _currentJob.HashType,
                    hashFilePath,
                    _ruleFileStore.GetRuleFilePath(_currentJob.JobId),
                    _currentWorkCts!.Token);

            case WorkAssignmentEnvelope.PayloadOneofCase.AssociationAssignment:
                Console.WriteLine($"Cracking association chunk: {chunk.RequestId}");
                return await hashcatWrapper.RunHashcatAssociationAttackAsync(
                    chunk.AssociationAssignment,
                    _currentJob.HashType,
                    hashFilePath,
                    _ruleFileStore.GetRuleFilePath(_currentJob.JobId),
                    _currentWorkCts!.Token);

            case WorkAssignmentEnvelope.PayloadOneofCase.HybridAssignment:
                Console.WriteLine($"Cracking hybrid chunk: {chunk.RequestId} ({chunk.HybridAssignment.KeyspaceStart} - {chunk.HybridAssignment.KeyspaceStart + chunk.HybridAssignment.KeyspaceLength})");
                return chunk.HybridAssignment.AttackMode switch
                {
                    (int)AttackMode.Hybrid_WordlistMask => await hashcatWrapper.RunHashcatHybridWordlistMaskAttackAsync(
                        chunk.HybridAssignment,
                        _currentJob.HashType,
                        hashFilePath,
                        _ruleFileStore.GetRuleFilePath(_currentJob.JobId),
                        _currentWorkCts!.Token),

                    (int)AttackMode.Hybrid_MaskWordlist => await hashcatWrapper.RunHashcatHybridMaskWordlistAttackAsync(
                        chunk.HybridAssignment,
                        _currentJob.HashType,
                        hashFilePath,
                        _ruleFileStore.GetRuleFilePath(_currentJob.JobId),
                        _currentWorkCts!.Token),

                    _ => []
                };

            default:
                return [];
        }
    }

    /// <summary>
    /// Cleans up job-level temporary files and cached combinator left chunks.
    /// </summary>
    private void CleanupJobData(string? jobId)
    {
        _hashFileStore.CleanupJobData(jobId);
        _ruleFileStore.CleanupJobData(jobId);
        _workAssignmentMaterializer.CleanupJobCache();
    }

    /// <summary>
    /// Returns to discovery when there is no queued, running, or available work.
    /// </summary>
    private void CheckJobCompletion(IContext context)
    {
        if (_noMoreWork && _workQueue.Count == 0 && !_isCracking)
        {
            Console.WriteLine("No more chunks for this job. Cleaning up and returning to discovery pool.");
            CleanupJobData(_currentJob?.JobId);
            _currentJob = null;
            // Add a delay to prevent tight polling if the job is waiting for straggler timeouts.
            Task.Delay(3000).ContinueWith(_ => context.Send(context.Self, new StartLoop()));
        }
    }

    /// <summary>
    /// Sends periodic telemetry heartbeat to the job coordinator while a job is active.
    /// </summary>
    private async Task SendHeartbeatAsync(IContext context)
    {
        if (_currentJob is not null)
        {
            try
            {
                var coordinator = cluster.GetJobCoordinatorGrain(_currentJob.JobId);

                var telemetry = new AgentTelemetry
                {
                    AgentId = new AgentId { Address = context.Self.Address, Id = context.Self.Id },
                    CurrentHashrate = hashcatWrapper.CurrentHashrate,
                    Temperature = hashcatWrapper.Temperature,
                    FanSpeed = hashcatWrapper.FanSpeed,
                    GpuUtilization = hashcatWrapper.GpuUtilization,
                    RejectRate = hashcatWrapper.RejectRate,
                };
                telemetry.GpuDevices.AddRange(hashcatWrapper.GpuDevices);

                await coordinator.Heartbeat(telemetry, CancellationToken.None);
            }
            catch { /* Ignore heartbeat network delivery failures */ }
        }
    }

}