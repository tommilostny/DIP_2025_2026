namespace DPCS.Coordinator.Strategies;

public class MaskJobStrategy(HashcatMaskJobSpecs specs) : IJobStrategy
{
    private ulong _currentOffset = 0;
    private readonly Dictionary<string, (ulong Start, ulong Length)> _activeChunks = [];
    private readonly Queue<(ulong Start, ulong Length)> _retryQueue = [];

    public AttackMode Mode => AttackMode.Mask;

    public MaskWorkAssignment? NextMaskChunk(string jobId, ulong hashRate)
    {
        ulong start, length;
        
        if (_retryQueue.Count > 0)
        {
            (start, length) = _retryQueue.Dequeue();
        }
        else
        {
            start = _currentOffset;
            length = Math.Max(hashRate * 10, 10000); // Ensure min size if hashrate is 0
            _currentOffset += length;
        }

        var requestId = Guid.NewGuid().ToString();
        var assignment = new MaskWorkAssignment
        {
            Meta = new WorkMetadata
            {
                JobId = jobId,
                RequestId = requestId,
                //ExtraArgs?
            },
            Mask = specs.Mask,
            KeyspaceStart = start,
            KeyspaceLength = length,
        };
        
        _activeChunks[requestId] = (start, length);
        return assignment;
    }

    public DictionaryWorkAssignment? NextDictionaryChunk(string jobId, ulong hashRate) => null;

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

    // Placeholder for actual progress calculation based on keyspace
    public double GetProgress() => 0.0;
}