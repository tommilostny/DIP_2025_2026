namespace DPCS.Agent.Services;

/// <summary>
/// Materializes per-job rule files once and reuses them across all chunk executions.
/// </summary>
public sealed class RuleFileStore
{
    private readonly Dictionary<string, string> _jobRuleFiles = [];

    /// <summary>
    /// Creates or refreshes the on-disk rule file for a job from the provided rule content.
    /// </summary>
    public async Task<string?> InitializeJobRulesAsync(string jobId, string? ruleContent, CancellationToken cancellationToken = default)
    {
        var normalizedRules = (ruleContent ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToArray();

        if (normalizedRules.Length == 0)
        {
            CleanupJobData(jobId);
            return null;
        }

        CleanupJobData(jobId);

        var rulePath = Path.Combine(Path.GetTempPath(), $"dpcs_rules_{SanitizeFileName(jobId)}_{Guid.NewGuid():N}.rule");
        await File.WriteAllLinesAsync(rulePath, normalizedRules, cancellationToken);
        _jobRuleFiles[jobId] = rulePath;

        return rulePath;
    }

    /// <summary>
    /// Returns the local rule file path for the job when available.
    /// </summary>
    public string? GetRuleFilePath(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        return _jobRuleFiles.TryGetValue(jobId, out var path) && File.Exists(path)
            ? path
            : null;
    }

    /// <summary>
    /// Deletes job-level rule file and removes its cache entry.
    /// </summary>
    public void CleanupJobData(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        if (_jobRuleFiles.Remove(jobId, out var path) && File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Ignore temporary file cleanup failures.
            }
        }
    }

    private static string SanitizeFileName(string value) => string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
