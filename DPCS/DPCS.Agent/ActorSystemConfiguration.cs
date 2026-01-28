using DPCS.Agent.Grains;

namespace DPCS.Agent;

public static class ActorSystemConfiguration
{
    public static void AddActorSystem(this IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton(provider =>
        {
            var actorSystemConfig = ActorSystemConfig
                .Setup();

            var remoteConfig = RemoteConfig
                .BindToLocalhost()
                .WithProtoMessages(MessagesReflection.Descriptor);

            var clusterConfig = ClusterConfig
                .Setup(
                    clusterName: "DistributedPasswordCrackingSystem",
                    clusterProvider: new TestProvider(new TestProviderOptions(), new InMemAgent()),
                    identityLookup: new PartitionIdentityLookup()
                )
                .WithClusterKind(
                    new ClusterKind(
                        NodeGuardianGrainActor.Kind,
                        Props.FromProducer(() =>
                            new NodeGuardianGrainActor(
                                (context, clusterIdentity) => new NodeGuardianGrain(context, clusterIdentity)
                            )
                        )
                    )
                );

            return new ActorSystem(actorSystemConfig)
                .WithServiceProvider(provider)
                .WithRemote(remoteConfig)
                .WithCluster(clusterConfig);
        });
    }
}