namespace Dalmarkit.Common.Services.HttpServices;

public interface IHttpService
{
    Task<TSuccessResponse?> DeleteFromJsonAsync<TRequest, TErrorResponse, TSuccessResponse>(
        Uri requestUrl, TRequest content, CancellationToken cancellationToken = default)
        where TRequest : class
        where TErrorResponse : class
        where TSuccessResponse : class;

    Task<TSuccessResponse?> GetFromJsonAsync<TErrorResponse, TSuccessResponse>(
        Uri requestUrl, CancellationToken cancellationToken = default)
        where TErrorResponse : class
        where TSuccessResponse : class;

    Task<TSuccessResponse?> PostAsJsonAsync<TRequest, TErrorResponse, TSuccessResponse>(
        Uri requestUrl, TRequest content, CancellationToken cancellationToken = default)
        where TRequest : class
        where TErrorResponse : class
        where TSuccessResponse : class;

    Task<TSuccessResponse?> PutAsJsonAsync<TRequest, TErrorResponse, TSuccessResponse>(
        Uri requestUrl, TRequest content, CancellationToken cancellationToken = default)
        where TRequest : class
        where TErrorResponse : class
        where TSuccessResponse : class;
}
