namespace DPCS.Agent.Actors;

public class CrackingServiceActor : IActor
{
    // Implement the ReceiveAsync method for message processing
    public Task ReceiveAsync(IContext context)
    {
        // Check if the received message is of type Hello
        if (context.Message is Hello hello)
        {
            // Print the greeting to the console
            Console.WriteLine($"Hello {hello.Who}");
        }
        // Return a completed task
        return Task.CompletedTask;
    }
}