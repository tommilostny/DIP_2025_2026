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
            .LastOrDefault() ?? "127.0.0.1";
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
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        
        var process = new Process { StartInfo = startInfo };
        var outputBuilder = new System.Text.StringBuilder();
        var outputLock = new object();
        var captureOutput = true;

        process.OutputDataReceived += (sender, e) =>
        {
            if (!captureOutput || string.IsNullOrEmpty(e.Data)) return;
            lock (outputLock) { outputBuilder.AppendLine(e.Data); }
        };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (!captureOutput || string.IsNullOrEmpty(e.Data)) return;
            lock (outputLock) { outputBuilder.AppendLine(e.Data); }
        };

        if (!process.Start()) throw new Exception("Failed to start Consul process.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (process.WaitForExit(1500))
        {
            process.WaitForExit(); // Wait for output streams to finish reading
            throw new Exception($"Consul exited with code {process.ExitCode}. Output:\n{outputBuilder}");
        }

        captureOutput = false;
        outputBuilder.Clear();

        Console.WriteLine("Consul started.");
        return process;
    }
}