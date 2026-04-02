using System.Runtime.InteropServices;

namespace DPCS.Coordinator.Strategies;

public sealed class DictionaryJobStrategy(string jobId, HashcatDictionaryJobSpecs specs, ulong chunkAttackSeconds, string serverBaseUrl) : IJobStrategy
{
    private int _currentWordlistIndex = 0;
    private int _currentIntervalIndex = 0;
    private long[]? _currentWordlistIndexData;

    private static readonly HttpClient HttpClient = new();

    private readonly Dictionary<string, ChunkState> _activeChunks = [];
    private readonly Queue<ChunkState> _retryQueue = [];

    private record ChunkState(int WordlistIndex, int StartInterval, int EndInterval, long StartByte, long EndByte);

    public AttackMode Mode => AttackMode.Dictionary;

    public HashcatDictionaryJobSpecs Specs => specs;

    public async Task<MaskWorkAssignment?> NextMaskChunkAsync(ulong hashRate) => null;

    public async Task<DictionaryWorkAssignment?> NextDictionaryChunkAsync(ulong hashRate)
    {
        if (_currentWordlistIndex >= specs.Wordlists.Count && _retryQueue.Count == 0)
        {
            return null;
        }

        ChunkState nextChunk;
        if (_retryQueue.Count > 0)
        {
            nextChunk = _retryQueue.Dequeue();
            Console.WriteLine($"{jobId}: Reassigning failed dictionary chunk for Wordlist Index {nextChunk.WordlistIndex}.");
        }
        else
        {
            if (_currentWordlistIndexData == null)
            {
                await LoadCurrentWordlistIndexAsync();
            }

            // Calculate how many intervals we need based on Hashrate.
            ulong linesNeeded = hashRate * chunkAttackSeconds;
            int intervalsNeeded = (int)Math.Max(1, linesNeeded / Constants.IndexInterval);

            int startInterval = _currentIntervalIndex;
            int endInterval = Math.Min(_currentWordlistIndexData!.Length - 1, startInterval + intervalsNeeded);

            long startByte = _currentWordlistIndexData[startInterval];
            
            // If we haven't reached the end of the index file, we know the exact end byte.
            // If we hit the end, we pass -1 to indicate "download to the end of the file".
            long endByte = (endInterval < _currentWordlistIndexData.Length - 1) 
                ? _currentWordlistIndexData[endInterval] - 1 
                : -1;

            nextChunk = new ChunkState(_currentWordlistIndex, startInterval, endInterval, startByte, endByte);

            _currentIntervalIndex = endInterval;

            // If we exhausted this wordlist, advance to the next one for the subsequent request
            if (_currentIntervalIndex >= _currentWordlistIndexData.Length - 1)
            {
                _currentWordlistIndex++;
                _currentIntervalIndex = 0;
                _currentWordlistIndexData = null;
            }
        }

        var requestId = Guid.NewGuid().ToString();
        var assignment = new DictionaryWorkAssignment
        {
            JobId = jobId,
            RequestId = requestId,
            ExtraArgs = string.Empty, // Placeholder for any extra args
            
            // Pass the byte bounds via query string so the Agent knows what Range to request
            DictionaryChunkUrl = $"{serverBaseUrl}/wordlists/{specs.Wordlists[nextChunk.WordlistIndex]}?startByte={nextChunk.StartByte}&endByte={nextChunk.EndByte}",
            DictionaryChunkChecksum = string.Empty, 
            //RuleFileContent = specs.Rules, // Assuming rules are provided as content
        };

        _activeChunks[requestId] = nextChunk;

        Console.WriteLine($"{jobId}: Assigning dictionary chunk - Wordlist: {specs.Wordlists[nextChunk.WordlistIndex]}, Bytes: {nextChunk.StartByte} to {(nextChunk.EndByte == -1 ? "EOF" : nextChunk.EndByte)}");

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

    public float GetProgress()
    {
        if (specs.Wordlists.Count == 0) return 0.0f;

        // Calculate progress across all wordlists
        var baseProgress = (float)_currentWordlistIndex / specs.Wordlists.Count * 100.0f;

        if (_currentWordlistIndexData is { Length: > 0 })
        {
            var currentWordlistProgress = (float)_currentIntervalIndex / _currentWordlistIndexData.Length * (100.0f / specs.Wordlists.Count);
            return baseProgress + currentWordlistProgress;
        }

        return baseProgress;
    }

    public void HandleRecoveredPasswords(IEnumerable<RecoveredPassword> recoveredPasswords)
    {
        foreach (var pwd in recoveredPasswords)
        {
            specs.Hashes.Remove(pwd.Hash);
        }
    }

    private async Task LoadCurrentWordlistIndexAsync()
    {
        var wordlistName = specs.Wordlists[_currentWordlistIndex];
        var idxUrl = $"{serverBaseUrl}/wordlists/{wordlistName}.idx";

        var bytes = await HttpClient.GetByteArrayAsync(idxUrl);

        // Reinterpret the downloaded bytes as longs and copy directly into an array
        _currentWordlistIndexData = MemoryMarshal.Cast<byte, long>(bytes).ToArray();
    }
}