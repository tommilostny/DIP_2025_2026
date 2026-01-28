using DPCS.Coordinator.Grains;

namespace DPCS.Coordinator;

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
                .WithClusterKinds([
                    new ClusterKind(
                        JobManagerGrainActor.Kind,
                        Props.FromProducer(() =>
                            new JobManagerGrainActor(
                                (context, clusterIdentity) => new JobManagerGrain(context, clusterIdentity)
                            )
                        )
                    ),
                    new ClusterKind(
                        JobCoordinatorGrainActor.Kind,
                        Props.FromProducer(() =>
                            new JobCoordinatorGrainActor(
                                (context, clusterIdentity) => new JobCoordinatorGrain(context, clusterIdentity)
                            )
                        )
                    ),
                ]);

            return new ActorSystem(actorSystemConfig)
                .WithServiceProvider(provider)
                .WithRemote(remoteConfig)
                .WithCluster(clusterConfig);
        });
    }
}