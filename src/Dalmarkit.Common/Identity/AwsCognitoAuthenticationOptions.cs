namespace Dalmarkit.Common.Identity;

public class AwsCognitoAuthenticationOptions
{
    public string? IssuerBaseUrl { get; set; }
    public string? UserPoolId { get; set; }
    public string? ValidClientIds { get; set; }
}
