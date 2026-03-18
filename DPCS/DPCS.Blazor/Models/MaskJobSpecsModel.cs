using System.ComponentModel.DataAnnotations;

namespace DPCS.Blazor.Models;

/// <summary>
/// Model representing the specifications for a mask attack job submission via the API.
/// Created because Google.Protobuf.Collections.RepeatedField<string> does not bind well
/// with ASP.NET Core's model binding when used directly in the API endpoint parameters.
/// It is then easy to map this model to the actual HashcatMaskJobSpecs used in the actor messages.
/// </summary>
public sealed record MaskJobSpecsModel
{
    [MinLength(1, ErrorMessage = "At least one hash must be provided.")]
    public required string[] Hashes { get; init; }
    
    [Required(AllowEmptyStrings = false, ErrorMessage = "A mask must be provided.")]
    public required string Mask { get; init; }

    // Used in mask attack to enable increment mode, where the attack will start with the minimum length
    // and incrementally increase up to the maximum length.
    // It is enabled if both MinLength and MaxLength are set to values greater than 0,
    // and MinLength is less than or equal to MaxLength.
    public int MinLength { get; init; } = -1;
    public int MaxLength { get; init; } = -1;

    public required int HashType { get; init; }
}