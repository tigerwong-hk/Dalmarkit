using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dalmarkit.Common.Dtos.RequestDtos;
using Mediator;
using Microsoft.Extensions.Logging;
using static Dalmarkit.Common.Services.WebSocketServices.IWebSocketClient;

namespace Dalmarkit.Common.Services.WebSocketServices;

public abstract class ClientWebSocketServiceBase(
    IWebSocketClient webSocketClient,
    ILogger<ClientWebSocketServiceBase> logger) : IClientWebSocketService,
        INotificationHandler<WebSocketClientEvents.OnWebSocketConnected>,
        INotificationHandler<WebSocketClientEvents.OnWebSocketConnecting>,
        INotificationHandler<WebSocketClientEvents.OnWebSocketDisconnected>,
        INotificationHandler<WebSocketClientEvents.OnWebSocketDisconnecting>
{
    public const int GracefulShutdownTimeoutMilliseconds = 10000;
    public const int SubscribedChannelMessageDefaultCapacity = 8192;
    public const int SubscribedChannelsInitialCapacity = 8209;

    public string? AuthenticationId { get; private set; }

    private readonly CancellationTokenSource _disposalCts = new();
    private readonly IWebSocketClient _webSocketClient = webSocketClient ?? throw new ArgumentNullException(nameof(webSocketClient));
    private readonly ILogger<ClientWebSocketServiceBase> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly ConcurrentDictionary<string, string> _subscribedChannels = new(Environment.ProcessorCount, SubscribedChannelsInitialCapacity);
    private readonly ConcurrentDictionary<string, Channel<WebSocketReceivedMessage<JsonNode>>> _notificationMessageTypes = new(Environment.ProcessorCount, SubscribedChannelsInitialCapacity);

    private int _hasSubscribedOnConnected;
    private bool HasSubscribedOnConnected => Volatile.Read(ref _hasSubscribedOnConnected) != 0;

    private volatile int _isDisposed;

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
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        if (!disposing)
        {
            return;
        }

        _receiveTextMessageCts?.Cancel();
        _disposalCts.Cancel();

        _receiveTextMessageCts?.Dispose();
        _receiveTextMessageCts = null;

        // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
        _receiveTextMessageTask = null;

        _disposalCts.Dispose();

        _receiveSemaphore.Dispose();
    }

    public virtual async ValueTask Handle(WebSocketClientEvents.OnWebSocketConnected notification, CancellationToken cancellationToken = default)
    {
        try
        {
            await NotifyWebSocketConnectionState(WebSocketConnectionState.Connected).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.HandleOnWebSocketConnectedNotifyWebSocketConnectionStateCanceledInfo();
        }
        catch (Exception ex)
        {
            _logger.HandleOnWebSocketConnectedNotifyWebSocketConnectionStateException(ex);
        }

        try
        {
            await _receiveSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.HandleOnWebSocketConnectedReceiveSemaphoreCanceledInfo();
            return;
        }

        List<string> channelNames = [.. _subscribedChannels.Keys];

        try
        {
            _receiveTextMessageCts?.Cancel();
            _receiveTextMessageCts?.Dispose();
            _receiveTextMessageCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token);

            // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
            _receiveTextMessageTask = ReceiveWebSocketTextMessagesAsync(_receiveTextMessageCts.Token);

            if (!string.IsNullOrWhiteSpace(AuthenticationId))
            {
                try
                {
                    await AuthenticateSessionAsync(AuthenticationId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.HandleOnWebSocketConnectedAuthenticateSessionException(channelNames, ex);
                    return;
                }
            }

            try
            {
                await SetupServerHeartbeatAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.HandleOnWebSocketConnectedSetupServerHeartbeatException(channelNames, ex);
            }

            _ = Interlocked.Exchange(ref _hasSubscribedOnConnected, 1);

            _logger.HandleOnWebSocketConnectedSubscribingExchangeChannelsInfo(channelNames);
            try
            {
                List<string> channelsNotSubscribed = await SubscribeExchangeChannelsAsync(channelNames, cancellationToken).ConfigureAwait(false);
                if (channelsNotSubscribed.Count > 0)
                {
                    _logger.HandleOnWebSocketConnectedNotSubscribedChannelsInfo(channelNames, channelsNotSubscribed);
                    await ReceiveChannelNotificationTasksRemoveAsync(channelsNotSubscribed, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.HandleOnWebSocketConnectedAllSubscribedChannelsInfo(channelNames);
                }
            }
            catch (Exception ex)
            {
                _logger.HandleOnWebSocketConnectedSubscribeExchangeChannelsException(channelNames, ex);
                await ReceiveChannelNotificationTasksRemoveAsync(channelNames, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.HandleOnWebSocketConnectedCanceledInfo(channelNames);
        }
        catch (Exception ex)
        {
            _logger.HandleOnWebSocketConnectedException(channelNames, ex);
        }
        finally
        {
            _ = _receiveSemaphore.Release();
        }
    }

    public virtual async ValueTask Handle(WebSocketClientEvents.OnWebSocketConnecting notification, CancellationToken cancellationToken = default)
    {
        try
        {
            await NotifyWebSocketConnectionState(WebSocketConnectionState.Connecting).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.HandleOnWebSocketConnectingNotifyWebSocketConnectionStateCanceledInfo();
        }
        catch (Exception ex)
        {
            _logger.HandleOnWebSocketConnectingNotifyWebSocketConnectionStateException(ex);
        }

    }

    public virtual async ValueTask Handle(WebSocketClientEvents.OnWebSocketDisconnected notification, CancellationToken cancellationToken = default)
    {
        try
        {
            await NotifyWebSocketConnectionState(WebSocketConnectionState.Disconnected).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.HandleOnWebSocketDisconnectedNotifyWebSocketConnectionStateCanceledInfo();
        }
        catch (Exception ex)
        {
            _logger.HandleOnWebSocketDisconnectedNotifyWebSocketConnectionStateException(ex);
        }

        AuthenticationId = null;
        _ = Interlocked.Exchange(ref _hasSubscribedOnConnected, 0);
        await ShutdownReceiveTextMessageTaskAsync(cancellationToken).ConfigureAwait(false);
    }

    public virtual async ValueTask Handle(WebSocketClientEvents.OnWebSocketDisconnecting notification, CancellationToken cancellationToken = default)
    {
        try
        {
            await NotifyWebSocketConnectionState(WebSocketConnectionState.Disconnecting).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.HandleOnWebSocketDisconnectingNotifyWebSocketConnectionStateCanceledInfo();
        }
        catch (Exception ex)
        {
            _logger.HandleOnWebSocketDisconnectingNotifyWebSocketConnectionStateException(ex);
        }

    }

    public virtual async Task ConnectAsync(string? authenticationId, CancellationToken cancellationToken = default)
    {
        AuthenticationId = authenticationId?.Trim();
        await _webSocketClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _webSocketClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public virtual List<string> GetSubscribedChannels()
    {
        return [.. _subscribedChannels.Keys];
    }

    public virtual async Task SendNotificationAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
    {
        await _webSocketClient.SendNotificationAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task<TResponse?> SendRequestAsync<TParams, TResponse>(JsonRpc2RequestDto<TParams> request, CancellationToken cancellationToken = default)
        where TResponse : class
    {
        return await _webSocketClient.SendJsonRpc2RequestAsync<TParams, TResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    protected virtual async Task ReceiveTextMessagesAsync(ChannelReader<WebSocketReceivedMessage<JsonNode>> channelReader, Func<JsonNode, Task<bool>> processReceiveTextMessage, CancellationToken cancellationToken = default)
    {
        try
        {
            while (await channelReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channelReader.TryRead(out WebSocketReceivedMessage<JsonNode> receivedTextMessage))
                {
                    if (receivedTextMessage.Data == null)
                    {
                        _logger.ReceiveTextMessagesAsyncNullTextMessageWarning(receivedTextMessage.ReceivedAt.ToString("u"));
                        continue;
                    }

                    _logger.ReceiveTextMessagesAsyncTextMessageDebug(receivedTextMessage.Data);

                    try
                    {
                        _ = await processReceiveTextMessage(receivedTextMessage.Data).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.ReceiveTextMessagesAsyncProcessReceiveTextMessageCanceledInfo();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.ReceiveTextMessagesAsyncProcessReceiveTextMessageException(receivedTextMessage.ReceivedAt.ToString("u"), receivedTextMessage.Data, ex);
                    }
                }
            }

            _logger.ReceiveTextMessagesAsyncCanceledInfo();
        }
        catch (OperationCanceledException)
        {
            _logger.ReceiveTextMessagesAsyncCanceledExceptionInfo();
            throw;
        }
        catch (Exception ex)
        {
            _logger.ReceiveTextMessagesAsyncException(ex);
        }
    }

    protected virtual async Task ReceiveWebSocketTextMessagesAsync(CancellationToken cancellationToken = default)
    {
        await ReceiveTextMessagesAsync(_webSocketClient.TextMessageReader,
            async (message) => await ProcessServerNotificationAsync(message, cancellationToken).ConfigureAwait(false),
        cancellationToken).ConfigureAwait(false);
    }

    protected virtual List<string> RemoveChannels(List<string> channelNames)
    {
        List<string> removedChannelNames = [];
        foreach (string channelName in channelNames)
        {
            bool isChannelRemoved = _subscribedChannels.TryRemove(channelName, out string? notificationMessageType);
            if (!isChannelRemoved)
            {
                _logger.RemoveChannelsSubscribedChannelNotRemovedWarning(channelName);
                continue;
            }
            if (string.IsNullOrWhiteSpace(notificationMessageType))
            {
                _logger.RemoveChannelsNullNotificationMessageTypeError(channelName);
                continue;
            }

            bool isMessageTypeRemoved = _notificationMessageTypes.TryRemove(notificationMessageType, out Channel<WebSocketReceivedMessage<JsonNode>>? messageChannel);
            if (!isMessageTypeRemoved)
            {
                _logger.RemoveChannelsNotificationMessageTypeNotRemovedWarning(channelName, notificationMessageType);
                continue;
            }

            removedChannelNames.Add(channelName);

            if (messageChannel == null)
            {
                _logger.RemoveChannelsNullMessageChannelWarning(channelName, notificationMessageType);
                continue;
            }

            bool isChannelMarkedComplete = messageChannel.Writer.TryComplete();
            if (!isChannelMarkedComplete)
            {
                _logger.RemoveChannelsNotMarkedCompleteMessageChannelWarning(channelName, notificationMessageType);
            }
        }

        return removedChannelNames;
    }

    protected virtual async Task ShutdownReceiveTextMessageTaskAsync(CancellationToken cancellationToken = default)
    {
        _logger.ShutdownReceiveTextMessageTaskAsyncInfo();

        try
        {
            await _receiveSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.ShutdownReceiveTextMessageTaskAsyncReceiveSemaphoreCanceledInfo();
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
            _logger.ShutdownReceiveTextMessageTaskAsyncGracefulShutdownTimeoutInfo();
        }
        catch (Exception ex)
        {
            _logger.ShutdownReceiveTextMessageTaskAsyncGracefulShutdownException(ex);
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

    protected virtual async Task<Dictionary<SubscriptionChannel, Channel<WebSocketReceivedMessage<JsonNode>>>> SubscribeChannelsInternalAsync(
        List<SubscriptionChannel> subscriptionChannels,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        if (subscriptionChannels.Count == 0)
        {
            _logger.SubscribeChannelsInternalAsyncNoSubscriptionChannelsError();
            throw new ArgumentException("No subscription channels");
        }

        List<string> addedChannelNames = [];
        Dictionary<SubscriptionChannel, Channel<WebSocketReceivedMessage<JsonNode>>> result = [];

        foreach (SubscriptionChannel subscriptionChannel in subscriptionChannels)
        {
            try
            {
                bool isChannelAdded = _subscribedChannels.TryAdd(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType ?? subscriptionChannel.ChannelName);
                if (!isChannelAdded)
                {
                    _logger.SubscribeChannelsInternalAsyncSubscriptionChannelNotAddedInfo(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);
                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.SubscribeChannelsInternalAsyncAddSubscriptionChannelException(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType, ex);
                continue;
            }

            Channel<WebSocketReceivedMessage<JsonNode>> messageChannel = Channel.CreateBounded(
                    new BoundedChannelOptions(SubscribedChannelMessageDefaultCapacity)
                    {
                        FullMode = BoundedChannelFullMode.DropWrite,
                        SingleReader = false,
                        SingleWriter = true,
                        AllowSynchronousContinuations = false
                    },
                    void (WebSocketReceivedMessage<JsonNode> dropped) => _logger.SubscribedChannelsInternalAsyncMessageDroppedWarning(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType, dropped.ReceivedAt, dropped.Data));

            try
            {
                bool isMessageTypeAdded = _notificationMessageTypes.TryAdd(subscriptionChannel.NotificationMessageType ?? subscriptionChannel.ChannelName, messageChannel);
                if (!isMessageTypeAdded)
                {
                    _logger.SubscribeChannelsInternalAsyncNotificationMessageTypeNotAddedError(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);

                    _ = messageChannel.Writer.TryComplete();

                    bool isChannelRemoved = _subscribedChannels.TryRemove(subscriptionChannel.ChannelName, out _);
                    if (!isChannelRemoved)
                    {
                        _logger.SubscribeChannelsInternalAsyncChannelNotRemovedForNotAddWarning(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);
                    }

                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.SubscribeChannelsInternalAsyncAddNotificationMessageTypeException(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType, ex);

                _ = messageChannel.Writer.TryComplete();

                bool isChannelRemoved = _subscribedChannels.TryRemove(subscriptionChannel.ChannelName, out _);
                if (!isChannelRemoved)
                {
                    _logger.SubscribeChannelsInternalAsyncChannelNotRemovedForAddExceptionWarning(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);
                }

                continue;
            }

            try
            {
                bool isTaskStarted = ReceiveChannelNotificationTaskStart(subscriptionChannel.ChannelName, messageChannel);
                if (!isTaskStarted)
                {
                    _logger.SubscribeChannelsInternalAsyncReceiveChannelNotificationTaskNotStartedError(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);

                    _ = messageChannel.Writer.TryComplete();

                    bool isNotificationMessageTypeRemoved = _notificationMessageTypes.TryRemove(subscriptionChannel.NotificationMessageType ?? subscriptionChannel.ChannelName, out _);
                    if (!isNotificationMessageTypeRemoved)
                    {
                        _logger.SubscribeChannelsInternalAsyncNotificationMessageTypeNotRemovedForTaskNotStartedWarning(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);
                    }

                    bool isChannelRemoved = _subscribedChannels.TryRemove(subscriptionChannel.ChannelName, out _);
                    if (!isChannelRemoved)
                    {
                        _logger.SubscribeChannelsInternalAsyncChannelNotRemovedForTaskNotStartedWarning(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);
                    }

                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.SubscribeChannelsInternalAsyncStartReceiveChannelNotificationTaskException(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType, ex);

                _ = messageChannel.Writer.TryComplete();

                bool isNotificationMessageTypeRemoved = _notificationMessageTypes.TryRemove(subscriptionChannel.NotificationMessageType ?? subscriptionChannel.ChannelName, out _);
                if (!isNotificationMessageTypeRemoved)
                {
                    _logger.SubscribeChannelsInternalAsyncNotificationMessageTypeNotRemovedForStartTaskExceptionWarning(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);
                }

                bool isChannelRemoved = _subscribedChannels.TryRemove(subscriptionChannel.ChannelName, out _);
                if (!isChannelRemoved)
                {
                    _logger.SubscribeChannelsInternalAsyncChannelNotRemovedForStartTaskExceptionWarning(subscriptionChannel.ChannelName, subscriptionChannel.NotificationMessageType);
                }

                continue;
            }

            addedChannelNames.Add(subscriptionChannel.ChannelName);
            result.Add(subscriptionChannel, messageChannel);
        }

        if (HasSubscribedOnConnected && addedChannelNames.Count > 0)
        {
            try
            {
                _logger.SubscribeChannelsInternalAsyncSubscribingChannelsInfo(addedChannelNames);
                List<string> channelsNotSubscribed = await SubscribeExchangeChannelsAsync(addedChannelNames, cancellationToken).ConfigureAwait(false);
                if (channelsNotSubscribed.Count > 0)
                {
                    _logger.SubscribeChannelsInternalAsyncChannelsNotSubscribedInfo(channelsNotSubscribed);
                    await ReceiveChannelNotificationTasksRemoveAsync(channelsNotSubscribed, cancellationToken).ConfigureAwait(false);
                    result = result.Where(kvp => !channelsNotSubscribed.Contains(kvp.Key.ChannelName, StringComparer.Ordinal))
                            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.SubscribeChannelsInternalAsyncCanceledInfo(addedChannelNames);
                throw;
            }
            catch (Exception ex)
            {
                _logger.SubscribeChannelsInternalAsyncException(addedChannelNames, ex);
                await ReceiveChannelNotificationTasksRemoveAsync(addedChannelNames, cancellationToken).ConfigureAwait(false);
                result = [];
            }
        }

        return result;
    }

    protected virtual async Task<List<string>> SubscribeExchangeChannelsAsync(List<string> channelNames, CancellationToken cancellationToken = default)
    {
        List<string> channelsSubscribed = [];
        try
        {
            channelsSubscribed = await SendSubscribeRequestAsync(channelNames, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.SubscribeExchangeChannelsAsyncSendSubscribeRequestException(channelNames, ex);
        }

        IEnumerable<string> channelsNotSubscribed = channelNames.Except(channelsSubscribed, StringComparer.Ordinal);
        List<string> channelsToRemove = [.. channelsNotSubscribed];
        if (channelsToRemove.Count > 0)
        {
            try
            {
                List<string> removedChannels = RemoveChannels(channelsToRemove);
                if (removedChannels.Count > 0)
                {
                    _logger.SubscribeExchangeChannelsAsyncRemovedChannelsInfo(removedChannels);
                }
            }
            catch (Exception ex)
            {
                _logger.SubscribeExchangeChannelsAsyncRemoveChannelsException(channelsToRemove, ex);
            }
        }

        return channelsToRemove;
    }

    protected virtual async Task<List<string>> UnsubscribeChannelsInternalAsync(List<string> channelNames, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        if (channelNames.Count == 0)
        {
            _logger.UnsubscribeChannelsInternalAsyncNoChannelNamesError();
            throw new ArgumentException("No channel names");
        }

        List<string> removedChannelNames = RemoveChannels(channelNames);

        if (HasSubscribedOnConnected && removedChannelNames.Count > 0)
        {
            try
            {
                _logger.UnsubscribeChannelsInternalAsyncUnsubscribingChannelsInfo(removedChannelNames);
                List<string> channelsNotUnsubscribed = await UnsubscribeExchangeChannelsAsync(removedChannelNames, cancellationToken).ConfigureAwait(false);
                _logger.UnsubscribeChannelsInternalAsyncChannelsNotUnsubscribedInfo(channelsNotUnsubscribed);
            }
            catch (OperationCanceledException)
            {
                _logger.UnsubscribeChannelsInternalAsyncUnsubscribeExchangeChannelsCanceledInfo(removedChannelNames);
                throw;
            }
            catch (Exception ex)
            {
                _logger.UnsubscribeChannelsInternalAsyncUnsubscribeExchangeChannelsException(removedChannelNames, ex);
            }
        }

        return removedChannelNames;
    }

    protected virtual async Task<List<string>> UnsubscribeExchangeChannelsAsync(List<string> channelNames, CancellationToken cancellationToken = default)
    {
        List<string> channelsUnsubscribed = [];
        try
        {
            channelsUnsubscribed = await SendUnsubscribeRequestAsync(channelNames, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.UnsubscribeExchangeChannelsAsyncSendUnsubscribeRequestException(channelNames, ex);
        }

        IEnumerable<string> channelsNotUnsubscribed = channelNames.Except(channelsUnsubscribed);

        return [.. channelsNotUnsubscribed];
    }

    protected virtual Task AuthenticateSessionAsync(string authenticationId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    protected abstract Task NotifyWebSocketConnectionState(WebSocketConnectionState webSocketConnectionState);

    protected abstract Task<bool> ProcessServerNotificationAsync(JsonNode messageJson, CancellationToken cancellationToken = default);

    protected abstract bool ReceiveChannelNotificationTaskStart(string channelName, Channel<WebSocketReceivedMessage<JsonNode>> receiveChannel);

    protected abstract Task ReceiveChannelNotificationTasksRemoveAsync(List<string> unsubscribedChannelNames, CancellationToken cancellationToken = default);

    protected abstract Task<List<string>> SendSubscribeRequestAsync(List<string> channelNames, CancellationToken cancellationToken = default);

    protected abstract Task<List<string>> SendUnsubscribeRequestAsync(List<string> channelNames, CancellationToken cancellationToken = default);

    protected abstract Task SetupServerHeartbeatAsync(CancellationToken cancellationToken = default);
}

public static partial class ClientWebSocketServiceBaseLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Information,
        Message = "HandleOnWebSocketConnected: notify websocket connection state canceled")]
    public static partial void HandleOnWebSocketConnectedNotifyWebSocketConnectionStateCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Error,
        Message = "HandleOnWebSocketConnected: notify connected connection state exception")]
    public static partial void HandleOnWebSocketConnectedNotifyWebSocketConnectionStateException(
        this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Information,
        Message = "HandleOnWebSocketConnected: receive semaphore canceled")]
    public static partial void HandleOnWebSocketConnectedReceiveSemaphoreCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 1040,
        Level = LogLevel.Error,
        Message = "HandleOnWebSocketConnected: authenticate session exception for channels `${ChannelNames}`")]
    public static partial void HandleOnWebSocketConnectedAuthenticateSessionException(
        this ILogger logger, List<string> channelNames, Exception exception);

    [LoggerMessage(
        EventId = 1050,
        Level = LogLevel.Error,
        Message = "HandleOnWebSocketConnected: setup server heartbeat exception for channels `${ChannelNames}`")]
    public static partial void HandleOnWebSocketConnectedSetupServerHeartbeatException(
        this ILogger logger, List<string> channelNames, Exception exception);

    [LoggerMessage(
        EventId = 1060,
        Level = LogLevel.Information,
        Message = "HandleOnWebSocketConnected: subscribing to exchange channels `{ChannelNames}`")]
    public static partial void HandleOnWebSocketConnectedSubscribingExchangeChannelsInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 1070,
        Level = LogLevel.Information,
        Message = "HandleOnWebSocketConnected: exchange channels not subscribed for channels `{ChannelNames}`: {NotSubscribedChannelNames}")]
    public static partial void HandleOnWebSocketConnectedNotSubscribedChannelsInfo(
        this ILogger logger, List<string> channelNames, List<string> notSubscribedChannelNames);

    [LoggerMessage(
        EventId = 1080,
        Level = LogLevel.Information,
        Message = "HandleOnWebSocketConnected: exchange channels all subscribed for channels `{ChannelNames}`")]
    public static partial void HandleOnWebSocketConnectedAllSubscribedChannelsInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 1090,
        Level = LogLevel.Error,
        Message = "HandleOnWebSocketConnected: subscribe exchange channels exception for channels `{ChannelNames}`")]
    public static partial void HandleOnWebSocketConnectedSubscribeExchangeChannelsException(
        this ILogger logger, List<string> channelNames, Exception exception);

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "HandleOnWebSocketConnected: canceled for channels `{ChannelNames}`")]
    public static partial void HandleOnWebSocketConnectedCanceledInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 1110,
        Level = LogLevel.Error,
        Message = "HandleOnWebSocketConnected: exception for channels `{ChannelNames}`")]
    public static partial void HandleOnWebSocketConnectedException(
        this ILogger logger, List<string> channelNames, Exception exception);

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Information,
        Message = "HandleOnWebSocketConnecting: notify websocket connection state canceled")]
    public static partial void HandleOnWebSocketConnectingNotifyWebSocketConnectionStateCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 2020,
        Level = LogLevel.Error,
        Message = "HandleOnWebSocketConnecting: notify websocket connection state exception")]
    public static partial void HandleOnWebSocketConnectingNotifyWebSocketConnectionStateException(
        this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Information,
        Message = "HandleOnWebSocketDisconnected: notify websocket connection state canceled")]
    public static partial void HandleOnWebSocketDisconnectedNotifyWebSocketConnectionStateCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 3020,
        Level = LogLevel.Error,
        Message = "HandleOnWebSocketDisconnected: notify websocket connection state exception")]
    public static partial void HandleOnWebSocketDisconnectedNotifyWebSocketConnectionStateException(
        this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Information,
        Message = "HandleOnWebSocketDisconnecting: notify websocket connection state canceled")]
    public static partial void HandleOnWebSocketDisconnectingNotifyWebSocketConnectionStateCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 4020,
        Level = LogLevel.Error,
        Message = "HandleOnWebSocketDisconnecting: notify websocket connection state exception")]
    public static partial void HandleOnWebSocketDisconnectingNotifyWebSocketConnectionStateException(
        this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 5010,
        Level = LogLevel.Warning,
        Message = "ReceiveTextMessagesAsync: received null or whitespace text message at {ReceivedAt}")]
    public static partial void ReceiveTextMessagesAsyncNullTextMessageWarning(
        this ILogger logger, string receivedAt);

    [LoggerMessage(
        EventId = 5020,
        Level = LogLevel.Debug,
        Message = "ReceiveTextMessagesAsync: received text message `{TextMessage}`")]
    public static partial void ReceiveTextMessagesAsyncTextMessageDebug(
        this ILogger logger, JsonNode textMessage);

    [LoggerMessage(
        EventId = 5030,
        Level = LogLevel.Information,
        Message = "ReceiveTextMessagesAsync: process receive text message canceled")]
    public static partial void ReceiveTextMessagesAsyncProcessReceiveTextMessageCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 5040,
        Level = LogLevel.Error,
        Message = "ReceiveTextMessagesAsync: process receive text message exception for text message `{TextMessage}` received at `{ReceivedAt}`")]
    public static partial void ReceiveTextMessagesAsyncProcessReceiveTextMessageException(
        this ILogger logger, string receivedAt, JsonNode textMessage, Exception exception);

    [LoggerMessage(
        EventId = 5050,
        Level = LogLevel.Information,
        Message = "ReceiveTextMessagesAsync: canceled")]
    public static partial void ReceiveTextMessagesAsyncCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 5060,
        Level = LogLevel.Information,
        Message = "ReceiveTextMessagesAsync: canceled exception")]
    public static partial void ReceiveTextMessagesAsyncCanceledExceptionInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 5070,
        Level = LogLevel.Error,
        Message = "ReceiveTextMessagesAsync: exception")]
    public static partial void ReceiveTextMessagesAsyncException(
        this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 6010,
        Level = LogLevel.Warning,
        Message = "RemoveChannels: subscribed channel not removed for channel `{ChannelName}`")]
    public static partial void RemoveChannelsSubscribedChannelNotRemovedWarning(
        this ILogger logger, string channelName);

    [LoggerMessage(
        EventId = 6020,
        Level = LogLevel.Error,
        Message = "RemoveChannels: null notification message type for channel `{ChannelName}`")]
    public static partial void RemoveChannelsNullNotificationMessageTypeError(
        this ILogger logger, string channelName);

    [LoggerMessage(
        EventId = 6030,
        Level = LogLevel.Warning,
        Message = "RemoveChannels: notification message type not removed for channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void RemoveChannelsNotificationMessageTypeNotRemovedWarning(
        this ILogger logger, string channelName, string notificationMessageType);

    [LoggerMessage(
        EventId = 6040,
        Level = LogLevel.Warning,
        Message = "RemoveChannels: null message channel for channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void RemoveChannelsNullMessageChannelWarning(
        this ILogger logger, string channelName, string notificationMessageType);

    [LoggerMessage(
        EventId = 6050,
        Level = LogLevel.Warning,
        Message = "RemoveChannels: channel not marked complete for channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void RemoveChannelsNotMarkedCompleteMessageChannelWarning(
        this ILogger logger, string channelName, string notificationMessageType);

    [LoggerMessage(
        EventId = 7010,
        Level = LogLevel.Information,
        Message = "ShutdownReceiveTextMessageTaskAsync: entry")]
    public static partial void ShutdownReceiveTextMessageTaskAsyncInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 7020,
        Level = LogLevel.Information,
        Message = "ShutdownReceiveTextMessageTaskAsync: receive semaphore canceled")]
    public static partial void ShutdownReceiveTextMessageTaskAsyncReceiveSemaphoreCanceledInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 7030,
        Level = LogLevel.Information,
        Message = "ShutdownReceiveTextMessageTaskAsync: graceful shutdown timeout")]
    public static partial void ShutdownReceiveTextMessageTaskAsyncGracefulShutdownTimeoutInfo(
        this ILogger logger);

    [LoggerMessage(
        EventId = 7040,
        Level = LogLevel.Error,
        Message = "ShutdownReceiveTextMessageTaskAsync: graceful shutdown exception")]
    public static partial void ShutdownReceiveTextMessageTaskAsyncGracefulShutdownException(
        this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 8010,
        Level = LogLevel.Error,
        Message = "SubscribeChannelsInternalAsync: no subscription channels")]
    public static partial void SubscribeChannelsInternalAsyncNoSubscriptionChannelsError(
        this ILogger logger);

    [LoggerMessage(
        EventId = 8020,
        Level = LogLevel.Information,
        Message = "SubscribeChannelsInternalAsync: subscription channel not added for channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void SubscribeChannelsInternalAsyncSubscriptionChannelNotAddedInfo(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 8030,
        Level = LogLevel.Error,
        Message = "SubscribeChannelsInternalAsync: add subscription channel exception for channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void SubscribeChannelsInternalAsyncAddSubscriptionChannelException(
        this ILogger logger, string channelName, string? notificationMessageType, Exception exception);

    [LoggerMessage(
        EventId = 8040,
        Level = LogLevel.Warning,
        Message = "SubscribeChannelsInternalAsync: message dropped for channel `{ChannelName}` with notification message type `{NotificationMessageType}` received at {ReceivedAt}: {Message}")]
    public static partial void SubscribedChannelsInternalAsyncMessageDroppedWarning(
        this ILogger logger, string channelName, string? notificationMessageType, DateTimeOffset receivedAt, JsonNode message);

    [LoggerMessage(
        EventId = 8050,
        Level = LogLevel.Error,
        Message = "SubscribeChannelsInternalAsync: notification message type not added for channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void SubscribeChannelsInternalAsyncNotificationMessageTypeNotAddedError(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 8060,
        Level = LogLevel.Warning,
        Message = "SubscribeChannelsInternalAsync: channel not removed for add error of channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void SubscribeChannelsInternalAsyncChannelNotRemovedForNotAddWarning(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 8070,
        Level = LogLevel.Error,
        Message = "SubscribeChannelsInternalAsync: add notification message type exception for channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void SubscribeChannelsInternalAsyncAddNotificationMessageTypeException(
        this ILogger logger, string channelName, string? notificationMessageType, Exception exception);

    [LoggerMessage(
        EventId = 8080,
        Level = LogLevel.Warning,
        Message = "SubscribeChannelsInternalAsync: channel not removed for add exception of channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void SubscribeChannelsInternalAsyncChannelNotRemovedForAddExceptionWarning(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 8090,
        Level = LogLevel.Error,
        Message = "SubscribeChannelsInternalAsync: receive channel notification task not started for channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void SubscribeChannelsInternalAsyncReceiveChannelNotificationTaskNotStartedError(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 8100,
        Level = LogLevel.Warning,
        Message = "SubscribeChannelsInternalAsync: notification message type not removed for task not started at channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void SubscribeChannelsInternalAsyncNotificationMessageTypeNotRemovedForTaskNotStartedWarning(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 8110,
        Level = LogLevel.Warning,
        Message = "SubscribeChannelsInternalAsync: channel not removed for task not started at channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void SubscribeChannelsInternalAsyncChannelNotRemovedForTaskNotStartedWarning(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 8120,
        Level = LogLevel.Error,
        Message = "SubscribeChannelsInternalAsync: start receive channel notification task exception for channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void SubscribeChannelsInternalAsyncStartReceiveChannelNotificationTaskException(
        this ILogger logger, string channelName, string? notificationMessageType, Exception exception);

    [LoggerMessage(
        EventId = 8130,
        Level = LogLevel.Warning,
        Message = "SubscribeChannelsInternalAsync: notification message type not removed for start task exception at channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void SubscribeChannelsInternalAsyncNotificationMessageTypeNotRemovedForStartTaskExceptionWarning(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 8140,
        Level = LogLevel.Warning,
        Message = "SubscribeChannelsInternalAsync: channel not removed for start task exception at channel `{ChannelName}` with notification message type `{NotificationMessageType}`")]
    public static partial void SubscribeChannelsInternalAsyncChannelNotRemovedForStartTaskExceptionWarning(
        this ILogger logger, string channelName, string? notificationMessageType);

    [LoggerMessage(
        EventId = 8150,
        Level = LogLevel.Information,
        Message = "SubscribeChannelsInternalAsync: subscribing channels `{ChannelNames}`")]
    public static partial void SubscribeChannelsInternalAsyncSubscribingChannelsInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 8160,
        Level = LogLevel.Information,
        Message = "SubscribeChannelsInternalAsync: not subscribed channels `{ChannelNames}")]
    public static partial void SubscribeChannelsInternalAsyncChannelsNotSubscribedInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 8170,
        Level = LogLevel.Information,
        Message = "SubscribeChannelsInternalAsync: canceled for `{ChannelNames}`")]
    public static partial void SubscribeChannelsInternalAsyncCanceledInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 8180,
        Level = LogLevel.Error,
        Message = "SubscribeChannelsInternalAsync: exception for `{ChannelNames}`")]
    public static partial void SubscribeChannelsInternalAsyncException(
        this ILogger logger, List<string> channelNames, Exception exception);

    [LoggerMessage(
        EventId = 9010,
        Level = LogLevel.Error,
        Message = "SubscribeExchangeChannelsAsync: send subscribe request exception for `{ChannelNames}`")]
    public static partial void SubscribeExchangeChannelsAsyncSendSubscribeRequestException(
        this ILogger logger, List<string> channelNames, Exception exception);

    [LoggerMessage(
        EventId = 9020,
        Level = LogLevel.Information,
        Message = "SubscribeExchangeChannelsAsync: removed channels `{ChannelNames}`")]
    public static partial void SubscribeExchangeChannelsAsyncRemovedChannelsInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 9030,
        Level = LogLevel.Error,
        Message = "SubscribeExchangeChannelsAsync: remove channels exception for channels `{ChannelNames}`")]
    public static partial void SubscribeExchangeChannelsAsyncRemoveChannelsException(
        this ILogger logger, List<string> channelNames, Exception exception);

    [LoggerMessage(
        EventId = 10010,
        Level = LogLevel.Error,
        Message = "UnsubscribeChannelsInternalAsync: no channel names")]
    public static partial void UnsubscribeChannelsInternalAsyncNoChannelNamesError(
        this ILogger logger);

    [LoggerMessage(
        EventId = 10020,
        Level = LogLevel.Information,
        Message = "UnsubscribeChannelsInternalAsync: unsubscribing channels `{ChannelNames}`")]
    public static partial void UnsubscribeChannelsInternalAsyncUnsubscribingChannelsInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 10030,
        Level = LogLevel.Information,
        Message = "UnsubscribeChannelsInternalAsync: not unsubscribed channels `{ChannelNames}`")]
    public static partial void UnsubscribeChannelsInternalAsyncChannelsNotUnsubscribedInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 10040,
        Level = LogLevel.Information,
        Message = "UnsubscribeChannelsInternalAsync: unsubscribe exchange channels canceled for channels `{ChannelNames}`")]
    public static partial void UnsubscribeChannelsInternalAsyncUnsubscribeExchangeChannelsCanceledInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 10050,
        Level = LogLevel.Error,
        Message = "UnsubscribeChannelsInternalAsync: unsubscribe exchange channels exception for channels `{ChannelNames}`")]
    public static partial void UnsubscribeChannelsInternalAsyncUnsubscribeExchangeChannelsException(
        this ILogger logger, List<string> channelNames, Exception exception);

    [LoggerMessage(
        EventId = 11010,
        Level = LogLevel.Error,
        Message = "UnsubscribeExchangeChannelsAsync: send unsubscribe request exception for channels `{ChannelNames}`")]
    public static partial void UnsubscribeExchangeChannelsAsyncSendUnsubscribeRequestException(
        this ILogger logger, List<string> channelNames, Exception exception);
}
