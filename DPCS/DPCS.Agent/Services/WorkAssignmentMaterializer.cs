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
                envelope.DictionaryAssignment.WordlistUrl = await DownloadFileRangeChunkAsync(
                    envelope.RequestId,
                    envelope.DictionaryAssignment.WordlistUrl,
                    envelope.DictionaryAssignment.StartByte,
                    envelope.DictionaryAssignment.EndByte,
                    "dictionary",
                    expectedChecksum: envelope.DictionaryAssignment.WordlistChunkChecksum);
                return envelope;

            case { PayloadCase: WorkAssignmentEnvelope.PayloadOneofCase.CombinatorAssignment }:
                var leftCacheKey = GetCacheKey(
                    envelope.CombinatorAssignment.LeftWordlistName,
                    envelope.CombinatorAssignment.LeftStartByte,
                    envelope.CombinatorAssignment.LeftEndByte);

                envelope.CombinatorAssignment.LeftWordlistUrl = await DownloadFileRangeChunkAsync(
                    envelope.RequestId,
                    envelope.CombinatorAssignment.LeftWordlistUrl,
                    envelope.CombinatorAssignment.LeftStartByte,
                    envelope.CombinatorAssignment.LeftEndByte,
                    "left",
                    cacheKey: leftCacheKey,
                    useCache: true,
                    expectedChecksum: envelope.CombinatorAssignment.LeftWordlistChunkChecksum);

                envelope.CombinatorAssignment.RightWordlistUrl = await DownloadFileRangeChunkAsync(
                    envelope.RequestId,
                    envelope.CombinatorAssignment.RightWordlistUrl,
                    envelope.CombinatorAssignment.RightStartByte,
                    envelope.CombinatorAssignment.RightEndByte,
                    "right",
                    expectedChecksum: envelope.CombinatorAssignment.RightWordlistChunkChecksum);

                Console.WriteLine($"Prepared combinator assignment {envelope.RequestId} with local left/right chunk files.");
                return envelope;

            case { PayloadCase: WorkAssignmentEnvelope.PayloadOneofCase.AssociationAssignment }:
                envelope.AssociationAssignment.WordlistUrl = await DownloadFileRangeChunkAsync(
                    envelope.RequestId,
                    envelope.AssociationAssignment.WordlistUrl,
                    envelope.AssociationAssignment.StartByte,
                    envelope.AssociationAssignment.EndByte,
                    "association",
                    cacheKey: GetCacheKey(
                        envelope.AssociationAssignment.WordlistName,
                        envelope.AssociationAssignment.StartByte,
                        envelope.AssociationAssignment.EndByte),
                    useCache: true,
                    expectedChecksum: envelope.AssociationAssignment.WordlistChunkChecksum);

                Console.WriteLine($"Prepared association assignment {envelope.RequestId} with a local dictionary file.");
                return envelope;

            case { PayloadCase: WorkAssignmentEnvelope.PayloadOneofCase.HybridAssignment }:
                envelope.HybridAssignment.WordlistUrl = await DownloadFileRangeChunkAsync(
                    envelope.RequestId,
                    envelope.HybridAssignment.WordlistUrl,
                    envelope.HybridAssignment.StartByte,
                    envelope.HybridAssignment.EndByte,
                    "hybrid",
                    cacheKey: GetCacheKey(
                        envelope.HybridAssignment.WordlistName,
                        envelope.HybridAssignment.StartByte,
                        envelope.HybridAssignment.EndByte),
                    useCache: true,
                    expectedChecksum: envelope.HybridAssignment.WordlistChunkChecksum);

                Console.WriteLine($"Prepared hybrid assignment {envelope.RequestId} with a local dictionary chunk file.");
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
                TryDeleteLocalFile(chunk.DictionaryAssignment.WordlistUrl);
                break;
            case WorkAssignmentEnvelope.PayloadOneofCase.AssociationAssignment:
                TryDeleteLocalFile(chunk.AssociationAssignment.WordlistUrl);
                break;
            case WorkAssignmentEnvelope.PayloadOneofCase.CombinatorAssignment:
                if (!IsCachedCombinatorLeftPath(chunk.CombinatorAssignment.LeftWordlistUrl))
                {
                    TryDeleteLocalFile(chunk.CombinatorAssignment.LeftWordlistUrl);
                }

                TryDeleteLocalFile(chunk.CombinatorAssignment.RightWordlistUrl);
                break;
            case WorkAssignmentEnvelope.PayloadOneofCase.HybridAssignment:
                TryDeleteLocalFile(chunk.HybridAssignment.WordlistUrl);
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

    private async Task<string> DownloadFileRangeChunkAsync(
        string requestId,
        string wordlistUrl,
        long startByte,
        long endByte,
        string label,
        string? cacheKey = null,
        bool useCache = false,
        string? expectedChecksum = null)
    {
        if (useCache && cacheKey is not null && _combinatorLeftChunkCache.TryGetValue(cacheKey, out var cachedPath) && File.Exists(cachedPath))
        {
            if (string.IsNullOrWhiteSpace(expectedChecksum))
            {
                Console.WriteLine($"Reusing cached {label} chunk: {requestId} ({Path.GetFileName(cachedPath)})");
                return cachedPath;
            }

            var cachedChecksum = await ComputeFileSha256Async(cachedPath);
            if (string.Equals(cachedChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Reusing cached {label} chunk: {requestId} ({Path.GetFileName(cachedPath)})");
                return cachedPath;
            }

            _combinatorLeftChunkCache.Remove(cacheKey);
            _combinatorLeftCachedPaths.Remove(cachedPath);
            TryDeleteLocalFile(cachedPath);
        }

        if (useCache && cacheKey is not null && _combinatorLeftChunkCache.TryGetValue(cacheKey, out var stalePath) && !File.Exists(stalePath))
        {
            _combinatorLeftChunkCache.Remove(cacheKey);
            _combinatorLeftCachedPaths.Remove(stalePath);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, wordlistUrl);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startByte, endByte == -1 ? null : endByte);

        Console.WriteLine($"Downloading {label} chunk: {requestId} (Bytes: {startByte}-{(endByte == -1 ? "EOF" : endByte)})");

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var chunkPath = Path.Combine(Path.GetTempPath(), $"dpcs_{label}_{requestId}_{Guid.NewGuid():N}.txt");
        await using (var fileStream = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await response.Content.CopyToAsync(fileStream);
        }

        if (!string.IsNullOrWhiteSpace(expectedChecksum))
        {
            var actualChecksum = await ComputeFileSha256Async(chunkPath);
            if (!string.Equals(actualChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteLocalFile(chunkPath);
                throw new InvalidDataException($"Checksum mismatch for {label} chunk {requestId}. Expected {expectedChecksum}, got {actualChecksum}.");
            }
        }

        if (useCache && cacheKey is not null)
        {
            _combinatorLeftChunkCache[cacheKey] = chunkPath;
            _combinatorLeftCachedPaths.Add(chunkPath);
        }

        return chunkPath;
    }

    private static async Task<string> ComputeFileSha256Async(string path)
    {
        Console.WriteLine($"Computing SHA256 for file: {path}");
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hashBytes = await sha256.ComputeHashAsync(stream);
        var hexString = Convert.ToHexString(hashBytes).ToLowerInvariant();
        Console.WriteLine($"SHA256 for file {path}: {hexString}");
        return hexString;
    }

    private static string GetCacheKey(string wordlistName, long startByte, long endByte)
    {
        return $"{wordlistName}|{startByte}|{endByte}";
    }

    private bool IsCachedCombinatorLeftPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && _combinatorLeftCachedPaths.Contains(path);
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
