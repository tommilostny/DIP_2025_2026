namespace DPCS.Coordinator.Strategies;

/// <summary>
/// Schedules dictionary attacks by slicing indexed wordlists into byte-range chunks.
/// Chunks are sized from hashrate and target chunk duration, retried on failure,
/// and progress is tracked per wordlist interval coverage.
/// </summary>
public sealed class DictionaryJobStrategy(string jobId, JobSpecsEnvelope specs, string serverBaseUrl) : IJobStrategy
{
    private int _currentWordlistIndex = 0;
    private int _currentIntervalIndex = 0;
    private long[]? _currentWordlistIndexData;
    
    private readonly int[] _completedIntervals = new int[specs.DictionaryJobSpecs.Wordlists.Count];
    private readonly int[] _totalIntervals = new int[specs.DictionaryJobSpecs.Wordlists.Count];

    private readonly Dictionary<string, ChunkState> _activeChunks = [];
    private readonly Queue<ChunkState> _retryQueue = [];
    private readonly WordlistIndexCache _indexCache = new(jobId, serverBaseUrl);
    private readonly int _ruleCount = Math.Max(1, specs.DictionaryJobSpecs.RuleFileContent
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Count(line => !string.IsNullOrWhiteSpace(line)));

    private record ChunkState(int WordlistIndex, int StartInterval, int EndInterval, long StartByte, long EndByte);

    public AttackMode Mode => AttackMode.Dictionary;

    public JobSpecsEnvelope Specs => specs;

    /// <summary>
    /// Produces the next dictionary assignment. Failed chunks are always reissued first,
    /// then fresh chunks are produced from the current indexed wordlist.
    /// </summary>
    public async Task<WorkAssignmentEnvelope?> NextChunkAsync(ulong hashRate, string? agentKey = null)
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

            // Convert hashrate to an interval budget for the target chunk duration.
            ulong linesNeeded = hashRate * specs.ChunkTimeSeconds;

            // Dictionary rules amplify candidates per base word, so chunk fewer base lines
            // to keep chunk wall-time closer to the requested chunk duration.
            ulong adjustedLinesNeeded = Math.Max(1UL, linesNeeded / (ulong)_ruleCount);
            int intervalsNeeded = (int)Math.Max(1, adjustedLinesNeeded / Constants.IndexInterval);

            int startInterval = _currentIntervalIndex;
            int endInterval = Math.Min(_currentWordlistIndexData!.Length - 1, startInterval + intervalsNeeded);

            long startByte = _currentWordlistIndexData[startInterval];
            
            // -1 means "to EOF" so the final range consumes the remainder.
            long endByte = (endInterval < _currentWordlistIndexData.Length - 1) 
                ? _currentWordlistIndexData[endInterval] - 1 
                : -1;

            nextChunk = new ChunkState(_currentWordlistIndex, startInterval, endInterval, startByte, endByte);

            _currentIntervalIndex = endInterval;

            // Advance to the next wordlist when this one is fully assigned.
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

        var wordlistName = specs.DictionaryJobSpecs.Wordlists[nextChunk.WordlistIndex];
        //var checksum = await ComputeChunkChecksumAsync(wordlistName, nextChunk.StartByte, nextChunk.EndByte);

        return new WorkAssignmentEnvelope
        {
            JobId = jobId,
            RequestId = requestId,
            DictionaryAssignment = new DictionaryWorkAssignment
            {
                // Pass the byte bounds via query string so the Agent knows what Range to request
                WordlistUrl = $"{serverBaseUrl}/wordlists/{wordlistName}",
                WordlistChunkChecksum = string.Empty,
                WordlistName = wordlistName,
                StartByte = nextChunk.StartByte,
                EndByte = nextChunk.EndByte,
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

    /// <summary>
    /// Reports aggregate progress by averaging per-wordlist interval completion.
    /// </summary>
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

    /// <summary>
    /// Loads the index offsets for the current wordlist from the coordinator cache.
    /// </summary>
    private async Task LoadCurrentWordlistIndexAsync()
    {
        var wordlistName = specs.DictionaryJobSpecs.Wordlists[_currentWordlistIndex];
        _currentWordlistIndexData = await _indexCache.LoadIndexDataAsync(wordlistName);
        _totalIntervals[_currentWordlistIndex] = Math.Max(1, _currentWordlistIndexData.Length - 1);
    }

    /// <summary>
    /// Downloads and caches all index files needed by this job.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _indexCache.InitializeAsync(specs.DictionaryJobSpecs.Wordlists);
    }

    /// <summary>
    /// Deletes coordinator-side temporary index cache for this job.
    /// </summary>
    public Task CleanupAsync()
    {
        _indexCache.Cleanup();
        _currentWordlistIndexData = null;
        return Task.CompletedTask;
    }
}