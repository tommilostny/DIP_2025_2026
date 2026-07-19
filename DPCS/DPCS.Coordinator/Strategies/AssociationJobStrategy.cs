namespace DPCS.Coordinator.Strategies;

/// <summary>
/// Schedules association attacks as a single full-file work unit.
/// Hashcat association mode requires the wordlist and hash list to remain aligned line-by-line,
/// so this strategy keeps the attack atomic instead of slicing it into independent chunks.
/// </summary>
public sealed class AssociationJobStrategy(string jobId, JobSpecsEnvelope specs, string serverBaseUrl) : IJobStrategy
{
    private readonly Queue<string> _retryQueue = [];
    private readonly HashSet<string> _activeRequests = [];
    private bool _completed;

    public AttackMode Mode => AttackMode.Association;
    public JobSpecsEnvelope Specs => specs;

    public Task InitializeAsync()
    {
        if (specs.AssociationJobSpecs.Wordlists.Count != 1)
        {
            throw new InvalidOperationException("AssociationJobStrategy requires exactly one wordlist.");
        }

        _completed = false;
        _retryQueue.Clear();
        _activeRequests.Clear();
        return Task.CompletedTask;
    }

    public Task CleanupAsync()
    {
        _retryQueue.Clear();
        _activeRequests.Clear();
        return Task.CompletedTask;
    }

    public Task<WorkAssignmentEnvelope?> NextChunkAsync(ulong hashRate, string? agentKey = null)
    {
        if (_completed || specs.Hashes.Count == 0)
        {
            return Task.FromResult<WorkAssignmentEnvelope?>(null);
        }

        if (_retryQueue.Count > 0)
        {
            var retryRequestId = _retryQueue.Dequeue();
            _activeRequests.Add(retryRequestId);
            return Task.FromResult<WorkAssignmentEnvelope?>(BuildEnvelope(retryRequestId));
        }

        if (_activeRequests.Count > 0)
        {
            return Task.FromResult<WorkAssignmentEnvelope?>(null);
        }

        var newRequestId = Guid.NewGuid().ToString();
        _activeRequests.Add(newRequestId);
        return Task.FromResult<WorkAssignmentEnvelope?>(BuildEnvelope(newRequestId));
    }

    public void CompleteChunk(string requestId)
    {
        if (_activeRequests.Remove(requestId))
        {
            _completed = true;
        }
    }

    public void FailChunk(string requestId)
    {
        if (_activeRequests.Remove(requestId))
        {
            _retryQueue.Enqueue(requestId);
        }
    }

    public float GetProgress()
    {
        if (specs.Hashes.Count == 0 || _completed)
        {
            return 100.0f;
        }

        return _activeRequests.Count > 0 ? 50.0f : 0.0f;
    }

    public void HandleRecoveredPasswords(IEnumerable<RecoveredPassword> recoveredPasswords)
    {
        foreach (var recoveredPassword in recoveredPasswords)
        {
            specs.Hashes.Remove(recoveredPassword.Hash);
        }

        if (specs.Hashes.Count == 0)
        {
            _completed = true;
        }
    }

    private WorkAssignmentEnvelope BuildEnvelope(string requestId)
    {
        var wordlistName = specs.AssociationJobSpecs.Wordlists[0];

        return new WorkAssignmentEnvelope
        {
            JobId = jobId,
            RequestId = requestId,
            AssociationAssignment = new AssociationWorkAssignment
            {
                WordlistUrl = $"{serverBaseUrl}/wordlists/{wordlistName}",
                WordlistChunkChecksum = string.Empty,
                WordlistName = wordlistName,
                StartByte = 0,
                EndByte = -1,
            }
        };
    }
}