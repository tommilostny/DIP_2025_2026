using System.Buffers;

namespace DPCS.Blazor.Services;

public class WordlistService
{
    private readonly string _storagePath;

    // Track byte offset every 10,000 lines - ballancing the index file size with the granularity of chunking the wordlist.
    /*
    Example: massive wordlist with 1 billion lines (roughly 10 GB of text).
             Because our binary index writes one long (8 bytes) per interval:
        
        Interval = 100,000: The index has 10,000 entries. File size = ~80 KB.
        (Minimum chunk size: 100,000 lines)
        Interval = 10,000: The index has 100,000 entries. File size = ~800 KB.
        (Minimum chunk size: 10,000 lines)
        Interval = 1,000: The index has 1,000,000 entries. File size = ~8 MB.
        (Minimum chunk size: 1,000 lines)
        Interval = 1: The index has 1,000,000,000 entries. File size = 8 GB.
        (This completely defeats the purpose of the index).
    */
    public const int IndexInterval = 10000;

    private const int BufferSize = 81920; // 80 kB, a common buffer size for file I/O.

    public WordlistService(IConfiguration configuration)
    {
        // Save wordlists in wwwroot/wordlists so they can be natively served via HTTP later
        _storagePath = configuration.GetValue<string>("WordlistStoragePath")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "wordlists");
        
        if (!Directory.Exists(_storagePath))
        {
            Directory.CreateDirectory(_storagePath);
        }
    }

    public async Task UploadWordlistAsync(string fileName, Stream fileStream)
    {
        var filePath = Path.Combine(_storagePath, fileName);
        var indexPath = Path.Combine(_storagePath, $"{fileName}.idx");

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
                    if (buffer[i] == '\n' && ++lineCount % IndexInterval == 0)
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
}