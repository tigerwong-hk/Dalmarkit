using Amazon.CognitoIdentityProvider.Model;
using Dalmarkit.Common.Services;

namespace Dalmarkit.Cloud.Aws.Services;

public interface IAwsCognitoService : IIamService
{
    Task<UserType?> GetUserDetailAsync(string identityProviderId, string emailAddress);
}
