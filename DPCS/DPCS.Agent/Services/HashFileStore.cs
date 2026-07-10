namespace DPCS.Agent.Services;

/// <summary>
/// Manages per-job hash files used by Hashcat executions on the agent.
/// </summary>
public sealed class HashFileStore
{
    private readonly Dictionary<string, string> _jobHashFilePaths = [];

    /// <summary>
    /// Returns an existing hash file for the job or creates a new one from the provided hashes.
    /// </summary>
    public async Task<string> GetOrCreateHashFileAsync(string jobId, IEnumerable<string> hashes)
    {
        if (_jobHashFilePaths.TryGetValue(jobId, out var path))
        {
            return path;
        }

        var newPath = Path.Combine(Path.GetTempPath(), $"dpcs_{jobId}_{Guid.NewGuid():N}.hashes");
        await File.WriteAllLinesAsync(newPath, hashes);
        _jobHashFilePaths[jobId] = newPath;
        Console.WriteLine($"Created hash file for job {jobId} at {newPath}");

        return newPath;
    }

    /// <summary>
    /// Removes recovered hashes from the in-memory hash set and rewrites the job hash file.
    /// </summary>
    public async Task UpdateHashFileAsync(string jobId, IList<string> hashes, IEnumerable<RecoveredPassword> recoveredPasswords)
    {
        if (!_jobHashFilePaths.TryGetValue(jobId, out var path))
        {
            Console.WriteLine($"No hash file found for job {jobId} when trying to update.");
            return;
        }

        foreach (var pwd in recoveredPasswords)
        {
            hashes.Remove(pwd.Hash);
        }

        await File.WriteAllLinesAsync(path, hashes);
        Console.WriteLine($"Updated hash file for job {jobId} with {hashes.Count} remaining hashes.");
    }

    /// <summary>
    /// Deletes hash file associated with a specific job.
    /// </summary>
    public void CleanupJobData(string? jobId)
    {
        if (jobId is null || !_jobHashFilePaths.Remove(jobId, out var path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Console.WriteLine($"Cleaned up hash file: {path}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error cleaning up hash file {path}: {ex.Message}");
        }
    }
}
