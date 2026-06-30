﻿using DPCS.Blazor.Grains;
using Proto.Cluster.Consul;

namespace DPCS.Blazor;

public static class ActorSystemConfiguration
{
    extension(IServiceCollection serviceCollection)
    {
        public void AddActorSystem() => serviceCollection.AddSingleton(provider =>
        {
            var actorSystemConfig = ActorSystemConfig
                .Setup();

            var config = provider.GetRequiredService<IConfiguration>();

            foreach (var kv in config.AsEnumerable())
                Console.WriteLine($"{kv.Key}={kv.Value}");

            // Aspire injects the endpoint for the 'consul-http' endpoint of the 'consul' resource.
            var consulAddress = config.GetConnectionString("consul-http")
                                ?? config["CONSUL:CONSUL_HTTP"]
                                ?? config["services:consul:consul-http:0"]
                                ?? config["ProtoActor:Consul"] 
                                ?? throw new InvalidOperationException("Consul address must be provided in configuration.");
            var host = _EmptyStringToNull(config["ProtoActor:Host"]) ?? "127.0.0.1";
            var port = _TryParseInt(config["ProtoActor:Port"]) ?? 0;

            Console.WriteLine($"Configuring ActorSystem with Consul at {consulAddress}, host {host}, port {port}");

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
                        JobManagerGrainActor.Kind,
                        Props.FromProducer(() =>
                            new JobManagerGrainActor(
                                (context, clusterIdentity) => new JobManagerGrain(context, clusterIdentity)
                            )
                        )
                    ),
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