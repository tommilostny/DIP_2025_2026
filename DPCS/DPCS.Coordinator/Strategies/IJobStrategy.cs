namespace DPCS.Coordinator.Strategies;

/// <summary>
/// Attack-mode strategy contract used by the coordinator to assign work,
/// track completion, and report progress in a mode-agnostic way.
/// </summary>
public interface IJobStrategy
{
    AttackMode Mode { get; }
    JobSpecsEnvelope Specs { get; }

    /// <summary>
    /// Prepares strategy state before the first work request.
    /// </summary>
    Task InitializeAsync();
    
    /// <summary>
    /// Releases temporary resources allocated during job execution.
    /// </summary>
    Task CleanupAsync();
    
    /// <summary>
    /// Returns the next assignment for an agent, or <c>null</c> when work is exhausted.
    /// </summary>
    Task<WorkAssignmentEnvelope?> NextChunkAsync(ulong hashRate, string? agentKey = null);
    
    /// <summary>
    /// Marks a chunk as complete.
    /// </summary>
    /// <param name="requestId">The ID of the completed chunk.</param>
    void CompleteChunk(string requestId);
    
    /// <summary>
    /// Marks a chunk as failed.
    /// </summary>
    /// <param name="requestId">The ID of the failed chunk.</param>
    void FailChunk(string requestId);
    
    /// <summary>
    /// Returns percentage progress in the range [0, 100].
    /// </summary>
    float GetProgress();
    
    /// <summary>
    /// Updates strategy state when recovered passwords are reported.
    /// </summary>
    /// <param name="recoveredPasswords">The collection of recovered passwords to handle.</param>
    void HandleRecoveredPasswords(IEnumerable<RecoveredPassword> recoveredPasswords);
}