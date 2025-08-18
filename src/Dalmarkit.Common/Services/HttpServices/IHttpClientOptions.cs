namespace Dalmarkit.Common.Services.HttpServices;

public interface IHttpClientOptions
{
    string BaseUrl { get; set; }
    IDictionary<string, string>? DefaultRequestHeaders { get; set; }
    int DefaultMaxRequestRetries { get; set; }
    int DefaultRequestAttemptTimeoutSeconds { get; set; }
    int DefaultTotalRequestTimeoutSeconds { get; set; }

    void Validate();
}
