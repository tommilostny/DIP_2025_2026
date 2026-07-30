namespace DPCS.HashGenerator.Models;

public sealed record HashedSample(string Plaintext, string Hash, string SourceRuleOrMask);
