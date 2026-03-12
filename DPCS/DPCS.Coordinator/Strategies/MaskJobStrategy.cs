namespace DPCS.Coordinator.Strategies;

public class MaskJobStrategy(HashcatMaskJobSpecs specs, HashcatWrapper hashcatWrapper) : IJobStrategy
{
    private ulong _currentOffset = 0;
    private ulong? _totalKeyspace;
    private ulong? _totalCandidates;

    private readonly Dictionary<string, (ulong Start, ulong Length)> _activeChunks = [];
    private readonly Queue<(ulong Start, ulong Length)> _retryQueue = [];

    private const ulong ChunkAttackSeconds = 10; // Aim for 10 second chunks, can be adjusted

    public AttackMode Mode => AttackMode.Mask;

    public async Task<MaskWorkAssignment?> NextMaskChunkAsync(string jobId, ulong hashRate)
    {
        ulong start, length;
        
        if (_retryQueue.Count > 0)
        {
            (start, length) = _retryQueue.Dequeue();
        }
        else
        {
            // Ensure keyspace and total candidates count are calculated
            _totalKeyspace ??= await hashcatWrapper.GetMaskKeyspaceSizeAsync(specs.Mask, specs.MinLength, specs.MaxLength);
            _totalCandidates ??= await hashcatWrapper.GetMaskCandidateCountAsync(specs.Mask, specs.MinLength, specs.MaxLength);

            // Check if job is complete
            if (_currentOffset >= _totalKeyspace.Value)
            {
                return null;
            }

            start = _currentOffset;
            
            // Calculate Amplification (Inner Loop Size) to adjust hashrate.
            // If we don't know total candidates, we default to 1, but this risks oversized chunks.
            ulong amplification = 1;
            if (_totalCandidates.HasValue && _totalKeyspace.Value > 0)
            {
                amplification = _totalCandidates.Value / _totalKeyspace.Value;
            }

            ulong adjustedLength = hashRate * ChunkAttackSeconds / amplification;
            length = Math.Max(adjustedLength, 1000); 
            
            // Clamp length so we don't exceed the total keyspace
            ulong remaining = _totalKeyspace.Value - _currentOffset;
            if (length > remaining) length = remaining;

            _currentOffset += length;
        }

        var requestId = Guid.NewGuid().ToString();
        var assignment = new MaskWorkAssignment
        {
            JobId = jobId,
            RequestId = requestId,
            ExtraArgs = string.Empty, // Placeholder for any extra args
            Mask = specs.Mask,
            KeyspaceStart = start,
            KeyspaceLength = length,
        };
        
        _activeChunks[requestId] = (start, length);
        return assignment;
    }

    public async Task<DictionaryWorkAssignment?> NextDictionaryChunkAsync(string jobId, ulong hashRate) => null;

    public void CompleteChunk(string requestId)
    {
        _activeChunks.Remove(requestId);
    }

    public void FailChunk(string requestId)
    {
        if (_activeChunks.Remove(requestId, out var chunkInfo))
        {
            _retryQueue.Enqueue(chunkInfo);
        }
    }

    public double GetProgress() 
    {
        if (!_totalKeyspace.HasValue || _totalKeyspace == 0) return 0.0;
        return (double)_currentOffset / _totalKeyspace.Value * 100.0;
    }
}