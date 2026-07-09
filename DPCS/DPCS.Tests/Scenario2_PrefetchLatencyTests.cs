namespace DPCS.Tests;

public class Scenario2_PrefetchLatencyTests : ClusterTestBase
{
    [Fact]
    public async Task Scenario2_PrefetchLatencyHiding_EvaluatesPerformance()
    {
        Console.WriteLine("--- Running Scenario 2: Prefetch Latency Hiding ---");

        // Passing 0 completely disables background prefetching, forcing the GPU to wait for the network
        var timeWithoutPrefetch = await SimulateJobRunAsync(prefetchQueueSize: 0);
        
        // Passing 1 allows exactly one chunk to be downloaded in the background while cracking
        var timeWithPrefetch = await SimulateJobRunAsync(prefetchQueueSize: 1);

        Console.WriteLine("");
        Console.WriteLine("| Architecture | Execution Time (s) | Performance Gain |");
        Console.WriteLine("|---|---|---|");
        Console.WriteLine($"| Polling (No Prefetch) | {timeWithoutPrefetch:F2} | Baseline |");
        
        double gain = (timeWithoutPrefetch - timeWithPrefetch) / timeWithoutPrefetch * 100;
        string sign = gain >= 0 ? "+" : "";
        Console.WriteLine($"| Event-Driven (Prefetch) | {timeWithPrefetch:F2} | {sign}{gain:F2}% |");

        GeneratePrefetchChart(timeWithoutPrefetch, timeWithPrefetch);
    }

    private static void GeneratePrefetchChart(double timeWithout, double timeWith)
    {
        var plt = new Plot();
        
        double[] values = { timeWithout, timeWith };
        var bar = plt.Add.Bars(values);
        
        double[] positions = [0, 1];
        string[] labels = ["No Prefetch (Polling)", "Prefetch Enabled"];
        plt.Axes.Bottom.SetTicks(positions, labels);

        plt.Title("Impact of Prefetch Queue on Job Execution Time");
        plt.YLabel("Total Execution Time (Seconds)");
        
        string chartPath = Path.Combine(Directory.GetCurrentDirectory(), "Prefetch_Evaluation.png");
        plt.SavePng(chartPath, 800, 600);
        Console.WriteLine($"\nChart saved to: {chartPath}");
    }

    private async Task<double> SimulateJobRunAsync(int prefetchQueueSize)
    {
        // We simulate a fast hash (e.g., MD5) where compute time per chunk is roughly 500ms
        var wrapper = new DummyHashcatWrapper(100_000) { TimeMultiplier = 0.05, MockKeyspaceSize = 10_000_000UL };
        
        var dbContextFactory = new TestDbContextFactory(DbConnectionString);
        var serverSystem = new ActorSystem()
            .WithRemote(RemoteConfig.BindToLocalhost())
            .WithCluster(ClusterConfig
                .Setup($"dpcs-test-{Guid.NewGuid()}", new TestProvider(new TestProviderOptions(), ConsulMock), new PartitionIdentityLookup())
                .WithClusterKind(JobManagerGrainActor.GetClusterKind((ctx, id) => new JobManagerGrain(ctx, id)))
                // We inject our special LatencyCoordinatorGrain to simulate internet round-trip time
                .WithClusterKind(JobCoordinatorGrainActor.GetClusterKind((ctx, id) => new LatencyCoordinatorGrain(ctx, id, wrapper, "http://localhost")))
                .WithClusterKind(ResultCollectorGrainActor.GetClusterKind((ctx, id) => new ResultCollectorGrain(ctx, id, dbContextFactory)))
            );
        await serverSystem.Cluster().StartMemberAsync();

        var jobManager = serverSystem.Cluster().GetJobManagerGrain("root");
        var request = new JobSpecsEnvelope
        {
            Hashes = { "5d41402abc4b2a76b9719d911017c592" },
            ChunkTimeSeconds = 10,
            HashType = 0,
            MaskJobSpecs = new HashcatMaskJobSpecs
            {
                Masks = { "?l?l?l?l?l" }
            }
        };
        
        var jobAssignment = await jobManager.JobSubmission(request, CancellationToken.None);
        var coordinator = serverSystem.Cluster().GetJobCoordinatorGrain(jobAssignment?.JobId ?? string.Empty);
        
        var stopwatch = Stopwatch.StartNew();

        // Spawn the worker AFTER job submission to avoid the 5-second discovery penalty loop!
        serverSystem.Root.Spawn(Props.FromProducer(() => new WorkerActor(serverSystem.Cluster(), wrapper, maxPrefetchQueueSize: prefetchQueueSize)));

        while (true)
        {
            var status = await coordinator.GetJobStatus(CancellationToken.None);
            if (status is { Status: "Completed" or "Cancelled" }) break;
            await Task.Delay(200);
        }
        
        stopwatch.Stop();
        await serverSystem.Cluster().ShutdownAsync();
        return stopwatch.Elapsed.TotalSeconds;
    }

    private class LatencyCoordinatorGrain(IContext context, ClusterIdentity clusterIdentity, IHashcatWrapper hashcatWrapper, string serverBaseUrl) 
        : JobCoordinatorGrain(context, clusterIdentity, hashcatWrapper, serverBaseUrl)
    {
        public override async Task<WorkAssignmentEnvelope> WorkRequest(WorkRequest request)
        {
            await Task.Delay(250); // Simulate 250ms of network routing and database I/O latency
            return await base.WorkRequest(request);
        }
    }
}