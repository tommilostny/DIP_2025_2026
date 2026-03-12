namespace DPCS.Coordinator.Strategies;

/// <summary>
/// Interface defining job strategies, which encapsulate the logic for how to divide
/// and assign work chunks to agents based on the type of attack (mask or dictionary)
/// and the job specifications.
/// </summary>
public interface IJobStrategy
{
    AttackMode Mode { get; }
    Task<MaskWorkAssignment?> NextMaskChunkAsync(string jobId, ulong hashRate);
    Task<DictionaryWorkAssignment?> NextDictionaryChunkAsync(string jobId, ulong hashRate);
    void CompleteChunk(string requestId);
    void FailChunk(string requestId);
    double GetProgress();
}