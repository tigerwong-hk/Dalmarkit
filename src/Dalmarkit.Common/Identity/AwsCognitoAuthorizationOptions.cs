namespace Dalmarkit.Common.Identity;

public class AwsCognitoAuthorizationOptions
{
    public string? BackofficeAdminAppClientIds { get; set; }
    public string? BackofficeAdminGroups { get; set; }
    public string? BackofficeAdminScopes { get; set; }
    public string? CommunityUserAppClientIds { get; set; }
    public string? CommunityUserGroups { get; set; }
    public string? CommunityUserScopes { get; set; }
    public string? CommunityViewerAppClientIds { get; set; }
    public string? CommunityViewerGroups { get; set; }
    public string? CommunityViewerScopes { get; set; }
    public string? TenantAdminAppClientIds { get; set; }
    public string? TenantAdminGroups { get; set; }
    public string? TenantAdminScopes { get; set; }
}
