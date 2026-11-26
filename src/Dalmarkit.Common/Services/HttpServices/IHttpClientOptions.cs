namespace Dalmarkit.Common.Services.HttpServices;

public interface IHttpClientOptions
{
    string BaseUrl { get; set; }

    IDictionary<string, string>? DefaultRequestHeaders { get; set; }

    int DefaultMaxRequestRetries { get; set; }

    int DefaultRequestAttemptTimeoutSeconds { get; set; }

    int DefaultTotalRequestTimeoutSeconds { get; set; }

    /// <summary>
    /// Optional cap, in bytes, on how much of a response <see cref="HttpClient"/> buffers
    /// (<see cref="HttpClient.MaxResponseContentBufferSize"/>). Null keeps the <see cref="HttpClient"/> default
    /// (<see cref="int.MaxValue"/>).
    /// </summary>
    /// <remarks>
    /// A response over the cap, success or error, fails inside <see cref="HttpClient"/> with
    /// <see cref="HttpRequestException"/> that carries no status code, so size it above the largest legitimate response.
    /// This is a default interface member, so existing implementations keep compiling and get null; to set it, declare
    /// <c>public long? MaxResponseContentBufferSize { get; set; }</c> on the options class, and configuration binding fills
    /// it like any other option.
    /// </remarks>
    long? MaxResponseContentBufferSize => null;

    void Validate();
}
