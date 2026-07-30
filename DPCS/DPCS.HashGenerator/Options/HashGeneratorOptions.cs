namespace DPCS.HashGenerator.Options;

public sealed class HashGeneratorOptions
{
    public string WordlistsPath { get; init; } = string.Empty;
    public string OutputDir { get; init; } = Path.Combine(Directory.GetCurrentDirectory(), "hash_datasets");
    public int Count { get; init; } = 10_000;
    public string[] HashTypes { get; init; } = ["md5", "bcrypt"];
    public int BcryptWorkFactor { get; init; } = 5;
    public int Threads { get; init; } = Environment.ProcessorCount;
}
