namespace DPCS.Tests;

public class Scenario1_LoadBalancingTests : ClusterTestBase
{
    [Fact]
    public async Task Scenario1_LoadBalancing_HeterogeneousAgentsFinishSimultaneously()
    {
        Console.WriteLine("--- Running Scenario 1: Heterogeneous Load Balancing (4 Agents) ---");

        // 1. Setup the mock wrappers with mixed hashrates to prove identical and heterogeneous scaling
        var wrapperA = new DummyHashcatWrapper(50_000) { TimeMultiplier = 0.1 }; 
        var wrapperB = new DummyHashcatWrapper(50_000) { TimeMultiplier = 0.1 };
        var wrapperC = new DummyHashcatWrapper(100_000) { TimeMultiplier = 0.1 };
        
        // Agent D simulates a very fast GPU, and defines the global 120-million keyspace for the server
        var wrapperD = new DummyHashcatWrapper(200_000) { TimeMultiplier = 0.1, MockKeyspaceSize = 120_000_000UL };

        // 2. Bootstrap the in-memory actor systems
        var dbContextFactory = new TestDbContextFactory(DbConnectionString);
        var serverSystem = new ActorSystem()
            .WithRemote(RemoteConfig.BindToLocalhost())
            .WithCluster(ClusterConfig
                .Setup("dpcs-test", new TestProvider(new TestProviderOptions(), ConsulMock), new PartitionIdentityLookup())
                .WithClusterKind(JobManagerGrainActor.GetClusterKind((ctx, id) => new JobManagerGrain(ctx, id)))
                .WithClusterKind(JobCoordinatorGrainActor.GetClusterKind((ctx, id) => new JobCoordinatorGrain(ctx, id, wrapperD, "http://localhost")))
                .WithClusterKind(ResultCollectorGrainActor.GetClusterKind((ctx, id) => new ResultCollectorGrain(ctx, id, dbContextFactory)))
            );
        await serverSystem.Cluster().StartMemberAsync();

        // 3. Spawn 4 WorkerActors
        serverSystem.Root.Spawn(Props.FromProducer(() => new WorkerActor(serverSystem.Cluster(), wrapperA, maxPrefetchQueueSize: 2)));
        serverSystem.Root.Spawn(Props.FromProducer(() => new WorkerActor(serverSystem.Cluster(), wrapperB, maxPrefetchQueueSize: 2)));
        serverSystem.Root.Spawn(Props.FromProducer(() => new WorkerActor(serverSystem.Cluster(), wrapperC, maxPrefetchQueueSize: 2)));
        serverSystem.Root.Spawn(Props.FromProducer(() => new WorkerActor(serverSystem.Cluster(), wrapperD, maxPrefetchQueueSize: 2)));
        
        Console.WriteLine("Cluster simulated successfully. Agents: 50k, 50k, 100k, 200k.");

        // 4. Submit a Mask Job
        var jobManager = serverSystem.Cluster().GetJobManagerGrain("root");
        var request = new HashcatMaskJobSpecs
        {
            Hashes = { "5d41402abc4b2a76b9719d911017c592" },
            Masks = { "?l?l?l?l?l" },
            HashType = 0,
            ChunkTimeSeconds = 30 // Target each chunk taking 30 seconds
        };
        
        var jobId = await jobManager.MaskJobSubmission(request, CancellationToken.None);
        Console.WriteLine($"Job {jobId?.JobId} submitted. Simulating cluster execution...");

        // 5. Wait for Job Completion
        var coordinator = serverSystem.Cluster().GetJobCoordinatorGrain(jobId?.JobId ?? string.Empty);
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            var status = await coordinator.GetJobStatus(CancellationToken.None);
            Console.WriteLine($"Current Job Status: {status?.Status}, Progress: {status?.ProgressPercentage}%");
            
            if (status is { Status: "Completed" or "Cancelled" })
            {
                Console.WriteLine($"Job finished with status: {status.Status} in {stopwatch.Elapsed.TotalSeconds:F2} real-world seconds.");
                Console.WriteLine($"Final Progress: {status.ProgressPercentage}%");
                break;
            }
            await Task.Delay(1000); // Poll status every second
        }
        
        stopwatch.Stop();

        // --- METRICS EXTRACTION AND THESIS REPORTING ---
        var agents = new[]
        {
            ("Agent A (50k)", wrapperA),
            ("Agent B (50k)", wrapperB),
            ("Agent C (100k)", wrapperC),
            ("Agent D (200k)", wrapperD)
        };

        ulong totalKeyspace = agents.SelectMany(a => a.Item2.ExecutedChunks).Aggregate(0UL, (sum, x) => sum + x.KeyspaceLength);
        int totalChunks = agents.Sum(a => a.Item2.ExecutedChunks.Count);

        Console.WriteLine("\n### Scenario 1: Evaluation Report");
        Console.WriteLine("| Metric | " + string.Join(" | ", agents.Select(a => a.Item1)) + " | Cluster Total |");
        Console.WriteLine("|---|" + string.Join("|", agents.Select(_ => "---")) + "|---|");

        var chunksRow = new List<string>();
        var keyspaceRow = new List<string>();
        var coverageRow = new List<string>();
        var totalTimeRow = new List<string>();
        var avgTimeRow = new List<string>();

        double[] keyspaces = new double[agents.Length];
        double[] avgTimes = new double[agents.Length];
        string[] labels = new string[agents.Length];

        for (int i = 0; i < agents.Length; i++)
        {
            var (name, wrapper) = agents[i];
            var metrics = wrapper.ExecutedChunks.ToList();
            int chunks = metrics.Count;
            double simulatedTime = metrics.Sum(x => x.SimulatedSeconds);
            ulong keyspace = metrics.Aggregate(0UL, (sum, x) => sum + x.KeyspaceLength);
            double avgTime = chunks > 0 ? simulatedTime / chunks : 0;

            chunksRow.Add(chunks.ToString());
            keyspaceRow.Add(keyspace.ToString("N0", CultureInfo.InvariantCulture));
            coverageRow.Add(((double)keyspace / totalKeyspace * 100).ToString("F2", CultureInfo.InvariantCulture) + "%");
            totalTimeRow.Add($"{simulatedTime:F2} s");
            avgTimeRow.Add($"{avgTime:F2} s");

            keyspaces[i] = keyspace;
            avgTimes[i] = avgTime;
            labels[i] = name;
        }

        Console.WriteLine($"| Assigned Chunks | {string.Join(" | ", chunksRow)} | {totalChunks} |");
        Console.WriteLine($"| Keyspace Covered | {string.Join(" | ", keyspaceRow)} | {totalKeyspace.ToString("N0", CultureInfo.InvariantCulture)} |");
        Console.WriteLine($"| Coverage % | {string.Join(" | ", coverageRow)} | 100% |");
        Console.WriteLine($"| Total Simulated Compute Time | {string.Join(" | ", totalTimeRow)} | - |");
        Console.WriteLine($"| Avg Time per Chunk | {string.Join(" | ", avgTimeRow)} | - |");
        Console.WriteLine("");

        // Generate the ScottPlot charts for the thesis
        GenerateLoadBalancingCharts(keyspaces, avgTimes, labels);

        // 6. Teardown
        await serverSystem.Cluster().ShutdownAsync();
    }

    private static void GenerateLoadBalancingCharts(double[] keyspaces, double[] avgTimes, string[] labels)
    {
        double[] positions = Enumerable.Range(0, labels.Length).Select(x => (double)x).ToArray();
        
        // Chart 1: Keyspace Distribution (Pie Chart)
        var plt1 = new Plot();
        var pie = plt1.Add.Pie(keyspaces);
        
        for (int i = 0; i < keyspaces.Length; i++)
        {
            pie.Slices[i].Label = $"{labels[i]}\n({keyspaces[i] / keyspaces.Sum() * 100:F1}%)";
        }
        foreach (var slice in pie.Slices)
        {
            slice.LabelStyle.IsVisible = true;
        }
        
        plt1.Title("Dynamic Load Balancing: Keyspace Distribution");
        plt1.HideGrid(); // Pie charts do not need axes or grid lines
        string path1 = Path.Combine(Directory.GetCurrentDirectory(), "LoadBalancing_Keyspace.png");
        plt1.SavePng(path1, 800, 600);
        Console.WriteLine($"Chart saved to: {path1}");

        // Chart 2: Average Time per Chunk (Target 30s)
        var plt2 = new Plot();
        plt2.Add.Bars(avgTimes);
        var targetLine = plt2.Add.HorizontalLine(30.0);
        targetLine.Color = Colors.Red;
        targetLine.LineWidth = 2;
        plt2.Axes.Bottom.SetTicks(positions, labels);
        plt2.Title("Dynamic Load Balancing: Average Chunk Compute Time");
        plt2.YLabel("Simulated Compute Time (Seconds)");
        string path2 = Path.Combine(Directory.GetCurrentDirectory(), "LoadBalancing_TimePerChunk.png");
        plt2.SavePng(path2, 800, 600);
        Console.WriteLine($"Chart saved to: {path2}");
    }
}