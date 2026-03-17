namespace DPCS.Coordinator.Strategies;

/// <summary>
/// Interface defining job strategies, which encapsulate the logic for how to divide
/// and assign work chunks to agents based on the type of attack (mask or dictionary)
/// and the job specifications.
/// </summary>
public interface IJobStrategy
{
    AttackMode Mode { get; }
    Task<MaskWorkAssignment?> NextMaskChunkAsync(ulong hashRate);
    Task<DictionaryWorkAssignment?> NextDictionaryChunkAsync(ulong hashRate);
    void CompleteChunk(string requestId);
    void FailChunk(string requestId);
    double GetProgress();
    void HandleRecoveredPasswords(IEnumerable<RecoveredPassword> recoveredPasswords);
}