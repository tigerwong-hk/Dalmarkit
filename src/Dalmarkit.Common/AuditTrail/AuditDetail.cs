namespace Dalmarkit.Common.AuditTrail;

public class AuditDetail(string clientId, string userId, DateTime dateTime)
{
    public string ClientId { get; set; } = clientId;
    public DateTime ModifiedOn { get; set; } = dateTime;
    public string ModifierId { get; set; } = userId;
}
