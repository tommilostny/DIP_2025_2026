using System.ComponentModel.DataAnnotations;

namespace DPCS.Blazor.Models;

/// <summary>
/// Model representing the specifications for a dictionary attack job submission via the API.
/// Created because Google.Protobuf.Collections.RepeatedField<string> does not bind well
/// with ASP.NET Core's model binding when used directly in the API endpoint parameters.
/// It is then easy to map this model to the actual HashcatDictionaryJobSpecs used in the actor messages.
/// </summary>
public sealed record DictionaryJobSpecsModel
{
    [MinLength(1, ErrorMessage = "At least one hash must be provided.")]
    public required string[] Hashes { get; init; }

    [MinLength(1, ErrorMessage = "At least one wordlist must be provided.")]
    public required string[] Wordlists { get; init; }

    public required int HashType { get; init; }
}