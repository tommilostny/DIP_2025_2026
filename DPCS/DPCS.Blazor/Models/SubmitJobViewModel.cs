using System.ComponentModel.DataAnnotations;

namespace DPCS.Blazor.Models;

public class SubmitJobViewModel : IValidatableObject
{
    public AttackMode AttackMode { get; set; } = AttackMode.Mask;

    [Required(ErrorMessage = "At least one hash must be provided.")]
    public string Hashes { get; set; } = "";

    public int ChunkTimeSeconds { get; set; } = Constants.DefaultChunkTimeSeconds;

    [Required(ErrorMessage = "Hash type must be specified.")]
    public string HashType { get; set; } = "0";

    public string? Masks { get; set; } = "";
    public int MinLength { get; set; } = -1;
    public int MaxLength { get; set; } = -1;
    public string? CustomCharset1 { get; set; } = "";
    public string? CustomCharset2 { get; set; } = "";
    public string? CustomCharset3 { get; set; } = "";
    public string? CustomCharset4 { get; set; } = "";


    public string? Wordlists { get; set; } = "";
    public string? DictionaryRules { get; set; } = "";
    public string? LeftWordlists { get; set; } = "";
    public string? RightWordlists { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hashesList = Hashes?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (hashesList.Length == 0)
        {
            yield return new ValidationResult("At least one hash must be provided.", [nameof(Hashes)]);
        }

        switch (AttackMode)
        {
        case AttackMode.Mask:
            if (string.IsNullOrWhiteSpace(Masks))
            {
                yield return new ValidationResult("At least one mask must be provided.", [nameof(Masks)]);
            }
            var maskService = validationContext.GetService<MaskService>();
            if (maskService is not null)
            {
                var customCharsets = new[] { CustomCharset1, CustomCharset2, CustomCharset3, CustomCharset4 };
                var masksList = Masks?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(m => m.Trim())
                    .Where(m => !string.IsNullOrWhiteSpace(m)) ?? [];

                foreach (var mask in masksList)
                {
                    if (!maskService.IsMaskValid(mask, customCharsets!))
                        yield return new ValidationResult($"The mask '{mask}' is invalid. It contains unknown placeholders.", [nameof(Masks)]);
                }
            }
            break;

        case AttackMode.Dictionary:
            var wordlistsList = Wordlists?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (wordlistsList.Length == 0)
            {
                yield return new ValidationResult("At least one wordlist must be provided.", [nameof(Wordlists)]);
            }
            break;

        case AttackMode.Combinator:
            var leftWordlistsList = LeftWordlists?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) ?? [];
            var rightWordlistsList = RightWordlists?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) ?? [];

            if (leftWordlistsList.Length == 0)
            {
                yield return new ValidationResult("At least one left wordlist must be provided.", [nameof(LeftWordlists)]);
            }

            if (rightWordlistsList.Length == 0)
            {
                yield return new ValidationResult("At least one right wordlist must be provided.", [nameof(RightWordlists)]);
            }
            break;
        }
    }
}