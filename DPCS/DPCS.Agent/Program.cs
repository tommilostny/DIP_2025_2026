using DPCS.Agent;
using DPCS.Agent.Hashcat;
using DPCS.Agent.Services;
using Microsoft.Extensions.Configuration;
using System.CommandLine;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

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
Option<int> consulHttpPortOption = new("--consul-http-port", "-chp")
{
    Description = "Consul HTTP port (defaults to 8500)",
    DefaultValueFactory = _ => 8500
};
Option<int> consulServerPortOption = new("--consul-server-port", "-csp")
{
    Description = "Consul Server RPC port (defaults to 8300)",
    DefaultValueFactory = _ => 8300
};
Option<int> consulSerfLanPortOption = new("--consul-serf-lan-port", "-cslp")
{
    Description = "Consul Serf LAN port (defaults to 8301)",
    DefaultValueFactory = _ => 8301
};
Option<int> consulSerfWanPortOption = new("--consul-serf-wan-port", "-cswp")
{
    Description = "Consul Serf WAN port (defaults to 8302)",
    DefaultValueFactory = _ => 8302
};
Option<int> consulGrpcPortOption = new("--consul-grpc-port", "-cgp")
{
    Description = "Consul gRPC port (defaults to 8502)",
    DefaultValueFactory = _ => 8502
};
Option<int> consulDnsPortOption = new("--consul-dns-port", "-cdp")
{
    Description = "Consul DNS port (defaults to 8600)",
    DefaultValueFactory = _ => 8600
};

RootCommand rootCommand = new("Distributed Password Cracking System - Agent Node");
rootCommand.Options.Add(hashcatPathOption);
rootCommand.Options.Add(workloadProfileOption);
rootCommand.Options.Add(consulPathOption);
rootCommand.Options.Add(serverIpOption);
rootCommand.Options.Add(hostOption);
rootCommand.Options.Add(portOption);
rootCommand.Options.Add(consulHttpPortOption);
rootCommand.Options.Add(consulServerPortOption);
rootCommand.Options.Add(consulSerfLanPortOption);
rootCommand.Options.Add(consulSerfWanPortOption);
rootCommand.Options.Add(consulGrpcPortOption);
rootCommand.Options.Add(consulDnsPortOption);

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

var consulPath = parseResult.GetValue(consulPathOption);
var serverIp = parseResult.GetValue(serverIpOption);
if (string.IsNullOrWhiteSpace(serverIp))
{
    Console.Error.WriteLine("Error: Server IP is required. Please provide it using --server-ip/-s.");
    return;
}
var hostIp = parseResult.GetValue(hostOption);
if (string.IsNullOrWhiteSpace(hostIp))
{
    hostIp = GetLocalIpAddress();
    Console.WriteLine($"Auto-detected Host IP: {hostIp}");
}
else
{
    Console.WriteLine($"Using Host IP: {hostIp}");
}
var port = parseResult.GetValue(portOption);
var consulHttpPort = parseResult.GetValue(consulHttpPortOption);
var consulServerPort = parseResult.GetValue(consulServerPortOption);
var consulSerfLanPort = parseResult.GetValue(consulSerfLanPortOption);
var consulSerfWanPort = parseResult.GetValue(consulSerfWanPortOption);
var consulGrpcPort = parseResult.GetValue(consulGrpcPortOption);
var consulDnsPort = parseResult.GetValue(consulDnsPortOption);

System.Diagnostics.Process? consulProcess = null;
try
{
    consulProcess = StartConsul(consulPath ?? "consul", hostIp, serverIp, consulHttpPort, consulServerPort, consulSerfLanPort, consulSerfWanPort, consulGrpcPort, consulDnsPort);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error starting Consul: {ex.Message}");
    return;
}

try
{
    // No need to start any actors in the worker nodes, all actors will be deployed using remoting.
    var host = Host.CreateDefaultBuilder(args)
        .ConfigureAppConfiguration((context, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                { "ProtoActor:Consul", $"http://localhost:{consulHttpPort}" },
                { "ProtoActor:Host", hostIp },
                { "ProtoActor:Port", port.ToString() }
            };
            config.AddInMemoryCollection(settings);
        })
        .ConfigureServices((context, services) =>
        {
            services.AddSingleton(new HashcatWrapper(hashcatFileInfo.FullName, workloadProfile));
            services.AddActorSystem();
            services.AddHostedService<ActorSystemClusterHostedService>();
            services.AddHostedService<AgentService>();
        })
        .Build();

    await host.RunAsync();
}
finally
{
    if (consulProcess != null && !consulProcess.HasExited)
    {
        Console.WriteLine("Stopping Consul...");
        consulProcess.Kill();
    }
}

static string GetLocalIpAddress()
{
    return NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString())
        .FirstOrDefault() ?? "127.0.0.1";
}

static System.Diagnostics.Process StartConsul(string consulPath, string hostIp, string serverIp, int httpPort, int serverPort, int serfLanPort, int serfWanPort, int grpcPort, int dnsPort)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = consulPath,
        Arguments = $"agent -dev -data-dir ./.consul -retry-join {serverIp} -bind {hostIp} -http-port {httpPort} -server-port {serverPort} -serf-lan-port {serfLanPort} -serf-wan-port {serfWanPort} -grpc-port {grpcPort} -dns-port {dnsPort}",
        UseShellExecute = false,
        CreateNoWindow = false
    };
    var process = System.Diagnostics.Process.Start(startInfo) ?? throw new Exception("Failed to start Consul process.");
    if (process.WaitForExit(500)) throw new Exception($"Consul exited with code {process.ExitCode}");
    Console.WriteLine("Consul started.");
    return process;
}