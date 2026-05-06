namespace Dalmarkit.Common.Services.WebSocketServices;

public interface IWebSocketServiceFactory<TService> : IDisposable
    where TService : class, IClientWebSocketPubService
{
    Task<TService> ConnectAsync(string webSocketId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string webSocketId, CancellationToken cancellationToken = default);
    TService GetService(string webSocketId);
    bool TryGetService(string webSocketId, out TService? service);
}
