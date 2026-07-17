namespace DPCS.Coordinator.Strategies;

/// <summary>
/// Schedules combinator attacks over the Cartesian product of left and right wordlists.
/// The scheduler reserves a left slice, advances a right-side cursor per reservation,
/// supports retries, and allows tail sharing when left-side creation is exhausted.
/// </summary>
public sealed class CombinatorJobStrategy(string jobId, JobSpecsEnvelope specs, string serverBaseUrl) : IJobStrategy
{
    private int _currentLeftWordlistIndex = 0;
    private int _currentLeftIntervalIndex = 0;
    private long[]? _currentLeftIndexData;

    private readonly Dictionary<string, ChunkState> _activeChunks = [];

    private readonly Queue<ChunkState> _retryQueue = [];

    private readonly Dictionary<string, LeftReservation> _leftReservations = [];
    private readonly Dictionary<string, string> _agentReservations = [];
    private readonly Queue<string> _availableReservations = [];

    private readonly WordlistIndexCache _indexCache = new(jobId, serverBaseUrl);

    private ulong _completedWorkUnits;
    private ulong _totalWorkUnits;

    /// <summary>
    /// Represents a reservation for a specific left wordlist chunk, allowing an agent to continue processing it without interference.
    /// </summary>
    private sealed record LeftReservation(
        string ReservationId,
        int LeftWordlistIndex,
        int LeftStartInterval,
        int LeftEndInterval,
        long LeftStartByte,
        long LeftEndByte,
        int RightWordlistIndex,
        int RightIntervalIndex);

    /// <summary>
    /// Represents the state of a chunk assigned to an agent, including both left and right wordlist intervals and byte ranges, as well as the reservation key for the left wordlist.
    /// </summary>
    private sealed record ChunkState(
        string ReservationId,
        string AgentKey,
        int LeftWordlistIndex,
        int RightWordlistIndex,
        int LeftStartInterval,
        int LeftEndInterval,
        int RightStartInterval,
        int RightEndInterval,
        long LeftStartByte,
        long LeftEndByte,
        long RightStartByte,
        long RightEndByte);

    public AttackMode Mode => AttackMode.Combinator;
    public JobSpecsEnvelope Specs => specs;

    /// <summary>
    /// Produces the next combinator assignment. Failed chunks are retried first,
    /// then reservation-based scheduling advances through remaining keyspace.
    /// </summary>
    public async Task<WorkAssignmentEnvelope?> NextChunkAsync(ulong hashRate, string? agentKey = null)
    {
        if (specs.CombinatorJobSpecs.LeftWordlists.Count == 0 || specs.CombinatorJobSpecs.RightWordlists.Count == 0)
        {
            return null;
        }

        if (_retryQueue.Count > 0)
        {
            var retry = _retryQueue.Dequeue();
            Console.WriteLine($"{jobId}: Reassigning failed combinator chunk for left='{specs.CombinatorJobSpecs.LeftWordlists[retry.LeftWordlistIndex]}' / right='{specs.CombinatorJobSpecs.RightWordlists[retry.RightWordlistIndex]}'");

            var retryChunk = retry with { AgentKey = agentKey ?? string.Empty };
            if (agentKey is not null)
            {
                _agentReservations[agentKey] = retryChunk.ReservationId;
            }

            var retryRequestId = Guid.NewGuid().ToString();
            _activeChunks[retryRequestId] = retryChunk;
            return await BuildEnvelopeAsync(retryChunk, retryRequestId);
        }

        var estimatedLines = Math.Max(1UL, hashRate * specs.ChunkTimeSeconds);
        var leftIntervalsNeeded = Math.Max(1, (int)(estimatedLines / Math.Max(1UL, Constants.IndexInterval / 2)));
        var rightIntervalsNeeded = Math.Max(1, (int)Math.Ceiling(leftIntervalsNeeded / 4.0));

        // Acquire a reservation either by owner affinity, reassignable pool, new left slice,
        // or tail-sharing mode once no new left slices remain.
        var reservation = await AcquireReservationAsync(agentKey, leftIntervalsNeeded);
        if (reservation is null)
        {
            return null;
        }

        var rightData = await LoadIndexDataAsync(isLeft: false, reservation.RightWordlistIndex);
        var rightStartInterval = reservation.RightIntervalIndex;
        var rightEndInterval = Math.Min(rightData.Length - 1, rightStartInterval + rightIntervalsNeeded);
        var rightStartByte = rightData[rightStartInterval];
        var rightEndByte = rightEndInterval < rightData.Length - 1 ? rightData[rightEndInterval] - 1 : -1;

        var nextRightWordlistIndex = reservation.RightWordlistIndex;
        var nextRightIntervalIndex = rightEndInterval;
        if (nextRightIntervalIndex >= rightData.Length - 1)
        {
            nextRightWordlistIndex++;
            nextRightIntervalIndex = 0;
        }

        var reservationCompleted = nextRightWordlistIndex >= specs.CombinatorJobSpecs.RightWordlists.Count;
        if (reservationCompleted)
        {
            _leftReservations.Remove(reservation.ReservationId);
            RemoveReservationOwners(reservation.ReservationId);
        }
        else
        {
            _leftReservations[reservation.ReservationId] = reservation with
            {
                RightWordlistIndex = nextRightWordlistIndex,
                RightIntervalIndex = nextRightIntervalIndex
            };
        }

        var chunkState = new ChunkState(
            reservation.ReservationId,
            agentKey ?? string.Empty,
            reservation.LeftWordlistIndex,
            reservation.RightWordlistIndex,
            reservation.LeftStartInterval,
            reservation.LeftEndInterval,
            rightStartInterval,
            rightEndInterval,
            reservation.LeftStartByte,
            reservation.LeftEndByte,
            rightStartByte,
            rightEndByte);

        var requestId = Guid.NewGuid().ToString();
        _activeChunks[requestId] = chunkState;

        Console.WriteLine($"{jobId}: Assigning combinator chunk - Left: {specs.CombinatorJobSpecs.LeftWordlists[chunkState.LeftWordlistIndex]} ({chunkState.LeftStartByte}-{(chunkState.LeftEndByte == -1 ? "EOF" : chunkState.LeftEndByte.ToString())}), Right: {specs.CombinatorJobSpecs.RightWordlists[chunkState.RightWordlistIndex]} ({chunkState.RightStartByte}-{(chunkState.RightEndByte == -1 ? "EOF" : chunkState.RightEndByte.ToString())})");

        return await BuildEnvelopeAsync(chunkState, requestId);
    }

    public void CompleteChunk(string requestId)
    {
        if (_activeChunks.Remove(requestId, out var chunkState))
        {
            // Progress is tracked in interval-pair work units to represent combinator coverage.
            var leftIntervalSpan = Math.Max(1, chunkState.LeftEndInterval - chunkState.LeftStartInterval + 1);
            var rightIntervalSpan = Math.Max(1, chunkState.RightEndInterval - chunkState.RightStartInterval + 1);
            _completedWorkUnits += (ulong)(leftIntervalSpan * rightIntervalSpan);
            Console.WriteLine($"Chunk completed: RequestId: {requestId}");
        }
    }

    /// <summary>
    /// Requeues failed chunks and releases reservation ownership so another agent can continue.
    /// </summary>
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
                if (_leftReservations.ContainsKey(chunkState.ReservationId) && !_availableReservations.Contains(chunkState.ReservationId))
                {
                    _availableReservations.Enqueue(chunkState.ReservationId);
                }
            }
        }
    }

    public float GetProgress()
    {
        if (_totalWorkUnits == 0) return 0.0f;
        return Math.Min(100.0f, (float)_completedWorkUnits / _totalWorkUnits * 100.0f);
    }

    /// <summary>
    /// Stops scheduling early when all target hashes are recovered.
    /// </summary>
    public void HandleRecoveredPasswords(IEnumerable<RecoveredPassword> recoveredPasswords)
    {
        foreach (var pwd in recoveredPasswords)
        {
            specs.Hashes.Remove(pwd.Hash);
        }

        if (specs.Hashes.Count == 0)
        {
            Console.WriteLine($"All hashes have been cracked for job {jobId}. Marking combinator job as complete.");
            _completedWorkUnits = _totalWorkUnits;
        }
    }

    /// <summary>
    /// Downloads and caches all required index files for deterministic scheduling and progress estimation.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _indexCache.InitializeAsync(specs.CombinatorJobSpecs.LeftWordlists.Concat(specs.CombinatorJobSpecs.RightWordlists));

        RefreshEstimatedWorkUnits();
    }

    /// <summary>
    /// Deletes coordinator-side temporary index files and clears in-memory scheduling state.
    /// </summary>
    public Task CleanupAsync()
    {
        _indexCache.Cleanup();
        _leftReservations.Clear();
        _agentReservations.Clear();
        _availableReservations.Clear();
        _currentLeftIndexData = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Converts internal chunk state into a wire assignment envelope.
    /// </summary>
    private async Task<WorkAssignmentEnvelope> BuildEnvelopeAsync(ChunkState chunkState, string requestId)
    {
        var leftWordlist = specs.CombinatorJobSpecs.LeftWordlists[chunkState.LeftWordlistIndex];
        var rightWordlist = specs.CombinatorJobSpecs.RightWordlists[chunkState.RightWordlistIndex];
        //var leftChecksum = await ComputeChunkChecksumAsync(leftWordlist, chunkState.LeftStartByte, chunkState.LeftEndByte);
        //var rightChecksum = await ComputeChunkChecksumAsync(rightWordlist, chunkState.RightStartByte, chunkState.RightEndByte);

        return new WorkAssignmentEnvelope
        {
            JobId = jobId,
            RequestId = requestId,
            CombinatorAssignment = new CombinatorWorkAssignment
            {
                LeftWordlistUrl = $"{serverBaseUrl}/wordlists/{leftWordlist}",
                RightWordlistUrl = $"{serverBaseUrl}/wordlists/{rightWordlist}",
                LeftWordlistChunkChecksum = string.Empty,
                RightWordlistChunkChecksum = string.Empty,
                LeftWordlistName = leftWordlist,
                RightWordlistName = rightWordlist,
                LeftStartByte = chunkState.LeftStartByte,
                LeftEndByte = chunkState.LeftEndByte,
                RightStartByte = chunkState.RightStartByte,
                RightEndByte = chunkState.RightEndByte
            }
        };
    }

    /// <summary>
    /// Loads a wordlist index from local cache and returns byte offsets as intervals.
    /// </summary>
    private async Task<long[]> LoadIndexDataAsync(bool isLeft, int wordlistIndex)
    {
        var wordlists = isLeft ? specs.CombinatorJobSpecs.LeftWordlists : specs.CombinatorJobSpecs.RightWordlists;
        var wordlistName = wordlists[wordlistIndex];
        return await _indexCache.LoadIndexDataAsync(wordlistName);
    }

    /// <summary>
    /// Resolves the best reservation source for an agent: existing ownership, reusable queue,
    /// new left reservation, or shared tail reservation.
    /// </summary>
    private async Task<LeftReservation?> AcquireReservationAsync(string? agentKey, int leftIntervalsNeeded)
    {
        if (agentKey is not null && _agentReservations.TryGetValue(agentKey, out var ownedReservationId) && _leftReservations.TryGetValue(ownedReservationId, out var ownedReservation))
        {
            return ownedReservation;
        }

        while (_availableReservations.Count > 0)
        {
            var reservationId = _availableReservations.Dequeue();
            if (_leftReservations.TryGetValue(reservationId, out var reusableReservation))
            {
                if (agentKey is not null)
                {
                    _agentReservations[agentKey] = reservationId;
                }

                return reusableReservation;
            }
        }

        if (_currentLeftWordlistIndex >= specs.CombinatorJobSpecs.LeftWordlists.Count)
        {
            var sharedReservation = TryAcquireSharedTailReservation(agentKey);
            if (sharedReservation is not null)
            {
                return sharedReservation;
            }

            return null;
        }

        _currentLeftIndexData ??= await LoadIndexDataAsync(isLeft: true, _currentLeftWordlistIndex);

        var leftData = _currentLeftIndexData;
        if (leftData is null || leftData.Length == 0)
        {
            return null;
        }

        var leftStartInterval = _currentLeftIntervalIndex;
        var leftEndInterval = Math.Min(leftData.Length - 1, leftStartInterval + leftIntervalsNeeded);
        var leftStartByte = leftData[leftStartInterval];
        var leftEndByte = leftEndInterval < leftData.Length - 1 ? leftData[leftEndInterval] - 1 : -1;

        var reservation = new LeftReservation(
            ReservationId: Guid.NewGuid().ToString(),
            LeftWordlistIndex: _currentLeftWordlistIndex,
            LeftStartInterval: leftStartInterval,
            LeftEndInterval: leftEndInterval,
            LeftStartByte: leftStartByte,
            LeftEndByte: leftEndByte,
            RightWordlistIndex: 0,
            RightIntervalIndex: 0);

        _leftReservations[reservation.ReservationId] = reservation;
        if (agentKey is not null)
        {
            _agentReservations[agentKey] = reservation.ReservationId;
        }

        _currentLeftIntervalIndex = leftEndInterval;
        if (_currentLeftIntervalIndex >= leftData.Length - 1)
        {
            AdvanceLeftWordlist();
        }

        return reservation;
    }

    /// <summary>
    /// Allows multiple agents to help finish remaining right-side work once no new left
    /// reservations can be created.
    /// </summary>
    private LeftReservation? TryAcquireSharedTailReservation(string? agentKey)
    {
        if (_leftReservations.Count == 0)
        {
            return null;
        }

        string? selectedReservationId = null;
        var selectedActiveCount = int.MaxValue;

        foreach (var reservationId in _leftReservations.Keys)
        {
            var activeCount = _activeChunks.Values.Count(c => c.ReservationId == reservationId);
            if (activeCount < selectedActiveCount)
            {
                selectedActiveCount = activeCount;
                selectedReservationId = reservationId;
            }
        }

        if (selectedReservationId is null || !_leftReservations.TryGetValue(selectedReservationId, out var selectedReservation))
        {
            return null;
        }

        if (agentKey is not null)
        {
            _agentReservations[agentKey] = selectedReservationId;
        }

        return selectedReservation;
    }

    /// <summary>
    /// Removes all agent ownership links to a completed reservation.
    /// </summary>
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

    /// <summary>
    /// Estimates total combinator workload from cached index interval counts.
    /// </summary>
    private void RefreshEstimatedWorkUnits()
    {
        ulong leftIntervals = 0UL;
        foreach (var wordlistName in specs.CombinatorJobSpecs.LeftWordlists)
        {
            if (_indexCache.TryGetCachedIndexPath(wordlistName, out var path) && File.Exists(path))
            {
                leftIntervals += (ulong)(new FileInfo(path).Length / sizeof(long));
            }
        }

        ulong rightIntervals = 0UL;
        foreach (var wordlistName in specs.CombinatorJobSpecs.RightWordlists)
        {
            if (_indexCache.TryGetCachedIndexPath(wordlistName, out var path) && File.Exists(path))
            {
                rightIntervals += (ulong)(new FileInfo(path).Length / sizeof(long));
            }
        }

        _totalWorkUnits = leftIntervals == 0 || rightIntervals == 0 ? 0UL : leftIntervals * rightIntervals;
    }

    /// <summary>
    /// Moves left-wordlist cursor to the next source when the current one is fully reserved.
    /// </summary>
    private void AdvanceLeftWordlist()
    {
        _currentLeftWordlistIndex++;
        _currentLeftIntervalIndex = 0;
        _currentLeftIndexData = null;
    }
}