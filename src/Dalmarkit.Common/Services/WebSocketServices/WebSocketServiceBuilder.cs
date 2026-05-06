using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dalmarkit.Common.Services.WebSocketServices;

internal static class WebSocketServiceBuilder
{
    public static TService Build<TService>(IServiceProvider serviceProvider, Type concreteType)
        where TService : class, IClientWebSocketPubService
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(concreteType);

        if (!typeof(TService).IsAssignableFrom(concreteType))
        {
            throw new ArgumentException(
                $"Concrete type `{concreteType.FullName}` does not implement `{typeof(TService).FullName}`",
                nameof(concreteType));
        }

        WebSocketClientEventDispatcher dispatcher = new(
            serviceProvider.GetRequiredService<ILogger<WebSocketClientEventDispatcher>>());

        WebSocketClient client = new(
            dispatcher,
            serviceProvider.GetRequiredService<IOptions<WebSocketClientOptions>>(),
            serviceProvider.GetRequiredService<ILogger<WebSocketClient>>());

        TService service;
        try
        {
            service = (TService)ActivatorUtilities.CreateInstance(serviceProvider, concreteType, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        dispatcher.RegisterHandler(service);
        return service;
    }
}
