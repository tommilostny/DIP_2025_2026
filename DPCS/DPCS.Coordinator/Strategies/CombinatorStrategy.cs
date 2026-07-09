namespace DPCS.Coordinator.Strategies;

public sealed class CombinatorStrategy : IJobStrategy
{
    public AttackMode Mode => AttackMode.Combinator;

    public JobSpecsEnvelope Specs => throw new NotImplementedException();

    public void CompleteChunk(string requestId)
    {
        throw new NotImplementedException();
    }

    public void FailChunk(string requestId)
    {
        throw new NotImplementedException();
    }

    public float GetProgress()
    {
        throw new NotImplementedException();
    }

    public void HandleRecoveredPasswords(IEnumerable<RecoveredPassword> recoveredPasswords)
    {
        throw new NotImplementedException();
    }

    public Task InitializeAsync()
    {
        throw new NotImplementedException();
    }

    public Task<WorkAssignmentEnvelope?> NextChunkAsync(ulong hashRate)
    {
        throw new NotImplementedException();
    }
}