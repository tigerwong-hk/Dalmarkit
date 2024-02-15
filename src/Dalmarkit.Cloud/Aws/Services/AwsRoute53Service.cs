using Amazon.Route53;
using Amazon.Route53.Model;
using Dalmarkit.Common.Validation;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Dalmarkit.Cloud.Aws.Services;

public class AwsRoute53Service(AmazonRoute53Client route53Client, ILogger<AwsRoute53Service> logger) : IAwsRoute53Service
{
    private readonly AmazonRoute53Client _route53Client = Guard.NotNull(route53Client, nameof(route53Client));
    private readonly ILogger _logger = Guard.NotNull(logger, nameof(logger));

    public async Task CreateCnameResourceRecordSetAsync(string recordValue, string domainName, string hostedZoneId, CancellationToken cancellationToken)
    {
        UriHostNameType recordValueHostnameType = Uri.CheckHostName(recordValue);
        if (recordValueHostnameType != UriHostNameType.Dns)
        {
            _logger.InvalidRecordValueFForError(domainName, hostedZoneId, recordValue);
            throw new ArgumentException($"Invalid CNAME record value: {recordValue}", nameof(recordValue));
        }

        await CreateResourceRecordSetAsync("CNAME", recordValue, domainName, hostedZoneId, cancellationToken);
    }

    public async Task CreateResourceRecordSetAsync(string recordType, string recordValue, string domainName, string hostedZoneId, CancellationToken cancellationToken)
    {
        RRType rrType = GetRrType(recordType);

        UriHostNameType domainNameHostnameType = Uri.CheckHostName(domainName);
        if (domainNameHostnameType != UriHostNameType.Dns)
        {
            _logger.InvalidDomainNameForError(hostedZoneId, recordType, recordValue, domainName);
            throw new ArgumentException($"Invalid domain name: {domainName}", nameof(domainName));
        }

        ResourceRecordSet recordSet = new()
        {
            Name = domainName,
            ResourceRecords = [new ResourceRecord { Value = recordValue }],
            TTL = 300,
            Type = rrType,
        };

        Change change = new()
        {
            Action = ChangeAction.CREATE,
            ResourceRecordSet = recordSet,
        };

        ChangeBatch changeBatch = new()
        {
            Changes = [change],
        };

        ChangeResourceRecordSetsRequest changeResourceRecordSetsRequest = new()
        {
            ChangeBatch = changeBatch,
            HostedZoneId = hostedZoneId,
        };

        ChangeResourceRecordSetsResponse changeResourceRecordSetsResponse = await _route53Client.ChangeResourceRecordSetsAsync(changeResourceRecordSetsRequest, cancellationToken);
        if (changeResourceRecordSetsResponse.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            _logger.HttpStatusCodeForError(domainName, hostedZoneId, recordType, recordValue, changeResourceRecordSetsResponse.HttpStatusCode);
            throw new HttpRequestException($"Error status for CreateResourceRecordSet({recordType}, {recordValue}, {domainName}) request: {changeResourceRecordSetsResponse.HttpStatusCode}");
        }
    }

    public async Task CreateTxtResourceRecordSetAsync(string recordValue, string domainName, string hostedZoneId, CancellationToken cancellationToken)
    {
        await CreateResourceRecordSetAsync("TXT", recordValue, domainName, hostedZoneId, cancellationToken);
    }

    protected static RRType GetRrType(string recordType)
    {
        return recordType switch
        {
            "A" => RRType.A,
            "AAAA" => RRType.AAAA,
            "CAA" => RRType.CAA,
            "CNAME" => RRType.CNAME,
            "DS" => RRType.DS,
            "MX" => RRType.MX,
            "NAPTR" => RRType.NAPTR,
            "NS" => RRType.NS,
            "PTR" => RRType.PTR,
            "SPF" => RRType.SPF,
            "SRV" => RRType.SRV,
            "TXT" => RRType.TXT,
            _ => throw new ArgumentException($"Unknown record type: {recordType}", nameof(recordType)),
        };
    }
}

public static partial class AwsRoute53ServiceLogs
{
    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Error status code for CreateResourceRecordSet in domain `{DomainName}` with hosted zone ID `{HostedZoneId})`, record type `({RecordType}` and record value `{RecordValue}`:  {HttpStatusCode}")]
    public static partial void HttpStatusCodeForError(
        this ILogger logger, string domainName, string hostedZoneId, string recordType, string recordValue, HttpStatusCode httpStatusCode);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Invalid domain name for CreateResourceRecordSet in hosted zone ID `{HostedZoneId})` with record type `{RecordType}` and record value `{RecordValue}`: ({DomainName}")]
    public static partial void InvalidDomainNameForError(
        this ILogger logger, string hostedZoneId, string recordType, string recordValue, string domainName);

    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Information,
        Message = "Invalid record value for CreateCnameResourceRecordSet in domain `{DomainName}` with hosted zone ID `{HostedZoneId}`: `{RecordValue}`")]
    public static partial void InvalidRecordValueFForError(
        this ILogger logger, string domainName, string hostedZoneId, string recordValue);
}
