﻿using DPCS.Agent;
using DPCS.Agent.Services;
using Microsoft.Extensions.Configuration;
using System.CommandLine;

Option<string> hashcatPathOption = new("--hashcat-path", "-p")
{
    Description = "Path to the hashcat executable (optional, defaults to 'hashcat' in system PATH)",
    DefaultValueFactory = _ => "hashcat"
};
Option<int> workloadProfileOption = new("--workload-profile", "-w")
{
    Description = "Hashcat workload profile (1-4, optional, defaults to 2)",
    DefaultValueFactory = _ => 2,
};
Option<string> consulPathOption = new("--consul-path", "-c")
{
    Description = "Path to the consul executable (optional, defaults to 'consul' in system PATH)",
    DefaultValueFactory = _ => "consul"
};
Option<string?> serverIpOption = new("--server-ip", "-s")
{
    Description = "IP address of the DPCS Coordinator server."
}; 
Option<string?> hostOption = new("--host", "-ip")
{
    Description = "Host IP address for Proto.Actor (optional, auto-detected if not provided)"
};
Option<int> portOption = new("--port", "-pt")
{
    Description = "Port for Proto.Actor (optional, defaults to 0 for dynamic)",
    DefaultValueFactory = _ => 0
};
Option<bool> noConsulOption = new("--no-consul", "-nc")
{
    Description = "Do not start the local Consul agent automatically"
};

RootCommand rootCommand = new("Distributed Password Cracking System - Agent Node");
rootCommand.Options.Add(hashcatPathOption);
rootCommand.Options.Add(workloadProfileOption);
rootCommand.Options.Add(consulPathOption);
rootCommand.Options.Add(serverIpOption);
rootCommand.Options.Add(hostOption);
rootCommand.Options.Add(portOption);
rootCommand.Options.Add(noConsulOption);

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

var hashcatPath = parseResult.GetValue(hashcatPathOption);
if (string.IsNullOrWhiteSpace(hashcatPath))
{
    Console.Error.WriteLine("Error: Hashcat path cannot be empty. Please provide a valid path or ensure 'hashcat' is in the system PATH.");
    return;
}
var workloadProfile = parseResult.GetValue(workloadProfileOption);
if (workloadProfile < 1 || workloadProfile > 4)
{
    Console.Error.WriteLine($"Error: Invalid workload profile '{workloadProfile}'. Valid values are 1, 2, 3, or 4.");
    return;
}

var consulPath = parseResult.GetValue(consulPathOption);
var serverIp = parseResult.GetValue(serverIpOption);
var noConsul = parseResult.GetValue(noConsulOption);
if (!noConsul && string.IsNullOrWhiteSpace(serverIp))
{
    Console.Error.WriteLine("Error: Server IP is required. Please provide it using --server-ip/-s.");
    return;
}
var hostIp = parseResult.GetValue(hostOption);
if (string.IsNullOrWhiteSpace(hostIp))
{
    hostIp = ConsulWrapper.GetLocalIpAddress();
    Console.WriteLine($"Auto-detected Host IP: {hostIp}");
}
else
{
    Console.WriteLine($"Using Host IP: {hostIp}");
}
var port = parseResult.GetValue(portOption);

System.Diagnostics.Process? consulProcess = null;
if (!noConsul)
{
    try
    {
        consulProcess = ConsulWrapper.StartConsulAgent(consulPath ?? "consul", hostIp, serverIp!);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error starting Consul: {ex.Message}");
        return;
    }
}

try
{
    // No need to start any actors in the worker nodes, all actors will be deployed using remoting.
    var host = Host.CreateDefaultBuilder(args)
        .ConfigureAppConfiguration((context, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                { "ProtoActor:Consul", ConsulWrapper.ConsulAddress },
                { "ProtoActor:Host", hostIp },
                { "ProtoActor:Port", port.ToString() }
            };
            config.AddInMemoryCollection(settings);
        })
        .ConfigureServices((context, services) =>
        {
            services.AddSingleton(new HashcatWrapper(hashcatPath, workloadProfile));
            services.AddActorSystem();
            services.AddHostedService<ActorSystemClusterHostedService>();
            services.AddHostedService<AgentService>();
        })
        .Build();

    await host.RunAsync();
}
finally
{
    if (consulProcess is { HasExited: false })
    {
        Console.WriteLine("Stopping Consul...");
        consulProcess.Kill();
    }
}