using DPCS.Blazor.Security;

namespace DPCS.Blazor.Grains;

public sealed class JobManagerGrain : JobManagerGrainBase
{
    private sealed record SubmissionFingerprintEntry(JobAssignment Assignment, DateTime CreatedAtUtc);

    private readonly ClusterIdentity _clusterIdentity;

    private int _globalCursor = 0;

    private readonly Dictionary<string, JobAssignment> _unfinishedJobs = [];

    private readonly Dictionary<string, int> _wordlistsInUse = [];

    private readonly Dictionary<string, SubmissionFingerprintEntry> _recentSubmissions = [];
    private static readonly TimeSpan SubmissionDeduplicationWindow = TimeSpan.FromSeconds(10);

    public JobManagerGrain(IContext context, ClusterIdentity clusterIdentity) : base(context)
    {
        _clusterIdentity = clusterIdentity;
        Console.WriteLine($"{_clusterIdentity.Identity}: created");
    }

    public override async Task<JobAssignment> JobDiscovery(AgentId request)
    {
        switch (_unfinishedJobs)
        {
            case { Count: 0 }:
                return new JobAssignment { ModeId = (int)AttackMode.Invalid };
            default:
                var assignment = _unfinishedJobs.Values.ElementAt(_globalCursor++ % _unfinishedJobs.Count);
                Console.WriteLine($"{_clusterIdentity.Identity}: assigning job {assignment.JobId} to agent {request.Address}/{request.Id}");
                return await Task.FromResult(assignment);
        }
    }

    public override async Task<JobAssignment> JobSubmission(JobSpecsEnvelope request)
    {
        CleanupExpiredSubmissionFingerprints();

        var requestFingerprint = BuildRequestFingerprint(request);
        if (_recentSubmissions.TryGetValue(requestFingerprint, out var existingSubmission)
            && DateTime.UtcNow - existingSubmission.CreatedAtUtc <= SubmissionDeduplicationWindow)
        {
            Console.WriteLine($"{_clusterIdentity.Identity}: duplicate job submission ignored for fingerprint {requestFingerprint}");
            return existingSubmission.Assignment;
        }

        var normalizedRequest = NormalizeJobSubmissionRequest(request);

        var guid = Guid.NewGuid();
        var signedJobId = JobIdSecurity.GenerateSignedId(guid);

        JobAssignment assignment = normalizedRequest.PayloadCase switch
        {
            JobSpecsEnvelope.PayloadOneofCase.MaskJobSpecs => new()
            {
                JobId = signedJobId,
                ModeId = (int)AttackMode.Mask,
                HashType = normalizedRequest.HashType,
                Hashes = { normalizedRequest.Hashes },
                Masks = { normalizedRequest.MaskJobSpecs.Masks },
            },
            JobSpecsEnvelope.PayloadOneofCase.DictionaryJobSpecs => new()
            {
                JobId = signedJobId,
                ModeId = (int)AttackMode.Dictionary,
                HashType = normalizedRequest.HashType,
                Hashes = { normalizedRequest.Hashes },
                Wordlists = { normalizedRequest.DictionaryJobSpecs.Wordlists },
                RuleFileContent = normalizedRequest.DictionaryJobSpecs.RuleFileContent,
            },
            JobSpecsEnvelope.PayloadOneofCase.CombinatorJobSpecs => new()
            {
                JobId = signedJobId,
                ModeId = (int)AttackMode.Combinator,
                HashType = normalizedRequest.HashType,
                Hashes = { normalizedRequest.Hashes },
                Wordlists = { normalizedRequest.CombinatorJobSpecs.LeftWordlists, normalizedRequest.CombinatorJobSpecs.RightWordlists },
                RuleFileContent = normalizedRequest.CombinatorJobSpecs.RuleFileContent,
            },
            JobSpecsEnvelope.PayloadOneofCase.AssociationJobSpecs => new()
            {
                JobId = signedJobId,
                ModeId = (int)AttackMode.Association,
                HashType = normalizedRequest.HashType,
                Hashes = { normalizedRequest.Hashes },
                Wordlists = { normalizedRequest.AssociationJobSpecs.Wordlists },
                RuleFileContent = normalizedRequest.AssociationJobSpecs.RuleFileContent,
            },
            JobSpecsEnvelope.PayloadOneofCase.HybridJobSpecs => new()
            {
                JobId = signedJobId,
                ModeId = normalizedRequest.HybridJobSpecs.AttackMode switch
                {
                    (int)AttackMode.Hybrid_WordlistMask or (int)AttackMode.Hybrid_MaskWordlist
                        => normalizedRequest.HybridJobSpecs.AttackMode,
                    _ => throw new InvalidOperationException($"Invalid hybrid attack mode: {normalizedRequest.HybridJobSpecs.AttackMode}. Expected {(int)AttackMode.Hybrid_WordlistMask} or {(int)AttackMode.Hybrid_MaskWordlist}.")
                },
                HashType = normalizedRequest.HashType,
                Hashes = { normalizedRequest.Hashes },
                Wordlists = { normalizedRequest.HybridJobSpecs.Wordlists },
                Masks = { normalizedRequest.HybridJobSpecs.Masks },
            },
            _ => throw new InvalidOperationException("Invalid job specs payload")
        };

        _unfinishedJobs[signedJobId] = assignment;
        _recentSubmissions[requestFingerprint] = new SubmissionFingerprintEntry(assignment, DateTime.UtcNow);

        Console.WriteLine($"{_clusterIdentity.Identity}: received job submission, assigned job id {signedJobId}: {JsonSerializer.Serialize(normalizedRequest)}");

        foreach (var wl in assignment.Wordlists)
        {
            _wordlistsInUse[wl] = _wordlistsInUse.TryGetValue(wl, out int value)
                ? ++value : 1;
            
            Console.WriteLine($"{_clusterIdentity.Identity}: wordlist '{wl}' usage count is now {_wordlistsInUse[wl]}");
        }

        var cluster = System.Cluster();
        await cluster
            .GetJobCoordinatorGrain(assignment.JobId)
            .JobInit(normalizedRequest, CancellationToken.None);

        return await Task.FromResult(assignment);
    }

    private static JobSpecsEnvelope NormalizeJobSubmissionRequest(JobSpecsEnvelope request)
    {
        if (request.PayloadCase == JobSpecsEnvelope.PayloadOneofCase.MaskJobSpecs
            && request.MaskJobSpecs.MinLength > 0 && request.MaskJobSpecs.MaxLength > 0
            && request.MaskJobSpecs.MinLength <= request.MaskJobSpecs.MaxLength)
        {
            var normalized = request.Clone();
            var expandedMasks = ExpandMasksForIncrementMode(
                normalized.MaskJobSpecs.Masks,
                normalized.MaskJobSpecs.MinLength,
                normalized.MaskJobSpecs.MaxLength);

            normalized.MaskJobSpecs.Masks.Clear();
            normalized.MaskJobSpecs.Masks.Add(expandedMasks);
            normalized.MaskJobSpecs.MinLength = 0;
            normalized.MaskJobSpecs.MaxLength = 0;

            return normalized;
        }
        return request;
    }

    private static IEnumerable<string> ExpandMasksForIncrementMode(IEnumerable<string> masks, int minLength, int maxLength)
    {
        static IEnumerable<string> _ExpandSingleMask(string mask, int minLength, int maxLength)
        {
            var tokens = new List<string>(mask.Length);

            for (var index = 0; index < mask.Length; index++)
            {
                if (mask[index] == '?' && index + 1 < mask.Length)
                {
                    tokens.Add(mask.Substring(index, 2));
                    index++;
                    continue;
                }

                tokens.Add(mask[index].ToString());
            }
            if (tokens.Count == 0)
            {
                return [mask];
            }

            var effectiveMin = Math.Max(1, minLength);
            var effectiveMax = Math.Min(maxLength, tokens.Count);

            if (effectiveMin > effectiveMax)
            {
                return [mask];
            }

            return Enumerable.Range(effectiveMin, effectiveMax - effectiveMin + 1)
                .Select(length => string.Concat(tokens.Take(length)));
        }

        return [.. masks
            .SelectMany(mask => _ExpandSingleMask(mask, minLength, maxLength))
            .Distinct(StringComparer.Ordinal)];
    }


    private void CleanupExpiredSubmissionFingerprints()
    {
        var now = DateTime.UtcNow;
        var expiredFingerprints = _recentSubmissions
            .Where(entry => now - entry.Value.CreatedAtUtc > SubmissionDeduplicationWindow)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var fingerprint in expiredFingerprints)
        {
            _recentSubmissions.Remove(fingerprint);
        }
    }

    private static string BuildRequestFingerprint(JobSpecsEnvelope request)
    {
        static string _JoinSorted(IEnumerable<string> values)
            => string.Join('\n', values.OrderBy(value => value, StringComparer.Ordinal));

        return request.PayloadCase switch
        {
            JobSpecsEnvelope.PayloadOneofCase.MaskJobSpecs => string.Join('|',
                "mask",
                request.HashType,
                request.ChunkTimeSeconds,
                _JoinSorted(request.Hashes),
                _JoinSorted(request.MaskJobSpecs.Masks),
                request.MaskJobSpecs.MinLength,
                request.MaskJobSpecs.MaxLength,
                request.MaskJobSpecs.CustomCharset1,
                request.MaskJobSpecs.CustomCharset2,
                request.MaskJobSpecs.CustomCharset3,
                request.MaskJobSpecs.CustomCharset4),

            JobSpecsEnvelope.PayloadOneofCase.DictionaryJobSpecs => string.Join('|',
                "dictionary",
                request.HashType,
                request.ChunkTimeSeconds,
                _JoinSorted(request.Hashes),
                _JoinSorted(request.DictionaryJobSpecs.Wordlists),
                request.DictionaryJobSpecs.RuleFileContent),

            JobSpecsEnvelope.PayloadOneofCase.CombinatorJobSpecs => string.Join('|',
                "combinator",
                request.HashType,
                request.ChunkTimeSeconds,
                _JoinSorted(request.Hashes),
                _JoinSorted(request.CombinatorJobSpecs.LeftWordlists),
                _JoinSorted(request.CombinatorJobSpecs.RightWordlists),
                request.CombinatorJobSpecs.RuleFileContent),

            JobSpecsEnvelope.PayloadOneofCase.AssociationJobSpecs => string.Join('|',
                "association",
                request.HashType,
                request.ChunkTimeSeconds,
                _JoinSorted(request.Hashes),
                _JoinSorted(request.AssociationJobSpecs.Wordlists),
                request.AssociationJobSpecs.RuleFileContent),

            JobSpecsEnvelope.PayloadOneofCase.HybridJobSpecs => string.Join('|',
                "hybrid",
                request.HashType,
                request.ChunkTimeSeconds,
                _JoinSorted(request.Hashes),
                _JoinSorted(request.HybridJobSpecs.Wordlists),
                _JoinSorted(request.HybridJobSpecs.Masks),
                request.HybridJobSpecs.CustomCharset1,
                request.HybridJobSpecs.CustomCharset2,
                request.HybridJobSpecs.CustomCharset3,
                request.HybridJobSpecs.CustomCharset4,
                request.HybridJobSpecs.AttackMode),

            _ => throw new InvalidOperationException("Invalid job specs payload")
        };
    }

    public override async Task CancelJob(JobId jobId)
    {
        if (_unfinishedJobs.TryGetValue(jobId.Id, out JobAssignment? assignment))
        {
            _unfinishedJobs.Remove(jobId.Id);
            
            foreach (var wl in assignment.Wordlists)
            {
                if (_wordlistsInUse.TryGetValue(wl, out int count))
                {
                    if (count <= 1)
                    {
                        _wordlistsInUse.Remove(wl);
                        continue;
                    }
                    _wordlistsInUse[wl] = --count;
                }
            }

            await System.Cluster()
                .GetJobCoordinatorGrain(jobId.Id)
                .CancelJob(CancellationToken.None);

            Console.WriteLine($"{_clusterIdentity.Identity}: cancelled job {jobId}");
        }
        else
        {
            Console.WriteLine($"{_clusterIdentity.Identity}: received cancel request with invalid job id {jobId}");
        }
    }

    public override async Task FinishAck(JobId jobId)
    {
        Console.WriteLine($"{_clusterIdentity.Identity}: job {jobId} finished");

        if (_unfinishedJobs.TryGetValue(jobId.Id, out JobAssignment? assignment))
        {
            _unfinishedJobs.Remove(jobId.Id);
            Console.WriteLine($"{_clusterIdentity.Identity}: removed job {jobId} from unfinished jobs, mode {(AttackMode)assignment.ModeId}");

            if (assignment.Wordlists.Count > 0)
            {
                Console.WriteLine($"{_clusterIdentity.Identity}: cleaning up wordlist usage for job {jobId}");
                foreach (var wl in assignment.Wordlists)
                {
                    Console.WriteLine($"{_clusterIdentity.Identity}: decrementing usage count for wordlist '{wl}'");
                    if (_wordlistsInUse.TryGetValue(wl, out int count))
                    {
                        if (count <= 1)
                        {
                            Console.WriteLine($"{_clusterIdentity.Identity}: wordlist '{wl}' is no longer in use, removing from tracking");
                            _wordlistsInUse.Remove(wl);
                            continue;
                        }
                        Console.WriteLine($"{_clusterIdentity.Identity}: wordlist '{wl}' usage count decremented to {count - 1}");
                        _wordlistsInUse[wl] = --count;
                    }
                }
            }
        }
    }

    public override async Task<JobsCollection> ListJobs()
    {
        return await Task.FromResult(new JobsCollection
        {
           Jobs = { _unfinishedJobs.Values }
        });
    }

    public override async Task<WordlistUsageResponse> IsWordlistInUse(WordlistQuery request)
    {
        return await Task.FromResult(new WordlistUsageResponse
        {
            IsInUse = _wordlistsInUse.ContainsKey(request.WordlistName)
        });
    }
}