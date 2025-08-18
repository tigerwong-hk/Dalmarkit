using Dalmarkit.Common.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace Dalmarkit.Common.Services.HttpServices;

public class HttpService(HttpClient httpClient, ILogger<HttpService> logger) : IHttpService, IDisposable
{
    private bool _disposed;
    private readonly HttpClient _httpClient = Guard.NotNull(httpClient, nameof(httpClient));
    private readonly ILogger _logger = Guard.NotNull(logger, nameof(logger));

    public static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

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

    public static IServiceCollection AddHttpService<THttpClientOptions>(
        IServiceCollection services, string configSectionKey, IConfiguration config)
        where THttpClientOptions : IHttpClientOptions
    {
        THttpClientOptions? httpClientOptions = config.GetSection(configSectionKey).Get<THttpClientOptions>();
        _ = Guard.NotNull(httpClientOptions, nameof(httpClientOptions));
        httpClientOptions!.Validate();

        IHttpClientBuilder httpClientBuilder = services.AddHttpClient<IHttpService, HttpService>(
            client =>
            {
                client.BaseAddress = new Uri(httpClientOptions.BaseUrl);
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

        if (!httpResponse.IsSuccessStatusCode)
        {
            await using Stream contentStream =
                await httpResponse.Content.ReadAsStreamAsync(cancellationToken);

            TErrorResponse? errorResponse = await JsonSerializer.DeserializeAsync<TErrorResponse>(contentStream, WebOptions, cancellationToken);
            if (errorResponse != null)
            {
                string errorMessage = JsonSerializer.Serialize(errorResponse, WebOptions);
                _logger.ErrorResponseForError(requestUrl, jsonBody, httpResponse.StatusCode, errorMessage);
                throw new HttpRequestException(errorMessage, null, httpResponse.StatusCode);
            }
        }

        _ = httpResponse.EnsureSuccessStatusCode();

        return await httpResponse.Content.ReadFromJsonAsync<TSuccessResponse>(WebOptions, cancellationToken);
    }

    public async Task<TSuccessResponse?> GetFromJsonAsync<TErrorResponse, TSuccessResponse>(
        Uri requestUrl, CancellationToken cancellationToken = default)
        where TErrorResponse : class
        where TSuccessResponse : class
    {
        using HttpResponseMessage httpResponse = await _httpClient.GetAsync(requestUrl, cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            await using Stream contentStream =
                await httpResponse.Content.ReadAsStreamAsync(cancellationToken);

            TErrorResponse? errorResponse = await JsonSerializer.DeserializeAsync<TErrorResponse>(
                contentStream, WebOptions, cancellationToken);
            if (errorResponse != null)
            {
                string errorMessage = JsonSerializer.Serialize(errorResponse, WebOptions);
                _logger.ErrorResponseForError(requestUrl, string.Empty, httpResponse.StatusCode, errorMessage);
                throw new HttpRequestException(errorMessage, null, httpResponse.StatusCode);
            }
        }

        _ = httpResponse.EnsureSuccessStatusCode();

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

        if (!httpResponse.IsSuccessStatusCode)
        {
            await using Stream contentStream =
                await httpResponse.Content.ReadAsStreamAsync(cancellationToken);

            TErrorResponse? errorResponse = await JsonSerializer.DeserializeAsync<TErrorResponse>(
                contentStream, WebOptions, cancellationToken);
            if (errorResponse != null)
            {
                string errorMessage = JsonSerializer.Serialize(errorResponse, WebOptions);
                _logger.ErrorResponseForError(requestUrl, jsonBody, httpResponse.StatusCode, errorMessage);
                throw new HttpRequestException(errorMessage, null, httpResponse.StatusCode);
            }
        }

        _ = httpResponse.EnsureSuccessStatusCode();

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

        if (!httpResponse.IsSuccessStatusCode)
        {
            await using Stream contentStream =
                await httpResponse.Content.ReadAsStreamAsync(cancellationToken);

            TErrorResponse? errorResponse = await JsonSerializer.DeserializeAsync<TErrorResponse>(
                contentStream, WebOptions, cancellationToken);
            if (errorResponse != null)
            {
                string errorMessage = JsonSerializer.Serialize(errorResponse, WebOptions);
                _logger.ErrorResponseForError(requestUrl, jsonBody, httpResponse.StatusCode, errorMessage);
                throw new HttpRequestException(errorMessage, null, httpResponse.StatusCode);
            }
        }

        _ = httpResponse.EnsureSuccessStatusCode();

        return await httpResponse.Content.ReadFromJsonAsync<TSuccessResponse>(WebOptions, cancellationToken);
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
}
