using System.Buffers;

namespace DPCS.Blazor.Services;

public class WordlistService
{
    private readonly string _storagePath;

    private const int BufferSize = 81920; // 80 kB, a common buffer size for file I/O.

    private readonly ActorSystem _actorSystem;

    public WordlistService(IConfiguration configuration, ActorSystem actorSystem)
    {
        _actorSystem = actorSystem;

        // Save wordlists in wwwroot/wordlists so they can be natively served via HTTP later
        _storagePath = configuration.GetValue<string>("WordlistStoragePath")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "wordlists");
        
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
}