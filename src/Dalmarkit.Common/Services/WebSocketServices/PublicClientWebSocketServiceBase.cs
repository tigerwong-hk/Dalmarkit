using Dalmarkit.Common.Dtos.RequestDtos;
using Mediator;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Dalmarkit.Common.Services.WebSocketServices;

public abstract class PublicClientWebSocketServiceBase(
    IWebSocketClient webSocketClient,
    ILogger<PublicClientWebSocketServiceBase> logger) : IPublicClientWebSocketService,
        INotificationHandler<WebSocketClientEvents.OnWebSocketConnected>,
        INotificationHandler<WebSocketClientEvents.OnWebSocketDisconnected>
{
    public const int GracefulShutdownTimeoutMilliseconds = 10000;
    public const int SubscribedChannelsInitialCapacity = 2053;

    private readonly IWebSocketClient _webSocketClient = webSocketClient ?? throw new ArgumentNullException(nameof(webSocketClient));
    private readonly ILogger<PublicClientWebSocketServiceBase> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly CancellationTokenSource _disposalCts = new();

    private static readonly ConcurrentDictionary<string, byte> _subscribedChannels = new(Environment.ProcessorCount, SubscribedChannelsInitialCapacity);
    private static volatile bool hasSubscribedOnConnected;

    private bool _isDisposed;
    private CancellationTokenSource? _receiveTextMessageCts;
    private Task? _receiveTextMessageTask;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (!disposing)
        {
            return;
        }

        _receiveTextMessageCts?.Dispose();
        _receiveTextMessageCts = null;

        _receiveTextMessageTask?.Dispose();
        _receiveTextMessageTask = null;

        _disposalCts.Dispose();
    }

    public virtual async ValueTask Handle(WebSocketClientEvents.OnWebSocketConnected notification, CancellationToken cancellationToken = default)
    {
        List<string> channels = [.. _subscribedChannels.Keys];

        try
        {
            _receiveTextMessageCts?.Dispose();
            _receiveTextMessageCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token);

            _receiveTextMessageTask?.Dispose();
            _receiveTextMessageTask = ReceiveTextMessagesAsync(_receiveTextMessageCts.Token);

            await SetupServerHeartbeatAsync(cancellationToken).ConfigureAwait(false);

            _ = Interlocked.Exchange(ref hasSubscribedOnConnected, true);

            _logger.SubscribingChannels(channels);
            await SendSubscribeRequestAsync(channels, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.SubscribeOnConnectedCanceledInfo();
        }
        catch (Exception ex)
        {
            _logger.SubscribeOnConnectedException(channels, ex.Message, ex.InnerException?.Message, ex.StackTrace);
        }
    }

    public virtual async ValueTask Handle(WebSocketClientEvents.OnWebSocketDisconnected notification, CancellationToken cancellationToken = default)
    {
        await ShutdownReceiveTextMessageTaskAsync(cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _webSocketClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _webSocketClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task SendNotificationAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
    {
        await _webSocketClient.SendJsonRpc2NotificationAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task<TResponse?> SendRequestAsync<TParams, TResponse>(JsonRpc2RequestDto<TParams> request, CancellationToken cancellationToken = default)
        where TResponse : class
    {
        return await _webSocketClient.SendJsonRpc2RequestAsync<TParams, TResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    protected virtual async Task ReceiveTextMessagesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (await _webSocketClient.TextMessageReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_webSocketClient.TextMessageReader.TryRead(out WebSocketReceivedMessage<string> receivedTextMessage))
                {
                    _logger.ReceivedTextMessageInfo(receivedTextMessage.ReceivedAt.ToString("u"), receivedTextMessage.Data);

                    if (string.IsNullOrWhiteSpace(receivedTextMessage.Data))
                    {
                        _logger.ReceivedNullOrWhitespaceTextMessageWarning(receivedTextMessage.ReceivedAt.ToString("u"));
                        continue;
                    }

                    JsonNode? messageJson;
                    try
                    {
                        messageJson = JsonNode.Parse(receivedTextMessage.Data);
                    }
                    catch (Exception ex)
                    {
                        _logger.ParseReceivedTextMessageException(receivedTextMessage.ReceivedAt.ToString("u"), receivedTextMessage.Data, ex.Message, ex.InnerException?.Message, ex.StackTrace);
                        continue;
                    }

                    if (messageJson == null)
                    {
                        _logger.ParseReceivedTextMessageError(receivedTextMessage.ReceivedAt.ToString("u"), receivedTextMessage.Data);
                        continue;
                    }

                    string? jsonrpc = (string?)messageJson["jsonrpc"];
                    if (string.IsNullOrWhiteSpace(jsonrpc))
                    {
                        _logger.JsonRpc2JsonRpcMemberNullError(receivedTextMessage.ReceivedAt.ToString("u"), receivedTextMessage.Data);
                        continue;
                    }

                    if (!jsonrpc.Equals("2.0", StringComparison.Ordinal))
                    {
                        _logger.JsonRpc2JsonRpcMemberInvalidError(receivedTextMessage.ReceivedAt.ToString("u"), receivedTextMessage.Data);
                        continue;
                    }

                    string? method = (string?)messageJson["method"];
                    if (string.IsNullOrWhiteSpace(method))
                    {
                        _logger.JsonRpc2MethodMemberNullError(receivedTextMessage.ReceivedAt.ToString("u"), receivedTextMessage.Data);
                        continue;
                    }

                    try
                    {
                        await ProcessServerNotificationAsync(method, receivedTextMessage.Data, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.ProcessServerNotificationCanceledInfo();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.ProcessServerNotificationException(receivedTextMessage.ReceivedAt.ToString("u"), receivedTextMessage.Data, ex.Message, ex.InnerException?.Message, ex.StackTrace);
                    }
                }
            }

            _logger.ReceiveTextMessagesCanceledInfo();
        }
        catch (OperationCanceledException)
        {
            _logger.ReceiveTextMessagesCanceledExceptionInfo();
            throw;
        }
        catch (Exception ex)
        {
            _logger.ReceiveTextMessagesException(ex.Message, ex.InnerException?.Message, ex.StackTrace);
        }
    }

    protected virtual async Task ShutdownReceiveTextMessageTaskAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _receiveTextMessageCts?.Cancel();

            if (_receiveTextMessageTask != null)
            {
                await _receiveTextMessageTask.WaitAsync(TimeSpan.FromMilliseconds(GracefulShutdownTimeoutMilliseconds), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (TimeoutException)
        {
            _logger.ReceiveTextMessageTaskShutdownTimeoutInfo();
        }
        catch (Exception ex)
        {
            _logger.ReceiveTextMessageTaskShutdownException(ex.Message, ex.InnerException?.Message, ex.StackTrace);
        }
        finally
        {
            _receiveTextMessageCts?.Dispose();
            _receiveTextMessageCts = null;

            _receiveTextMessageTask?.Dispose();
            _receiveTextMessageTask = null;
        }
    }

    public virtual async Task SubscribeChannelsAsync(List<string> channels, CancellationToken cancellationToken = default)
    {
        List<string> newlyAddedChannels = [];
        foreach (string channel in channels)
        {
            bool isAdded = _subscribedChannels.TryAdd(channel, byte.MinValue);
            if (isAdded)
            {
                newlyAddedChannels.Add(channel);
            }
        }

        if (hasSubscribedOnConnected && newlyAddedChannels.Count > 0)
        {
            try
            {
                await SendSubscribeRequestAsync(newlyAddedChannels, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.SubscribeChannelsCanceledInfo(newlyAddedChannels);
                throw;
            }
            catch (Exception ex)
            {
                _logger.SubscribeChannelsException(newlyAddedChannels, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            }
        }
    }

    public virtual async Task UnsubscribeChannelsAsync(List<string> channels, CancellationToken cancellationToken = default)
    {
        List<string> newlyRemovedChannels = [];
        foreach (string channel in channels)
        {
            bool isRemoved = _subscribedChannels.TryRemove(new KeyValuePair<string, byte>(channel, byte.MinValue));
            if (isRemoved)
            {
                newlyRemovedChannels.Add(channel);
            }
        }

        if (hasSubscribedOnConnected && newlyRemovedChannels.Count > 0)
        {
            try
            {
                await SendUnsubscribeRequestAsync(newlyRemovedChannels, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.UnsubscribeChannelsCanceledInfo(newlyRemovedChannels);
                throw;
            }
            catch (Exception ex)
            {
                _logger.UnsubscribeChannelsException(newlyRemovedChannels, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            }
        }
    }

    protected virtual async Task ProcessServerNotificationAsync(string method, string message, CancellationToken cancellationToken = default)
    {
    }

    protected virtual async Task SendSubscribeRequestAsync(List<string> channels, CancellationToken cancellationToken = default)
    {
    }

    protected virtual async Task SendUnsubscribeRequestAsync(List<string> channels, CancellationToken cancellationToken = default)
    {
    }

    protected virtual async Task SetupServerHeartbeatAsync(CancellationToken cancellationToken = default)
    {
    }
}

public static partial class PublicClientWebSocketServiceBaseLogs
{
    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "Subscribing to channels: {Channels}")]
    public static partial void SubscribingChannels(
        this ILogger logger, List<string> channels);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Information,
        Message = "Subscribe on connected canceled")]
    public static partial void SubscribeOnConnectedCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Error,
        Message = "Subscribe on connected exception for channels `{Channels}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SubscribeOnConnectedException(
        this ILogger logger, List<string> channels, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Information,
        Message = "Received text message at {ReceivedAt}: {TextMessage}")]
    public static partial void ReceivedTextMessageInfo(
        this ILogger logger, string receivedAt, string textMessage);

    [LoggerMessage(
        EventId = 50,
        Level = LogLevel.Warning,
        Message = "Received null or whitespace text message at {ReceivedAt}")]
    public static partial void ReceivedNullOrWhitespaceTextMessageWarning(
        this ILogger logger, string receivedAt);

    [LoggerMessage(
        EventId = 60,
        Level = LogLevel.Error,
        Message = "Deserialize received text message exception for `{ReceivedAt} {TextMessage}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ParseReceivedTextMessageException(
        this ILogger logger, string receivedAt, string textMessage, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 70,
        Level = LogLevel.Error,
        Message = "Deserialize received text message error: {ReceivedAt} {TextMessage}")]
    public static partial void ParseReceivedTextMessageError(
        this ILogger logger, string receivedAt, string textMessage);

    [LoggerMessage(
        EventId = 80,
        Level = LogLevel.Error,
        Message = "Missing jsonrpc member error: {ReceivedAt} {TextMessage}")]
    public static partial void JsonRpc2JsonRpcMemberNullError(
        this ILogger logger, string receivedAt, string textMessage);

    [LoggerMessage(
        EventId = 90,
        Level = LogLevel.Error,
        Message = "Invalid jsonrpc member error: {ReceivedAt} {TextMessage}")]
    public static partial void JsonRpc2JsonRpcMemberInvalidError(
        this ILogger logger, string receivedAt, string textMessage);

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Error,
        Message = "Method null error: {ReceivedAt} {TextMessage}")]
    public static partial void JsonRpc2MethodMemberNullError(
        this ILogger logger, string receivedAt, string textMessage);

    [LoggerMessage(
        EventId = 110,
        Level = LogLevel.Information,
        Message = "Process server notification canceled")]
    public static partial void ProcessServerNotificationCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 120,
        Level = LogLevel.Error,
        Message = "Process server notification exception for `{ReceivedAt} {TextMessage}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ProcessServerNotificationException(
        this ILogger logger, string receivedAt, string textMessage, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 130,
        Level = LogLevel.Information,
        Message = "Receive text messages canceled")]
    public static partial void ReceiveTextMessagesCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 140,
        Level = LogLevel.Information,
        Message = "Receive text messages canceled exception")]
    public static partial void ReceiveTextMessagesCanceledExceptionInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 150,
        Level = LogLevel.Error,
        Message = "Receive text message exception with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReceiveTextMessagesException(
        this ILogger logger, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 160,
        Level = LogLevel.Information,
        Message = "Receive text message task shutdown timeout")]
    public static partial void ReceiveTextMessageTaskShutdownTimeoutInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 170,
        Level = LogLevel.Error,
        Message = "Receive text message task shutdown exception with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReceiveTextMessageTaskShutdownException(
        this ILogger logger, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 180,
        Level = LogLevel.Information,
        Message = "Subscribe channels canceled for `{Channels}`")]
    public static partial void SubscribeChannelsCanceledInfo(
        this ILogger logger, List<string> channels);

    [LoggerMessage(
        EventId = 190,
        Level = LogLevel.Error,
        Message = "Subscribe channels exception for `{Channels}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SubscribeChannelsException(
        this ILogger logger, List<string> channels, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 200,
        Level = LogLevel.Information,
        Message = "Unsubscribe channels canceled for `{Channels}`")]
    public static partial void UnsubscribeChannelsCanceledInfo(
        this ILogger logger, List<string> channels);

    [LoggerMessage(
        EventId = 210,
        Level = LogLevel.Error,
        Message = "Unsubscribe channels exception for `{Channels}`with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void UnsubscribeChannelsException(
        this ILogger logger, List<string> channels, string exceptionMessage, string? innerException, string? stackTrace);
}
