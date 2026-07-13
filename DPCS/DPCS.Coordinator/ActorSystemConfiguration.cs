﻿using DPCS.Coordinator.Grains;
using DPCS.DAL;
using Proto.Cluster.Consul;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DPCS.Coordinator;

public static class ActorSystemConfiguration
{
    extension(IServiceCollection serviceCollection)
    {
        public void AddActorSystem() => serviceCollection.AddSingleton(provider =>
        {
            var actorSystemConfig = ActorSystemConfig
                .Setup();

            var config = provider.GetRequiredService<IConfiguration>();

            // Aspire injects the endpoint for the 'consul-http' endpoint of the 'consul' resource.
            var consulAddress = config.GetConnectionString("consul-http")
                                ?? config["CONSUL:CONSUL_HTTP"]
                                ?? config["services:consul:consul-http:0"]
                                ?? config["ProtoActor:Consul"] 
                                ?? throw new InvalidOperationException("Consul address must be provided in configuration.");
            var host = _EmptyStringToNull(config["ProtoActor:Host"]) ?? "127.0.0.1";
            var port = _TryParseInt(config["ProtoActor:Port"]) ?? 0;
            var serverBaseUrl = config["DPCS:ServerBaseUrl"]
                                ?? config["services:dpcs-blazor:http:0"]
                                ?? config["services:dpcs-blazor:https:0"]
                                ?? config["services__dpcs-blazor__http__0"]
                                ?? config["services__dpcs-blazor__https__0"]
                                ?? "http://localhost:5065";

            Console.WriteLine($"Configuring ActorSystem with Consul at {consulAddress}, host {host}, port {port}");
            Console.WriteLine($"Using server base URL for wordlist serving: {serverBaseUrl}");

            var remoteConfig = RemoteConfig
                .BindTo(host, port)
                .WithProtoMessages(MessagesReflection.Descriptor);

            var clusterConfig = ClusterConfig
                .Setup(
                    clusterName: Constants.ClusterName,
                    clusterProvider: new ConsulProvider(
                        new ConsulProviderConfig(), 
                        clientConfiguration: c => c.Address = new Uri(consulAddress)
                    ),
                    identityLookup: new PartitionIdentityLookup()
                )
                .WithGossipRequestTimeout(TimeSpan.FromSeconds(10))
                .WithClusterKinds([
                    new ClusterKind(
                        JobCoordinatorGrainActor.Kind,
                        Props.FromProducer(() =>
                            new JobCoordinatorGrainActor(
                                (context, clusterIdentity) =>
                                    new JobCoordinatorGrain(
                                        context,
                                        clusterIdentity,
                                        provider.GetRequiredService<IHashcatWrapper>(),
                                        serverBaseUrl
                                    )
                            )
                        )
                    ),
                    new ClusterKind(
                        ResultCollectorGrainActor.Kind,
                        Props.FromProducer(() =>
                            new ResultCollectorGrainActor(
                                (context, clusterIdentity) =>
                                    new ResultCollectorGrain(
                                        context,
                                        clusterIdentity,
                                        provider.GetRequiredService<IDbContextFactory<DpcsDbContext>>()
                                    )
                            )
                        )
                    )
                ]);

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