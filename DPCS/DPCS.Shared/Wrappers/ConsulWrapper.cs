using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DPCS.Shared.Wrappers;

/// <summary>
/// Helper class for managing Consul-related functionality, such as starting a Consul server and retrieving the local IP address.
/// </summary>
public static class ConsulWrapper
{
    public const string ConsulAddress = "http://localhost:8500";

    public static string GetLocalIpAddress()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .FirstOrDefault() ?? "127.0.0.1";
    }

    public static Process StartConsulServer(string consulPath, string hostIp)
    {
        return StartConsul(consulPath, $"agent -server -bootstrap -data-dir ./.consul -bind {hostIp} -client 0.0.0.0");
    }

    public static Process StartConsulAgent(string consulPath, string hostIp, string serverIp)
    {
        return StartConsul(consulPath, $"agent -data-dir ./.consul -retry-join {serverIp} -bind {hostIp}");
    }

    private static Process StartConsul(string consulPath, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = consulPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(startInfo) ?? throw new Exception("Failed to start Consul process.");
        if (process.WaitForExit(1500)) throw new Exception($"Consul exited with code {process.ExitCode}");
        Console.WriteLine("Consul started.");
        return process;
    }
}