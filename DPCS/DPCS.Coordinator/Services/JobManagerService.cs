namespace DPCS.Coordinator.Services;

public class JobManagerService(ActorSystem clusterActorSystem) : BackgroundService
{
    protected override /*async*/ Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
        /*
        var random = new Random();

        var lightBulbs = new[] { "living_room_1", "living_room_2", "bedroom", "kitchen" };

        while (!stoppingToken.IsCancellationRequested)
        {
            var randomIdentity = lightBulbs[random.Next(lightBulbs.Length)];

            var smartBulbGrainClient = actorSystem
                .Cluster()
                .GetSmartBulbGrain(randomIdentity);

            if (random.Next(2) > 0)
            {
                await smartBulbGrainClient.TurnOn(stoppingToken);
            }
            else
            {
                await smartBulbGrainClient.TurnOff(stoppingToken);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
        */
    }
}