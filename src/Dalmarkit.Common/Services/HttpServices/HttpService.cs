using Dalmarkit.Common.Converters;
using Dalmarkit.Common.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;


namespace Dalmarkit.Common.Services.HttpServices;

public class HttpService(HttpClient httpClient, ILogger<HttpService> logger) : IHttpService, IDisposable
{
    /// <summary>
    /// Max characters of a non-JSON error body kept in the log and in the exception message.
    /// </summary>
    public const int MaxErrorBodyExcerptLength = 2000;

    /// <summary>
    /// Max bytes of an error body read to parse it and build the excerpt. A longer body is treated as non-JSON, and
    /// only this much of it is read, so a huge error page can't cause a large extra allocation.
    /// </summary>
    public const int MaxErrorBodyBytes = 10 * 1024 * 1024;

    private const string TruncatedMarker = "…(truncated)";

    private bool _disposed;
    private readonly HttpClient _httpClient = Guard.NotNull(httpClient, nameof(httpClient));
    private readonly ILogger _logger = Guard.NotNull(logger, nameof(logger));

    public static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = {
            new DecimalJsonConverter(),
            new JsonStringEnumConverter(),
        }
    };

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _httpClient.Dispose();
        }

        _disposed = true;
    }

    public static IServiceCollection AddHttpService<THttpClientOptions, THttpService>(
        IServiceCollection services, string configSectionKey, IConfiguration config)
        where THttpClientOptions : IHttpClientOptions
        where THttpService : class, IHttpService
    {
        THttpClientOptions? httpClientOptions = config.GetSection(configSectionKey).Get<THttpClientOptions>();
        _ = Guard.NotNull(httpClientOptions, nameof(httpClientOptions));
        httpClientOptions!.Validate();

        // HttpClient accepts 1..int.MaxValue; fail at registration rather than on the first request.
        long? maxResponseContentBufferSize = httpClientOptions.MaxResponseContentBufferSize;
        if (maxResponseContentBufferSize is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                maxResponseContentBufferSize,
                $"{configSectionKey}:{nameof(IHttpClientOptions.MaxResponseContentBufferSize)} must be between 1 and {int.MaxValue}.");
        }

        IHttpClientBuilder httpClientBuilder = services.AddHttpClient<THttpService>(
            client =>
            {
                client.BaseAddress = new Uri(httpClientOptions.BaseUrl);
                if (maxResponseContentBufferSize is long maxBufferSize)
                {
                    client.MaxResponseContentBufferSize = maxBufferSize;
                }

                if (httpClientOptions.DefaultRequestHeaders?.Count > 0)
                {
                    foreach (KeyValuePair<string, string> header in httpClientOptions.DefaultRequestHeaders)
                    {
                        client.DefaultRequestHeaders.Add(header.Key, header.Value);
                    }
                }
            });

        _ = httpClientBuilder.AddStandardResilienceHandler(
            options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(httpClientOptions.DefaultRequestAttemptTimeoutSeconds);
                options.Retry.MaxRetryAttempts = httpClientOptions.DefaultMaxRequestRetries;
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(httpClientOptions.DefaultTotalRequestTimeoutSeconds);
            }
        );

        return services;
    }

    public async Task<TSuccessResponse?> DeleteFromJsonAsync<TRequest, TErrorResponse, TSuccessResponse>(
        Uri requestUrl, TRequest content, CancellationToken cancellationToken = default)
        where TRequest : class
        where TErrorResponse : class
        where TSuccessResponse : class
    {
        string jsonBody = JsonSerializer.Serialize(content, WebOptions);
        using StringContent jsonContent = new(
            jsonBody,
            Encoding.UTF8,
            MediaTypeNames.Application.Json);

        using HttpRequestMessage request = new(HttpMethod.Delete, requestUrl)
        {
            Content = jsonContent,
        };

        using HttpResponseMessage httpResponse =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        await ThrowIfErrorAsync<TErrorResponse>(httpResponse, requestUrl, jsonBody, cancellationToken);

        return await httpResponse.Content.ReadFromJsonAsync<TSuccessResponse>(WebOptions, cancellationToken);
    }

    public async Task<TSuccessResponse?> GetFromJsonAsync<TErrorResponse, TSuccessResponse>(
        Uri requestUrl, CancellationToken cancellationToken = default)
        where TErrorResponse : class
        where TSuccessResponse : class
    {
        using HttpResponseMessage httpResponse = await _httpClient.GetAsync(requestUrl, cancellationToken);

        await ThrowIfErrorAsync<TErrorResponse>(httpResponse, requestUrl, string.Empty, cancellationToken);

        return await httpResponse.Content.ReadFromJsonAsync<TSuccessResponse>(WebOptions, cancellationToken);
    }

    public async Task<TSuccessResponse?> GetFromJsonWithHeadersAsync<TErrorResponse, TSuccessResponse>(
        Uri requestUrl, Dictionary<string, string>? requestHeaders, CancellationToken cancellationToken = default)
        where TErrorResponse : class
        where TSuccessResponse : class
    {
        using HttpRequestMessage request = new(HttpMethod.Get, requestUrl);

        if (requestHeaders?.Count > 0)
        {
            foreach (KeyValuePair<string, string> kvp in requestHeaders)
            {
                request.Headers.Add(kvp.Key, kvp.Value);
            }
        }

        using HttpResponseMessage httpResponse =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        await ThrowIfErrorAsync<TErrorResponse>(httpResponse, requestUrl, string.Empty, cancellationToken);

        return await httpResponse.Content.ReadFromJsonAsync<TSuccessResponse>(WebOptions, cancellationToken);
    }

    public async Task<TSuccessResponse?> PostAsJsonAsync<TRequest, TErrorResponse, TSuccessResponse>(
        Uri requestUrl, TRequest content, CancellationToken cancellationToken = default)
        where TRequest : class
        where TErrorResponse : class
        where TSuccessResponse : class
    {
        string jsonBody = JsonSerializer.Serialize(content, WebOptions);
        using StringContent jsonContent = new(
            jsonBody,
            Encoding.UTF8,
            MediaTypeNames.Application.Json);

        using HttpResponseMessage httpResponse =
            await _httpClient.PostAsync(requestUrl, jsonContent, cancellationToken);

        await ThrowIfErrorAsync<TErrorResponse>(httpResponse, requestUrl, jsonBody, cancellationToken);

        return await httpResponse.Content.ReadFromJsonAsync<TSuccessResponse>(WebOptions, cancellationToken);
    }

    public async Task<TSuccessResponse?> PostAsJsonWithHeadersAsync<TRequest, TErrorResponse, TSuccessResponse>(
        Uri requestUrl, Dictionary<string, string>? requestHeaders, TRequest content, CancellationToken cancellationToken = default)
        where TRequest : class
        where TErrorResponse : class
        where TSuccessResponse : class
    {
        string jsonBody = JsonSerializer.Serialize(content, WebOptions);
        using StringContent jsonContent = new(
            jsonBody,
            Encoding.UTF8,
            MediaTypeNames.Application.Json);

        using HttpRequestMessage request = new(HttpMethod.Post, requestUrl)
        {
            Content = jsonContent,
        };

        if (requestHeaders?.Count > 0)
        {
            foreach (KeyValuePair<string, string> kvp in requestHeaders)
            {
                request.Headers.Add(kvp.Key, kvp.Value);
            }
        }

        using HttpResponseMessage httpResponse =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        await ThrowIfErrorAsync<TErrorResponse>(httpResponse, requestUrl, jsonBody, cancellationToken);

        return await httpResponse.Content.ReadFromJsonAsync<TSuccessResponse>(WebOptions, cancellationToken);
    }

    public async Task<TSuccessResponse?> PutAsJsonAsync<TRequest, TErrorResponse, TSuccessResponse>(
        Uri requestUrl, TRequest content, CancellationToken cancellationToken = default)
        where TRequest : class
        where TErrorResponse : class
        where TSuccessResponse : class
    {
        string jsonBody = JsonSerializer.Serialize(content, WebOptions);
        using StringContent jsonContent = new(
            jsonBody,
            Encoding.UTF8,
            MediaTypeNames.Application.Json);

        using HttpResponseMessage httpResponse =
            await _httpClient.PutAsync(requestUrl, jsonContent, cancellationToken);

        await ThrowIfErrorAsync<TErrorResponse>(httpResponse, requestUrl, jsonBody, cancellationToken);

        return await httpResponse.Content.ReadFromJsonAsync<TSuccessResponse>(WebOptions, cancellationToken);
    }

    /// <summary>
    /// Throws <see cref="HttpRequestException"/> carrying the response's status code for any non-success response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At most <see cref="MaxErrorBodyBytes"/> of the error body are read, once, so a huge error page can't cause
    /// a large extra allocation; the same bytes are used to parse the error and to build the log excerpt.
    /// </para>
    /// <para>
    /// An error body that is read in full and deserializes into <typeparamref name="TErrorResponse"/> as
    /// UTF-8, is re-serialized into the exception message, so callers can deserialize the message back
    /// into <typeparamref name="TErrorResponse"/>.
    /// </para>
    /// <para>
    /// Any other error body (HTML or text from a load balancer, gateway or WAF, an empty body, JSON that doesn't fit
    /// <typeparamref name="TErrorResponse"/>, or a body over <see cref="MaxErrorBodyBytes"/>) is logged, and the exception
    /// message is a short text excerpt of the body. The <see cref="JsonException"/>, if any, is kept as the inner exception.
    /// </para>
    /// </remarks>
    /// <typeparam name="TErrorResponse">Type of the upstream's JSON error body.</typeparam>
    /// <param name="httpResponse">Response to check.</param>
    /// <param name="requestUrl">Request URL, for the log.</param>
    /// <param name="jsonBody">Request body, for the log.</param>
    /// <param name="cancellationToken">Cancels reading the error body.</param>
    /// <exception cref="HttpRequestException">Carries response's status code</exception>
    protected virtual async Task ThrowIfErrorAsync<TErrorResponse>(
        HttpResponseMessage httpResponse, Uri requestUrl, string jsonBody, CancellationToken cancellationToken)
        where TErrorResponse : class
    {
        if (httpResponse.IsSuccessStatusCode)
        {
            return;
        }

        (byte[] buffer, int length, bool exceeded) = await ReadErrorBodyAsync(httpResponse.Content, cancellationToken);
        ReadOnlyMemory<byte> errorBody = buffer.AsMemory(0, length);

        TErrorResponse? errorResponse = null;
        JsonException? parseError = null;
        if (!exceeded && !IsBlank(errorBody.Span))
        {
            try
            {
                errorResponse = JsonSerializer.Deserialize<TErrorResponse>(errorBody.Span, WebOptions);
            }
            catch (JsonException ex)
            {
                parseError = ex;
            }
        }

        if (errorResponse != null)
        {
            string errorMessage = JsonSerializer.Serialize(errorResponse, WebOptions);
            _logger.ErrorResponseForError(requestUrl, jsonBody, httpResponse.StatusCode, errorMessage);
            throw new HttpRequestException(errorMessage, null, httpResponse.StatusCode);
        }

        string mediaType = httpResponse.Content.Headers.ContentType?.MediaType ?? "none";
        string excerpt = Excerpt(buffer, length, exceeded);
        _logger.NonJsonErrorResponseForError(requestUrl, jsonBody, httpResponse.StatusCode, mediaType, excerpt);
        throw new HttpRequestException(
            $"Response status code does not indicate success: {(int)httpResponse.StatusCode} ({httpResponse.ReasonPhrase}); "
                + $"{mediaType} error body: {(excerpt.Length == 0 ? "(empty)" : excerpt)}",
            parseError,
            httpResponse.StatusCode);
    }

    /// <summary>
    /// Reads at most <see cref="MaxErrorBodyBytes"/> (plus one byte, to detect a longer body) of the error body.
    /// </summary>
    /// <param name="content">Error body.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private static async Task<(byte[] Buffer, int Length, bool Exceeded)> ReadErrorBodyAsync(
        HttpContent content, CancellationToken cancellationToken)
    {
        // Grows with the body rather than allocating the limit up front, so a small error body stays small
        long? declared = content.Headers.ContentLength;
        MemoryStream body = new(declared is >= 0 and <= MaxErrorBodyBytes ? (int)declared.Value + 1 : 0);
        byte[] chunk = new byte[16 * 1024];

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        while (body.Length <= MaxErrorBodyBytes)
        {
            int wanted = (int)Math.Min(chunk.Length, MaxErrorBodyBytes + 1 - body.Length);
            int count = await stream.ReadAsync(chunk.AsMemory(0, wanted), cancellationToken);
            if (count == 0)
            {
                break;
            }

            body.Write(chunk, 0, count);
        }

        bool exceeded = body.Length > MaxErrorBodyBytes;
        return (body.GetBuffer(), (int)Math.Min(body.Length, MaxErrorBodyBytes), exceeded);
    }

    private static bool IsBlank(ReadOnlySpan<byte> body)
    {
        foreach (byte b in body)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decodes the body as UTF-8 a little at a time, collapsing whitespace and dropping control characters, and stops
    /// at <see cref="MaxErrorBodyExcerptLength"/> visible characters, so building the excerpt never decodes the whole
    /// body. Leading whitespace doesn't use up the limit.
    /// </summary>
    /// <param name="buffer">Buffer holding the error body bytes read.</param>
    /// <param name="length">Number of body bytes in <paramref name="buffer"/>.</param>
    /// <param name="exceeded">The body was longer than <see cref="MaxErrorBodyBytes"/>.</param>
    private static string Excerpt(byte[] buffer, int length, bool exceeded)
    {
        using StreamReader reader = new(
            new MemoryStream(buffer, 0, length, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        StringBuilder builder = new();
        bool pendingSpace = false;
        int next;
        while ((next = reader.Read()) >= 0)
        {
            char c = (char)next;
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsControl(c))
            {
                continue;
            }

            if (builder.Length + (pendingSpace ? 2 : 1) > MaxErrorBodyExcerptLength)
            {
                return builder.Append(TruncatedMarker).ToString();
            }

            if (pendingSpace)
            {
                _ = builder.Append(' ');
                pendingSpace = false;
            }

            _ = builder.Append(c);
        }

        return exceeded ? builder.Append(TruncatedMarker).ToString() : builder.ToString();
    }
}

public static partial class HttpServiceLogs
{
    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Error,
        Message = "Error response for {RequestUrl}: StatusCode={StatusCode}, Message={Message}, Body={JsonBody}")]
    public static partial void ErrorResponseForError(
        this ILogger logger, Uri requestUrl, string jsonBody, HttpStatusCode statusCode, string message);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Non-JSON error response for {RequestUrl}: StatusCode={StatusCode}, MediaType={MediaType}, ErrorBody={ErrorBody}, Body={JsonBody}")]
    public static partial void NonJsonErrorResponseForError(
        this ILogger logger, Uri requestUrl, string jsonBody, HttpStatusCode statusCode, string mediaType, string errorBody);
}
