using System.ComponentModel.DataAnnotations;

namespace DPCS.Blazor.Models;

public class SubmitJobViewModel : IValidatableObject
    {
        public AttackMode AttackMode { get; set; } = AttackMode.Mask;

        [Required(ErrorMessage = "At least one hash must be provided.")]
        public string Hashes { get; set; } = "";

        [Required]
        public string HashType { get; set; } = "0";

        public string? Mask { get; set; } = "";
        public int MinLength { get; set; } = -1;
        public int MaxLength { get; set; } = -1;
        public string? CustomCharset1 { get; set; } = "";
        public string? CustomCharset2 { get; set; } = "";
        public string? CustomCharset3 { get; set; } = "";
        public string? CustomCharset4 { get; set; } = "";


        public string? Wordlists { get; set; } = "";

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            var hashesList = Hashes?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            if (hashesList.Length == 0)
            {
                yield return new ValidationResult("At least one hash must be provided.", new[] { nameof(Hashes) });
            }

            if (AttackMode == AttackMode.Mask && string.IsNullOrWhiteSpace(Mask))
            {
                yield return new ValidationResult("A mask must be provided.", new[] { nameof(Mask) });
            }

            if (AttackMode == AttackMode.Dictionary)
            {
                var wordlistsList = Wordlists?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                if (wordlistsList.Length == 0)
                {
                    yield return new ValidationResult("At least one wordlist must be provided.", new[] { nameof(Wordlists) });
                }
            }
        }
    }