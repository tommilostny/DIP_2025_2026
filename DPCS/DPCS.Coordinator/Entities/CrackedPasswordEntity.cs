using System.ComponentModel.DataAnnotations;

namespace DPCS.Coordinator.Entities;

public class CrackedPasswordEntity
{
    [Key]
    public required string Hash { get; set; }
    public required string Plaintext { get; set; }
    public required int HashType { get; set; }
    public required DateTime CrackedAt { get; set; }
    public required TimeSpan TimeTaken { get; set; }
    public required int AttackMode { get; set; }
    public required string JobId { get; set; }
}