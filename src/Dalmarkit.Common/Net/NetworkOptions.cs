namespace Dalmarkit.Common.Net;

public class NetworkOptions
{
    public string? ClientIpHeader { get; set; }
    public ICollection<string>? KnownNetworks { get; set; }
    public ICollection<string>? KnownProxies { get; set; }
}
