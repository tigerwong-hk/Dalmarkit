using Dalmarkit.Common.Dtos.RequestDtos;
using Mediator;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Dalmarkit.Common.Services.WebSocketServices;

public abstract class PublicClientWebSocketServiceBase(
    IWebSocketClient webSocketClient,
    ILogger<PublicClientWebSocketServiceBase> logger) : IPublicClientWebSocketService,
        INotificationHandler<WebSocketClientEvents.OnWebSocketConnected>,
        INotificationHandler<WebSocketClientEvents.OnWebSocketDisconnected>
{
    public const int GracefulShutdownTimeoutMilliseconds = 10000;
    public const int SubscribedChannelMessageDefaultCapacity = 8192;
    public const int SubscribedChannelsInitialCapacity = 2053;

    private readonly IWebSocketClient _webSocketClient = webSocketClient ?? throw new ArgumentNullException(nameof(webSocketClient));
    private readonly ILogger<PublicClientWebSocketServiceBase> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly ConcurrentDictionary<string, string> _subscribedChannels = new(Environment.ProcessorCount, SubscribedChannelsInitialCapacity);
    private readonly ConcurrentDictionary<string, Channel<WebSocketReceivedMessage<string>>> _notificationMessageTypes = new(Environment.ProcessorCount, SubscribedChannelsInitialCapacity);

    private int _hasSubscribedOnConnected;
    private bool HasSubscribedOnConnected => Volatile.Read(ref _hasSubscribedOnConnected) != 0;

    private bool _isDisposed;
    private readonly SemaphoreSlim _receiveSemaphore = new(1, 1);
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

        _receiveTextMessageCts?.Cancel();
        _receiveTextMessageCts?.Dispose();
        _receiveTextMessageCts = null;

        // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
        _receiveTextMessageTask = null;

        _receiveSemaphore.Dispose();
    }

    public virtual async ValueTask Handle(WebSocketClientEvents.OnWebSocketConnected notification, CancellationToken cancellationToken = default)
    {
        try
        {
            await _receiveSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.SubscribeOnConnectedSemaphoreCanceledInfo();
            return;
        }

        List<string> channelNames = [.. _subscribedChannels.Keys];

        try
        {
            _receiveTextMessageCts?.Cancel();
            _receiveTextMessageCts?.Dispose();
            _receiveTextMessageCts = new();

            // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
            _receiveTextMessageTask = ReceiveWebSocketTextMessagesAsync(_receiveTextMessageCts.Token);

            await SetupServerHeartbeatAsync(cancellationToken).ConfigureAwait(false);

            _ = Interlocked.Exchange(ref _hasSubscribedOnConnected, 1);

            _logger.SubscribingChannels(channelNames);
            await SendSubscribeRequestAsync(channelNames, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.SubscribeOnConnectedCanceledInfo();
        }
        catch (Exception ex)
        {
            _logger.SubscribeOnConnectedException(channelNames, ex.Message, ex.InnerException?.Message, ex.StackTrace);
        }
        finally
        {
            _ = _receiveSemaphore.Release();
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

    protected virtual async Task ReceiveTextMessagesAsync(ChannelReader<WebSocketReceivedMessage<string>> channelReader, Func<string, Task<bool>> processReceiveTextMessage, CancellationToken cancellationToken = default)
    {
        try
        {
            while (await channelReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channelReader.TryRead(out WebSocketReceivedMessage<string> receivedTextMessage))
                {
                    _logger.ReceivedTextMessageDebug(receivedTextMessage.Data);

                    if (string.IsNullOrWhiteSpace(receivedTextMessage.Data))
                    {
                        _logger.ReceivedNullOrWhitespaceTextMessageWarning(receivedTextMessage.ReceivedAt.ToString("u"));
                        continue;
                    }

                    try
                    {
                        bool isSuccess = await processReceiveTextMessage(receivedTextMessage.Data).ConfigureAwait(false);
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

    protected virtual async Task ReceiveWebSocketTextMessagesAsync(CancellationToken cancellationToken = default)
    {
        await ReceiveTextMessagesAsync(_webSocketClient.TextMessageReader,
            async (message) => await ProcessServerNotificationAsync(message, cancellationToken).ConfigureAwait(false),
        cancellationToken).ConfigureAwait(false);
    }

    protected virtual async Task ShutdownReceiveTextMessageTaskAsync(CancellationToken cancellationToken = default)
    {
        _logger.ReceiveTextMessageTaskShutdownInfo();

        try
        {
            await _receiveSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.ReceiveTextMessageTaskShutdownSemaphoreCanceledInfo();
            return;
        }

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
            // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
            _receiveTextMessageTask = null;

            _receiveTextMessageCts?.Dispose();
            _receiveTextMessageCts = null;

            _ = _receiveSemaphore.Release();
        }
    }

    protected virtual async Task<Dictionary<SubscriptionChannel, Channel<WebSocketReceivedMessage<string>>>> SubscribeChannelsInternalAsync(
        List<SubscriptionChannel> subscriptionChannels,
        Func<string, Channel<WebSocketReceivedMessage<string>>, bool> startReceiveChannelTaskDelegate,
        CancellationToken cancellationToken = default)
    {
        if (subscriptionChannels.Count == 0)
        {
            _logger.SubscribeChannelsInternalNoSubscriptionChannelsError();
            throw new ArgumentException("No subscription channels");
        }

        List<string> newlyAddedChannelNames = [];
        Dictionary<SubscriptionChannel, Channel<WebSocketReceivedMessage<string>>> result = [];

        foreach (SubscriptionChannel subscriptionChannel in subscriptionChannels)
        {
            try
            {
                bool isChannelAdded = _subscribedChannels.TryAdd(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType ?? subscriptionChannel.ChannelName);
                if (!isChannelAdded)
                {
                    _logger.SubscribeChannelsInternalSubscriptionChannelNotAddedInfo(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);
                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.SubscribeChannelsInternalSubscriptionChannelAddException(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType, ex.Message, ex.InnerException?.Message, ex.StackTrace);
                continue;
            }

            Channel<WebSocketReceivedMessage<string>> messageChannel = Channel.CreateBounded(
                    new BoundedChannelOptions(SubscribedChannelMessageDefaultCapacity)
                    {
                        FullMode = BoundedChannelFullMode.DropWrite,
                        SingleReader = false,
                        SingleWriter = true,
                        AllowSynchronousContinuations = false
                    },
                    void (WebSocketReceivedMessage<string> dropped) => _logger.SubscribedChannelMessageDroppedWarning(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType, dropped.ReceivedAt, dropped.Data));

            try
            {
                bool isMessageTypeAdded = _notificationMessageTypes.TryAdd(subscriptionChannel.NotificationMessageType ?? subscriptionChannel.ChannelName, messageChannel);
                if (!isMessageTypeAdded)
                {
                    _logger.SubscribeChannelsInternalNotificationMessageTypeNotAddedError(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);

                    bool isChannelRemoved = _subscribedChannels.TryRemove(subscriptionChannel.ChannelName, out _);
                    if (!isChannelRemoved)
                    {
                        _logger.SubscribeChannelsInternalChannelNotRemovedForAddErrorWarning(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);
                    }

                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.SubscribeChannelsInternalNotificationMessageTypeAddException(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType, ex.Message, ex.InnerException?.Message, ex.StackTrace);

                bool isChannelRemoved = _subscribedChannels.TryRemove(subscriptionChannel.ChannelName, out _);
                if (!isChannelRemoved)
                {
                    _logger.SubscribeChannelsInternalChannelNotRemovedForAddExceptionWarning(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);
                }

                continue;
            }

            try
            {
                _ = startReceiveChannelTaskDelegate(subscriptionChannel.ChannelName, messageChannel);
            }
            catch (Exception ex)
            {
                _logger.SubscribeChannelsInternalStartReceiveChannelTaskException(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            }

            newlyAddedChannelNames.Add(subscriptionChannel.ChannelName);
            result.Add(subscriptionChannel, messageChannel);
        }

        if (HasSubscribedOnConnected && newlyAddedChannelNames.Count > 0)
        {
            try
            {
                await SendSubscribeRequestAsync(newlyAddedChannelNames, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.SubscribeChannelsInternalCanceledInfo(newlyAddedChannelNames);
                throw;
            }
            catch (Exception ex)
            {
                _logger.SubscribeChannelsInternalException(newlyAddedChannelNames, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            }
        }

        return result;
    }

    protected virtual async Task<List<string>> UnsubscribeChannelsInternalAsync(List<string> channelNames, CancellationToken cancellationToken = default)
    {
        if (channelNames.Count == 0)
        {
            _logger.UnsubscribeChannelsInternalNoChannelNamesError();
            throw new ArgumentException("No channel names");
        }

        List<string> newlyRemovedChannelNames = [];
        foreach (string channelName in channelNames)
        {
            bool isChannelRemoved = _subscribedChannels.TryRemove(channelName, out string? notificationMessageType);
            if (!isChannelRemoved)
            {
                _logger.UnsubscribeChannelsInternalSubscriptionChannelNotRemovedWarning(channelName);
                continue;
            }
            if (string.IsNullOrWhiteSpace(notificationMessageType))
            {
                _logger.UnsubscribeChannelsInternalNotificationMessageTypeNullError(channelName);
                continue;
            }

            bool isMessageTypeRemoved = _notificationMessageTypes.TryRemove(notificationMessageType, out Channel<WebSocketReceivedMessage<string>>? messageChannel);
            if (!isMessageTypeRemoved)
            {
                _logger.UnsubscribeChannelsInternalNotificationMessageTypeNotRemovedError(channelName, notificationMessageType);
                continue;
            }

            newlyRemovedChannelNames.Add(channelName);

            if (messageChannel == null)
            {
                _logger.UnsubscribeChannelsInternalMessageChannelNullWarning(channelName, notificationMessageType);
                continue;
            }

            bool isChannelMarkedComplete = messageChannel.Writer.TryComplete();
            if (!isChannelMarkedComplete)
            {
                _logger.UnsubscribeChannelsInternalMessageChannelNotMarkedCompleteWarning(channelName, notificationMessageType);
            }
        }

        if (HasSubscribedOnConnected && newlyRemovedChannelNames.Count > 0)
        {
            try
            {
                await SendUnsubscribeRequestAsync(newlyRemovedChannelNames, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.UnsubscribeChannelsInternalCanceledInfo(newlyRemovedChannelNames);
                throw;
            }
            catch (Exception ex)
            {
                _logger.UnsubscribeChannelsInternalException(newlyRemovedChannelNames, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            }
        }

        return newlyRemovedChannelNames;
    }

    protected abstract Task<bool> ProcessServerNotificationAsync(string message, CancellationToken cancellationToken = default);

    protected abstract Task SendSubscribeRequestAsync(List<string> channelNames, CancellationToken cancellationToken = default);

    protected abstract Task SendUnsubscribeRequestAsync(List<string> channelNames, CancellationToken cancellationToken = default);

    protected abstract Task SetupServerHeartbeatAsync(CancellationToken cancellationToken = default);
}

public static partial class PublicClientWebSocketServiceBaseLogs
{
    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "Subscribing to channels: {ChannelNames}")]
    public static partial void SubscribingChannels(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Information,
        Message = "Subscribe on connected canceled")]
    public static partial void SubscribeOnConnectedCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Information,
        Message = "Subscribe on connected semaphore canceled")]
    public static partial void SubscribeOnConnectedSemaphoreCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Error,
        Message = "Subscribe on connected exception for channels `{ChannelNames}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SubscribeOnConnectedException(
        this ILogger logger, List<string> channelNames, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 50,
        Level = LogLevel.Debug,
        Message = "Received text message: {TextMessage}")]
    public static partial void ReceivedTextMessageDebug(
        this ILogger logger, string textMessage);

    [LoggerMessage(
        EventId = 60,
        Level = LogLevel.Warning,
        Message = "Received null or whitespace text message at {ReceivedAt}")]
    public static partial void ReceivedNullOrWhitespaceTextMessageWarning(
        this ILogger logger, string receivedAt);

    [LoggerMessage(
        EventId = 70,
        Level = LogLevel.Error,
        Message = "Deserialize received text message exception for `{ReceivedAt} {TextMessage}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ParseReceivedTextMessageException(
        this ILogger logger, string receivedAt, string textMessage, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 80,
        Level = LogLevel.Error,
        Message = "Deserialize received text message error: {ReceivedAt} {TextMessage}")]
    public static partial void ParseReceivedTextMessageError(
        this ILogger logger, string receivedAt, string textMessage);

    [LoggerMessage(
        EventId = 90,
        Level = LogLevel.Error,
        Message = "Missing jsonrpc member error: {ReceivedAt} {TextMessage}")]
    public static partial void JsonRpc2JsonRpcMemberNullError(
        this ILogger logger, string receivedAt, string textMessage);

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Error,
        Message = "Invalid jsonrpc member error: {ReceivedAt} {TextMessage}")]
    public static partial void JsonRpc2JsonRpcMemberInvalidError(
        this ILogger logger, string receivedAt, string textMessage);

    [LoggerMessage(
        EventId = 110,
        Level = LogLevel.Error,
        Message = "Method null error: {ReceivedAt} {TextMessage}")]
    public static partial void JsonRpc2MethodMemberNullError(
        this ILogger logger, string receivedAt, string textMessage);

    [LoggerMessage(
        EventId = 120,
        Level = LogLevel.Information,
        Message = "Process server notification canceled")]
    public static partial void ProcessServerNotificationCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 130,
        Level = LogLevel.Error,
        Message = "Process server notification exception for `{ReceivedAt} {TextMessage}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ProcessServerNotificationException(
        this ILogger logger, string receivedAt, string textMessage, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 140,
        Level = LogLevel.Information,
        Message = "Receive text messages canceled")]
    public static partial void ReceiveTextMessagesCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 150,
        Level = LogLevel.Information,
        Message = "Receive text messages canceled exception")]
    public static partial void ReceiveTextMessagesCanceledExceptionInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 160,
        Level = LogLevel.Error,
        Message = "Receive text message exception with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReceiveTextMessagesException(
        this ILogger logger, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 170,
        Level = LogLevel.Information,
        Message = "Receive text message task shutdown timeout")]
    public static partial void ReceiveTextMessageTaskShutdownTimeoutInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 180,
        Level = LogLevel.Information,
        Message = "Receive text message task shutdown")]
    public static partial void ReceiveTextMessageTaskShutdownInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 190,
        Level = LogLevel.Information,
        Message = "Receive text message task shutdown semaphore canceled")]
    public static partial void ReceiveTextMessageTaskShutdownSemaphoreCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 200,
        Level = LogLevel.Error,
        Message = "Receive text message task shutdown exception with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ReceiveTextMessageTaskShutdownException(
        this ILogger logger, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 210,
        Level = LogLevel.Error,
        Message = "Subscribe channels internal with no subscription channels")]
    public static partial void SubscribeChannelsInternalNoSubscriptionChannelsError(
        this ILogger logger);

    [LoggerMessage(
        EventId = 220,
        Level = LogLevel.Information,
        Message = "Subscribe channels internal subscription channel not added for `{ChannelName}`: {NotificationMessageType}")]
    public static partial void SubscribeChannelsInternalSubscriptionChannelNotAddedInfo(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 230,
        Level = LogLevel.Error,
        Message = "Subscribe channels internal add subscription channel exception for `{ChannelName}`/`{NotificationMessageType}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SubscribeChannelsInternalSubscriptionChannelAddException(
        this ILogger logger, string channelName, string? notificationMessageType, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 240,
        Level = LogLevel.Error,
        Message = "Subscribe channels internal notification message type not added for `{ChannelName}`: {NotificationMessageType}")]
    public static partial void SubscribeChannelsInternalNotificationMessageTypeNotAddedError(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 250,
        Level = LogLevel.Warning,
        Message = "Subscribe channels internal channel not removed for add error of `{ChannelName}`: {NotificationMessageType}")]
    public static partial void SubscribeChannelsInternalChannelNotRemovedForAddErrorWarning(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 260,
        Level = LogLevel.Error,
        Message = "Subscribe channels internal add notification message type exception for `{ChannelName}`/`{NotificationMessageType}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SubscribeChannelsInternalNotificationMessageTypeAddException(
        this ILogger logger, string channelName, string? notificationMessageType, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 270,
        Level = LogLevel.Warning,
        Message = "Subscribe channels internal channel not removed for add exception of `{ChannelName}`: {NotificationMessageType}")]
    public static partial void SubscribeChannelsInternalChannelNotRemovedForAddExceptionWarning(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 280,
        Level = LogLevel.Error,
        Message = "Subscribe channels internal start receive channel task exception for `{ChannelName}`/`{NotificationMessageType}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SubscribeChannelsInternalStartReceiveChannelTaskException(
        this ILogger logger, string channelName, string? notificationMessageType, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 290,
        Level = LogLevel.Warning,
        Message = "Subscribed channel message dropped for `{ChannelName}`:`{NotificationMessageType}`: {ReceivedAt} `{Message}`")]
    public static partial void SubscribedChannelMessageDroppedWarning(
        this ILogger logger, string channelName, string? notificationMessageType, DateTimeOffset receivedAt, string message);

    [LoggerMessage(
        EventId = 300,
        Level = LogLevel.Information,
        Message = "Subscribe channels internal canceled for `{ChannelNames}`")]
    public static partial void SubscribeChannelsInternalCanceledInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 310,
        Level = LogLevel.Error,
        Message = "Subscribe channels internal exception for `{ChannelNames}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SubscribeChannelsInternalException(
        this ILogger logger, List<string> channelNames, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 320,
        Level = LogLevel.Error,
        Message = "Unsubscribe channels internal with no channel names")]
    public static partial void UnsubscribeChannelsInternalNoChannelNamesError(
        this ILogger logger);

    [LoggerMessage(
        EventId = 330,
        Level = LogLevel.Warning,
        Message = "Unsubscribe channels internal subscription channel not removed for `{ChannelName}`")]
    public static partial void UnsubscribeChannelsInternalSubscriptionChannelNotRemovedWarning(
        this ILogger logger, string channelName);

    [LoggerMessage(
        EventId = 340,
        Level = LogLevel.Error,
        Message = "Unsubscribe channels internal notification message type null for `{ChannelName}`")]
    public static partial void UnsubscribeChannelsInternalNotificationMessageTypeNullError(
        this ILogger logger, string channelName);

    [LoggerMessage(
        EventId = 350,
        Level = LogLevel.Error,
        Message = "Unsubscribe channels internal notification message type not removed for `{ChannelName}`: {NotificationMessageType}")]
    public static partial void UnsubscribeChannelsInternalNotificationMessageTypeNotRemovedError(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 360,
        Level = LogLevel.Warning,
        Message = "Unsubscribe channels internal message channel null for `{ChannelName}`: {NotificationMessageType}")]
    public static partial void UnsubscribeChannelsInternalMessageChannelNullWarning(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 370,
        Level = LogLevel.Warning,
        Message = "Unsubscribe channels internal message channel not marked as complete for `{ChannelName}`: {NotificationMessageType}")]
    public static partial void UnsubscribeChannelsInternalMessageChannelNotMarkedCompleteWarning(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 380,
        Level = LogLevel.Information,
        Message = "Unsubscribe channels internal canceled for `{ChannelNames}`")]
    public static partial void UnsubscribeChannelsInternalCanceledInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 390,
        Level = LogLevel.Error,
        Message = "Unsubscribe channels internal exception for `{ChannelNames}`with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void UnsubscribeChannelsInternalException(
        this ILogger logger, List<string> channelNames, string exceptionMessage, string? innerException, string? stackTrace);
}
