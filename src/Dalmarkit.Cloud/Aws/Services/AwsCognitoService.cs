using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Dalmarkit.Common.Validation;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Dalmarkit.Cloud.Aws.Services;

public class AwsCognitoService(IAmazonCognitoIdentityProvider cognitoService, ILogger<AwsCognitoService> logger) : IAwsCognitoService
{
    private readonly IAmazonCognitoIdentityProvider _cognitoService = Guard.NotNull(cognitoService, nameof(cognitoService));
    private readonly ILogger _logger = Guard.NotNull(logger, nameof(logger));

    public async Task<string?> GetUserEmailAddressAsync(string identityProviderId, string userId)
    {
        _ = Guard.NotNullOrWhiteSpace(identityProviderId, nameof(identityProviderId));
        _ = Guard.NotNullOrWhiteSpace(userId, nameof(userId));

        ListUsersRequest request = new()
        {
            AttributesToGet =
                [
                    "email"
                ],
            Filter = $"\"sub\"=\"{userId}\"",
            UserPoolId = identityProviderId
        };

        List<UserType> users = [];

        IListUsersPaginator usersPaginator = _cognitoService.Paginators.ListUsers(request);
        await foreach (ListUsersResponse? response in usersPaginator.Responses)
        {
            users.AddRange(response.Users);
        }

        if (users.Count == 0)
        {
            _logger.UserIdNotFoundForError(userId);
            return null;
        }

        if (users.Count > 1)
        {
            _logger.MultipleSameUserIdFoundForError(userId);
            return string.Empty;
        }

        return users[0].Attributes.Find(attrType => attrType.Name == "email")?.Value;
    }
}

public static partial class AwsCognitoServiceLogs
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Multiple same user ID found for: {UserId}")]
    public static partial void MultipleSameUserIdFoundForError(
        this ILogger logger, string userId);

    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Information,
        Message = "User ID not found for: {UserId}")]
    public static partial void UserIdNotFoundForError(
        this ILogger logger, string userId);
}
