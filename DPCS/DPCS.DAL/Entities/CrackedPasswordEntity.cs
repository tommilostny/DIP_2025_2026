using System.ComponentModel.DataAnnotations;
//using System.ComponentModel.DataAnnotations.Schema;

namespace DPCS.DAL.Entities;

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

    //[ForeignKey(nameof(JobId))]
    //public JobRecordEntity? JobRecord { get; set; }
}