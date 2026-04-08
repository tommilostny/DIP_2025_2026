namespace DPCS.Shared;

public static class Constants
{
    public const string ClusterName = "DistributedPasswordCrackingSystem";
    public const string JobManagerGrainIdentity = "root";

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
    public const int IndexInterval = 10_000;

    public const int DefaultChunkTimeSeconds = 30;
}