namespace DPCS.Agent.Actors;

public sealed class WorkerActor(Cluster cluster, IHashcatWrapper hashcatWrapper, int maxPrefetchQueueSize = 1, TimeSpan? heartbeatInterval = null) : IActor
{
    private CancellationTokenSource? _currentWorkCts;
    private JobAssignment? _currentJob;
    private readonly Dictionary<string, string> _jobHashFilePaths = [];

    private readonly Queue<object> _workQueue = new();
    private bool _isPrefetching;
    private bool _isCracking;
    private bool _noMoreWork;
    private static readonly HttpClient _httpClient = new();
    private Timer? _heartbeatTimer;
    private readonly TimeSpan _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(15);

    public Task ReceiveAsync(IContext context) => context.Message switch
    {
        Started => OnStarted(context),
        Stopped => OnStopped(),
        StartLoop => FindJob(context),
        StopWork => OnStopWork(context),
        PrefetchWork => OnPrefetchWork(context),
        ProcessWork => OnProcessWork(context),
        HeartbeatTick => OnHeartbeatTick(context),
        _ => Task.CompletedTask
    };

    private Task OnStarted(IContext context)
    {
        Console.WriteLine($"WorkerActor {context.Self} started.");
        _heartbeatTimer = new Timer(_ => context.Send(context.Self, new HeartbeatTick()), null, _heartbeatInterval, _heartbeatInterval);
        context.Send(context.Self, new StartLoop());
        return Task.CompletedTask;
    }

    private Task OnStopped()
    {
        _heartbeatTimer?.Dispose();
        return Task.CompletedTask;
    }

    private Task OnStopWork(IContext context)
    {
        Console.WriteLine("WorkerActor received StopWork signal.");
        _currentWorkCts?.Cancel();
        
        // Cleanup any prefetched wordlists hanging out in the queue
        foreach (var chunk in _workQueue)
        {
            if (chunk is DictionaryWorkAssignment dChunk && File.Exists(dChunk.DictionaryChunkUrl))
            {
                try { File.Delete(dChunk.DictionaryChunkUrl); } catch { }
            }
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

    private async Task FindJob(IContext context)
    {
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
            _noMoreWork = false;
            _isPrefetching = false;
            _isCracking = false;
            _workQueue.Clear();
            
            // Kick off the decoupled prefetch and process loops
            //Console.WriteLine("Before sending PrefetchWork.");
            context.Send(context.Self, new PrefetchWork());
            //Console.WriteLine("Before sending ProcessWork.");
            //context.Send(context.Self, new ProcessWork());
            //Console.WriteLine("After sending ProcessWork.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Discovery error: {ex.Message}");
            await Task.Delay(5000);
            context.Send(context.Self, new StartLoop());
        }
    }

    private Task OnPrefetchWork(IContext context)
    {
        if (_currentJob is null || _isPrefetching || _noMoreWork)
        {
            //Console.WriteLine("Prefetch work skipped...");
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

        //Console.WriteLine("Prefetch work starting on next chunk...");

        _isPrefetching = true;
        var fetchTask = FetchNextChunkAsync(context);

        // ReenterAfter executes the fetch asynchronously without blocking the actor mailbox
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
            
            // Re-trigger prefetch in case we are still below the MaxPrefetchQueueSize
            //Console.WriteLine("Triggering another PrefetchWork just in case...");
            context.Send(context.Self, new PrefetchWork());
            // Alert the processor that a chunk is ready just in case it was idle
            //Console.WriteLine("Triggering ProcessWork in case processor is idle...");
            context.Send(context.Self, new ProcessWork());
        });

        return Task.CompletedTask;
    }

    private Task OnProcessWork(IContext context)
    {
        //Console.WriteLine("WorkerActor received ProcessWork signal.");

        // Don't process if we are currently cracking or if there's nothing in the queue
        if (_currentJob is null || _isCracking || _workQueue.Count == 0)
        {
            //Console.WriteLine("Process work skipped...");
            //Console.WriteLine($"CurrentJob: {_currentJob?.JobId}, IsCracking: {_isCracking}, QueueCount: {_workQueue.Count}");
            //Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(_currentJob));
            return Task.CompletedTask;
        }

        //Console.WriteLine("Process work starting on next chunk...");

        _isCracking = true;
        var chunk = _workQueue.Dequeue();

        //Console.WriteLine("Process work, sending PrefetchWork to ensure queue is topped off...");
        // Now that a spot has opened in the queue, trigger a prefetch
        context.Send(context.Self, new PrefetchWork());

        _currentWorkCts = new CancellationTokenSource();

        //Console.WriteLine("Before calling ProcessAndSubmitAsync...");
        var processTask = ProcessAndSubmitAsync(chunk, context);
        
        context.ReenterAfter(processTask, task =>
        {
            _isCracking = false;
            CheckJobCompletion(context);

            // If we are in strict polling mode, we must trigger the next fetch now that the GPU is idle
            if (maxPrefetchQueueSize == 0)
            {
                context.Send(context.Self, new PrefetchWork());
            }

            // Continue processing remaining queued chunks
            context.Send(context.Self, new ProcessWork()); 
        });

        return Task.CompletedTask;
    }

    private async Task ProcessAndSubmitAsync(object chunk, IContext context)
    {
        //Console.WriteLine("Starting ProcessAndSubmitAsync for chunk...");

        List<RecoveredPassword> recovered = [];
        bool isFaulted = false;
        try
        {
            //Console.WriteLine("Before calling ProcessChunkAsync...");
            recovered = await ProcessChunkAsync(chunk);
            //Console.WriteLine("After calling ProcessChunkAsync...");
        }
        catch (Exception ex)
        {
            isFaulted = true;
            Console.Error.WriteLine($"Hashcat task failed: {ex.GetBaseException().Message}");
        }
        finally
        {
            // Ensure the temporary dictionary file is deleted
            if (chunk is DictionaryWorkAssignment dChunk && File.Exists(dChunk.DictionaryChunkUrl))
            {
                try { File.Delete(dChunk.DictionaryChunkUrl); } catch { }
            }
        }

        var workResult = new WorkResult
        {
            AgentId = new AgentId { Address = context.Self.Address, Id = context.Self.Id },
            Success = !isFaulted && recovered.Count > 0,
            RecoveredPasswords = { recovered },
            RequestId = chunk switch {
                MaskWorkAssignment m => m.RequestId,
                DictionaryWorkAssignment d => d.RequestId,
                _ => ""
            },
        };

        try
        {
            var coordinator = cluster.GetJobCoordinatorGrain(_currentJob!.JobId);
            await coordinator.WorkResultSubmission(workResult, CancellationToken.None);

            if (recovered.Count > 0)
            {
                await UpdateHashFileAsync(_currentJob!.JobId, recovered);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to submit work result: {ex.Message}");
        }
    }

    private async Task<object?> FetchNextChunkAsync(IContext context)
    {
        //Console.WriteLine("Starting FetchNextChunkAsync...");

        var hashrate = await hashcatWrapper.GetBenchmarkHashrateAsync(_currentJob!.HashType);
        var coordinator = cluster.GetJobCoordinatorGrain(_currentJob!.JobId);
        var workRequest = new WorkRequest
        {
            AgentId = new AgentId { Address = context.Self.Address, Id = context.Self.Id },
            JobId = _currentJob!.JobId,
            CurrentHashrate = hashrate,
        };

        //Console.WriteLine("Sending work request to coordinator...");

        if (_currentJob!.ModeId == (int)AttackMode.Mask)
        {
            var chunk = await coordinator.MaskWorkRequest(workRequest, CancellationToken.None);
            return string.IsNullOrEmpty(chunk?.RequestId) ? null : chunk;
        }
        else if (_currentJob!.ModeId == (int)AttackMode.Dictionary)
        {
            var chunk = await coordinator.DictionaryWorkRequest(workRequest, CancellationToken.None);
            if (string.IsNullOrEmpty(chunk?.RequestId))
                return null;

            chunk.DictionaryChunkUrl = await DownloadDictionaryChunkAsync(chunk);
            return chunk;
        }
        
        return null;
    }

    private static async Task<string> DownloadDictionaryChunkAsync(DictionaryWorkAssignment chunk)
    {
        // Parse the start and end bytes from the dynamically generated coordinator URL
        var uri = new Uri(chunk.DictionaryChunkUrl);
        var queryParams = uri.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('='))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

        long startByte = long.Parse(queryParams["startByte"]);
        long endByte = long.Parse(queryParams["endByte"]);
        var cleanUrl = uri.GetLeftPart(UriPartial.Path); // Remove the query string for the actual HTTP request

        // Configure the native HTTP byte Range request
        using var request = new HttpRequestMessage(HttpMethod.Get, cleanUrl);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startByte, endByte == -1 ? null : endByte);

        Console.WriteLine($"Downloading Wordlist Chunk: {chunk.RequestId} (Bytes: {startByte}-{(endByte == -1 ? "EOF" : endByte)})");
        
        // Stream the perfectly sliced exact bytes without loading into RAM
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var wordlistPath = Path.Combine(Path.GetTempPath(), $"dpcs_{chunk.RequestId}.txt");
        using var fileStream = new FileStream(wordlistPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fileStream);

        return wordlistPath;
    }

    private async Task<List<RecoveredPassword>> ProcessChunkAsync(object chunk)
    {
        var hashFilePath = await GetOrCreateHashFileAsync(_currentJob!.JobId, _currentJob!.Hashes);

        if (chunk is MaskWorkAssignment maskChunk)
        {
            Console.WriteLine($"Cracking mask chunk: {maskChunk.RequestId} ({maskChunk.KeyspaceStart} - {maskChunk.KeyspaceStart + maskChunk.KeyspaceLength})");
            return await hashcatWrapper.RunHashcatMaskAttackAsync(maskChunk, _currentJob!.HashType, hashFilePath, _currentWorkCts!.Token);
        }
        else if (chunk is DictionaryWorkAssignment dictChunk)
        {
            Console.WriteLine($"Cracking dictionary chunk: {dictChunk.RequestId}");
            return await hashcatWrapper.RunHashcatDictionaryAttackAsync(dictChunk, _currentJob!.HashType, hashFilePath, _currentWorkCts!.Token);
        }

        return [];
    }

    private async Task<string> GetOrCreateHashFileAsync(string jobId, IEnumerable<string> hashes)
    {
        if (_jobHashFilePaths.TryGetValue(jobId, out var path))
        {
            return path;
        }

        // Using a Guid ensures the file path is unique per actor instance, preventing locking issues in local simulation.
        var newPath = Path.Combine(Path.GetTempPath(), $"dpcs_{jobId}_{Guid.NewGuid():N}.hashes");
        await File.WriteAllLinesAsync(newPath, hashes);
        _jobHashFilePaths[jobId] = newPath;
        Console.WriteLine($"Created hash file for job {jobId} at {newPath}");

        return newPath;
    }

    private void CleanupJobData(string? jobId)
    {
        if (jobId is null || !_jobHashFilePaths.Remove(jobId, out var path))
        {
            return;
        }
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Console.WriteLine($"Cleaned up hash file: {path}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error cleaning up hash file {path}: {ex.Message}");
        }
    }

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

    private async Task UpdateHashFileAsync(string jobId, IEnumerable<RecoveredPassword> recoveredPasswords)
    {
        if (!_jobHashFilePaths.TryGetValue(jobId, out var path))
        {
            Console.WriteLine($"No hash file found for job {jobId} when trying to update.");
            return;
        }
        try
        {
            foreach (var pwd in recoveredPasswords)
            {
                _currentJob!.Hashes.Remove(pwd.Hash);
            }
            await File.WriteAllLinesAsync(path, _currentJob!.Hashes);
            Console.WriteLine($"Updated hash file for job {jobId} with {_currentJob!.Hashes.Count} remaining hashes.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating hash file {path}: {ex.Message}");
        }
    }

    private async Task OnHeartbeatTick(IContext context)
    {
        //Console.WriteLine("Heartbeat tick...");
        if (_currentJob is not null)
        {
            try
            {
                //Console.WriteLine($"Sending heartbeat to coordinator - Temp: {hashcatWrapper.Temperature}°C, Fan: {hashcatWrapper.FanSpeed}%, Utilization: {hashcatWrapper.GpuUtilization}%, Hashrate: {hashcatWrapper.CurrentHashrate} H/s");
                var coordinator = cluster.GetJobCoordinatorGrain(_currentJob.JobId);
                
                await coordinator.Heartbeat(new AgentTelemetry 
                {
                    AgentId = new AgentId { Address = context.Self.Address, Id = context.Self.Id },
                    CurrentHashrate = hashcatWrapper.CurrentHashrate,
                    Temperature = hashcatWrapper.Temperature,
                    FanSpeed = hashcatWrapper.FanSpeed,
                    GpuUtilization = hashcatWrapper.GpuUtilization,
                    RejectRate = hashcatWrapper.RejectRate,
                }, CancellationToken.None);
            }
            catch { /* Ignore heartbeat network delivery failures */ }
        }
    }
}