namespace Dalmarkit.Common.Services;

public interface IIamService
{
    Task AdminAddUserToGroupAsync(string identityProviderId, string groupName, string username);
    Task<string?> AdminCreateUserAsync(string identityProviderId, string emailAddress, string phoneNumber);
    Task AdminDeleteUserAsync(string identityProviderId, string username);
    Task AdminDisableUserAsync(string identityProviderId, string username);
    Task AdminEnableUserAsync(string identityProviderId, string username);
    Task AdminRemoveUserFromGroupAsync(string identityProviderId, string groupName, string username);
    Task AdminUpdateUserAsync(string identityProviderId, string emailAddress, string phoneNumber, string username);
    Task<string?> GetUserEmailAddressAsync(string identityProviderId, string userId);
}
