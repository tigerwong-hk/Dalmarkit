using AWS.MSK.Auth;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Dalmarkit.Messaging.Kafka.Handlers;

public class AwsMskIamOauthHandlers(ILogger<AwsMskIamOauthHandlers> logger) : IOauthHandlers
{
    private const string TokenPrincipal = "AwsMskIamClient";

    private readonly ILogger<AwsMskIamOauthHandlers> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly AWSMSKAuthTokenGenerator _tokenGenerator = new();

    public Action<IClient, string> GetOauthBearerTokenRefreshHandler(string region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        Amazon.RegionEndpoint regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        return (client, _) =>
        {
            try
            {
                (string token, long expiryMs) = _tokenGenerator.GenerateAuthToken(regionEndpoint, true);
                client.OAuthBearerSetToken(token, expiryMs, TokenPrincipal);

                _logger.CreateOauthBearerTokenRefreshHandlerRefreshedDebug(region, expiryMs);
            }
            catch (Exception ex)
            {
                _logger.CreateOauthBearerTokenRefreshHandlerGenerateAuthTokenException(region, ex);
                client.OAuthBearerSetTokenFailure(ex.Message);
            }
        };
    }
}

public static partial class AwsMskIamOauthHandlersLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Debug,
        Message = "GetOauthBearerTokenRefreshHandler: refreshed AWS MSK IAM OAuth bearer token for region `{Region}` which expires in `{ExpiryMs}` milliseconds")]
    public static partial void CreateOauthBearerTokenRefreshHandlerRefreshedDebug(
        this ILogger logger, string region, long expiryMs);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Error,
        Message = "GetOauthBearerTokenRefreshHandler: generate auth token exception for AWS region `{Region}`")]
    public static partial void CreateOauthBearerTokenRefreshHandlerGenerateAuthTokenException(
        this ILogger logger, string region, Exception exception);
}
