using System.Diagnostics;
using System.Text;

namespace DPCS.Agent.Hashcat;

public sealed class HashcatWrapper(string hashcatPath = "hashcat", int workloadProfile = 2)
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

    public async Task<long> GetBenchmarkHashrateAsync(int hashType, CancellationToken cancellationToken = default)
    {
        var cacheDir = ".hashcat";
        var cacheFile = Path.Combine(cacheDir, $"{hashType}.txt");

        if (!Directory.Exists(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }

        string output;
        if (File.Exists(cacheFile))
        {
            output = await File.ReadAllTextAsync(cacheFile, cancellationToken);
        }
        else
        {
            // --machine-readable: outputs easier to parse format
            // --quiet: suppresses header/footer
            var arguments = $"--benchmark -m {hashType} --machine-readable --quiet -w {workloadProfile}";
            
            var startInfo = new ProcessStartInfo
            {
                FileName = hashcatPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            var outputBuilder = new StringBuilder();
            
            process.OutputDataReceived += (sender, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            
            process.Start();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync(cancellationToken);
            
            output = outputBuilder.ToString();
            await File.WriteAllTextAsync(cacheFile, output, cancellationToken);
        }

        // Parse machine readable output to sum up speed from all devices
        // Format example: 1741637135:0:1:0:9942800000:160.13:64:1024:1024:8
        // Field 4 (0-indexed) is speed in H/s
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':'))
            .Where(parts => parts.Length > 4 && long.TryParse(parts[4], out _))
            .Sum(parts => long.Parse(parts[4]));
    }
}