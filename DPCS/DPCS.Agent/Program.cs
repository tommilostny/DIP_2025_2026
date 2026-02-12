using DPCS.Agent;
using DPCS.Agent.Services;

// No need to start any actors in the worker nodes, all actors will be deployed using remoting.
var host = new HostBuilder()
    .ConfigureServices((context, services) =>
    {
        services.AddActorSystem();
        services.AddHostedService<ActorSystemClusterHostedService>();
        services.AddHostedService<AgentService>();
    }).Build();

await host.StartAsync();
await host.WaitForShutdownAsync();