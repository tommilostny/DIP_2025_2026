using System.Collections.Concurrent;

namespace DPCS.Tests.Wrappers;

public record ChunkExecutionRecord(ulong KeyspaceStart, ulong KeyspaceLength, double SimulatedSeconds, double RealWaitSeconds);

public sealed class DummyHashcatWrapper : IHashcatWrapper
{
    private readonly long _defaultSimulatedHashrate;

    public DummyHashcatWrapper(long simulatedHashrate)
    {
        CurrentHashrate = _defaultSimulatedHashrate = simulatedHashrate;
    }

    public int Temperature { get; private set; } = 65;

    public int FanSpeed { get; private set; } = 50;

    public int GpuUtilization { get; private set; } = 99;

    public float RejectRate { get; private set; } = 0.0f;

    public long CurrentHashrate { get; private set; }

    public IReadOnlyList<GpuDeviceTelemetry> GpuDevices =>
    [
        new GpuDeviceTelemetry
        {
            DeviceIndex = 0,
            DeviceName = "Dummy GPU",
            CurrentHashrate = CurrentHashrate,
            Temperature = Temperature,
            FanSpeed = FanSpeed,
            GpuUtilization = GpuUtilization,
            VramTotalBytes = -1,
            VramUsedBytes = -1,
            VramUtilization = -1
        }
    ];

    // Allows tests to explicitly dictate the fake keyspace size for testing dynamic chunking math
    public ulong MockKeyspaceSize { get; set; } = 10_000_000UL; 
    
    // Speeds up actual test execution (e.g. 0.1 means a 10-second chunk is simulated in 1 second)
    public double TimeMultiplier { get; set; } = 1.0;

    // Thread-safe collection to capture metrics for the thesis report
    public ConcurrentBag<ChunkExecutionRecord> ExecutedChunks { get; } = [];

    public Task<ulong> GetBenchmarkHashrateAsync(int hashType, CancellationToken cancellationToken = default)
    {
        return Task.FromResult((ulong)CurrentHashrate);
    }

    public Task<ulong> GetMaskCandidateCountAsync(HashcatMaskJobSpecs maskJobSpecs, string mask, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(MockKeyspaceSize);
    }

    public Task<ulong> GetMaskKeyspaceSizeAsync(HashcatMaskJobSpecs maskJobSpecs, string mask, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(MockKeyspaceSize);
    }

    public async Task<List<RecoveredPassword>> RunHashcatDictionaryAttackAsync(DictionaryWorkAssignment chunk, int hashType, string hashFilePath, string? jobRuleFilePath, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1 * TimeMultiplier), ct);
        }
        catch (TaskCanceledException) { }
        return [];
    }

    public async Task<List<RecoveredPassword>> RunHashcatCombinatorAttackAsync(CombinatorWorkAssignment chunk, int hashType, string hashFilePath, string? jobRuleFilePath, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1 * TimeMultiplier), ct);
        }
        catch (TaskCanceledException) { }
        return [];
    }

    public async Task<List<RecoveredPassword>> RunHashcatMaskAttackAsync(MaskWorkAssignment chunk, int hashType, string hashFilePath, CancellationToken ct)
    {
        // Simulated Time = Chunk Size / Simulated Hashrate
        double simulatedSeconds = (double)chunk.KeyspaceLength / CurrentHashrate;
        double actualWaitSeconds = simulatedSeconds * TimeMultiplier;

        var record = new ChunkExecutionRecord(chunk.KeyspaceStart, chunk.KeyspaceLength, simulatedSeconds, actualWaitSeconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(actualWaitSeconds), ct);
        }
        catch (TaskCanceledException) { }
        finally {
            ExecutedChunks.Add(record);
        }
        return [];
    }

    public void ResetMetrics()
    {
        // Reset metrics to default values
        Temperature = 65;
        FanSpeed = 50;
        GpuUtilization = 99;
        RejectRate = 0.0f;
        CurrentHashrate = _defaultSimulatedHashrate;
    }

    public async Task<List<RecoveredPassword>> RunHashcatHybridMaskWordlistAttackAsync(HybridWorkAssignment chunk, int hashType, string hashFilePath, string? jobRuleFilePath, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1 * TimeMultiplier), ct);
        }
        catch (TaskCanceledException) { }
        return [];
    }

    public async Task<List<RecoveredPassword>> RunHashcatHybridWordlistMaskAttackAsync(HybridWorkAssignment chunk, int hashType, string hashFilePath, string? jobRuleFilePath, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1 * TimeMultiplier), ct);
        }
        catch (TaskCanceledException) { }
        return [];
    }
}