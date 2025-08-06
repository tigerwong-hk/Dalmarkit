using System.Net;
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

    public async Task AdminAddUserToGroupAsync(string identityProviderId, string groupName, string username)
    {
        _ = Guard.NotNullOrWhiteSpace(identityProviderId, nameof(identityProviderId));
        _ = Guard.NotNullOrWhiteSpace(groupName, nameof(groupName));
        _ = Guard.NotNullOrWhiteSpace(username, nameof(username));

        AdminAddUserToGroupRequest request = new()
        {
            GroupName = groupName,
            Username = username,
            UserPoolId = identityProviderId,
        };

        AdminAddUserToGroupResponse response = await _cognitoService.AdminAddUserToGroupAsync(request);
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for AdminAddUserToGroup({identityProviderId}, {groupName}, {username}) request: {response.HttpStatusCode}");
        }
    }

    public async Task<string?> AdminCreateUserAsync(string identityProviderId, string emailAddress, string phoneNumber)
    {
        _ = Guard.NotNullOrWhiteSpace(identityProviderId, nameof(identityProviderId));
        _ = Guard.NotNullOrWhiteSpace(emailAddress, nameof(emailAddress));
        _ = Guard.NotNullOrWhiteSpace(phoneNumber, nameof(phoneNumber));

        AdminCreateUserRequest request = new()
        {
            DesiredDeliveryMediums =
                [
                    "EMAIL"
                ],
            ForceAliasCreation = false,
            UserAttributes =
                [
                    new AttributeType { Name = "email", Value = emailAddress },
                    new AttributeType { Name = "email_verified", Value = "true" },
                    new AttributeType { Name = "phone_number", Value = phoneNumber },
                    new AttributeType { Name = "phone_number_verified", Value = "true" },
                ],
            Username = emailAddress,
            UserPoolId = identityProviderId,
        };

        AdminCreateUserResponse response = await _cognitoService.AdminCreateUserAsync(request);
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for AdminCreateUser({identityProviderId}, {emailAddress}) request: {response.HttpStatusCode}");
        }

        UserType user = response.User;
        AttributeType? subAttribute = response.User.Attributes.FirstOrDefault(a => a.Name == "sub");
        return subAttribute?.Value;
    }

    public async Task AdminDeleteUserAsync(string identityProviderId, string username)
    {
        _ = Guard.NotNullOrWhiteSpace(identityProviderId, nameof(identityProviderId));
        _ = Guard.NotNullOrWhiteSpace(username, nameof(username));

        AdminDeleteUserRequest request = new()
        {
            Username = username,
            UserPoolId = identityProviderId,
        };

        AdminDeleteUserResponse response = await _cognitoService.AdminDeleteUserAsync(request);
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for AdminDeleteUser({identityProviderId}, {username}) request: {response.HttpStatusCode}");
        }
    }

    public async Task AdminDisableUserAsync(string identityProviderId, string username)
    {
        _ = Guard.NotNullOrWhiteSpace(identityProviderId, nameof(identityProviderId));
        _ = Guard.NotNullOrWhiteSpace(username, nameof(username));

        AdminDisableUserRequest request = new()
        {
            Username = username,
            UserPoolId = identityProviderId,
        };

        AdminDisableUserResponse response = await _cognitoService.AdminDisableUserAsync(request);
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for AdminDisableUser({identityProviderId}, {username}) request: {response.HttpStatusCode}");
        }
    }

    public async Task AdminEnableUserAsync(string identityProviderId, string username)
    {
        _ = Guard.NotNullOrWhiteSpace(identityProviderId, nameof(identityProviderId));
        _ = Guard.NotNullOrWhiteSpace(username, nameof(username));

        AdminEnableUserRequest request = new()
        {
            Username = username,
            UserPoolId = identityProviderId,
        };

        AdminEnableUserResponse response = await _cognitoService.AdminEnableUserAsync(request);
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for AdminEnableUser({identityProviderId}, {username}) request: {response.HttpStatusCode}");
        }
    }

    public async Task<IEnumerable<string>> AdminListGroupsForUserAsync(string identityProviderId, string username)
    {
        _ = Guard.NotNullOrWhiteSpace(identityProviderId, nameof(identityProviderId));
        _ = Guard.NotNullOrWhiteSpace(username, nameof(username));

        AdminListGroupsForUserRequest request = new()
        {
            Username = username,
            UserPoolId = identityProviderId,
        };

        List<GroupType> groups = [];

        IAdminListGroupsForUserPaginator groupsPaginator = _cognitoService.Paginators.AdminListGroupsForUser(request);
        await foreach (AdminListGroupsForUserResponse? response in groupsPaginator.Responses)
        {
            groups.AddRange(response.Groups);
        }

        return groups.Select(g => g.GroupName);
    }

    public async Task AdminRemoveUserFromGroupAsync(string identityProviderId, string groupName, string username)
    {
        _ = Guard.NotNullOrWhiteSpace(identityProviderId, nameof(identityProviderId));
        _ = Guard.NotNullOrWhiteSpace(groupName, nameof(groupName));
        _ = Guard.NotNullOrWhiteSpace(username, nameof(username));

        AdminRemoveUserFromGroupRequest request = new()
        {
            GroupName = groupName,
            Username = username,
            UserPoolId = identityProviderId,
        };

        AdminRemoveUserFromGroupResponse response = await _cognitoService.AdminRemoveUserFromGroupAsync(request);
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for AdminRemoveUserFromGroup({identityProviderId}, {groupName}, {username}) request: {response.HttpStatusCode}");
        }
    }

    public async Task AdminUpdateUserAsync(string identityProviderId, string emailAddress, string phoneNumber, string username)
    {
        _ = Guard.NotNullOrWhiteSpace(identityProviderId, nameof(identityProviderId));
        _ = Guard.NotNullOrWhiteSpace(emailAddress, nameof(emailAddress));
        _ = Guard.NotNullOrWhiteSpace(phoneNumber, nameof(phoneNumber));
        _ = Guard.NotNullOrWhiteSpace(username, nameof(username));

        AdminUpdateUserAttributesRequest request = new()
        {
            UserAttributes =
                [
                    new AttributeType { Name = "email", Value = emailAddress },
                    new AttributeType { Name = "email_verified", Value = "true" },
                    new AttributeType { Name = "phone_number", Value = phoneNumber },
                    new AttributeType { Name = "phone_number_verified", Value = "true" },
                ],
            Username = username,
            UserPoolId = identityProviderId,
        };

        AdminUpdateUserAttributesResponse response = await _cognitoService.AdminUpdateUserAttributesAsync(request);
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for AdminUpdateUser({identityProviderId}, {emailAddress}, {username}) request: {response.HttpStatusCode}");
        }
    }

    public async Task<UserType?> GetUserDetailAsync(string identityProviderId, string emailAddress)
    {
        _ = Guard.NotNullOrWhiteSpace(identityProviderId, nameof(identityProviderId));
        _ = Guard.NotNullOrWhiteSpace(emailAddress, nameof(emailAddress));

        ListUsersRequest request = new()
        {
            Filter = $"\"email\"=\"{emailAddress}\"",
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
            _logger.EmailAddressNotFoundForError(emailAddress);
            return null;
        }

        if (users.Count > 1)
        {
            _logger.MultipleSameEmailAddressFoundForError(emailAddress);
            return null;
        }

        return users[0];
    }

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
        EventId = 0,
        Level = LogLevel.Information,
        Message = "Multiple same user ID found for: {UserId}")]
    public static partial void MultipleSameUserIdFoundForError(
        this ILogger logger, string userId);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "User ID not found for: {UserId}")]
    public static partial void UserIdNotFoundForError(
        this ILogger logger, string userId);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Multiple same email address found for: {EmailAddress}")]
    public static partial void MultipleSameEmailAddressFoundForError(
        this ILogger logger, string emailAddress);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Email address not found for: {EmailAddress}")]
    public static partial void EmailAddressNotFoundForError(
        this ILogger logger, string emailAddress);
}
