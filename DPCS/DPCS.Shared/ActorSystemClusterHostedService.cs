using Microsoft.Extensions.Hosting;
using Proto;
using Proto.Cluster;

namespace DPCS.Shared;

public class ActorSystemClusterHostedService(ActorSystem actorSystem) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Starting a cluster member");

        await actorSystem
            .Cluster()
            .StartMemberAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Shutting down a cluster member");

        await actorSystem
            .Cluster()
            .ShutdownAsync();
    }
}