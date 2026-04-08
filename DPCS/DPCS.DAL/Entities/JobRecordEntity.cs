using System.ComponentModel.DataAnnotations;

namespace DPCS.DAL.Entities;

public class JobRecordEntity
{
    [Key]
    public string JobId { get; set; } = string.Empty;
    public int AttackMode { get; set; }
    public int HashType { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string Status { get; set; } = "Initializing";
    public float ProgressPercentage { get; set; }
}