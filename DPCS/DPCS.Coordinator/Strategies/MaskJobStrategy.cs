namespace DPCS.Coordinator.Strategies;

public sealed class MaskJobStrategy(string jobId, JobSpecsEnvelope specs, IHashcatWrapper hashcatWrapper) : IJobStrategy
{
    // State for iterating through multiple masks
    private int _currentMaskIndex = 0;
    private ulong _currentMaskOffset = 0;
    private ulong? _currentMaskKeyspace;
    private ulong? _currentMaskCandidates;

    // Overall job progress tracking
    private ulong _totalJobKeyspace = 0;
    private ulong _completedKeyspace = 0;
    private readonly Dictionary<string, (int MaskIndex, ulong Start, ulong Length)> _activeChunks = [];
    private readonly Queue<(int MaskIndex, ulong Start, ulong Length)> _retryQueue = [];

    public AttackMode Mode => AttackMode.Mask;
    public JobSpecsEnvelope Specs => specs;
    private readonly HashSet<ulong> _keyspaceSizeCache = [];

    public async Task<WorkAssignmentEnvelope?> NextChunkAsync(ulong hashRate)
    {
        ulong start, length;
        int maskIndex;
        
        if (_retryQueue.Count > 0)
        {
            (maskIndex, start, length) = _retryQueue.Dequeue();
            Console.WriteLine($"{jobId}: Reassigning failed chunk for mask '{specs.MaskJobSpecs.Masks[maskIndex]}' - Start: {start}, Length: {length}");
        }
        else
        {
            // If we have processed all masks, the job is done.
            if (_currentMaskIndex >= specs.MaskJobSpecs.Masks.Count)
            {
                Console.WriteLine($"{jobId}: All masks processed. Job complete.");
                return null;
            }

            // If we are starting a new mask, calculate its specific keyspace.
            if (_currentMaskKeyspace is null)
            {
                await CalculateKeyspaceAndCandidatesCountAsync(_currentMaskIndex);
            }

            // If the current mask is fully assigned, move to the next one.
            if (_currentMaskOffset >= _currentMaskKeyspace!.Value)
            {
                _currentMaskIndex++;
                _currentMaskOffset = 0;
                _currentMaskKeyspace = null;
                _currentMaskCandidates = null;
                // Recursively call to either get the next chunk or finish the job.
                return await NextChunkAsync(hashRate);
            }

            maskIndex = _currentMaskIndex;
            start = _currentMaskOffset;
            
            // Calculate Amplification (Inner Loop Size) to adjust hashrate.
            ulong amplification = 1;
            if (_currentMaskCandidates.HasValue && _currentMaskKeyspace!.Value > 0)
            {
                amplification = Math.Max(1UL, _currentMaskCandidates.Value / _currentMaskKeyspace!.Value);
            }

            ulong adjustedLength = hashRate * specs.ChunkTimeSeconds / amplification;
            length = Math.Max(adjustedLength, 1000);
            
            // Clamp length so we don't exceed the total keyspace
            ulong remaining = _currentMaskKeyspace!.Value - _currentMaskOffset;
            if (length > remaining) length = remaining;

            _currentMaskOffset += length;

            Console.WriteLine($"{jobId}: Assigning chunk for mask '{specs.MaskJobSpecs.Masks[maskIndex]}' - Start: {start}, Length: {length}");
        
            //Print all calculation metrics:
            Console.WriteLine($"{jobId}: Mask '{specs.MaskJobSpecs.Masks[maskIndex]}' - Total Keyspace: {_currentMaskKeyspace}, Candidates: {_currentMaskCandidates}, Amplification: {amplification}, Adjusted Length: {adjustedLength}, Remaining: {remaining}, Hashrate: {hashRate}, Chunk Attack Seconds: {specs.ChunkTimeSeconds}");
        }

        var requestId = Guid.NewGuid().ToString();
        var assignment = new MaskWorkAssignment
        {
            Mask = specs.MaskJobSpecs.Masks[maskIndex],
            KeyspaceStart = start,
            KeyspaceLength = length,
            CustomCharset1 = specs.MaskJobSpecs.CustomCharset1,
            CustomCharset2 = specs.MaskJobSpecs.CustomCharset2,
            CustomCharset3 = specs.MaskJobSpecs.CustomCharset3,
            CustomCharset4 = specs.MaskJobSpecs.CustomCharset4,
        };
        
        _activeChunks[requestId] = (maskIndex, start, length);
        return new WorkAssignmentEnvelope
        {
            JobId = jobId,
            RequestId = requestId,
            MaskAssignment = assignment
        };
    }

    public async Task InitializeAsync()
    {
        // This is now only for calculating total progress.
        for (int i = 0; i < specs.MaskJobSpecs.Masks.Count; i++)
        {
            var mask = specs.MaskJobSpecs.Masks[i];
            var keyspace = await hashcatWrapper.GetMaskKeyspaceSizeAsync(specs.MaskJobSpecs, mask, CancellationToken.None);
            _totalJobKeyspace += keyspace;
            _keyspaceSizeCache.Add(keyspace);
        }
        Console.WriteLine($"{jobId}: Total calculated job keyspace for {specs.MaskJobSpecs.Masks.Count} masks: {_totalJobKeyspace}");
    }

    private async Task CalculateKeyspaceAndCandidatesCountAsync(int maskIndex)
    {
        var mask = specs.MaskJobSpecs.Masks[maskIndex];
        _currentMaskKeyspace = _keyspaceSizeCache.ElementAt(maskIndex);
        _currentMaskCandidates = await hashcatWrapper.GetMaskCandidateCountAsync(specs.MaskJobSpecs, mask, CancellationToken.None);
        Console.WriteLine($"{jobId}: Calculated keyspace for mask '{mask}': {_currentMaskKeyspace}, Candidates: {_currentMaskCandidates}");
    }

    public void CompleteChunk(string requestId)
    {
        if (_activeChunks.Remove(requestId, out var chunkInfo))
        {
            _completedKeyspace += chunkInfo.Length;
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
        if (_totalJobKeyspace == 0)
            return 0.0f;

        if (specs.Hashes.Count == 0 || (_currentMaskIndex >= specs.MaskJobSpecs.Masks.Count && _retryQueue.Count == 0 && _activeChunks.Count == 0))
            return 100.0f;

        return Math.Min(100.0f, (float)_completedKeyspace / _totalJobKeyspace * 100.0f);
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
            _currentMaskIndex = specs.MaskJobSpecs.Masks.Count; // Force completion
            _completedKeyspace = _totalJobKeyspace;
        }
    }

    public ulong GetStoredKeyspaceForMask(string mask)
    {
        var index = specs.MaskJobSpecs.Masks.IndexOf(mask);
        if (index >= 0 && index < _keyspaceSizeCache.Count)
        {
            return _keyspaceSizeCache.ElementAt(index);
        }
        return 0;
    }
}