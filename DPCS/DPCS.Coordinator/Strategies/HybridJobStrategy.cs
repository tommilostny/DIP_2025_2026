namespace DPCS.Coordinator.Strategies;

/// <summary>
/// Schedules hybrid attacks by reserving wordlist slices and pairing each slice with
/// mask keyspace chunks. Once all fresh wordlist slices are exhausted, reservations can
/// be shared across agents in the same way as combinator tail sharing.
/// </summary>
public sealed class HybridJobStrategy(string jobId, JobSpecsEnvelope specs, IHashcatWrapper hashcatWrapper, string serverBaseUrl) : IJobStrategy
{
    private int _currentWordlistIndex = 0;
    private int _currentIntervalIndex = 0;
    private long[]? _currentWordlistIndexData;

    private readonly Dictionary<string, ChunkState> _activeChunks = [];
    private readonly Queue<ChunkState> _retryQueue = [];
    private readonly Dictionary<string, WordlistReservation> _wordlistReservations = [];
    private readonly Dictionary<string, string> _agentReservations = [];
    private readonly Queue<string> _availableReservations = [];
    private readonly WordlistIndexCache _indexCache = new(jobId, serverBaseUrl);
    private readonly List<ulong> _keyspaceSizeCache = [];

    private ulong _completedWorkUnits;
    private ulong _totalWorkUnits;

    private sealed record WordlistReservation(
        string ReservationId,
        int WordlistIndex,
        int StartInterval,
        int EndInterval,
        long StartByte,
        long EndByte,
        int MaskIndex,
        ulong MaskOffset);

    private sealed record ChunkState(
        string ReservationId,
        string AgentKey,
        int WordlistIndex,
        int MaskIndex,
        int StartInterval,
        int EndInterval,
        ulong MaskStart,
        ulong MaskLength,
        long StartByte,
        long EndByte);

    public AttackMode Mode => (AttackMode)specs.HybridJobSpecs.AttackMode;
    public JobSpecsEnvelope Specs => specs;

    /// <summary>
    /// Produces the next hybrid assignment. Failed chunks are retried first,
    /// then fresh wordlist reservations are allocated, and finally existing
    /// reservations are shared once no new wordlist slices remain.
    /// </summary>
    public async Task<WorkAssignmentEnvelope?> NextChunkAsync(ulong hashRate, string? agentKey = null)
    {
        if (specs.HybridJobSpecs.Wordlists.Count == 0 || specs.HybridJobSpecs.Masks.Count == 0)
        {
            return null;
        }

        if (_retryQueue.Count > 0)
        {
            var retry = _retryQueue.Dequeue();
            Console.WriteLine($"{jobId}: Reassigning failed hybrid chunk for wordlist='{specs.HybridJobSpecs.Wordlists[retry.WordlistIndex]}' / mask='{specs.HybridJobSpecs.Masks[retry.MaskIndex]}'");

            var retryChunk = retry with { AgentKey = agentKey ?? string.Empty };
            if (agentKey is not null)
            {
                _agentReservations[agentKey] = retryChunk.ReservationId;
            }

            var retryRequestId = Guid.NewGuid().ToString();
            _activeChunks[retryRequestId] = retryChunk;
            return BuildEnvelope(retryChunk, retryRequestId);
        }

        var estimatedCandidates = Math.Max(1UL, hashRate * specs.ChunkTimeSeconds);
        var largestMaskKeyspace = GetLargestMaskKeyspace();
        var wordlistIntervalsNeeded = Math.Max(1, (int)(estimatedCandidates / Math.Max(1UL, largestMaskKeyspace)));

        var reservation = await AcquireReservationAsync(agentKey, wordlistIntervalsNeeded);
        if (reservation is null)
        {
            return null;
        }

        var maskKeyspace = _keyspaceSizeCache[reservation.MaskIndex];
        var remainingMaskKeyspace = maskKeyspace - reservation.MaskOffset;
        var targetMaskLength = Math.Max(1UL, estimatedCandidates / (ulong)Math.Max(1, wordlistIntervalsNeeded));
        var maskLength = Math.Min(remainingMaskKeyspace, targetMaskLength);

        var nextMaskIndex = reservation.MaskIndex;
        var nextMaskOffset = reservation.MaskOffset + maskLength;
        if (nextMaskOffset >= maskKeyspace)
        {
            nextMaskIndex++;
            nextMaskOffset = 0;
        }

        var reservationCompleted = nextMaskIndex >= specs.HybridJobSpecs.Masks.Count;
        if (reservationCompleted)
        {
            _wordlistReservations.Remove(reservation.ReservationId);
            RemoveReservationOwners(reservation.ReservationId);
        }
        else
        {
            _wordlistReservations[reservation.ReservationId] = reservation with
            {
                MaskIndex = nextMaskIndex,
                MaskOffset = nextMaskOffset
            };
        }

        var chunkState = new ChunkState(
            reservation.ReservationId,
            agentKey ?? string.Empty,
            reservation.WordlistIndex,
            reservation.MaskIndex,
            reservation.StartInterval,
            reservation.EndInterval,
            reservation.MaskOffset,
            maskLength,
            reservation.StartByte,
            reservation.EndByte);

        var requestId = Guid.NewGuid().ToString();
        _activeChunks[requestId] = chunkState;

        Console.WriteLine($"{jobId}: Assigning hybrid chunk - Wordlist: {specs.HybridJobSpecs.Wordlists[chunkState.WordlistIndex]} ({chunkState.StartByte}-{(chunkState.EndByte == -1 ? "EOF" : chunkState.EndByte.ToString())}), Mask: {specs.HybridJobSpecs.Masks[chunkState.MaskIndex]} ({chunkState.MaskStart}-{chunkState.MaskStart + chunkState.MaskLength})");

        return BuildEnvelope(chunkState, requestId);
    }

    public void CompleteChunk(string requestId)
    {
        if (_activeChunks.Remove(requestId, out var chunkState))
        {
            var wordlistIntervalSpan = (ulong)Math.Max(1, chunkState.EndInterval - chunkState.StartInterval + 1);
            var maskLength = Math.Max(1UL, chunkState.MaskLength);
            _completedWorkUnits += wordlistIntervalSpan * maskLength;
            Console.WriteLine($"Chunk completed: RequestId: {requestId}");
        }
    }

    public void FailChunk(string requestId)
    {
        if (_activeChunks.Remove(requestId, out var chunkState))
        {
            _retryQueue.Enqueue(chunkState);
            if (!string.IsNullOrWhiteSpace(chunkState.AgentKey)
                && _agentReservations.TryGetValue(chunkState.AgentKey, out var reservationId)
                && reservationId == chunkState.ReservationId)
            {
                _agentReservations.Remove(chunkState.AgentKey);
                if (_wordlistReservations.ContainsKey(chunkState.ReservationId) && !_availableReservations.Contains(chunkState.ReservationId))
                {
                    _availableReservations.Enqueue(chunkState.ReservationId);
                }
            }
        }
    }

    public float GetProgress()
    {
        if (_totalWorkUnits == 0)
        {
            return 0.0f;
        }

        if (specs.Hashes.Count == 0 || (_currentWordlistIndex >= specs.HybridJobSpecs.Wordlists.Count && _retryQueue.Count == 0 && _activeChunks.Count == 0))
        {
            return 100.0f;
        }

        return Math.Min(100.0f, (float)_completedWorkUnits / _totalWorkUnits * 100.0f);
    }

    public void HandleRecoveredPasswords(IEnumerable<RecoveredPassword> recoveredPasswords)
    {
        foreach (var pwd in recoveredPasswords)
        {
            specs.Hashes.Remove(pwd.Hash);
        }

        if (specs.Hashes.Count == 0)
        {
            Console.WriteLine($"All hashes have been cracked for job {jobId}. Marking hybrid job as complete.");
            _completedWorkUnits = _totalWorkUnits;
        }
    }

    public async Task InitializeAsync()
    {
        await _indexCache.InitializeAsync(specs.HybridJobSpecs.Wordlists);

        _keyspaceSizeCache.Clear();
        var hybridMaskSpecs = CreateMaskSpecs();
        foreach (var mask in specs.HybridJobSpecs.Masks)
        {
            var keyspace = await hashcatWrapper.GetMaskKeyspaceSizeAsync(hybridMaskSpecs, mask, CancellationToken.None);
            _keyspaceSizeCache.Add(keyspace);
        }

        RefreshEstimatedWorkUnits();
    }

    public Task CleanupAsync()
    {
        _indexCache.Cleanup();
        _keyspaceSizeCache.Clear();
        _wordlistReservations.Clear();
        _agentReservations.Clear();
        _availableReservations.Clear();
        _currentWordlistIndexData = null;
        return Task.CompletedTask;
    }

    public ulong GetStoredKeyspaceForMask(string mask)
    {
        var index = specs.HybridJobSpecs.Masks.IndexOf(mask);
        if (index >= 0 && index < _keyspaceSizeCache.Count)
        {
            return _keyspaceSizeCache[index];
        }

        return 0;
    }

    private async Task<WordlistReservation?> AcquireReservationAsync(string? agentKey, int wordlistIntervalsNeeded)
    {
        if (agentKey is not null && _agentReservations.TryGetValue(agentKey, out var ownedReservationId) && _wordlistReservations.TryGetValue(ownedReservationId, out var ownedReservation))
        {
            return ownedReservation;
        }

        while (_availableReservations.Count > 0)
        {
            var reservationId = _availableReservations.Dequeue();
            if (_wordlistReservations.TryGetValue(reservationId, out var reusableReservation))
            {
                if (agentKey is not null)
                {
                    _agentReservations[agentKey] = reservationId;
                }

                return reusableReservation;
            }
        }

        if (_currentWordlistIndex >= specs.HybridJobSpecs.Wordlists.Count)
        {
            return TryAcquireSharedTailReservation(agentKey);
        }

        _currentWordlistIndexData ??= await LoadCurrentWordlistIndexAsync();
        var wordlistData = _currentWordlistIndexData;
        if (wordlistData is null || wordlistData.Length == 0)
        {
            return null;
        }

        var startInterval = _currentIntervalIndex;
        var endInterval = Math.Min(wordlistData.Length - 1, startInterval + wordlistIntervalsNeeded);
        var startByte = wordlistData[startInterval];
        var endByte = endInterval < wordlistData.Length - 1 ? wordlistData[endInterval] - 1 : -1;

        var reservation = new WordlistReservation(
            ReservationId: Guid.NewGuid().ToString(),
            WordlistIndex: _currentWordlistIndex,
            StartInterval: startInterval,
            EndInterval: endInterval,
            StartByte: startByte,
            EndByte: endByte,
            MaskIndex: 0,
            MaskOffset: 0);

        _wordlistReservations[reservation.ReservationId] = reservation;
        if (agentKey is not null)
        {
            _agentReservations[agentKey] = reservation.ReservationId;
        }

        _currentIntervalIndex = endInterval;
        if (_currentIntervalIndex >= wordlistData.Length - 1)
        {
            AdvanceWordlistCursor();
        }

        return reservation;
    }

    private WordlistReservation? TryAcquireSharedTailReservation(string? agentKey)
    {
        if (_wordlistReservations.Count == 0)
        {
            return null;
        }

        string? selectedReservationId = null;
        var selectedActiveCount = int.MaxValue;

        foreach (var reservationId in _wordlistReservations.Keys)
        {
            var activeCount = _activeChunks.Values.Count(c => c.ReservationId == reservationId);
            if (activeCount < selectedActiveCount)
            {
                selectedActiveCount = activeCount;
                selectedReservationId = reservationId;
            }
        }

        if (selectedReservationId is null || !_wordlistReservations.TryGetValue(selectedReservationId, out var selectedReservation))
        {
            return null;
        }

        if (agentKey is not null)
        {
            _agentReservations[agentKey] = selectedReservationId;
        }

        return selectedReservation;
    }

    private void RemoveReservationOwners(string reservationId)
    {
        var ownedAgents = _agentReservations
            .Where(pair => pair.Value == reservationId)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var ownedAgent in ownedAgents)
        {
            _agentReservations.Remove(ownedAgent);
        }
    }

    private void RefreshEstimatedWorkUnits()
    {
        ulong wordlistIntervals = 0UL;
        foreach (var wordlistName in specs.HybridJobSpecs.Wordlists)
        {
            if (_indexCache.TryGetCachedIndexPath(wordlistName, out var path) && File.Exists(path))
            {
                wordlistIntervals += (ulong)(new FileInfo(path).Length / sizeof(long));
            }
        }

        ulong maskKeyspace = 0UL;
        foreach (var keyspace in _keyspaceSizeCache)
        {
            maskKeyspace += keyspace;
        }

        _totalWorkUnits = wordlistIntervals == 0 || maskKeyspace == 0 ? 0UL : wordlistIntervals * maskKeyspace;
    }

    private async Task<long[]> LoadCurrentWordlistIndexAsync() => await _indexCache.LoadIndexDataAsync(specs.HybridJobSpecs.Wordlists[_currentWordlistIndex]);

    private void AdvanceWordlistCursor()
    {
        _currentWordlistIndex++;
        _currentIntervalIndex = 0;
        _currentWordlistIndexData = null;
    }

    private ulong GetLargestMaskKeyspace()
    {
        var largest = 0UL;
        foreach (var keyspace in _keyspaceSizeCache)
        {
            if (keyspace > largest)
            {
                largest = keyspace;
            }
        }

        return largest;
    }

    private HashcatMaskJobSpecs CreateMaskSpecs()
    {
        return new HashcatMaskJobSpecs
        {
            CustomCharset1 = specs.HybridJobSpecs.CustomCharset1,
            CustomCharset2 = specs.HybridJobSpecs.CustomCharset2,
            CustomCharset3 = specs.HybridJobSpecs.CustomCharset3,
            CustomCharset4 = specs.HybridJobSpecs.CustomCharset4,
        };
    }

    private WorkAssignmentEnvelope BuildEnvelope(ChunkState chunkState, string requestId)
    {
        var wordlistName = specs.HybridJobSpecs.Wordlists[chunkState.WordlistIndex];
        var mask = specs.HybridJobSpecs.Masks[chunkState.MaskIndex];

        return new WorkAssignmentEnvelope
        {
            JobId = jobId,
            RequestId = requestId,
            HybridAssignment = new HybridWorkAssignment
            {
                WordlistUrl = $"{serverBaseUrl}/wordlists/{wordlistName}",
                WordlistChunkChecksum = string.Empty,
                WordlistName = wordlistName,
                StartByte = chunkState.StartByte,
                EndByte = chunkState.EndByte,
                Mask = mask,
                KeyspaceStart = chunkState.MaskStart,
                KeyspaceLength = chunkState.MaskLength,
                CustomCharset1 = specs.HybridJobSpecs.CustomCharset1,
                CustomCharset2 = specs.HybridJobSpecs.CustomCharset2,
                CustomCharset3 = specs.HybridJobSpecs.CustomCharset3,
                CustomCharset4 = specs.HybridJobSpecs.CustomCharset4,
                AttackMode = specs.HybridJobSpecs.AttackMode
            }
        };
    }
}