namespace DPCS.Coordinator.Strategies;

/// <summary>
/// Interface defining job strategies, which encapsulate the logic for how to divide
/// and assign work chunks to agents based on the type of attack (mask or dictionary)
/// and the job specifications.
/// </summary>
public interface IJobStrategy
{
    AttackMode Mode { get; }
    MaskWorkAssignment? NextMaskChunk(string jobId, ulong hashRate);
    DictionaryWorkAssignment? NextDictionaryChunk(string jobId, ulong hashRate);
    void CompleteChunk(string requestId);
    void FailChunk(string requestId);
    double GetProgress();
}