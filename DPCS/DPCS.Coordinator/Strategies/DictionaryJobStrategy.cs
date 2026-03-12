namespace DPCS.Coordinator.Strategies;

public class DictionaryJobStrategy(HashcatDictionaryJobSpecs specs, HashcatWrapper hashcatWrapper) : IJobStrategy
{
    private int _currentWordlistIndex = 0;
    private long _currentFileOffset = 0;
    private readonly Dictionary<string, (int WordlistIndex, long Offset)> _activeChunks = [];
    private readonly Queue<(int WordlistIndex, long Offset)> _retryQueue = [];
    private const long ChunkSize = 1000;

    public AttackMode Mode => AttackMode.Dictionary;

    public async Task<MaskWorkAssignment?> NextMaskChunkAsync(string jobId, ulong hashRate) => null;

    public async Task<DictionaryWorkAssignment?> NextDictionaryChunkAsync(string jobId, ulong hashRate)
    {
        // Logic to calculate next dictionary chunk
        if (_currentWordlistIndex >= specs.Wordlists.Count && _retryQueue.Count == 0) return null;

        int wordlistIndex;
        long offset;

        if (_retryQueue.Count > 0)
        {
            (wordlistIndex, offset) = _retryQueue.Dequeue();
        }
        else
        {
            wordlistIndex = _currentWordlistIndex;
            offset = _currentFileOffset;
            _currentFileOffset += ChunkSize;
        }

        var requestId = Guid.NewGuid().ToString();

        var assignment = new DictionaryWorkAssignment
        {
            Meta = new WorkMetadata
            {
                JobId = jobId,
                RequestId = requestId,
                //ExtraArgs?
            },
            DictionaryChunkUrl = "http://example.com/dictionary_chunk", // Placeholder URL
            DictionaryChunkChecksum = "abc123", // Placeholder checksum
            //RuleFileContent = specs.Rules, // Assuming rules are provided as content
        };

        _activeChunks[requestId] = (wordlistIndex, offset);
        return assignment;
    }

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

    public double GetProgress() => 0.0;
}