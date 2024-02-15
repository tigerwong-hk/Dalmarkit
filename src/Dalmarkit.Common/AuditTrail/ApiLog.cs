using Dalmarkit.Common.Entities.BaseEntities;
using System.ComponentModel.DataAnnotations;

namespace Dalmarkit.Common.AuditTrail;

public class ApiLog : LogEntityBase
{
    public string? ActionName { get; set; }

    [Required]
    public string EventType { get; set; } = null!;

    public int ResponseStatusCode { get; set; }

    [Required]
    public string Url { get; set; } = null!;

    public string? UserIp { get; set; }
}
