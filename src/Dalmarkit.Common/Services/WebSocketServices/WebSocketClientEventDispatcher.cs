using Dalmarkit.Common.Events;
using Mediator;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;

namespace Dalmarkit.Common.Services.WebSocketServices;

public sealed class WebSocketClientEventDispatcher(ILogger<WebSocketClientEventDispatcher> logger) : IEventDispatcher
{
    private const int DispatchByNotificationTypesInitialCapacity = 67;

    private readonly ILogger<WebSocketClientEventDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly Lock _handlersLock = new();
    private readonly List<object> _handlers = [];

    private static readonly ConcurrentDictionary<Type, HandlerDispatch> _dispatchByNotificationTypes = new(Environment.ProcessorCount, DispatchByNotificationTypesInitialCapacity);

    private readonly record struct HandlerDispatch(Type HandlerInterface, MethodInfo HandleMethod);

    public void RegisterHandler(object handler)
    {
        ArgumentNullException.ThrowIfNull(handler, nameof(handler));

        bool implementsAnyNotificationHandler = false;
        foreach (Type itf in handler.GetType().GetInterfaces())
        {
            if (itf.IsGenericType && itf.GetGenericTypeDefinition() == typeof(INotificationHandler<>))
            {
                implementsAnyNotificationHandler = true;
                break;
            }
        }

        if (!implementsAnyNotificationHandler)
        {
            _logger.WebSocketClientDispatcherRegisterHandlerUnsupportedTypeError();
            return;
        }

        lock (_handlersLock)
        {
            _handlers.Add(handler);
        }
    }

    public async Task DispatchEventAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
    {
        if (message is not INotification notification)
        {
            throw new InvalidOperationException($"Message type {typeof(TMessage).Name} is not supported. Expected INotification.");
        }

        object[] handlersSnapshot;
        lock (_handlersLock)
        {
            handlersSnapshot = [.. _handlers];
        }

        if (handlersSnapshot.Length == 0)
        {
            return;
        }

        HandlerDispatch dispatch = _dispatchByNotificationTypes.GetOrAdd(notification.GetType(), static t =>
        {
            Type handlerInterface = typeof(INotificationHandler<>).MakeGenericType(t);
            MethodInfo handleMethod = handlerInterface.GetMethod("Handle")
                ?? throw new InvalidOperationException($"INotificationHandler<{t.Name}> is missing Handle method");
            return new HandlerDispatch(handlerInterface, handleMethod);
        });

        try
        {
            foreach (object handler in handlersSnapshot)
            {
                if (!dispatch.HandlerInterface.IsInstanceOfType(handler))
                {
                    continue;
                }

                ValueTask handleTask = (ValueTask)dispatch.HandleMethod.Invoke(handler, [notification, cancellationToken])!;
                await handleTask.ConfigureAwait(false);
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException)
        {
            _logger.WebSocketClientDispatcherDispatchEventAsyncHandleInvokeCanceledInfo(typeof(TMessage).Name);
            throw ex.InnerException;
        }
        catch (TargetInvocationException ex)
        {
            _logger.WebSocketClientDispatcherDispatchEventAsyncHandleInvokeException(typeof(TMessage).Name, ex.InnerException ?? ex);
            throw ex.InnerException ?? ex;
        }
        catch (OperationCanceledException)
        {
            _logger.WebSocketClientDispatcherDispatchEventAsyncHandleCanceledInfo(typeof(TMessage).Name);
            throw;
        }
        catch (Exception ex)
        {
            _logger.WebSocketClientDispatcherDispatchEventAsyncHandleException(typeof(TMessage).Name, ex);
            throw;
        }
    }
}

public static partial class WebSocketClientEventDispatcherLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Error,
        Message = "WebSocketClientDispatcherRegisterHandler: unsupported handler type")]
    public static partial void WebSocketClientDispatcherRegisterHandlerUnsupportedTypeError(
        this ILogger logger);

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Information,
        Message = "WebSocketClientDispatcherDispatchEventAsync: handle invoke canceled for message type `{MessageType}`")]
    public static partial void WebSocketClientDispatcherDispatchEventAsyncHandleInvokeCanceledInfo(
        this ILogger logger, string messageType);

    [LoggerMessage(
        EventId = 2020,
        Level = LogLevel.Error,
        Message = "WebSocketClientDispatcherDispatchEventAsync: handle invoke exception for message type `{MessageType}`")]
    public static partial void WebSocketClientDispatcherDispatchEventAsyncHandleInvokeException(
        this ILogger logger, string messageType, Exception exception);

    [LoggerMessage(
        EventId = 2030,
        Level = LogLevel.Information,
        Message = "WebSocketClientDispatcherDispatchEventAsync: handle canceled for message type `{MessageType}`")]
    public static partial void WebSocketClientDispatcherDispatchEventAsyncHandleCanceledInfo(
        this ILogger logger, string messageType);

    [LoggerMessage(
        EventId = 2040,
        Level = LogLevel.Error,
        Message = "WebSocketClientDispatcherDispatchEventAsync: handle exception for message type `{MessageType}`")]
    public static partial void WebSocketClientDispatcherDispatchEventAsyncHandleException(
        this ILogger logger, string messageType, Exception exception);
}
