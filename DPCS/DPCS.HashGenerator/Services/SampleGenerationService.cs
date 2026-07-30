using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DPCS.HashGenerator.Models;
using DPCS.HashGenerator.Options;

namespace DPCS.HashGenerator.Services;

public sealed class SampleGenerationService(HashGeneratorOptions options)
{
    private readonly HashGeneratorOptions _options = options;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void GenerateAll()
    {
        if (!Directory.Exists(_options.WordlistsPath))
        {
            throw new DirectoryNotFoundException($"The provided wordlists directory does not exist: '{_options.WordlistsPath}'");
        }

        Directory.CreateDirectory(_options.OutputDir);
        Console.WriteLine($"Outputting datasets and manifests to: {_options.OutputDir}");
        Console.WriteLine($"Target sample size per dataset: {_options.Count}");
        Console.WriteLine($"Hash types: {string.Join(", ", _options.HashTypes)} (BCrypt Work Factor: {_options.BcryptWorkFactor})");
        Console.WriteLine($"Parallel threads: {_options.Threads}\n");

        var rockyouPath = Path.Combine(_options.WordlistsPath, "rockyou.txt");
        var largeWordlistPath = Path.Combine(_options.WordlistsPath, "weakpass_4.policy.txt");
        if (!File.Exists(largeWordlistPath))
        {
            largeWordlistPath = Path.Combine(_options.WordlistsPath, "large_wordlist.txt");
        }

        if (!File.Exists(rockyouPath))
        {
            throw new FileNotFoundException($"rockyou.txt not found at '{rockyouPath}'");
        }

        Console.WriteLine("--- [Scenario A1] Dictionary Attack without Rules ---");
        var dictSmallSamples = ReservoirSampler.SampleFile(rockyouPath, _options.Count).ToList();
        GenerateDatasetSuite("dict_rockyou_norules", dictSmallSamples, 0, new DictionaryManifestSpecs
        {
            Wordlists = ["rockyou.txt"]
        });

        Console.WriteLine("\n--- [Scenario A2] Dictionary Attack with Rules (best64.rule simulation) ---");
        var ruleBaseWords = ReservoirSampler.SampleFile(rockyouPath, _options.Count).ToList();
        var ruleTransformed = RuleSimulationEngine.ApplyRules(ruleBaseWords).ToList();
        GenerateDatasetSuite("dict_rockyou_best64_rules", ruleTransformed, 0, new DictionaryManifestSpecs
        {
            Wordlists = ["rockyou.txt"],
            RuleFileContent = "c\nC\n$\n^!\n^@\ns/a/@/\ns/e/3/\ns/o/0/\ns/s/$/\nd"
        });

        if (File.Exists(largeWordlistPath))
        {
            Console.WriteLine($"\n--- [Scenario C1] Large Endurance Dictionary Attack ({Path.GetFileName(largeWordlistPath)}) ---");
            var dictLargeSamples = ReservoirSampler.SampleFile(largeWordlistPath, _options.Count).ToList();
            GenerateDatasetSuite("dict_weakpass_large", dictLargeSamples, 0, new DictionaryManifestSpecs
            {
                Wordlists = [Path.GetFileName(largeWordlistPath)]
            });
        }
        else
        {
            Console.WriteLine($"\nWarning: Large wordlist ('weakpass_4.policy.txt' or 'large_wordlist.txt') not found in '{_options.WordlistsPath}'. Skipping Scenario C1.");
        }

        Console.WriteLine("\n--- [Scenario A3.1] Mask Attack - Simple Length 6 Lowercase ---");
        var maskSimpleWords = MaskGeneratorEngine.GenerateMatching("?l?l?l?l?l?l", _options.Count).ToList();
        GenerateDatasetSuite("mask_simple_len6", maskSimpleWords, 3, new MaskManifestSpecs
        {
            Masks = ["?l?l?l?l?l?l"]
        });

        Console.WriteLine("\n--- [Scenario A3.2] Mask Attack - Complex Upper/Lower/Digits/Specials ---");
        var maskComplexWords = MaskGeneratorEngine.GenerateMatching("?u?l?l?l?d?s", _options.Count).ToList();
        GenerateDatasetSuite("mask_complex_u_l_l_l_d_s", maskComplexWords, 3, new MaskManifestSpecs
        {
            Masks = ["?u?l?l?l?d?s"]
        });

        Console.WriteLine("\n--- [Scenario A3.3] Mask Attack - Custom Hex Charset (?1 = ?l?d) ---");
        var maskCustomWords = MaskGeneratorEngine.GenerateMatching("?1?1?1?1?1?1", _options.Count, customCharset1: "?l?d").ToList();
        GenerateDatasetSuite("mask_custom_hex", maskCustomWords, 3, new MaskManifestSpecs
        {
            Masks = ["?1?1?1?1?1?1"],
            CustomCharset1 = "?l?d"
        });

        Console.WriteLine("\n--- [Scenario A3.4] Mask Attack - Iteration Mode (Length 4 to 8) ---");
        var maskIterWords = MaskGeneratorEngine.GenerateIncrementing("?l?l?l?l?l?l?l?l", _options.Count, minLen: 4, maxLen: 8).ToList();
        GenerateDatasetSuite("mask_increment_len4_to_8", maskIterWords, 3, new MaskManifestSpecs
        {
            Masks = ["?l?l?l?l?l?l?l?l"],
            MinLength = 4,
            MaxLength = 8
        });

        Console.WriteLine("\n--- [Scenario A4] Combinator Attack (rockyou left + right) ---");
        var combinatorWords = CombinatorEngine.GenerateCombinations(rockyouPath, _options.Count).ToList();
        GenerateDatasetSuite("combinator_rockyou_pairs", combinatorWords, 1, new CombinatorManifestSpecs
        {
            LeftWordlists = ["rockyou.txt"],
            RightWordlists = ["rockyou.txt"]
        });

        Console.WriteLine("\n--- [Scenario A5.1] Hybrid Mode 6 (Wordlist + Mask ?d?d!@) ---");
        var hybrid6Words = HybridEngine.GenerateWordlistMask(rockyouPath, "?d?d!@", _options.Count).ToList();
        GenerateDatasetSuite("hybrid_mode6_word_mask", hybrid6Words, 6, new HybridManifestSpecs
        {
            Wordlists = ["rockyou.txt"],
            Masks = ["?d?d!@"],
            AttackMode = 6
        });

        Console.WriteLine("\n--- [Scenario A5.2] Hybrid Mode 7 (Mask 2026?d?d + Wordlist) ---");
        var hybrid7Words = HybridEngine.GenerateMaskWordlist("2026?d?d", rockyouPath, _options.Count).ToList();
        GenerateDatasetSuite("hybrid_mode7_mask_word", hybrid7Words, 7, new HybridManifestSpecs
        {
            Wordlists = ["rockyou.txt"],
            Masks = ["2026?d?d"],
            AttackMode = 7
        });

        Console.WriteLine("All Thesis Datasets & Manifests Successfully Generated");
    }

    private void GenerateDatasetSuite(string baseName, IReadOnlyList<PasswordSample> samples, int attackMode, object modeSpecs)
    {
        foreach (var hashType in _options.HashTypes)
        {
            var hashName = hashType.Trim().ToLowerInvariant();
            var hashCatType = hashName == "md5" ? 0 : hashName == "bcrypt" ? 3200 : 0;
            var fileNameBase = $"{baseName}_{hashName}";

            Console.WriteLine($"  [{hashName.ToUpperInvariant()}] Hashing {samples.Count} items using {_options.Threads} threads...");

            var hashedResults = new ConcurrentBag<HashedSample>();
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _options.Threads };

            Parallel.ForEach(samples, parallelOptions, sample =>
            {
                var hash = hashName switch
                {
                    "md5" => HashMD5(sample.Plaintext),
                    "bcrypt" => HashBCrypt(sample.Plaintext, _options.BcryptWorkFactor),
                    _ => HashMD5(sample.Plaintext)
                };

                hashedResults.Add(new HashedSample(sample.Plaintext, hash, sample.SourceRuleOrMask));
            });

            var orderedResults = hashedResults.ToList();
            var txtPath = Path.Combine(_options.OutputDir, $"{fileNameBase}.txt");
            File.WriteAllLines(txtPath, orderedResults.Select(r => r.Hash));
            Console.WriteLine($"    -> Hash list: {txtPath}");

            var csvPath = Path.Combine(_options.OutputDir, $"{fileNameBase}.truth.csv");
            using var streamWriter = new StreamWriter(csvPath, false, Encoding.UTF8);
            streamWriter.WriteLine("Hash,Plaintext,AttackMode,SourceRuleOrMask");
            foreach (var result in orderedResults)
            {
                var escapedRule = result.SourceRuleOrMask.Replace("\"", "\"\"");
                streamWriter.WriteLine($"{result.Hash},{result.Plaintext},{attackMode},\"{escapedRule}\"");
            }

            Console.WriteLine($"    -> Truth CSV: {csvPath}");

            var manifestPath = Path.Combine(_options.OutputDir, $"{fileNameBase}_manifest.json");
            var manifest = new
            {
                hashes = orderedResults.Select(r => r.Hash).ToArray(),
                chunk_time_seconds = 30,
                hash_type = hashCatType,
                mask_job_specs = attackMode == 3 ? modeSpecs : null,
                dictionary_job_specs = attackMode == 0 ? modeSpecs : null,
                combinator_job_specs = attackMode == 1 ? modeSpecs : null,
                association_job_specs = attackMode == 9 ? modeSpecs : null,
                hybrid_job_specs = attackMode is 6 or 7 ? modeSpecs : null
            };

            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, _jsonOptions));
            Console.WriteLine($"    -> Manifest:  {manifestPath}");
        }
    }

    private static string HashMD5(string password)
    {
        var inputBytes = Encoding.UTF8.GetBytes(password);
        var hashBytes = MD5.HashData(inputBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string HashBCrypt(string password, int workFactor)
    {
        return BCrypt.Net.BCrypt.HashPassword(password, workFactor);
    }
}
