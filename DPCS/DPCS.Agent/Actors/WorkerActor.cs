using DPCS.Agent.Hashcat;

namespace DPCS.Agent.Actors;

public sealed class WorkerActor(Cluster cluster, HashcatWrapper hashcatWrapper) : IActor
{
    private CancellationTokenSource? _currentWorkCts;
    private JobAssignment? _currentJob;

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

    private Task OnStarted(IContext context)
    {
        Console.WriteLine($"WorkerActor {context.Self} started.");
        context.Send(context.Self, new StartLoop());
        return Task.CompletedTask;
    }

    private Task OnStopWork()
    {
        Console.WriteLine("WorkerActor received StopWork signal.");
        _currentWorkCts?.Cancel();
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

            if (assignment is null or { ModeId: (long)AttackMode.Invalid })
            {
                // No job, retry later
                await Task.Delay(5000);
                context.Send(context.Self, new StartLoop());
                return;
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
            var coordinator = cluster.GetJobCoordinatorGrain(_currentJob.JobId);

            // Could be MaskWorkAssignment or DictionaryWorkAssignment depending on job type
            object? chunk = null;
            var workRequest = new WorkRequest
            {
                AgentId = new AgentId
                {
                    Address = context.Self.Address,
                    Id = context.Self.Id
                },
                JobId = _currentJob.JobId,
                CurrentHashrate = 123, // Placeholder, you can track actual hashrate in a real implementation
            };

            switch ((AttackMode)_currentJob.ModeId)
            {
                case AttackMode.Mask:
                    chunk = await coordinator.MaskWorkRequest(workRequest, CancellationToken.None);
                    break;
                case AttackMode.Dictionary:
                    chunk = await coordinator.DictionaryWorkRequest(workRequest, CancellationToken.None);
                    break;
                default:
                    Console.WriteLine($"Unknown attack mode: {_currentJob.ModeId}");
                    _currentJob = null;
                    context.Send(context.Self, new StartLoop());
                    return;
            }

            // Process the chunk WITHOUT blocking the actor
            _currentWorkCts = new CancellationTokenSource();
            // ReenterAfter allows the actor to process other messages (like StopWork) 
            // while this task runs. When the task finishes, the continuation is executed.

            switch (chunk)
            {
                case MaskWorkAssignment maskChunk:
                    Console.WriteLine($"Received mask chunk: {maskChunk.Meta.RequestId}");
                    context.ReenterAfter(
                        RunHashcatMaskAttack(maskChunk, _currentWorkCts.Token), 
                        _ => 
                        {
                            // When finished, loop back
                            context.Send(context.Self, new ChunkProcessed());
                            return Task.CompletedTask;
                        }
                    );
                    break;
                
                case DictionaryWorkAssignment dictChunk:
                    Console.WriteLine($"Received dictionary chunk: {dictChunk.Meta.RequestId}");
                    context.ReenterAfter(
                        RunHashcatDictionaryAttack(dictChunk, _currentWorkCts.Token), 
                        _ => 
                        {
                            // When finished, loop back
                            context.Send(context.Self, new ChunkProcessed());
                            return Task.CompletedTask;
                        }
                    );
                    break;
                
                default:
                    // Check if job is done or cancelled (assuming empty assignment means done)
                    Console.WriteLine("Job finished or no more chunks.");
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

    private async Task RunHashcatMaskAttack(MaskWorkAssignment chunk, CancellationToken ct)
    {
        // Simulate arguments
        await hashcatWrapper.StartHashcatProcessAsync("--benchmark", ct);
    }

    private async Task RunHashcatDictionaryAttack(DictionaryWorkAssignment chunk, CancellationToken ct)
    {
        // Simulate arguments
        await hashcatWrapper.StartHashcatProcessAsync("--benchmark", ct);
    }
}