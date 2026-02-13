using DPCS.Agent.Actors;
using DPCS.Agent.Hashcat;

namespace DPCS.Agent.Services;

public class AgentService(ActorSystem actorSystem, HashcatWrapper hashcatWrapper) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Spawn the Worker Actor
        // We use Dependency Injection to pass the Cluster and AgentId
        var props = Props.FromProducer(() => new WorkerActor(actorSystem.Cluster(), hashcatWrapper));
        var pid = actorSystem.Root.Spawn(props);
        
        Console.WriteLine($"Agent Service {pid} started.");
        // Keep the service alive
        try 
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            await actorSystem.Root.StopAsync(pid);
        }
    }
}