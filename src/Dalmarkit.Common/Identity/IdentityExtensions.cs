using System.Security.Claims;
using System.Security.Principal;

namespace Dalmarkit.Common.Identity;

public static class IdentityExtensions
{
    public static string GetAccountId(this IIdentity identity)
    {
        return GetClaimValue(identity, ClaimTypes.NameIdentifier);
    }

    public static string GetClaimValue(IIdentity identity, string type)
    {
        ClaimsIdentity? claimsIdentity = identity as ClaimsIdentity;
        Claim? claim = claimsIdentity?.Claims?.SingleOrDefault(x => x.Type == type);

        return claim is null ? string.Empty : claim.Value;
    }

    public static string GetClientId(this IIdentity identity)
    {
        return GetClaimValue(identity, AwsCognitoJwtClaims.ClientId);
    }

    public static string GetUsername(this IIdentity identity)
    {
        return GetClaimValue(identity, AwsCognitoJwtClaims.Username);
    }
}
