using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dalmarkit.Common.Entities.BaseEntities;

public class LogEntityBase
{
    [Key]
    [Required]
    public long Id { get; set; }

    [Required]
    public DateTime CreatedOn { get; set; }

    public int DurationMsec { get; set; }

    [Column(TypeName = "jsonb")]
    [Required]
    public string LogDetail { get; set; } = null!;

    [Required]
    public DateTime ModifiedOn { get; set; }

    public bool Status { get; set; }

    [Required]
    public string TraceId { get; set; } = null!;

    public string? UserId { get; set; }
}
