namespace Dalmarkit.Common.Identity;

public class AwsCognitoAuthorizationOptions
{
    public string? BackofficeAdminClientIds { get; set; }
    public string? BackofficeAdminGroups { get; set; }
    public string? BackofficeAdminScopes { get; set; }
    public string? CommunityUserClientIds { get; set; }
    public string? CommunityUserGroups { get; set; }
    public string? CommunityUserScopes { get; set; }
    public string? TenantAdminClientIds { get; set; }
    public string? TenantAdminGroups { get; set; }
    public string? TenantAdminScopes { get; set; }
}
