using DPCS.Blazor.Security;

namespace DPCS.Blazor.Grains;

public sealed class JobManagerGrain : JobManagerGrainBase
{
    private readonly ClusterIdentity _clusterIdentity;

    private int _globalCursor = 0;

    private readonly Dictionary<string, JobAssignment> _unfinishedJobs = [];

    private readonly Dictionary<string, int> _wordlistsInUse = [];

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
            },
            JobSpecsEnvelope.PayloadOneofCase.CombinatorJobSpecs => new()
            {
                JobId = signedJobId,
                ModeId = (int)AttackMode.Combinator,
                HashType = request.HashType,
                Hashes = { request.Hashes },
                Wordlists = { request.CombinatorJobSpecs.LeftWordlists, request.CombinatorJobSpecs.RightWordlists },
            },
            _ => throw new InvalidOperationException("Invalid job specs payload")
        };

        _unfinishedJobs[signedJobId] = assignment;

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