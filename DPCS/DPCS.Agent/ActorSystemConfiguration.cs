//using Proto.Cluster.Seed;

using Microsoft.Extensions.Configuration;
using Proto.Cluster.Consul;

namespace DPCS.Agent;

public static class ActorSystemConfiguration
{
    public static void AddActorSystem(this IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton(provider =>
        {
            var actorSystemConfig = ActorSystemConfig
                .Setup();

            var config = provider.GetRequiredService<IConfiguration>();

            var consulAddress = config["ProtoActor:Consul"] ?? throw new InvalidOperationException("Consul address must be provided in configuration under 'ProtoActor:Consul'");
            var host = _EmptyStringToNull(config["ProtoActor:Host"]) ?? "127.0.0.1";
            var port = _TryParseInt(config["ProtoActor:Port"]) ?? 0;

            Console.WriteLine($"Configuring ActorSystem with Consul at {consulAddress}, host {host}, port {port}");

            var remoteConfig = RemoteConfig
                .BindTo(host, port)
                .WithProtoMessages(MessagesReflection.Descriptor);

            var clusterConfig = ClusterConfig
                .Setup(
                    clusterName: "DistributedPasswordCrackingSystem",
                    clusterProvider: new ConsulProvider(
                        new ConsulProviderConfig(), 
                        clientConfiguration: c => c.Address = new Uri(consulAddress)
                    ),
                    identityLookup: new PartitionIdentityLookup()
                )
                /*
                .WithClusterKind(
                    new ClusterKind(
                        NodeGuardianGrainActor.Kind,
                        Props.FromProducer(() =>
                            new NodeGuardianGrainActor(
                                (context, clusterIdentity) => new NodeGuardianGrain(context, clusterIdentity)
                            )
                        )
                    )
                )*/;

            return new ActorSystem(actorSystemConfig)
                .WithServiceProvider(provider)
                .WithRemote(remoteConfig)
                .WithCluster(clusterConfig);

            int? _TryParseInt(string? intAsString) =>
                string.IsNullOrEmpty(intAsString)
                    ? null
                    : int.Parse(intAsString);

            string? _EmptyStringToNull(string? someString) => 
                string.IsNullOrEmpty(someString)
                    ? null
                    : someString;
        });
    }
}