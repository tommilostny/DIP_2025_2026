using System.Diagnostics;
using System.Text;

namespace DPCS.Shared.Wrappers;

/// <summary>
/// A wrapper for interacting with the Hashcat tool.
/// </summary>
/// <param name="hashcatPath">Path to the Hashcat executable. Defaults to "hashcat" assuming it's in the system PATH.</param>
/// <param name="workloadProfile">The workload profile to use. Defaults to 2 (/4).</param>
public sealed class HashcatWrapper(string hashcatPath = "hashcat", int workloadProfile = 2)
{
    public async Task<List<RecoveredPassword>> RunHashcatMaskAttackAsync(MaskWorkAssignment chunk, int hashType, string hashFilePath, CancellationToken ct)
    {
        var argumentsBuilder = new StringBuilder();
        argumentsBuilder.Append($"-a {(int)AttackMode.Mask} ");
        argumentsBuilder.Append($"-m {hashType} ");
        argumentsBuilder.Append(chunk.ExtraArgs);
        argumentsBuilder.Append($" --skip {chunk.KeyspaceStart} ");
        argumentsBuilder.Append($"--limit {chunk.KeyspaceLength} ");
        argumentsBuilder.Append($"\"{hashFilePath}\" ");
        argumentsBuilder.Append($"\"{chunk.Mask}\"");
        
        return await RunHashcatAttackAsync(argumentsBuilder, ct);
    }

    public async Task<List<RecoveredPassword>> RunHashcatDictionaryAttackAsync(DictionaryWorkAssignment chunk, int hashType, string hashFilePath, CancellationToken ct)
    {
        var argumentsBuilder = new StringBuilder();
        argumentsBuilder.Append($"-a {(int)AttackMode.Dictionary} ");
        argumentsBuilder.Append($"-m {hashType} ");
        argumentsBuilder.Append(chunk.ExtraArgs);
        argumentsBuilder.Append($" \"{hashFilePath}\" ");
        argumentsBuilder.Append($"\"{chunk.DictionaryChunkUrl}\""); // Assuming this will be a local path to the downloaded wordlist chunk

        return await RunHashcatAttackAsync(argumentsBuilder, ct);
    }

    private async Task<List<RecoveredPassword>> RunHashcatAttackAsync(StringBuilder arguments, CancellationToken cancellationToken = default)
    {
        arguments.Append($" -w {workloadProfile}");
        arguments.Append(" --machine-readable"); // Ensure output is in a consistent, parseable format
        arguments.Append(" --show"); // Show cracked passwords after attack completes (even if cached by hashcat)
        var output = await ExecuteHashcatInternalAsync(arguments.ToString(), captureOutput: true, cancellationToken);
    
        if (string.IsNullOrWhiteSpace(output)) return [];

        // Hashcat's machine-readable output for found passwords is in the format:
        // HASH:PLAIN_TEXT
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var recovered = new List<RecoveredPassword>(lines.Length);

        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2)
            {
                var recoveredPassword = new RecoveredPassword
                {
                    Hash = parts[0],
                    Plaintext = parts[1]
                };
                recovered.Add(recoveredPassword);
                Console.WriteLine($"!!! Recovered password: '{recoveredPassword.Plaintext}' for hash '{recoveredPassword.Hash}'");
            }
        }
        return recovered;
    }

    public async Task<ulong> GetBenchmarkHashrateAsync(int hashType, CancellationToken cancellationToken = default)
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
            
            output = await ExecuteHashcatInternalAsync(arguments, captureOutput: true, cancellationToken);
            await File.WriteAllTextAsync(cacheFile, output, cancellationToken);
        }

        // Parse machine readable output to sum up speed from all devices
        // Format example: 1741637135:0:1:0:9942800000:160.13:64:1024:1024:8
        // Field 4 (0-indexed) is speed in H/s
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':'))
            .Where(parts => parts.Length > 4 && ulong.TryParse(parts[4], out _))
            .Aggregate(0UL, (sum, parts) => sum + ulong.Parse(parts[4]));
    }

    public static bool IsIncrementMode(int incMinLen, int incMaxLen)
    {
        return incMinLen > 0 && incMaxLen > 0 && incMinLen <= incMaxLen;
    }

    public async Task<ulong> GetMaskKeyspaceSizeAsync(string mask, int incMinLen, int incMaxLen, CancellationToken cancellationToken = default)
    {
        var arguments = IsIncrementMode(incMinLen, incMaxLen)
            ? $"--keyspace --increment --increment-min={incMinLen} --increment-max={incMaxLen} {mask}"
            : $"--keyspace {mask}";

        var output = await ExecuteHashcatInternalAsync(arguments, captureOutput: true, cancellationToken);

        // Hashcat outputs the keyspace size in the last line of the output
        var lastLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (lastLine != null && ulong.TryParse(lastLine, out var keyspaceSize))
        {
            return keyspaceSize;
        }
        throw new Exception("Failed to parse keyspace size from Hashcat output.");
    }

    public async Task<ulong> GetMaskCandidateCountAsync(string mask, int incMinLen, int incMaxLen, CancellationToken cancellationToken = default)
    {
        var arguments = IsIncrementMode(incMinLen, incMaxLen)
            ? $"--total-candidates --increment --increment-min={incMinLen} --increment-max={incMaxLen} {mask}"
            : $"--total-candidates {mask}";

        var output = await ExecuteHashcatInternalAsync(arguments, captureOutput: true, cancellationToken);

        // Hashcat outputs the total candidates in the last line of the output
        var lastLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (lastLine != null && ulong.TryParse(lastLine, out var candidateCount))
        {
            return candidateCount;
        }
        throw new Exception("Failed to parse total candidates from Hashcat output.");
    }

    private async Task<string> ExecuteHashcatInternalAsync(string arguments, bool captureOutput, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = hashcatPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        var outputBuilder = captureOutput ? new StringBuilder() : null;

        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                if (captureOutput) outputBuilder?.AppendLine(e.Data);
                else Console.WriteLine($"[Hashcat Output] {e.Data}");
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

        return outputBuilder?.ToString() ?? string.Empty;
    }
}