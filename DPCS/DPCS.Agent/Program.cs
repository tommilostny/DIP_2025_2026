using DPCS.Agent;
using DPCS.Agent.Hashcat;
using DPCS.Agent.Services;
using System.CommandLine;

Option<FileInfo> hashcatPathOption = new("--hashcat-path", "-p")
{
    Description = "Path to the hashcat executable (optional, defaults to 'hashcat' in system PATH)",
    DefaultValueFactory = _ => new FileInfo("hashcat")
};
Option<int> workloadProfileOption = new("--workload-profile", "-w")
{
    Description = "Hashcat workload profile (1-4, optional, defaults to 2)",
    DefaultValueFactory = _ => 2,
};

RootCommand rootCommand = new("Distributed Password Cracking System - Agent Node");
rootCommand.Options.Add(hashcatPathOption);
rootCommand.Options.Add(workloadProfileOption);
if (args.Contains("--help") || args.Contains("-h"))
{
    rootCommand.Parse("-h").Invoke();
    return;
}

ParseResult parseResult = rootCommand.Parse(args);
if (parseResult.Errors.Count > 0)
{
    Console.Error.WriteLine("Error parsing command-line arguments:");
    foreach (var error in parseResult.Errors)
    {
        Console.Error.WriteLine($"  {error.Message}");
    }
    return;
}

var hashcatFileInfo = parseResult.GetValue(hashcatPathOption);
if (hashcatFileInfo is null or { Exists: false })
{
    Console.Error.WriteLine($"Error: Hashcat executable not found at path '{hashcatFileInfo?.FullName}'. Please provide a valid path using --hashcat-path.");
    return;
}
var workloadProfile = parseResult.GetValue(workloadProfileOption);
if (workloadProfile < 1 || workloadProfile > 4)
{
    Console.Error.WriteLine($"Error: Invalid workload profile '{workloadProfile}'. Valid values are 1, 2, 3, or 4.");
    return;
}

// No need to start any actors in the worker nodes, all actors will be deployed using remoting.
var host = new HostBuilder()
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton(new HashcatWrapper(hashcatFileInfo.FullName, workloadProfile));
        services.AddActorSystem();
        services.AddHostedService<ActorSystemClusterHostedService>();
        services.AddHostedService<AgentService>();
    }).Build();

await host.StartAsync();
await host.WaitForShutdownAsync();