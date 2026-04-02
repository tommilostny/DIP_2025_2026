namespace DPCS.Coordinator.Strategies;

public sealed class MaskJobStrategy(string jobId, HashcatMaskJobSpecs specs, HashcatWrapper hashcatWrapper, ulong chunkAttackSeconds) : IJobStrategy
{
    private ulong _currentOffset = 0;
    private ulong? _totalKeyspace;
    private ulong? _totalCandidates;

    private readonly Dictionary<string, (ulong Start, ulong Length)> _activeChunks = [];
    private readonly Queue<(ulong Start, ulong Length)> _retryQueue = [];

    public AttackMode Mode => AttackMode.Mask;

    public HashcatMaskJobSpecs Specs => specs;

    public async Task<MaskWorkAssignment?> NextMaskChunkAsync(ulong hashRate)
    {
        ulong start, length;
        
        if (_retryQueue.Count > 0)
        {
            (start, length) = _retryQueue.Dequeue();
            Console.WriteLine($"{jobId}: Reassigning failed chunk - Start: {start}, Length: {length}");
        }
        else
        {
            // Ensure keyspace and total candidates count are calculated
            _totalKeyspace ??= await hashcatWrapper.GetMaskKeyspaceSizeAsync(specs.Mask, specs.MinLength, specs.MaxLength, specs.CustomCharset1, specs.CustomCharset2, specs.CustomCharset3, specs.CustomCharset4);
            _totalCandidates ??= await hashcatWrapper.GetMaskCandidateCountAsync(specs.Mask, specs.MinLength, specs.MaxLength, specs.CustomCharset1, specs.CustomCharset2, specs.CustomCharset3, specs.CustomCharset4);

            // Check if job is complete
            if (_currentOffset >= _totalKeyspace.Value)
            {
                Console.WriteLine($"{jobId}: Job complete. Total Keyspace: {_totalKeyspace}, Total Candidates: {_totalCandidates}");
                return null;
            }

            start = _currentOffset;
            
            // Calculate Amplification (Inner Loop Size) to adjust hashrate.
            // If we don't know total candidates, we default to 1, but this risks oversized chunks.
            ulong amplification = 1;
            if (_totalCandidates.HasValue && _totalKeyspace.Value > 0)
            {
                // Ensure amplification is at least 1 to prevent DivideByZeroException
                amplification = Math.Max(1UL, _totalCandidates.Value / _totalKeyspace.Value);
            }

            ulong adjustedLength = hashRate * chunkAttackSeconds / amplification;
            length = Math.Max(adjustedLength, 1000); 
            
            // Clamp length so we don't exceed the total keyspace
            ulong remaining = _totalKeyspace.Value - _currentOffset;
            if (length > remaining) length = remaining;

            _currentOffset += length;

            Console.WriteLine($"{jobId}: Assigning chunk - Start: {start}, Length: {length}, Hashrate: {hashRate}, Amplification: {amplification}, TotalKeyspace: {_totalKeyspace}, TotalCandidates: {_totalCandidates}, CurrentOffset: {_currentOffset}");
        }

        var requestId = Guid.NewGuid().ToString();
        var assignment = new MaskWorkAssignment
        {
            JobId = jobId,
            RequestId = requestId,
            Mask = specs.Mask,
            KeyspaceStart = start,
            KeyspaceLength = length,
            CustomCharset1 = specs.CustomCharset1,
            CustomCharset2 = specs.CustomCharset2,
            CustomCharset3 = specs.CustomCharset3,
            CustomCharset4 = specs.CustomCharset4,
        };
        
        _activeChunks[requestId] = (start, length);
        return assignment;
    }

    public async Task<DictionaryWorkAssignment?> NextDictionaryChunkAsync(ulong hashRate) => null;

    public void CompleteChunk(string requestId)
    {
        Console.WriteLine($"Chunk completed: RequestId: {requestId}");
        _activeChunks.Remove(requestId);
    }

    public void FailChunk(string requestId)
    {
        if (_activeChunks.Remove(requestId, out var chunkInfo))
        {
            _retryQueue.Enqueue(chunkInfo);
        }
    }

    public float GetProgress() 
    {
        if (!_totalKeyspace.HasValue || _totalKeyspace == 0) return 0.0f;
        return (float)_currentOffset / _totalKeyspace.Value * 100.0f;
    }

    public void HandleRecoveredPasswords(IEnumerable<RecoveredPassword> recoveredPasswords)
    {
        foreach (var pwd in recoveredPasswords)
        {
            specs.Hashes.Remove(pwd.Hash);
        }
        if (specs.Hashes.Count == 0)
        {
            // All hashes have been cracked, we can consider the job complete.
            Console.WriteLine($"All hashes have been cracked for job {jobId}. Marking job as complete.");
            _currentOffset = _totalKeyspace ?? 0; // Force completion
        }
    }
}