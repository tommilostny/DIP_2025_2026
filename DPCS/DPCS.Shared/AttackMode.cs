namespace DPCS.Shared;

public enum AttackMode
{
    Invalid = -1,
    Dictionary = 0, // Straight
    Combinator = 1,
    Mask = 3, // Brute-force
    Hybrid_WordlistMask = 6,
    Hybrid_MaskWordlist = 7,
    Association = 9,
}