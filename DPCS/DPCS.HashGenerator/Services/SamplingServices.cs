using System.Text;
using DPCS.HashGenerator.Models;

namespace DPCS.HashGenerator.Services;

public static class ReservoirSampler
{
    public static IEnumerable<PasswordSample> SampleFile(string filePath, int count)
    {
        var reservoir = new List<string>(count);
        var random = new Random();
        long linesRead = 0;

        using var reader = new StreamReader(filePath, Encoding.UTF8, true, 65_536);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 64)
            {
                continue;
            }

            linesRead++;
            if (reservoir.Count < count)
            {
                reservoir.Add(trimmed);
            }
            else
            {
                var replacementIndex = random.Next((int)linesRead);
                if (replacementIndex < count)
                {
                    reservoir[replacementIndex] = trimmed;
                }
            }
        }

        return reservoir.Select(word => new PasswordSample(word, "None/Straight"));
    }
}

public static class RuleSimulationEngine
{
    private static readonly string[] RuleNames = ["c", "C", "$!", "$0", "^@", "s/a/@/", "s/e/3/", "s/o/0/", "s/s/$/", "d"];

    public static IEnumerable<PasswordSample> ApplyRules(IEnumerable<PasswordSample> baseSamples)
    {
        var random = new Random();
        foreach (var sample in baseSamples)
        {
            var word = sample.Plaintext;
            if (string.IsNullOrWhiteSpace(word))
            {
                continue;
            }

            var rule = RuleNames[random.Next(RuleNames.Length)];
            var transformed = rule switch
            {
                "c" => word.Length > 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word,
                "C" => word.ToUpperInvariant(),
                "$!" => word + "!",
                "$0" => word + "0",
                "^@" => "@" + word,
                "s/a/@/" => word.Replace('a', '@').Replace('A', '@'),
                "s/e/3/" => word.Replace('e', '3').Replace('E', '3'),
                "s/o/0/" => word.Replace('o', '0').Replace('O', '0'),
                "s/s/$/" => word.Replace('s', '$').Replace('S', '$'),
                "d" => word + word,
                _ => word
            };

            yield return new PasswordSample(transformed, rule);
        }
    }
}

public static class MaskGeneratorEngine
{
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digits = "0123456789";
    private const string Specials = "!@#$%^&*()-_+=~";

    public static IEnumerable<PasswordSample> GenerateMatching(string mask, int count, string? customCharset1 = null)
    {
        var random = new Random();
        var tokens = ParseMask(mask);
        var customSet = ResolveCustomSet(customCharset1);

        for (var index = 0; index < count; index++)
        {
            var builder = new StringBuilder();
            foreach (var token in tokens)
            {
                var source = token switch
                {
                    "?l" => Lower,
                    "?u" => Upper,
                    "?d" => Digits,
                    "?s" => Specials,
                    "?a" => Lower + Upper + Digits + Specials,
                    "?1" => customSet,
                    _ => token
                };

                builder.Append(source[random.Next(source.Length)]);
            }

            yield return new PasswordSample(builder.ToString(), mask);
        }
    }

    public static IEnumerable<PasswordSample> GenerateIncrementing(string baseMask, int count, int minLen, int maxLen)
    {
        var random = new Random();
        var tokens = ParseMask(baseMask);

        for (var index = 0; index < count; index++)
        {
            var length = random.Next(minLen, Math.Min(maxLen, tokens.Count) + 1);
            var builder = new StringBuilder();
            for (var tokenIndex = 0; tokenIndex < length; tokenIndex++)
            {
                var source = tokens[tokenIndex] switch
                {
                    "?l" => Lower,
                    "?u" => Upper,
                    "?d" => Digits,
                    "?s" => Specials,
                    _ => Lower
                };

                builder.Append(source[random.Next(source.Length)]);
            }

            yield return new PasswordSample(builder.ToString(), $"Increment({minLen}-{maxLen}): {string.Join(string.Empty, tokens.Take(length))}");
        }
    }

    private static List<string> ParseMask(string mask)
    {
        var tokens = new List<string>();
        for (var index = 0; index < mask.Length; index++)
        {
            if (mask[index] == '?' && index + 1 < mask.Length)
            {
                tokens.Add(mask.Substring(index, 2));
                index++;
            }
            else
            {
                tokens.Add(mask[index].ToString());
            }
        }

        return tokens;
    }

    private static string ResolveCustomSet(string? customDef)
    {
        if (string.IsNullOrEmpty(customDef))
        {
            return Lower + Digits;
        }

        var builder = new StringBuilder();
        foreach (var token in ParseMask(customDef))
        {
            builder.Append(token switch
            {
                "?l" => Lower,
                "?u" => Upper,
                "?d" => Digits,
                "?s" => Specials,
                _ => token
            });
        }

        return builder.ToString();
    }
}

public static class CombinatorEngine
{
    public static IEnumerable<PasswordSample> GenerateCombinations(string wordlistPath, int count)
    {
        var left = ReservoirSampler.SampleFile(wordlistPath, count).ToList();
        var right = ReservoirSampler.SampleFile(wordlistPath, count).ToList();
        var random = new Random();

        for (var index = 0; index < count; index++)
        {
            var first = left[random.Next(left.Count)].Plaintext;
            var second = right[random.Next(right.Count)].Plaintext;
            yield return new PasswordSample(first + second, $"Combinator({first} + {second})");
        }
    }
}

public static class HybridEngine
{
    public static IEnumerable<PasswordSample> GenerateWordlistMask(string wordlistPath, string mask, int count)
    {
        var words = ReservoirSampler.SampleFile(wordlistPath, count).ToList();
        var masks = MaskGeneratorEngine.GenerateMatching(mask, count).ToList();
        for (var index = 0; index < count; index++)
        {
            var word = words[index % words.Count].Plaintext;
            var generatedMask = masks[index].Plaintext;
            yield return new PasswordSample(word + generatedMask, $"Hybrid6({word} + {mask})");
        }
    }

    public static IEnumerable<PasswordSample> GenerateMaskWordlist(string mask, string wordlistPath, int count)
    {
        var words = ReservoirSampler.SampleFile(wordlistPath, count).ToList();
        var masks = MaskGeneratorEngine.GenerateMatching(mask, count).ToList();
        for (var index = 0; index < count; index++)
        {
            var word = words[index % words.Count].Plaintext;
            var generatedMask = masks[index].Plaintext;
            yield return new PasswordSample(generatedMask + word, $"Hybrid7({mask} + {word})");
        }
    }
}
