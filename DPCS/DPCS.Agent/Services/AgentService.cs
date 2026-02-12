namespace DPCS.Agent.Services;

public class AgentService(ActorSystem clusterActorSystem) : BackgroundService
{
    // Generate a unique identity for this specific agent node instance
    private readonly AgentId _agentId = new() { Id = Guid.NewGuid().ToString() };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine($"Agent {_agentId} started. Waiting for cluster...");
        
        // Allow some time for the cluster to stabilize
        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Discovery: Ask the JobManager for a Job
                var assignment = await clusterActorSystem
                    .Cluster()
                    .GetJobManagerGrain("root")
                    .JobDiscovery(_agentId, stoppingToken);

                if (assignment is null or { ModeId: (long)AttackMode.Invalid })
                {
                    // No job available, wait and retry
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                Console.WriteLine($"Received job assignment: {assignment.JobId} (Mode: {assignment.ModeId})");

                // 2. Processing: Connect to the specific JobCoordinator and process work chunks
                await ProcessJob(assignment, stoppingToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in agent loop: {ex.Message}");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ProcessJob(JobAssignment assignment, CancellationToken ct)
    {
        // This method would contain the logic to loop through WorkChunks
        // For example:
        // var coordinator = clusterActorSystem.Cluster().GetJobCoordinatorGrain(assignment.JobId);
        // while (!ct.IsCancellationRequested) {
        //      var chunk = await coordinator.MaskWorkRequest(...);
        //      if (chunk.IsFinished) break;
        //      HashcatWrapper.StartHashcatProcess(...);
        //      await coordinator.WorkResultSubmission(...);
        // }
        
        // Placeholder delay to simulate work for now
        await Task.Delay(10000, ct);
    }
}