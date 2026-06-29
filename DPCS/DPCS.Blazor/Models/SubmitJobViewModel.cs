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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hashesList = Hashes?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (hashesList.Length == 0)
        {
            yield return new ValidationResult("At least one hash must be provided.", [nameof(Hashes)]);
        }

        if (AttackMode == AttackMode.Mask && string.IsNullOrWhiteSpace(Masks))
        {
            yield return new ValidationResult("At least one mask must be provided.", [nameof(Masks)]);
        }

        if (AttackMode == AttackMode.Dictionary)
        {
            var wordlistsList = Wordlists?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (wordlistsList.Length == 0)
            {
                yield return new ValidationResult("At least one wordlist must be provided.", [nameof(Wordlists)]);
            }
        }
    }
}