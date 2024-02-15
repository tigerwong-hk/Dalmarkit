namespace Dalmarkit.Common.Services;

public interface IDnsManagementService
{
    Task CreateCnameResourceRecordSetAsync(string recordValue, string domainName, string hostedZoneId, CancellationToken cancellationToken);
    Task CreateResourceRecordSetAsync(string recordType, string recordValue, string domainName, string hostedZoneId, CancellationToken cancellationToken);
    Task CreateTxtResourceRecordSetAsync(string recordValue, string domainName, string hostedZoneId, CancellationToken cancellationToken);
}
