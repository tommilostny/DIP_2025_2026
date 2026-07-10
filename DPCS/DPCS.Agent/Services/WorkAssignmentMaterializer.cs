namespace DPCS.Agent.Services;

/// <summary>
/// Materializes coordinator assignments into local executable inputs by downloading ranged files
/// and reusing cached combinator left slices when possible.
/// </summary>
public sealed class WorkAssignmentMaterializer
{
    private static readonly HttpClient HttpClient = new();

    private readonly Dictionary<string, string> _combinatorLeftChunkCache = [];
    private readonly HashSet<string> _combinatorLeftCachedPaths = [];

    /// <summary>
    /// Converts remote chunk URLs into local temporary files for execution.
    /// </summary>
    public async Task<WorkAssignmentEnvelope?> MaterializeAsync(WorkAssignmentEnvelope? envelope)
    {
        switch (envelope)
        {
            case null:
            case { PayloadCase: WorkAssignmentEnvelope.PayloadOneofCase.None }:
                return null;

            case { PayloadCase: WorkAssignmentEnvelope.PayloadOneofCase.MaskAssignment }:
                return envelope;

            case { PayloadCase: WorkAssignmentEnvelope.PayloadOneofCase.DictionaryAssignment }:
                envelope.DictionaryAssignment.DictionaryChunkUrl = await DownloadFileRangeChunkAsync(
                    envelope.RequestId,
                    envelope.DictionaryAssignment.DictionaryChunkUrl,
                    "dictionary");
                return envelope;

            case { PayloadCase: WorkAssignmentEnvelope.PayloadOneofCase.CombinatorAssignment }:
                var leftCacheKey = GetCombinatorLeftCacheKey(envelope.CombinatorAssignment);
                envelope.CombinatorAssignment.LeftDictionaryChunkUrl = await DownloadFileRangeChunkAsync(
                    envelope.RequestId,
                    envelope.CombinatorAssignment.LeftDictionaryChunkUrl,
                    "left",
                    cacheKey: leftCacheKey,
                    useCache: true);

                envelope.CombinatorAssignment.RightDictionaryChunkUrl = await DownloadFileRangeChunkAsync(
                    envelope.RequestId,
                    envelope.CombinatorAssignment.RightDictionaryChunkUrl,
                    "right");

                Console.WriteLine($"Prepared combinator assignment {envelope.RequestId} with local left/right chunk files.");
                return envelope;

            default:
                return null;
        }
    }

    /// <summary>
    /// Deletes temporary local files associated with a materialized chunk.
    /// </summary>
    public void CleanupAssignmentFiles(WorkAssignmentEnvelope chunk)
    {
        switch (chunk.PayloadCase)
        {
            case WorkAssignmentEnvelope.PayloadOneofCase.DictionaryAssignment:
                TryDeleteLocalFile(chunk.DictionaryAssignment.DictionaryChunkUrl);
                break;
            case WorkAssignmentEnvelope.PayloadOneofCase.CombinatorAssignment:
                if (!IsCachedCombinatorLeftPath(chunk.CombinatorAssignment.LeftDictionaryChunkUrl))
                {
                    TryDeleteLocalFile(chunk.CombinatorAssignment.LeftDictionaryChunkUrl);
                }

                TryDeleteLocalFile(chunk.CombinatorAssignment.RightDictionaryChunkUrl);
                break;
        }
    }

    /// <summary>
    /// Clears reusable combinator-left cache files for the current job lifecycle.
    /// </summary>
    public void CleanupJobCache()
    {
        foreach (var cachedPath in _combinatorLeftCachedPaths)
        {
            TryDeleteLocalFile(cachedPath);
        }

        _combinatorLeftCachedPaths.Clear();
        _combinatorLeftChunkCache.Clear();
    }

    private async Task<string> DownloadFileRangeChunkAsync(string requestId, string chunkUrlWithQuery, string label, string? cacheKey = null, bool useCache = false)
    {
        var uri = new Uri(chunkUrlWithQuery);
        var queryParams = uri.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('='))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

        long startByte = long.Parse(queryParams["startByte"]);
        long endByte = long.Parse(queryParams["endByte"]);
        var cleanUrl = uri.GetLeftPart(UriPartial.Path);

        if (useCache && cacheKey is not null && _combinatorLeftChunkCache.TryGetValue(cacheKey, out var cachedPath) && File.Exists(cachedPath))
        {
            Console.WriteLine($"Reusing cached {label} chunk: {requestId} ({Path.GetFileName(cachedPath)})");
            return cachedPath;
        }

        if (useCache && cacheKey is not null && _combinatorLeftChunkCache.TryGetValue(cacheKey, out var stalePath) && !File.Exists(stalePath))
        {
            _combinatorLeftChunkCache.Remove(cacheKey);
            _combinatorLeftCachedPaths.Remove(stalePath);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, cleanUrl);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startByte, endByte == -1 ? null : endByte);

        Console.WriteLine($"Downloading {label} chunk: {requestId} (Bytes: {startByte}-{(endByte == -1 ? "EOF" : endByte)})");

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var chunkPath = Path.Combine(Path.GetTempPath(), $"dpcs_{label}_{requestId}_{Guid.NewGuid():N}.txt");
        using var fileStream = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fileStream);

        if (useCache && cacheKey is not null)
        {
            _combinatorLeftChunkCache[cacheKey] = chunkPath;
            _combinatorLeftCachedPaths.Add(chunkPath);
        }

        return chunkPath;
    }

    private bool IsCachedCombinatorLeftPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && _combinatorLeftCachedPaths.Contains(path);
    }

    private static string GetCombinatorLeftCacheKey(CombinatorWorkAssignment assignment)
    {
        return $"{assignment.LeftWordlistName}|{assignment.LeftStartByte}|{assignment.LeftEndByte}";
    }

    private static void TryDeleteLocalFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

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
