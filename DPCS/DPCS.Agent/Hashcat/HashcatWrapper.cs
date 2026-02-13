using System.Diagnostics;

namespace DPCS.Agent.Hashcat;

public class HashcatWrapper(string hashcatPath = "hashcat", int workloadProfile = 2)
{
    public async Task StartHashcatProcessAsync(string arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = hashcatPath, // Ensure hashcat is in the system PATH or provide full path
            Arguments = arguments + $" -w {workloadProfile}", // Append workload profile to arguments
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Console.WriteLine($"[Hashcat Output] {e.Data}");
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Console.WriteLine($"[Hashcat Error] {e.Data}");
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);
    }
}