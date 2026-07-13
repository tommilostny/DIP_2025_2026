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

        var guid = Guid.NewGuid();
        var signedJobId = JobIdSecurity.GenerateSignedId(guid);

        JobAssignment assignment = request.PayloadCase switch
        {
            JobSpecsEnvelope.PayloadOneofCase.MaskJobSpecs => new()
            {
                JobId = signedJobId,
                ModeId = (int)AttackMode.Mask,
                HashType = request.HashType,
                Hashes = { request.Hashes },
                Masks = { request.MaskJobSpecs.Masks },
            },
            JobSpecsEnvelope.PayloadOneofCase.DictionaryJobSpecs => new()
            {
                JobId = signedJobId,
                ModeId = (int)AttackMode.Dictionary,
                HashType = request.HashType,
                Hashes = { request.Hashes },
                Wordlists = { request.DictionaryJobSpecs.Wordlists },
                RuleFileContent = request.DictionaryJobSpecs.RuleFileContent,
            },
            JobSpecsEnvelope.PayloadOneofCase.CombinatorJobSpecs => new()
            {
                JobId = signedJobId,
                ModeId = (int)AttackMode.Combinator,
                HashType = request.HashType,
                Hashes = { request.Hashes },
                Wordlists = { request.CombinatorJobSpecs.LeftWordlists, request.CombinatorJobSpecs.RightWordlists },
                RuleFileContent = request.CombinatorJobSpecs.RuleFileContent,
            },
            _ => throw new InvalidOperationException("Invalid job specs payload")
        };

        _unfinishedJobs[signedJobId] = assignment;
    _recentSubmissions[requestFingerprint] = new SubmissionFingerprintEntry(assignment, DateTime.UtcNow);

        Console.WriteLine($"{_clusterIdentity.Identity}: received job submission, assigned job id {signedJobId}: {JsonSerializer.Serialize(request)}");

        foreach (var wl in assignment.Wordlists)
        {
            _wordlistsInUse[wl] = _wordlistsInUse.TryGetValue(wl, out int value)
                ? ++value : 1;
            
            Console.WriteLine($"{_clusterIdentity.Identity}: wordlist '{wl}' usage count is now {_wordlistsInUse[wl]}");
        }

        var cluster = System.Cluster();
        await cluster
            .GetJobCoordinatorGrain(assignment.JobId)
            .JobInit(request, CancellationToken.None);

        return await Task.FromResult(assignment);
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
        static string JoinSorted(IEnumerable<string> values) => string.Join('\n', values.OrderBy(value => value, StringComparer.Ordinal));

        return request.PayloadCase switch
        {
            JobSpecsEnvelope.PayloadOneofCase.MaskJobSpecs => string.Join('|',
                "mask",
                request.HashType,
                request.ChunkTimeSeconds,
                JoinSorted(request.Hashes),
                JoinSorted(request.MaskJobSpecs.Masks),
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
                JoinSorted(request.Hashes),
                JoinSorted(request.DictionaryJobSpecs.Wordlists),
                request.DictionaryJobSpecs.RuleFileContent),

            JobSpecsEnvelope.PayloadOneofCase.CombinatorJobSpecs => string.Join('|',
                "combinator",
                request.HashType,
                request.ChunkTimeSeconds,
                JoinSorted(request.Hashes),
                JoinSorted(request.CombinatorJobSpecs.LeftWordlists),
                JoinSorted(request.CombinatorJobSpecs.RightWordlists),
                request.CombinatorJobSpecs.RuleFileContent),

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