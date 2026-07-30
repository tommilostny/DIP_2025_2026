namespace DPCS.HashGenerator.Models;

public sealed class DictionaryManifestSpecs
{
    public string[] Wordlists { get; set; } = [];
    public string RuleFileContent { get; set; } = string.Empty;
}

public sealed class MaskManifestSpecs
{
    public string[] Masks { get; set; } = [];
    public int MinLength { get; set; }
    public int MaxLength { get; set; }
    public string CustomCharset1 { get; set; } = string.Empty;
    public string CustomCharset2 { get; set; } = string.Empty;
    public string CustomCharset3 { get; set; } = string.Empty;
    public string CustomCharset4 { get; set; } = string.Empty;
}

public sealed class CombinatorManifestSpecs
{
    public string[] LeftWordlists { get; set; } = [];
    public string[] RightWordlists { get; set; } = [];
    public string RuleFileContent { get; set; } = string.Empty;
}

public sealed class HybridManifestSpecs
{
    public string[] Wordlists { get; set; } = [];
    public string[] Masks { get; set; } = [];
    public string CustomCharset1 { get; set; } = string.Empty;
    public string CustomCharset2 { get; set; } = string.Empty;
    public string CustomCharset3 { get; set; } = string.Empty;
    public string CustomCharset4 { get; set; } = string.Empty;
    public int AttackMode { get; set; }
}
