using System.Text;

namespace DPCS.Agent.Actors;

public sealed class WorkerActor(Cluster cluster, HashcatWrapper hashcatWrapper) : IActor
{
    private CancellationTokenSource? _currentWorkCts;
    private JobAssignment? _currentJob;
    private readonly Dictionary<string, string> _jobHashFilePaths = new();

    public Task ReceiveAsync(IContext context)
    {
        return context.Message switch
        {
            Started => OnStarted(context),
            StartLoop => FindJob(context),
            StopWork => OnStopWork(),
            ChunkProcessed => RequestNextChunk(context),
            _ => Task.CompletedTask
        };
    }

    private static Task OnStarted(IContext context)
    {
        Console.WriteLine($"WorkerActor {context.Self} started.");
        context.Send(context.Self, new StartLoop());
        return Task.CompletedTask;
    }

    private Task OnStopWork()
    {
        Console.WriteLine("WorkerActor received StopWork signal.");
        _currentWorkCts?.Cancel();
        if (_currentJob != null)
        {
            CleanupJobData(_currentJob.JobId);
            _currentJob = null;
        }
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
            if (_currentJob != null && _currentJob.JobId != assignment.JobId)
            {
                CleanupJobData(_currentJob.JobId);
            }

            Console.WriteLine($"Job found: {assignment.JobId}");
            _currentJob = assignment;
            
            // Start processing chunks
            context.Send(context.Self, new ChunkProcessed());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Discovery error: {ex.Message}");
            await Task.Delay(5000);
            context.Send(context.Self, new StartLoop());
        }
    }

    private async Task RequestNextChunk(IContext context)
    {
        if (_currentJob == null) return;
        try
        {
            // Run or retrieve cached benchmark.
            var hashrate = await hashcatWrapper.GetBenchmarkHashrateAsync(_currentJob.HashType);

            // Get the coordinator grain from the cluster and request the next chunk of work.
            var coordinator = cluster.GetJobCoordinatorGrain(_currentJob.JobId);
            var workRequest = new WorkRequest
            {
                AgentId = new AgentId
                {
                    Address = context.Self.Address,
                    Id = context.Self.Id
                },
                JobId = _currentJob.JobId,
                CurrentHashrate = hashrate,
            };

            // Process the chunk WITHOUT blocking the actor
            _currentWorkCts = new CancellationTokenSource();
            // ReenterAfter allows the actor to process other messages (like StopWork) 
            // while this task runs. When the task finishes, the continuation is executed.

            // Get or create the hash file for the current job, and get its path
            var hashFilePath = await GetOrCreateHashFileAsync(_currentJob.JobId, _currentJob.Hashes);

            switch (_currentJob.ModeId)
            {
                case (int)AttackMode.Mask:
                    var maskChunk = await coordinator.MaskWorkRequest(workRequest, CancellationToken.None);
                    if (string.IsNullOrEmpty(maskChunk?.RequestId))
                    {
                        Console.WriteLine("No more mask chunks for this job. Cleaning up and finding a new job.");
                        CleanupJobData(_currentJob.JobId);
                        _currentJob = null;
                        context.Send(context.Self, new StartLoop());
                        return;
                    }
                    Console.WriteLine($"Received mask chunk: {maskChunk.RequestId}");

                    context.ReenterAfter(
                        hashcatWrapper.RunHashcatMaskAttackAsync(maskChunk, _currentJob.HashType, hashFilePath, _currentWorkCts.Token), 
                        async task => {
                            // Report completion to the coordinator so it can track progress
                            var workResult = new WorkResult
                            {
                                JobId = _currentJob.JobId,
                                Success = !task.IsFaulted && task.Result.Count > 0,
                                RecoveredPasswords = { task.IsFaulted ? [] : task.Result }
                            };
                            await coordinator.WorkResultSubmission(workResult, CancellationToken.None);
                            if (task.IsFaulted)
                            {
                                Console.Error.WriteLine($"Hashcat task failed: {task.Exception?.GetBaseException().Message}");
                            }
                            // When finished (successfully or not), request the next chunk
                            context.Send(context.Self, new ChunkProcessed());
                        }
                    );
                    break;
            
                case (int)AttackMode.Dictionary:
                    var dictChunk = await coordinator.DictionaryWorkRequest(workRequest, CancellationToken.None);
                    if (string.IsNullOrEmpty(dictChunk?.RequestId))
                    {
                        Console.WriteLine("No more dictionary chunks for this job. Cleaning up and finding a new job.");
                        CleanupJobData(_currentJob.JobId);
                        _currentJob = null;
                        context.Send(context.Self, new StartLoop());
                        return;
                    }
                    Console.WriteLine($"Received dictionary chunk: {dictChunk.RequestId}");

                    context.ReenterAfter(
                        hashcatWrapper.RunHashcatDictionaryAttackAsync(dictChunk, _currentJob.HashType, hashFilePath, _currentWorkCts.Token),
                        async task => {
                            // Report completion to the coordinator so it can track progress
                            var workResult = new WorkResult
                            {
                                JobId = _currentJob.JobId,
                                Success = !task.IsFaulted && task.Result.Count > 0,
                                RecoveredPasswords = { task.IsFaulted ? [] : task.Result }
                            };
                            await coordinator.WorkResultSubmission(workResult, CancellationToken.None);
                            if (task.IsFaulted)
                            {
                                Console.Error.WriteLine($"Hashcat task failed: {task.Exception?.GetBaseException().Message}");
                            }
                            // When finished (successfully or not), request the next chunk
                            context.Send(context.Self, new ChunkProcessed());
                        }
                    );
                    break;
            
                default:
                    Console.WriteLine($"Unknown attack mode: {_currentJob.ModeId}");
                    _currentJob = null;
                    context.Send(context.Self, new StartLoop());
                    return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Chunk processing error: {ex.Message}");
            _currentJob = null;
            context.Send(context.Self, new StartLoop());
        }
    }

    private async Task<string> GetOrCreateHashFileAsync(string jobId, IEnumerable<string> hashes)
    {
        if (_jobHashFilePaths.TryGetValue(jobId, out var path))
        {
            return path;
        }

        var newPath = Path.Combine(Path.GetTempPath(), $"dpcs_{jobId}.hashes");
        await File.WriteAllLinesAsync(newPath, hashes);
        _jobHashFilePaths[jobId] = newPath;
        Console.WriteLine($"Created hash file for job {jobId} at {newPath}");

        return newPath;
    }

    private void CleanupJobData(string? jobId)
    {
        if (jobId == null || !_jobHashFilePaths.Remove(jobId, out var path))
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
}