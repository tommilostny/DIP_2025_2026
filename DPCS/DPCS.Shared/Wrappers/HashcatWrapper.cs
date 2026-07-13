using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DPCS.Shared.Wrappers;

/// <summary>
/// A wrapper for interacting with the Hashcat tool.
/// </summary>
/// <param name="hashcatPath">Path to the Hashcat executable. Defaults to "hashcat" assuming it's in the system PATH.</param>
/// <param name="workloadProfile">The workload profile to use. Defaults to 2 (/4).</param>
public sealed class HashcatWrapper(string hashcatPath = "hashcat", int workloadProfile = 2) : IHashcatWrapper
{
    public int Temperature { get; private set; }
    public int FanSpeed { get; private set; }
    public int GpuUtilization { get; private set; }
    public float RejectRate { get; private set; }
    public long CurrentHashrate { get; private set; }

    public async Task<List<RecoveredPassword>> RunHashcatMaskAttackAsync(MaskWorkAssignment chunk, int hashType, string hashFilePath, CancellationToken ct)
    {
        var argumentsBuilder = new StringBuilder();
        argumentsBuilder.Append($"-a {(int)AttackMode.Mask} ");
        argumentsBuilder.Append($"-m {hashType} ");
        if (!string.IsNullOrEmpty(chunk.CustomCharset1))
        {
            argumentsBuilder.Append($"-1 \"{chunk.CustomCharset1}\" ");
        }
        if (!string.IsNullOrEmpty(chunk.CustomCharset2))
        {
            argumentsBuilder.Append($"-2 \"{chunk.CustomCharset2}\" ");
        }
        if (!string.IsNullOrEmpty(chunk.CustomCharset3))
        {
            argumentsBuilder.Append($"-3 \"{chunk.CustomCharset3}\" ");
        }
        if (!string.IsNullOrEmpty(chunk.CustomCharset4))
        {
            argumentsBuilder.Append($"-4 \"{chunk.CustomCharset4}\" ");
        }
        argumentsBuilder.Append($"--skip {chunk.KeyspaceStart} ");
        argumentsBuilder.Append($"--limit {chunk.KeyspaceLength} ");
        argumentsBuilder.Append($"\"{hashFilePath}\" ");
        argumentsBuilder.Append($"\"{chunk.Mask}\"");
        
        return await RunHashcatAttackAsync(argumentsBuilder, ct);
    }

    public async Task<List<RecoveredPassword>> RunHashcatDictionaryAttackAsync(DictionaryWorkAssignment chunk, int hashType, string hashFilePath, string? jobRuleFilePath, CancellationToken ct)
    {
        var argumentsBuilder = new StringBuilder();
        argumentsBuilder.Append($"-a {(int)AttackMode.Dictionary} ");
        argumentsBuilder.Append($"-m {hashType} ");
        argumentsBuilder.Append($" \"{hashFilePath}\" ");
        argumentsBuilder.Append($"\"{chunk.DictionaryChunkUrl}\"");

        if (!string.IsNullOrWhiteSpace(jobRuleFilePath))
        {
            argumentsBuilder.Append($" -r \"{jobRuleFilePath}\"");
        }

        return await RunHashcatAttackAsync(argumentsBuilder, ct);
    }

    public async Task<List<RecoveredPassword>> RunHashcatCombinatorAttackAsync(CombinatorWorkAssignment chunk, int hashType, string hashFilePath, string? jobRuleFilePath, CancellationToken ct)
    {
        var argumentsBuilder = new StringBuilder();
        argumentsBuilder.Append($"-a {(int)AttackMode.Combinator} ");
        argumentsBuilder.Append($"-m {hashType} ");
        argumentsBuilder.Append($" \"{hashFilePath}\" ");
        argumentsBuilder.Append($"\"{chunk.LeftDictionaryChunkUrl}\" ");
        argumentsBuilder.Append($"\"{chunk.RightDictionaryChunkUrl}\"");

        if (!string.IsNullOrWhiteSpace(jobRuleFilePath))
        {
            argumentsBuilder.Append($" -r \"{jobRuleFilePath}\"");
        }

        return await RunHashcatAttackAsync(argumentsBuilder, ct);
    }

    private async Task<List<RecoveredPassword>> RunHashcatAttackAsync(StringBuilder arguments, CancellationToken cancellationToken = default)
    {
        var outFilePath = Path.Combine(Path.GetTempPath(), $"dpcs_cracked_{Guid.NewGuid():N}.txt");

        arguments.Append($" -w {workloadProfile}");
        arguments.Append(" -O");
        arguments.Append(" --quiet"); // Suppress non-essential output for cleaner logs
        arguments.Append(" --potfile-disable"); // Do not skip work using cached potfiles
        arguments.Append($" --outfile=\"{outFilePath}\""); // Safely write cracked passwords to a temp file
        arguments.Append(" --status --status-json --status-timer 5"); // Periodically print JSON status to stdout

        // We set captureOutput: false to prevent memory bloat during long jobs, 
        // but onLine will still receive stdout because we force redirection internally.
        await ExecuteHashcatInternalAsync(arguments.ToString(), captureOutput: false, cancellationToken, line => 
        {
            var trimmedLine = line.Trim();
            if (trimmedLine.StartsWith('{') && trimmedLine.EndsWith('}'))
            {
                ParseTelemetryJson(trimmedLine);
            }
            else if (!string.IsNullOrWhiteSpace(trimmedLine))
            {
                Console.WriteLine(trimmedLine);
            }
        });

        if (!File.Exists(outFilePath)) return [];

        var lines = await File.ReadAllLinesAsync(outFilePath, cancellationToken);
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
                Console.BackgroundColor = ConsoleColor.Green;
                Console.ForegroundColor = ConsoleColor.Black;
                Console.WriteLine($"!!! Recovered password: '{recoveredPassword.Plaintext}' for hash '{recoveredPassword.Hash}'");
                Console.ResetColor();
            }
        }

        try
        {
            File.Delete(outFilePath);
        }
        catch
        {
            // Ignore file deletion errors
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
        // Old format example: 1741637135:0:1:0:9942800000:160.13:64:1024:1024:8 (Hashrate at index 4)
        // Hashcat 7.x example: 1:0:210:405:14.79:301380616 (Hashrate at index 5)
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':'))
            .Select(parts => {
                // Handle Hashcat 7.x format (6 parts, speed is last)
                if (parts.Length == 6 && ulong.TryParse(parts[5], out var speedNew)) return speedNew;
                // Handle older Hashcat formats (usually 10 parts, speed is at index 4)
                if (parts.Length > 6 && ulong.TryParse(parts[4], out var speedOld)) return speedOld;
                return 0UL;
            })
            .Aggregate(0UL, (sum, speed) => sum + speed);
    }

    public static bool IsIncrementMode(int incMinLen, int incMaxLen)
    {
        return incMinLen > 0 && incMaxLen > 0 && incMinLen <= incMaxLen;
    }

    public static async Task<string> CreateTemporaryMaskFileAsync(IEnumerable<string> masks, CancellationToken cancellationToken = default)
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"dpcs_masks_{Guid.NewGuid():N}.hcmask");
        await File.WriteAllLinesAsync(tempFilePath, masks, cancellationToken);
        return tempFilePath;
    }

    public async Task<ulong> GetMaskKeyspaceSizeAsync(HashcatMaskJobSpecs maskJobSpecs, string mask, CancellationToken cancellationToken = default)
    {
        var argumentsBuilder = new StringBuilder();
        argumentsBuilder.Append($"-a {(int)AttackMode.Mask} --keyspace ");
        if (IsIncrementMode(maskJobSpecs.MinLength, maskJobSpecs.MaxLength))
        {
            argumentsBuilder.Append($"--increment --increment-min={maskJobSpecs.MinLength} --increment-max={maskJobSpecs.MaxLength} ");
        }

        if (!string.IsNullOrEmpty(maskJobSpecs.CustomCharset1)) argumentsBuilder.Append($"-1 \"{maskJobSpecs.CustomCharset1}\" ");
        if (!string.IsNullOrEmpty(maskJobSpecs.CustomCharset2)) argumentsBuilder.Append($"-2 \"{maskJobSpecs.CustomCharset2}\" ");
        if (!string.IsNullOrEmpty(maskJobSpecs.CustomCharset3)) argumentsBuilder.Append($"-3 \"{maskJobSpecs.CustomCharset3}\" ");
        if (!string.IsNullOrEmpty(maskJobSpecs.CustomCharset4)) argumentsBuilder.Append($"-4 \"{maskJobSpecs.CustomCharset4}\" ");
        
        argumentsBuilder.Append($"\"{mask}\"");

        var arguments = argumentsBuilder.ToString();
        var output = await ExecuteHashcatInternalAsync(arguments, captureOutput: true, cancellationToken);

        if (output.Contains("Integer overflow detected", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Integer overflow detected in keyspace of mask. The mask is too large.");
        }

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (ulong.TryParse(lines[i].Trim(), out var keyspaceSize))
            {
                return keyspaceSize;
            }
        }
        throw new Exception($"Failed to parse keyspace size from Hashcat output.\n\nOutput was:\n{output}\n\nThe command: {hashcatPath} {arguments}\n\n");
    }

    public async Task<ulong> GetMaskCandidateCountAsync(HashcatMaskJobSpecs maskJobSpecs, string mask, CancellationToken cancellationToken = default)
    {
        var argumentsBuilder = new StringBuilder();
        argumentsBuilder.Append($"-a {(int)AttackMode.Mask} --total-candidates ");
        if (IsIncrementMode(maskJobSpecs.MinLength, maskJobSpecs.MaxLength))
        {
            argumentsBuilder.Append($"--increment --increment-min={maskJobSpecs.MinLength} --increment-max={maskJobSpecs.MaxLength} ");
        }

        if (!string.IsNullOrEmpty(maskJobSpecs.CustomCharset1)) argumentsBuilder.Append($"-1 \"{maskJobSpecs.CustomCharset1}\" ");
        if (!string.IsNullOrEmpty(maskJobSpecs.CustomCharset2)) argumentsBuilder.Append($"-2 \"{maskJobSpecs.CustomCharset2}\" ");
        if (!string.IsNullOrEmpty(maskJobSpecs.CustomCharset3)) argumentsBuilder.Append($"-3 \"{maskJobSpecs.CustomCharset3}\" ");
        if (!string.IsNullOrEmpty(maskJobSpecs.CustomCharset4)) argumentsBuilder.Append($"-4 \"{maskJobSpecs.CustomCharset4}\" ");
        
        argumentsBuilder.Append($"\"{mask}\"");

        var arguments = argumentsBuilder.ToString();
        var output = await ExecuteHashcatInternalAsync(arguments, captureOutput: true, cancellationToken);

        if (output.Contains("Integer overflow detected", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Integer overflow detected in keyspace of mask. The mask is too large.");
        }

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (ulong.TryParse(lines[i].Trim(), out var candidateCount))
            {
                return candidateCount;
            }
        }
        throw new Exception("Failed to parse total candidates from Hashcat output.");
    }

    private void ParseTelemetryJson(string json)
    {
        try
        {
            //Console.WriteLine($"Received telemetry JSON: {json}");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
            {
                int tempSum = 0, fanSum = 0, utilSum = 0;
                int validTemps = 0, validFans = 0, validUtils = 0;
                long speedSum = 0;

                foreach (var device in devices.EnumerateArray())
                {
                    if (device.TryGetProperty("temp", out var t) && t.ValueKind == JsonValueKind.Number)
                    {
                        var temp = t.GetInt32();
                        if (temp > 0) { tempSum += temp; validTemps++; }
                    }
                    if (device.TryGetProperty("fanspeed", out var f) && f.ValueKind == JsonValueKind.Number)
                    {
                        var fan = f.GetInt32();
                        if (fan >= 0) { fanSum += fan; validFans++; }
                    }
                    if (device.TryGetProperty("util", out var u) && u.ValueKind == JsonValueKind.Number)
                    {
                        var util = u.GetInt32();
                        if (util >= 0) { utilSum += util; validUtils++; }
                    }
                    if (device.TryGetProperty("speed", out var s) && s.ValueKind == JsonValueKind.Number)
                    {
                        speedSum += s.GetInt64();
                    }
                }

                Temperature = validTemps > 0 ? tempSum / validTemps : 0;
                FanSpeed = validFans > 0 ? fanSum / validFans : 0;
                GpuUtilization = validUtils > 0 ? utilSum / validUtils : 0;
                CurrentHashrate = speedSum;

                //Console.WriteLine($"Updated telemetry - Temp: {Temperature}°C, Fan: {FanSpeed}%, Utilization: {GpuUtilization}%, Hashrate: {CurrentHashrate} H/s");
            }

            if (root.TryGetProperty("rejected", out var rej) && rej.ValueKind == JsonValueKind.Number && 
                root.TryGetProperty("progress", out var prog) && prog.ValueKind == JsonValueKind.Array && prog.GetArrayLength() > 0 &&
                prog[0].ValueKind == JsonValueKind.Number)
            {
                var rejected = rej.GetInt64();
                var progress = prog[0].GetInt64();
                RejectRate = progress > 0 ? ((float)rejected / progress) : 0;
            }
        }
        catch { /* Ignore parse errors from malformed JSON strings */ }
    }

    private async Task<string> ExecuteHashcatInternalAsync(string arguments, bool captureOutput, CancellationToken cancellationToken, Action<string>? onLine = null)
    {
        //Console.WriteLine($"{hashcatPath} {arguments}");
        var startInfo = new ProcessStartInfo
        {
            FileName = hashcatPath,
            Arguments = arguments,
            RedirectStandardOutput = true, // Always redirect to intercept telemetry
            RedirectStandardError = true, // Always redirect to prevent buffer deadlocks
            RedirectStandardInput = true, // Crucial: Prevent Hashcat from pausing to ask for terminal input
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (hashcatPath != "hashcat")
        {
            // Hashcat requires the working directory to be set to its installation folder
            // to correctly locate dependencies like ./OpenCL/ and configuration files.
            var workingDirectory = Path.GetDirectoryName(hashcatPath);
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var outputBuilder = captureOutput ? new StringBuilder() : null;
        var outputLock = new object();

        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                if (captureOutput) lock (outputLock) { outputBuilder?.AppendLine(e.Data); }
                onLine?.Invoke(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                if (captureOutput)
                {
                    lock (outputLock) { outputBuilder?.AppendLine(e.Data); }
                    Console.Error.WriteLine($"[Hashcat Error] {e.Data}");
                }
            }
        };

        process.Start();

        process.BeginOutputReadLine(); // Always begin reading to prevent the OS pipe from filling up and blocking
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                // Ensure the process is forcefully terminated if cancelled or stuck
                try { process.Kill(true); } catch { /* Ignore race conditions if it just exited */ }
            }
        }

        return outputBuilder?.ToString() ?? string.Empty;
    }

    public void ResetMetrics()
    {
        Temperature = 0;
        FanSpeed = 0;
        GpuUtilization = 0;
        RejectRate = 0.0f;
        CurrentHashrate = 0;
    }
}