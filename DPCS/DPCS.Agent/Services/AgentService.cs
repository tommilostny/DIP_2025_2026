using DPCS.Agent.Actors;
using DPCS.Agent.Hashcat;

namespace DPCS.Agent.Services;

public sealed class AgentService(ActorSystem actorSystem, HashcatWrapper hashcatWrapper) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Spawn the Worker Actor
        // We use Dependency Injection to pass the Cluster and AgentId
        var props = Props.FromProducer(() => new WorkerActor(actorSystem.Cluster(), hashcatWrapper));
        var pid = actorSystem.Root.Spawn(props);
        
        // Keep the service alive
        try 
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("AgentService is stopping...");
            await actorSystem.Root.StopAsync(pid);
        }
    }
}