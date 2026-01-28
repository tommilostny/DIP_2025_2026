using DPCS.Coordinator.Enums;

namespace DPCS.Coordinator.Grains;

public class JobCoordinatorGrain : JobCoordinatorGrainBase
{
    private readonly ClusterIdentity _clusterIdentity;

    private ulong _globalCursor = 0;

    private Dictionary<ClusterIdentity, WorkChunkState> _workChunks = [];

    public JobCoordinatorGrain(IContext context, ClusterIdentity clusterIdentity) : base(context)
    {
        _clusterIdentity = clusterIdentity;

        Console.WriteLine($"{_clusterIdentity.Identity}: created");
    }

    public override async Task<MaskWorkAssignment> MaskWorkRequest(WorkRequest request)
    {
        return await Task.FromResult(new MaskWorkAssignment());
    }

    public override async Task<DictionaryWorkAssignment> DictionaryWorkRequest(WorkRequest request)
    {
        return await Task.FromResult(new DictionaryWorkAssignment());
    }

    public override /*async*/ Task WorkResultSubmission(WorkResult request)
    {
        return Task.CompletedTask;
    }

    public override /*async*/ Task Heartbeat(AgentTelemetry request)
    {
        return Task.CompletedTask;
    }
}