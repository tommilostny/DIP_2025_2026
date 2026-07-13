using System.Buffers;
using System.Security.Cryptography;

namespace DPCS.Blazor.Services;

public class WordlistService
{
    private readonly string _storagePath;

    private const int BufferSize = 81920; // 80 kB, a common buffer size for file I/O.

    private readonly ActorSystem _actorSystem;

    public WordlistService(IConfiguration configuration, IWebHostEnvironment environment, ActorSystem actorSystem)
    {
        _actorSystem = actorSystem;

        // Store wordlists under the application's web root so StaticFiles can serve them at runtime.
        _storagePath = configuration.GetValue<string>("WordlistStoragePath")
            ?? Path.Combine(environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "wordlists");
        
        if (!Directory.Exists(_storagePath))
        {
            Directory.CreateDirectory(_storagePath);
        }
    }

    public async Task UploadWordlistAsync(string fileName, Stream fileStream, bool canOverwrite)
    {
        var filePath = Path.Combine(_storagePath, fileName);
        var indexPath = Path.Combine(_storagePath, $"{fileName}.idx");

        if (File.Exists(filePath))
        {
            if (canOverwrite)
            {
                var _jobManager = _actorSystem.Cluster().GetJobManagerGrain("root");
                var response = await _jobManager.IsWordlistInUse(new WordlistQuery { WordlistName = fileName }, CancellationToken.None);
                if (response is null or { IsInUse: true })
                {
                    throw new InvalidOperationException($"Wordlist '{fileName}' is currently in use by an active job and cannot be overwritten.");
                }
                File.Delete(filePath);
                if (File.Exists(indexPath))
                {
                    File.Delete(indexPath);
                }
            }
            else
            {
                throw new InvalidOperationException($"Wordlist '{fileName}' already exists. To overwrite, set the checkbox above and try again.");
            }
        }

        // Rent a buffer instead of allocating a new one to prevent GC pressure.
        // Note: The rented array might be slightly larger than requested.
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        int bytesRead;
        long currentOffset = 0;
        long lineCount = 0;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
            using var indexFs = new FileStream(indexPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
            using var indexWriter = new BinaryWriter(indexFs);

            // The first chunk always starts at byte 0 (Line 0)
            indexWriter.Write(0L);

            while ((bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, BufferSize))) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, bytesRead));

                for (int i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == '\n' && ++lineCount % Constants.IndexInterval == 0)
                    {
                        // Record the byte offset immediately following the newline character
                        indexWriter.Write(currentOffset + i + 1);
                    }
                }
                currentOffset += bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public IEnumerable<string> GetAvailableWordlists()
    {
        if (!Directory.Exists(_storagePath)) return [];
        // Return only .txt files, ignoring the binary .idx files
        return Directory.GetFiles(_storagePath, "*.txt").Select(Path.GetFileName)!;
    }

    public void DeleteWordlist(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        var filePath = Path.Combine(_storagePath, safeFileName);
        var indexPath = Path.Combine(_storagePath, $"{safeFileName}.idx");

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        if (File.Exists(indexPath))
        {
            File.Delete(indexPath);
        }
    }

    public long GetWordlistFileSize(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        var filePath = Path.Combine(_storagePath, safeFileName);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Wordlist '{fileName}' not found.");
        }

        return new FileInfo(filePath).Length;
    }

    public async Task<string> GetWordlistRangeChecksumAsync(string fileName, long startByte, long endByte, CancellationToken cancellationToken = default)
    {
        var safeFileName = Path.GetFileName(fileName);
        var filePath = Path.Combine(_storagePath, safeFileName);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Wordlist '{fileName}' not found.");
        }

        if (startByte < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startByte), "startByte must be non-negative.");
        }

        if (endByte != -1 && endByte < startByte)
        {
            throw new ArgumentOutOfRangeException(nameof(endByte), "endByte must be -1 (EOF) or greater than or equal to startByte.");
        }

        var fileInfo = new FileInfo(filePath);
        if (startByte >= fileInfo.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startByte), "startByte must be within file bounds.");
        }

        var effectiveEndByte = endByte == -1 ? fileInfo.Length - 1 : Math.Min(endByte, fileInfo.Length - 1);
        var bytesRemaining = effectiveEndByte - startByte + 1;

        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            fileStream.Seek(startByte, SeekOrigin.Begin);

            while (bytesRemaining > 0)
            {
                var bytesToRead = (int)Math.Min(buffer.Length, bytesRemaining);
                var bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                incrementalHash.AppendData(buffer, 0, bytesRead);
                bytesRemaining -= bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(incrementalHash.GetHashAndReset()).ToLowerInvariant();
    }
}