using System.CommandLine;
using DPCS.HashGenerator.Options;

namespace DPCS.HashGenerator.Services;

public static class CliService
{
    public static RootCommand BuildRootCommand()
    {
        var wordlistsOption = new Option<string>("--wordlists")
        {
            Description = "Directory containing the input wordlists.",
            Required = true,
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Output directory for generated datasets.",
            DefaultValueFactory = _ => Path.Combine(Directory.GetCurrentDirectory(), "hash_datasets")
        };

        var countOption = new Option<int>("--count")
        {
            Description = "Number of password samples per dataset.",
            DefaultValueFactory = _ => 10_000
        };

        var hashTypesOption = new Option<string[]>("--hash-types")
        {
            Description = "Comma-separated hash types to generate (md5,bcrypt).",
            DefaultValueFactory = _ => ["md5", "bcrypt"]
        };

        var bcryptWorkFactorOption = new Option<int>("--bcrypt-workfactor")
        {
            Description = "BCrypt work factor.",
            DefaultValueFactory = _ => 5
        };

        var threadsOption = new Option<int>("--threads")
        {
            Description = "Parallel hashing threads.",
            DefaultValueFactory = _ => Environment.ProcessorCount
        };

        var root = new RootCommand("Generate thesis-oriented password hash datasets.")
        {
            wordlistsOption,
            outputOption,
            countOption,
            hashTypesOption,
            bcryptWorkFactorOption,
            threadsOption
        };

        root.SetAction(parseResult =>
        {
            var options = new HashGeneratorOptions
            {
                WordlistsPath = parseResult.GetValue(wordlistsOption)!,
                OutputDir = parseResult.GetValue(outputOption)!,
                Count = parseResult.GetValue(countOption),
                HashTypes = parseResult.GetValue(hashTypesOption)!
                    .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToArray(),
                BcryptWorkFactor = parseResult.GetValue(bcryptWorkFactorOption),
                Threads = parseResult.GetValue(threadsOption)
            };

            return Task.FromResult(0);
        });

        return root;
    }

    public static async Task<HashGeneratorOptions> ParseAsync(string[] args)
    {
        var root = BuildRootCommand();
        var parseResult = root.Parse(args);
        var options = new HashGeneratorOptions
        {
            WordlistsPath = parseResult.GetValue<string>("--wordlists") ?? string.Empty,
            OutputDir = parseResult.GetValue<string>("--output") ?? string.Empty,
            Count = parseResult.GetValue<int>("--count"),
            HashTypes = parseResult.GetValue<string[]>("--hash-types")?
                .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray() ?? ["md5", "bcrypt"],
            BcryptWorkFactor = parseResult.GetValue<int>("--bcrypt-workfactor"),
            Threads = parseResult.GetValue<int>("--threads")
        };

        if (string.IsNullOrWhiteSpace(options.WordlistsPath))
        {
            throw new InvalidOperationException("Wordlists path is required.");
        }

        return options;
    }
}
