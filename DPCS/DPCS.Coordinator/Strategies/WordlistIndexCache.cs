using System.Runtime.InteropServices;

namespace DPCS.Coordinator.Strategies;

/// <summary>
/// Shared cache for downloaded wordlist index files used by dictionary, combinator,
/// and hybrid scheduling strategies.
/// </summary>
internal sealed class WordlistIndexCache(string jobId, string serverBaseUrl)
{
    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<string, string> _cachedIndexFiles = [];
    private string? _jobCacheDirectory;

    public async Task InitializeAsync(IEnumerable<string> wordlistNames)
    {
        _jobCacheDirectory = Path.Combine(Path.GetTempPath(), "dpcs-coordinator-indexes", SanitizeFileName(jobId));
        Directory.CreateDirectory(_jobCacheDirectory);

        foreach (var wordlistName in wordlistNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cachePath = Path.Combine(_jobCacheDirectory, $"{SanitizeFileName(wordlistName)}.idx");
            var idxUrl = $"{serverBaseUrl}/wordlists/{wordlistName}.idx";
            using var response = await _httpClient.GetAsync(idxUrl);
            response.EnsureSuccessStatusCode();
            await File.WriteAllBytesAsync(cachePath, await response.Content.ReadAsByteArrayAsync());
            _cachedIndexFiles[wordlistName] = cachePath;
        }
    }

    public async Task<long[]> LoadIndexDataAsync(string wordlistName)
    {
        var cachePath = _cachedIndexFiles[wordlistName];
        var bytes = await File.ReadAllBytesAsync(cachePath);
        return MemoryMarshal.Cast<byte, long>(bytes).ToArray();
    }

    public bool TryGetCachedIndexPath(string wordlistName, out string path)
    {
        return _cachedIndexFiles.TryGetValue(wordlistName, out path!);
    }

    public void Cleanup()
    {
        if (!string.IsNullOrWhiteSpace(_jobCacheDirectory) && Directory.Exists(_jobCacheDirectory))
        {
            Directory.Delete(_jobCacheDirectory, recursive: true);
        }

        _cachedIndexFiles.Clear();
        _jobCacheDirectory = null;
    }

    private static string SanitizeFileName(string value) => string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}