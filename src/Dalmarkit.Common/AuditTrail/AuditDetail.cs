namespace Dalmarkit.Common.AuditTrail;

public class AuditDetail(string appClientId, string userId, DateTime dateTime)
{
    public string AppClientId { get; set; } = appClientId;
    public DateTime ModifiedOn { get; set; } = dateTime;
    public string ModifierId { get; set; } = userId;
}
