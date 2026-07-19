namespace DPCS.Shared.Wrappers;

public interface IHashcatWrapper
{
    int Temperature { get; }
    int FanSpeed { get; }
    int GpuUtilization { get; }
    float RejectRate { get; }
    long CurrentHashrate { get; }
    IReadOnlyList<GpuDeviceTelemetry> GpuDevices { get; }
    Task<List<RecoveredPassword>> RunHashcatMaskAttackAsync(MaskWorkAssignment chunk, int hashType, string hashFilePath, CancellationToken ct);
    Task<List<RecoveredPassword>> RunHashcatDictionaryAttackAsync(DictionaryWorkAssignment chunk, int hashType, string hashFilePath, string? jobRuleFilePath, CancellationToken ct);
    Task<List<RecoveredPassword>> RunHashcatCombinatorAttackAsync(CombinatorWorkAssignment chunk, int hashType, string hashFilePath, string? jobRuleFilePath, CancellationToken ct);
    Task<List<RecoveredPassword>> RunHashcatAssociationAttackAsync(AssociationWorkAssignment chunk, int hashType, string hashFilePath, string? jobRuleFilePath, CancellationToken ct);
    Task<List<RecoveredPassword>> RunHashcatHybridMaskWordlistAttackAsync(HybridWorkAssignment chunk, int hashType, string hashFilePath, string? jobRuleFilePath, CancellationToken ct);
    Task<List<RecoveredPassword>> RunHashcatHybridWordlistMaskAttackAsync(HybridWorkAssignment chunk, int hashType, string hashFilePath, string? jobRuleFilePath, CancellationToken ct);
    Task<ulong> GetBenchmarkHashrateAsync(int hashType, CancellationToken cancellationToken = default);
    Task<ulong> GetMaskKeyspaceSizeAsync(HashcatMaskJobSpecs maskJobSpecs, string mask, CancellationToken cancellationToken = default);
    Task<ulong> GetMaskCandidateCountAsync(HashcatMaskJobSpecs maskJobSpecs, string mask, CancellationToken cancellationToken = default);
    void ResetMetrics();
}