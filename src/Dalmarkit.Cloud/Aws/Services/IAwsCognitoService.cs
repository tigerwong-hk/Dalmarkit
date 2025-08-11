using Amazon.CognitoIdentityProvider.Model;
using Dalmarkit.Common.Services;

namespace Dalmarkit.Cloud.Aws.Services;

public interface IAwsCognitoService : IIamService
{
    Task<UserType?> GetUserDetailByEmailAddressAsync(string identityProviderId, string emailAddress);
    Task<UserType?> GetUserDetailByUserIdAsync(string identityProviderId, string userId);
}
