using Dalmarkit.Common.Entities.BaseEntities;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dalmarkit.Common.AuditTrail;

public class AuditLog : LogEntityBase
{
    [Required]
    public Guid AuditScopeId { get; set; }

    [Required]
    public string Action { get; set; } = null!;

    [Column(TypeName = "jsonb")]
    public string? ChangedValues { get; set; }

    public string? Error { get; set; }

    [Required]
    public string PrimaryKey { get; set; } = null!;

    [Required]
    public string Table { get; set; } = null!;
}
