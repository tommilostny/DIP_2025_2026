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

            switch (_currentJob.ModeId)
            {
                case (int)AttackMode.Mask:
                    var maskChunk = await coordinator.MaskWorkRequest(workRequest, CancellationToken.None);
                    if (maskChunk is null)
                    {
                        Console.WriteLine("No mask chunk received, retrying...");
                        context.Send(context.Self, new ChunkProcessed());
                        return;
                    }
                    Console.WriteLine($"Received mask chunk: {maskChunk.Meta.RequestId}");
                    context.ReenterAfter(
                        RunHashcatMaskAttack(maskChunk, _currentWorkCts.Token), 
                        _ => {
                            // When finished, loop back
                            context.Send(context.Self, new ChunkProcessed());
                            return Task.CompletedTask;
                        }
                    );
                    break;
            
                case (int)AttackMode.Dictionary:
                    var dictChunk = await coordinator.DictionaryWorkRequest(workRequest, CancellationToken.None);
                    if (dictChunk is null)
                    {
                        Console.WriteLine("No dictionary chunk received, retrying...");
                        context.Send(context.Self, new ChunkProcessed());
                        return;
                    }
                    Console.WriteLine($"Received dictionary chunk: {dictChunk.Meta.RequestId}");
                    context.ReenterAfter(
                        RunHashcatDictionaryAttack(dictChunk, _currentWorkCts.Token), 
                        _ => {
                            // When finished, loop back
                            context.Send(context.Self, new ChunkProcessed());
                            return Task.CompletedTask;
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