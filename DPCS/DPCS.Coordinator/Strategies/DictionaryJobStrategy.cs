using System.Runtime.InteropServices;

namespace DPCS.Coordinator.Strategies;

public sealed class DictionaryJobStrategy(string jobId, JobSpecsEnvelope specs, string serverBaseUrl) : IJobStrategy
{
    private int _currentWordlistIndex = 0;
    private int _currentIntervalIndex = 0;
    private long[]? _currentWordlistIndexData;
    
    private readonly int[] _completedIntervals = new int[specs.DictionaryJobSpecs.Wordlists.Count];
    private readonly int[] _totalIntervals = new int[specs.DictionaryJobSpecs.Wordlists.Count];

    private static readonly HttpClient HttpClient = new();

    private readonly Dictionary<string, ChunkState> _activeChunks = [];
    private readonly Queue<ChunkState> _retryQueue = [];

    private record ChunkState(int WordlistIndex, int StartInterval, int EndInterval, long StartByte, long EndByte);

    public AttackMode Mode => AttackMode.Dictionary;

    public JobSpecsEnvelope Specs => specs;

    public async Task<WorkAssignmentEnvelope?> NextChunkAsync(ulong hashRate)
    {
        if (_currentWordlistIndex >= specs.DictionaryJobSpecs.Wordlists.Count && _retryQueue.Count == 0)
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
            if (_currentWordlistIndexData is null)
            {
                await LoadCurrentWordlistIndexAsync();
            }

            // Calculate how many intervals we need based on Hashrate.
            ulong linesNeeded = hashRate * specs.ChunkTimeSeconds;
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
        _activeChunks[requestId] = nextChunk;

        Console.WriteLine($"{jobId}: Assigning dictionary chunk - Wordlist: {specs.DictionaryJobSpecs.Wordlists[nextChunk.WordlistIndex]}, Bytes: {nextChunk.StartByte} to {(nextChunk.EndByte == -1 ? "EOF" : nextChunk.EndByte)}");

        return new WorkAssignmentEnvelope
        {
            JobId = jobId,
            RequestId = requestId,
            DictionaryAssignment = new DictionaryWorkAssignment
            {
                // Pass the byte bounds via query string so the Agent knows what Range to request
                DictionaryChunkUrl = $"{serverBaseUrl}/wordlists/{specs.DictionaryJobSpecs.Wordlists[nextChunk.WordlistIndex]}?startByte={nextChunk.StartByte}&endByte={nextChunk.EndByte}",
                DictionaryChunkChecksum = string.Empty, 
                //RuleFileContent = specs.Rules, // Assuming rules are provided as content
            },
        };
    }

    public void CompleteChunk(string requestId)
    {
        if (_activeChunks.Remove(requestId, out var chunkInfo))
        {
            _completedIntervals[chunkInfo.WordlistIndex] += chunkInfo.EndInterval - chunkInfo.StartInterval;
            Console.WriteLine($"Chunk completed: RequestId: {requestId}");
        }
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
        if (specs.Hashes.Count == 0) return 100.0f;
        if (specs.DictionaryJobSpecs.Wordlists.Count == 0) return 0.0f;

        float progress = 0f;
        float slice = 100.0f / specs.DictionaryJobSpecs.Wordlists.Count;

        for (int i = 0; i < specs.DictionaryJobSpecs.Wordlists.Count; i++)
        {
            if (_totalIntervals[i] > 0)
            {
                progress += (float)_completedIntervals[i] / _totalIntervals[i] * slice;
            }
            else if (i < _currentWordlistIndex)
            {
                progress += slice;
            }
        }

        return Math.Min(100.0f, progress);
    }

    public void HandleRecoveredPasswords(IEnumerable<RecoveredPassword> recoveredPasswords)
    {
        foreach (var pwd in recoveredPasswords)
        {
            specs.Hashes.Remove(pwd.Hash);
        }
        if (specs.Hashes.Count == 0)
        {
            Console.WriteLine($"All hashes have been cracked for job {jobId}. Marking job as complete.");
            _currentWordlistIndex = specs.DictionaryJobSpecs.Wordlists.Count; // Force completion
        }
    }

    private async Task LoadCurrentWordlistIndexAsync()
    {
        var wordlistName = specs.DictionaryJobSpecs.Wordlists[_currentWordlistIndex];
        var idxUrl = $"{serverBaseUrl}/wordlists/{wordlistName}.idx";

        var bytes = await HttpClient.GetByteArrayAsync(idxUrl);

        // Reinterpret the downloaded bytes as longs and copy directly into an array
        _currentWordlistIndexData = MemoryMarshal.Cast<byte, long>(bytes).ToArray();
        _totalIntervals[_currentWordlistIndex] = Math.Max(1, _currentWordlistIndexData.Length - 1);
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }
}