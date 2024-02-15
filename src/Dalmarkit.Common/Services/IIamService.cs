namespace Dalmarkit.Common.Services;

public interface IIamService
{
    Task<string?> GetUserEmailAddressAsync(string identityProviderId, string userId);
}
