namespace DPCS.Tests;

public class Scenario3_FaultToleranceTests : ClusterTestBase
{
    [Fact]
    public async Task Scenario3_FaultTolerance_LostChunksAreRequeued()
    {
        Console.WriteLine("--- Running Scenario 3: Fault Tolerance (Agent Termination) ---");

        // Agent simulates 100k H/s. Total mock keyspace = 12M candidates.
        // With ChunkTimeSeconds = 30, chunks are 3M candidates. Total 4 chunks.
        var wrapper = new DummyHashcatWrapper(100_000) { TimeMultiplier = 0.1, MockKeyspaceSize = 12_000_000UL };
        
        var dbContextFactory = new TestDbContextFactory(DbConnectionString);
        var serverSystem = new ActorSystem()
            .WithRemote(RemoteConfig.BindToLocalhost())
            .WithCluster(ClusterConfig
                .Setup($"dpcs-test-{Guid.NewGuid()}", new TestProvider(new TestProviderOptions(), ConsulMock), new PartitionIdentityLookup())
                .WithClusterKind(JobManagerGrainActor.GetClusterKind((ctx, id) => new JobManagerGrain(ctx, id)))
                // Inject a highly aggressive 6-second timeout for rapid testing
                .WithClusterKind(JobCoordinatorGrainActor.GetClusterKind((ctx, id) => new JobCoordinatorGrain(ctx, id, wrapper, "http://localhost", TimeSpan.FromSeconds(6))))
                .WithClusterKind(ResultCollectorGrainActor.GetClusterKind((ctx, id) => new ResultCollectorGrain(ctx, id, dbContextFactory)))
            );
        await serverSystem.Cluster().StartMemberAsync();

        var jobManager = serverSystem.Cluster().GetJobManagerGrain("root");
        var request = new HashcatMaskJobSpecs
        {
            Hashes = { "5d41402abc4b2a76b9719d911017c592" },
            Mask = "?l?l?l?l?l",
            HashType = 0,
            ChunkTimeSeconds = 30 // Target each chunk taking 30 simulated seconds (3 real seconds)
        };
        
        var jobId = await jobManager.MaskJobSubmission(request, CancellationToken.None);
        var coordinator = serverSystem.Cluster().GetJobCoordinatorGrain(jobId?.JobId ?? string.Empty);
        var stopwatch = Stopwatch.StartNew();

        // Spawn two workers. We use strict polling (size 1) so it's easier to reason about chunk assignments during the kill.
        // We also inject a rapid 2-second heartbeat so they stay alive under the strict test timeouts
        var workerA = serverSystem.Root.Spawn(Props.FromProducer(() => new WorkerActor(serverSystem.Cluster(), wrapper, maxPrefetchQueueSize: 1, heartbeatInterval: TimeSpan.FromSeconds(2))));
        var workerB = serverSystem.Root.Spawn(Props.FromProducer(() => new WorkerActor(serverSystem.Cluster(), wrapper, maxPrefetchQueueSize: 1, heartbeatInterval: TimeSpan.FromSeconds(2))));

        List<double> timeXs = [];
        List<double> progressYs = [];

        Console.WriteLine("Cluster running. Waiting for initial chunk assignments...");
        
        for (int i = 0; i < 3; i++) // 3 * 500ms = 1.5s
        {
            var initialStatus = await coordinator.GetJobStatus(CancellationToken.None);
            timeXs.Add(stopwatch.Elapsed.TotalSeconds);
            progressYs.Add(initialStatus?.ProgressPercentage ?? 0);
            await Task.Delay(500);
        }

        double timeOfKill = stopwatch.Elapsed.TotalSeconds;
        Console.WriteLine("\n[!] INJECTING FATAL FAULT: Terminating Agent A unconditionally mid-flight...\n");
        await serverSystem.Root.StopAsync(workerA);

        Console.WriteLine("Agent A terminated. Agent B will eventually run out of work and go idle.");
        Console.WriteLine("Waiting for the Coordinator's liveness probe to detect the timeout and re-queue the lost chunk (~6-8 seconds)...");

        while (true)
        {
            var status = await coordinator.GetJobStatus(CancellationToken.None);
            timeXs.Add(stopwatch.Elapsed.TotalSeconds);
            progressYs.Add(status?.ProgressPercentage ?? 0);

            if (status is { Status: "Completed" or "Cancelled" }) break;
            await Task.Delay(1000); 
        }
        
        stopwatch.Stop();
        Console.WriteLine($"\nJob finished successfully in {stopwatch.Elapsed.TotalSeconds:F2} seconds despite node failure!");

        var finalStatus = await coordinator.GetJobStatus(CancellationToken.None);
        Assert.Equal("Completed", finalStatus?.Status);
        Assert.Equal(100, finalStatus?.ProgressPercentage);

        // Generate the ScottPlot chart for the thesis
        var plt = new Plot();
        var scatter = plt.Add.Scatter(timeXs.ToArray(), progressYs.ToArray());
        scatter.LineWidth = 3;

        var killLine = plt.Add.VerticalLine(timeOfKill);
        killLine.Color = Colors.Red;
        killLine.LineWidth = 2;
        plt.Add.Text("Agent A Terminated", timeOfKill + 1, 100);

        plt.Title("Fault Tolerance: Recovery from Node Failure");
        plt.XLabel("Execution Time (Seconds)");
        plt.YLabel("Global Job Progress (%)");
        plt.Axes.SetLimitsY(0, 110);
        plt.Axes.SetLimitsX(0, timeXs.Last() + 5);

        string chartPath = Path.Combine(Directory.GetCurrentDirectory(), "FaultTolerance_Recovery.png");
        plt.SavePng(chartPath, 800, 400);
        Console.WriteLine($"Chart saved to: {chartPath}");

        await serverSystem.Cluster().ShutdownAsync();
    }
}