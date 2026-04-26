using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dalmarkit.Common.Dtos.Events;
using Dalmarkit.Common.PubSub;
using Microsoft.Extensions.Logging;
using static Dalmarkit.Common.Services.WebSocketServices.IWebSocketClient;

namespace Dalmarkit.Common.Services.WebSocketServices;

public abstract class ClientWebSocketPubServiceBase(
    IWebSocketClient webSocketClient,
    ITopicPublisherService topicPublisherService,
    ILogger<ClientWebSocketPubServiceBase> logger) : ClientWebSocketServiceBase(webSocketClient, logger), IClientWebSocketPubService
{
    public const int SubscribedChannelTasksInitialCapacity = 8209;

    private readonly ILogger<ClientWebSocketPubServiceBase> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ITopicPublisherService _topicPublisherService = topicPublisherService ?? throw new ArgumentNullException(nameof(topicPublisherService));
    private readonly ConcurrentDictionary<string, ReceiveChannelMessagesTask> _subscribedChannelTasks = new(Environment.ProcessorCount, SubscribedChannelTasksInitialCapacity);

    private volatile int _isDisposed;

    protected class ReceiveChannelMessagesTask
    {
        public required Channel<WebSocketReceivedMessage<JsonNode>> ReceiveChannel { get; set; }
        public required CancellationTokenSource ReceiveCts { get; set; }
        public required Task ReceiveTask { get; set; }
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        if (disposing)
        {
            foreach (string channelName in (List<string>)[.. _subscribedChannelTasks.Keys])
            {
                _ = _subscribedChannelTasks.TryRemove(channelName, out ReceiveChannelMessagesTask? receiveChannelMessagesTaskToRemove);
                DisposeReceiveChannelMessagesTask(receiveChannelMessagesTaskToRemove);
            }
        }

        base.Dispose(disposing);
    }

    public virtual async Task<List<string>> SubscribeChannelsAsync(List<string> channelNames, CancellationToken cancellationToken = default)
    {
        if (channelNames.Count == 0)
        {
            _logger.SubscribeChannelsAsyncNoChannelNamesError();
            throw new ArgumentException("No channel names");
        }

        List<SubscriptionChannel> subscriptionChannels = [];
        foreach (string channelName in channelNames)
        {
            SubscriptionChannel subscriptionChannel = new()
            {
                ChannelName = channelName,
            };

            subscriptionChannels.Add(subscriptionChannel);
        }

        Dictionary<SubscriptionChannel, Channel<WebSocketReceivedMessage<JsonNode>>> subscribedChannels =
            await SubscribeChannelsInternalAsync(subscriptionChannels, cancellationToken).ConfigureAwait(false);
        if (subscribedChannels.Count > 0)
        {
            return [.. subscribedChannels.Keys.Select(key => key.ChannelName)];
        }

        _logger.SubscribeChannelsAsyncNoSubscribedChannelsInfo(channelNames);
        return [];
    }

    public virtual async Task<List<string>> UnsubscribeChannelsAsync(List<string> channelNames, CancellationToken cancellationToken = default)
    {
        if (channelNames.Count == 0)
        {
            _logger.UnsubscribeChannelsAsyncNoChannelNamesError();
            throw new ArgumentException("No channel names");
        }

        List<string> unsubscribedChannelNames = await UnsubscribeChannelsInternalAsync(channelNames, cancellationToken).ConfigureAwait(false);
        if (unsubscribedChannelNames.Count > 0)
        {
            await ReceiveChannelNotificationTasksRemoveAsync(unsubscribedChannelNames, cancellationToken).ConfigureAwait(false);
            return unsubscribedChannelNames;
        }

        _logger.UnsubscribeChannelsAsyncNoUnsubscribedChannelsInfo(channelNames);
        return [];
    }

    protected virtual async Task<bool> ProcessSubscriptionNotificationAsync(string channelName, JsonNode dataNode, CancellationToken cancellationToken = default)
    {
        bool isChannelFound = _subscribedChannelTasks.TryGetValue(channelName, out ReceiveChannelMessagesTask? receiveChannelMessagesTask);
        if (!isChannelFound)
        {
            _logger.ProcessSubscriptionNotificationAsyncChannelNotFoundError(channelName, dataNode);
            return false;
        }

        if (receiveChannelMessagesTask == null)
        {
            _logger.ProcessSubscriptionNotificationAsyncReceiveChannelMessagesTaskNullError(channelName, dataNode);
            return false;
        }

        if (receiveChannelMessagesTask.ReceiveChannel == null)
        {
            _logger.ProcessSubscriptionNotificationAsyncReceiveChannelNullError(channelName, dataNode);
            return false;
        }

        WebSocketReceivedMessage<JsonNode> channelMessage = new()
        {
            Data = dataNode.DeepClone(),
            ReceivedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await receiveChannelMessagesTask.ReceiveChannel.Writer.WriteAsync(channelMessage, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.ProcessSubscriptionNotificationAsyncWriteChannelCanceledInfo(channelName, dataNode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.ProcessSubscriptionNotificationAsyncWriteChannelException(channelName, dataNode, ex);
            return false;
        }
    }

    protected virtual async Task PublishTopicAsync<TDto>(string topic, string method, TDto publishDto, string? key = default, CancellationToken cancellationToken = default)
    {
        await _topicPublisherService.PublishToTopicAsync(topic, method, publishDto, key, cancellationToken).ConfigureAwait(false);
    }

    protected virtual void PublishAndForgetTopic<TDto>(string topic, string method, TDto publishDto, string? key = default, CancellationToken cancellationToken = default)
    {
        _ = _topicPublisherService.PublishToTopicAsync(topic, method, publishDto, key, cancellationToken).ContinueWith(
            t => _logger.PublishAndForgetTopicException(topic, t.Exception!),
            cancellationToken,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    protected virtual bool ReceiveChannelNotificationTaskStart(string channelName, Channel<WebSocketReceivedMessage<JsonNode>> receiveChannel, Func<string, ChannelReader<WebSocketReceivedMessage<JsonNode>>, CancellationToken, Task> receiveNotificationsAsync, CancellationToken cancellationToken = default)
    {
        if (_isDisposed == 1)
        {
            _logger.ReceiveChannelNotificationTaskStartIsDisposedError(channelName);
            return false;
        }

        CancellationTokenSource receiveChannelNotificationsCts = new();
        Task receiveChannelNotificationsTask = receiveNotificationsAsync(channelName,
                receiveChannel.Reader, receiveChannelNotificationsCts.Token).ContinueWith(
                    t => _logger.ReceiveChannelNotificationTaskStartReceiveNotificationException(channelName, t.Exception!),
                    cancellationToken,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);

        bool isAddTaskSuccess = _subscribedChannelTasks.TryAdd(channelName, new ReceiveChannelMessagesTask()
        {
            ReceiveChannel = receiveChannel,
            ReceiveCts = receiveChannelNotificationsCts,
            ReceiveTask = receiveChannelNotificationsTask,
        });
        if (isAddTaskSuccess)
        {
            if (_isDisposed == 0)
            {
                return true;
            }

            _logger.ReceiveChannelNotificationTaskStartTaskAddedIsDisposedError(channelName);

            bool isRemoved = _subscribedChannelTasks.TryRemove(channelName, out ReceiveChannelMessagesTask? receiveChannelMessagesTaskToRemove);
            if (isRemoved)
            {
                DisposeReceiveChannelMessagesTask(receiveChannelMessagesTaskToRemove);
            }

            return false;
        }

        try
        {
            receiveChannelNotificationsCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore if CTS already disposed
        }
        finally
        {
            receiveChannelNotificationsCts.Dispose();

            // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
        }

        _logger.ReceiveChannelNotificationTaskStartTaskNotAddedError(channelName);
        return false;
    }

    protected virtual async Task ReceiveNotificationsAsync(string channelName, ChannelReader<WebSocketReceivedMessage<JsonNode>> channelReader, Func<string, JsonNode, CancellationToken, Task<bool>> processNotificationsAsync, CancellationToken cancellationToken = default)
    {
        await ReceiveTextMessagesAsync(channelReader,
            async (messageJson) => await processNotificationsAsync(channelName, messageJson, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    protected virtual async Task RemoveReceiveChannelNotificationTasksAsync(List<string> unsubscribedChannelNames, Action<string>? removeResourcesForChannel, CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < unsubscribedChannelNames.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(unsubscribedChannelNames[i]))
            {
                _logger.RemoveReceiveChannelNotificationTasksAsyncUnsubscribedChannelNameNullOrWhitespaceError(i, unsubscribedChannelNames);
                continue;
            }

            bool isRemoved = _subscribedChannelTasks.TryRemove(unsubscribedChannelNames[i], out ReceiveChannelMessagesTask? receiveChannelMessagesTask);
            if (!isRemoved)
            {
                _logger.RemoveReceiveChannelNotificationTasksAsyncUnsubscribedChannelNotFoundWarning(unsubscribedChannelNames[i], i, unsubscribedChannelNames);
                continue;
            }

            if (receiveChannelMessagesTask == null)
            {
                _logger.RemoveReceiveChannelNotificationTasksAsyncReceiveChannelMessagesTaskNullError(unsubscribedChannelNames[i], i, unsubscribedChannelNames);
                continue;
            }

            try
            {
                await StopReceiveChannelMessagesTaskAsync(i, unsubscribedChannelNames[i], unsubscribedChannelNames, receiveChannelMessagesTask, cancellationToken).ConfigureAwait(false);
                removeResourcesForChannel?.Invoke(unsubscribedChannelNames[i]);
            }
            catch (Exception ex)
            {
                _logger.RemoveReceiveChannelNotificationTasksAsyncStopReceiveChannelMessagesTaskException(unsubscribedChannelNames[i], i, unsubscribedChannelNames, ex);
            }
        }
    }

    protected virtual async Task<bool> ResubscribeExchangeChannelAsync(string channelName, CancellationToken cancellationToken = default)
    {
        _logger.ResubscribeExchangeChannelAsyncInfo(channelName);

        try
        {
            _logger.ResubscribeExchangeChannelAsyncUnsubscribingExchangeChannelInfo(channelName);
            List<string> channelsNotUnsubscribed = await UnsubscribeExchangeChannelsAsync([channelName], cancellationToken).ConfigureAwait(false);
            if (channelsNotUnsubscribed.Count > 0)
            {
                _logger.ResubscribeExchangeChannelAsyncChannelsNotUnsubscribedInfo(channelsNotUnsubscribed);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.ResubscribeExchangeChannelAsyncUnsubscribeExchangeChannelsException(channelName, ex);
            return false;
        }

        try
        {
            _logger.ResubscribeExchangeChannelAsyncSubscribingExchangeChannelInfo(channelName);
            List<string> channelsNotSubscribed = await SubscribeExchangeChannelsAsync([channelName], cancellationToken).ConfigureAwait(false);
            if (channelsNotSubscribed.Count == 0)
            {
                return true;
            }

            _logger.ResubscribeExchangeChannelAsyncChannelsNotSubscribedInfo(channelsNotSubscribed);
            await ReceiveChannelNotificationTasksRemoveAsync(channelsNotSubscribed, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            _logger.ResubscribeExchangeChannelAsyncSubscribeExchangeChannelException(channelName, ex);
            await ReceiveChannelNotificationTasksRemoveAsync([channelName], cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    protected virtual async Task StopReceiveChannelMessagesTaskAsync(int index, string channelName, List<string> unsubscribedChannelNames, ReceiveChannelMessagesTask receiveChannelMessagesTask, CancellationToken cancellationToken)
    {
        if (receiveChannelMessagesTask.ReceiveChannel == null)
        {
            _logger.StopReceiveChannelMessagesTaskAsyncReceiveChannelNullError(channelName, index, unsubscribedChannelNames);
        }
        else
        {
            bool isChannelMarkComplete = receiveChannelMessagesTask.ReceiveChannel.Writer.TryComplete();
            if (!isChannelMarkComplete)
            {
                _logger.StopReceiveChannelMessagesTaskAsyncReceiveChannelNotMarkCompleteWarning(channelName, index, unsubscribedChannelNames);
            }
        }

        try
        {
            if (receiveChannelMessagesTask.ReceiveCts == null)
            {
                _logger.StopReceiveChannelMessagesTaskAsyncReceiveCtsNullError(channelName, index, unsubscribedChannelNames);
            }
            else
            {
                receiveChannelMessagesTask.ReceiveCts.Cancel();
            }

            if (receiveChannelMessagesTask.ReceiveTask == null)
            {
                _logger.StopReceiveChannelMessagesTaskAsyncReceiveTaskNullError(channelName, index, unsubscribedChannelNames);
            }
            else
            {
                await receiveChannelMessagesTask.ReceiveTask.WaitAsync(TimeSpan.FromMilliseconds(GracefulShutdownTimeoutMilliseconds), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (TimeoutException)
        {
            _logger.StopReceiveChannelMessagesTaskAsyncReceiveTaskShutdownTimeoutInfo(channelName, index, unsubscribedChannelNames);
        }
        catch (Exception ex)
        {
            _logger.StopReceiveChannelMessagesTaskAsyncReceiveTaskShutdownException(channelName, index, unsubscribedChannelNames, ex);
        }
        finally
        {
            try
            {
                receiveChannelMessagesTask.ReceiveCts?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.StopReceiveChannelMessagesTaskAsyncReceiveCtsDisposeException(channelName, index, unsubscribedChannelNames, ex);
            }

            // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
        }
    }

    protected override async Task NotifyWebSocketConnectionState(WebSocketConnectionState webSocketConnectionState, string? key = default, CancellationToken cancellationToken = default)
    {
        string connectionStateTopic = WebSocketClientTopics.GetConnectionStateTopic(WebSocketName);

        ConnectionStateEventDto connectionStateEventDto = new(
            webSocketConnectionState.ToString(),
            DateTimeOffset.UtcNow);
        try
        {
            await _topicPublisherService.PublishToTopicAsync(connectionStateTopic, "Update", connectionStateEventDto, key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.NotifyWebSocketConnectionStatePublishToTopicException(connectionStateTopic, webSocketConnectionState.ToString(), ex);
        }
    }

    private void DisposeReceiveChannelMessagesTask(ReceiveChannelMessagesTask? receiveChannelMessagesTask)
    {
        if (receiveChannelMessagesTask == null)
        {
            return;
        }

        try
        {
            _ = receiveChannelMessagesTask.ReceiveChannel.Writer.TryComplete();

            receiveChannelMessagesTask.ReceiveCts?.Cancel();
            receiveChannelMessagesTask.ReceiveCts?.Dispose();

            // No need to dispose of tasks https://devblogs.microsoft.com/dotnet/do-i-need-to-dispose-of-tasks/
        }
        catch (Exception ex)
        {
            _logger.DisposeReceiveChannelMessagesTaskException(ex);
        }
    }
}

public static partial class ClientWebSocketPubServiceBaseLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Error,
        Message = "SubscribeChannelsAsync: no channel names")]
    public static partial void SubscribeChannelsAsyncNoChannelNamesError(
        this ILogger logger);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Information,
        Message = "SubscribeChannelsAsync: no subscribed channels for `{ChannelNames}`")]
    public static partial void SubscribeChannelsAsyncNoSubscribedChannelsInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Error,
        Message = "UnsubscribeChannelsAsync: no channel names")]
    public static partial void UnsubscribeChannelsAsyncNoChannelNamesError(
        this ILogger logger);

    [LoggerMessage(
        EventId = 2020,
        Level = LogLevel.Information,
        Message = "UnsubscribeChannelsAsync: no unsubscribed channels for `{ChannelNames}`")]
    public static partial void UnsubscribeChannelsAsyncNoUnsubscribedChannelsInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Error,
        Message = "ProcessSubscriptionNotificationAsync: channel not found for `{ChannelName}` with data `{DataJson}`")]
    public static partial void ProcessSubscriptionNotificationAsyncChannelNotFoundError(
        this ILogger logger, string channelName, JsonNode dataJson);

    [LoggerMessage(
        EventId = 3020,
        Level = LogLevel.Error,
        Message = "ProcessSubscriptionNotificationAsync: receive channel messages task null for `{ChannelName}` with data `{DataJson}`")]
    public static partial void ProcessSubscriptionNotificationAsyncReceiveChannelMessagesTaskNullError(
        this ILogger logger, string channelName, JsonNode dataJson);

    [LoggerMessage(
        EventId = 3030,
        Level = LogLevel.Error,
        Message = "ProcessSubscriptionNotificationAsync: receive channel null for `{ChannelName}` with data `{DataJson}`")]
    public static partial void ProcessSubscriptionNotificationAsyncReceiveChannelNullError(
        this ILogger logger, string channelName, JsonNode dataJson);

    [LoggerMessage(
        EventId = 3040,
        Level = LogLevel.Information,
        Message = "ProcessSubscriptionNotificationAsync: write channel canceled for `{ChannelName}` with data `{DataJson}`")]
    public static partial void ProcessSubscriptionNotificationAsyncWriteChannelCanceledInfo(
        this ILogger logger, string channelName, JsonNode dataJson);

    [LoggerMessage(
        EventId = 3050,
        Level = LogLevel.Error,
        Message = "Process subscription notification write channel exception for {ChannelName} with data `{DataNode}`")]
    public static partial void ProcessSubscriptionNotificationAsyncWriteChannelException(
        this ILogger logger, string channelName, JsonNode dataNode, Exception exception);

    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Error,
        Message = "PublishAndForgetTopic: exception for topic `{Topic}`")]
    public static partial void PublishAndForgetTopicException(
        this ILogger logger, string topic, AggregateException exception);

    [LoggerMessage(
        EventId = 5010,
        Level = LogLevel.Error,
        Message = "ReceiveChannelNotificationTaskStart: is disposed for channel `{ChannelName}`")]
    public static partial void ReceiveChannelNotificationTaskStartIsDisposedError(
        this ILogger logger, string channelName);

    [LoggerMessage(
        EventId = 5020,
        Level = LogLevel.Error,
        Message = "ReceiveChannelNotificationTaskStart: receive notification exception for channel `{ChannelName}`")]
    public static partial void ReceiveChannelNotificationTaskStartReceiveNotificationException(
        this ILogger logger, string channelName, AggregateException exception);

    [LoggerMessage(
        EventId = 5030,
        Level = LogLevel.Error,
        Message = "ReceiveChannelNotificationTaskStart: task added is disposed for channel `{ChannelName}`")]
    public static partial void ReceiveChannelNotificationTaskStartTaskAddedIsDisposedError(
        this ILogger logger, string channelName);

    [LoggerMessage(
        EventId = 5040,
        Level = LogLevel.Error,
        Message = "ReceiveChannelNotificationTaskStart: task not added for channel `{ChannelName}`")]
    public static partial void ReceiveChannelNotificationTaskStartTaskNotAddedError(
        this ILogger logger, string channelName);

    [LoggerMessage(
        EventId = 6010,
        Level = LogLevel.Error,
        Message = "RemoveReceiveChannelNotificationTasksAsync: unsubscribed channel name null or whitespace at {Index} of `{ChannelNames}`")]
    public static partial void RemoveReceiveChannelNotificationTasksAsyncUnsubscribedChannelNameNullOrWhitespaceError(
        this ILogger logger, int index, List<string> channelNames);

    [LoggerMessage(
        EventId = 6020,
        Level = LogLevel.Warning,
        Message = "RemoveReceiveChannelNotificationTasksAsync: unsubscribed channel not found for channel `{ChannelName}` at {Index} of `{ChannelNames}`")]
    public static partial void RemoveReceiveChannelNotificationTasksAsyncUnsubscribedChannelNotFoundWarning(
        this ILogger logger, string channelName, int index, List<string> channelNames);

    [LoggerMessage(
        EventId = 6030,
        Level = LogLevel.Error,
        Message = "RemoveReceiveChannelNotificationTasksAsync: receive channel messages task null for channel `{ChannelName}` at {Index} of `{ChannelNames}`")]
    public static partial void RemoveReceiveChannelNotificationTasksAsyncReceiveChannelMessagesTaskNullError(
        this ILogger logger, string channelName, int index, List<string> channelNames);

    [LoggerMessage(
        EventId = 6040,
        Level = LogLevel.Error,
        Message = "RemoveReceiveChannelNotificationTasksAsync: stop receive channel messages task exception for channel `{ChannelName}` at {Index} of `{ChannelNames}`")]
    public static partial void RemoveReceiveChannelNotificationTasksAsyncStopReceiveChannelMessagesTaskException(
        this ILogger logger, string channelName, int index, List<string> channelNames, Exception exception);

    [LoggerMessage(
        EventId = 7010,
        Level = LogLevel.Information,
        Message = "ResubscribeExchangeChannelAsync: channel `{ChannelName}`")]
    public static partial void ResubscribeExchangeChannelAsyncInfo(
        this ILogger logger, string channelName);

    [LoggerMessage(
        EventId = 7020,
        Level = LogLevel.Information,
        Message = "ResubscribeExchangeChannelAsync: unsubscribing exchange channel `{ChannelName}`")]
    public static partial void ResubscribeExchangeChannelAsyncUnsubscribingExchangeChannelInfo(
        this ILogger logger, string channelName);

    [LoggerMessage(
        EventId = 7030,
        Level = LogLevel.Information,
        Message = "ResubscribeExchangeChannelAsync: not unsubscribed channels `{ChannelNames}`")]
    public static partial void ResubscribeExchangeChannelAsyncChannelsNotUnsubscribedInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 7040,
        Level = LogLevel.Error,
        Message = "ResubscribeExchangeChannelAsync: unsubscribe exchange channels exception for channel `{ChannelName}`")]
    public static partial void ResubscribeExchangeChannelAsyncUnsubscribeExchangeChannelsException(
        this ILogger logger, string channelName, Exception exception);

    [LoggerMessage(
        EventId = 7050,
        Level = LogLevel.Information,
        Message = "ResubscribeExchangeChannelAsync: subscribing exchange channel `{ChannelName}`")]
    public static partial void ResubscribeExchangeChannelAsyncSubscribingExchangeChannelInfo(
        this ILogger logger, string channelName);

    [LoggerMessage(
        EventId = 7060,
        Level = LogLevel.Information,
        Message = "ResubscribeExchangeChannelAsync: not subscribed channels `{ChannelNames}`")]
    public static partial void ResubscribeExchangeChannelAsyncChannelsNotSubscribedInfo(
        this ILogger logger, List<string> channelNames);

    [LoggerMessage(
        EventId = 7070,
        Level = LogLevel.Error,
        Message = "ResubscribeExchangeChannelAsync: subscribe exchange channel exception for channel `{ChannelName}`")]
    public static partial void ResubscribeExchangeChannelAsyncSubscribeExchangeChannelException(
        this ILogger logger, string channelName, Exception exception);

    [LoggerMessage(
        EventId = 8010,
        Level = LogLevel.Error,
        Message = "StopReceiveChannelMessagesTaskAsync: receive channel null for channel `{ChannelName}` at {Index} of `{ChannelNames}`")]
    public static partial void StopReceiveChannelMessagesTaskAsyncReceiveChannelNullError(
        this ILogger logger, string channelName, int index, List<string> channelNames);

    [LoggerMessage(
        EventId = 8020,
        Level = LogLevel.Warning,
        Message = "StopReceiveChannelMessagesTaskAsync: receive channel not mark complete for channel `{ChannelName}` at {Index} of `{ChannelNames}`")]
    public static partial void StopReceiveChannelMessagesTaskAsyncReceiveChannelNotMarkCompleteWarning(
        this ILogger logger, string channelName, int index, List<string> channelNames);

    [LoggerMessage(
        EventId = 8030,
        Level = LogLevel.Error,
        Message = "StopReceiveChannelMessagesTaskAsync: receive cancellation token source null for channel `{ChannelName}` at {Index} of `{ChannelNames}`")]
    public static partial void StopReceiveChannelMessagesTaskAsyncReceiveCtsNullError(
        this ILogger logger, string channelName, int index, List<string> channelNames);

    [LoggerMessage(
        EventId = 8040,
        Level = LogLevel.Error,
        Message = "StopReceiveChannelMessagesTaskAsync: receive task null for channel `{ChannelName}` at {Index} of `{ChannelNames}`")]
    public static partial void StopReceiveChannelMessagesTaskAsyncReceiveTaskNullError(
        this ILogger logger, string channelName, int index, List<string> channelNames);

    [LoggerMessage(
        EventId = 8050,
        Level = LogLevel.Information,
        Message = "StopReceiveChannelMessagesTaskAsync: receive task shutdown timeout for channel `{ChannelName}` at {Index} of `{ChannelNames}`")]
    public static partial void StopReceiveChannelMessagesTaskAsyncReceiveTaskShutdownTimeoutInfo(
        this ILogger logger, string channelName, int index, List<string> channelNames);

    [LoggerMessage(
        EventId = 8060,
        Level = LogLevel.Error,
        Message = "StopReceiveChannelMessagesTaskAsync: receive task shutdown exception for channel `{ChannelName}` at {Index} of `{ChannelNames}`")]
    public static partial void StopReceiveChannelMessagesTaskAsyncReceiveTaskShutdownException(
        this ILogger logger, string channelName, int index, List<string> channelNames, Exception exception);

    [LoggerMessage(
        EventId = 8070,
        Level = LogLevel.Error,
        Message = "StopReceiveChannelMessagesTaskAsync: receive cancellation token source dispose exception for channel `{ChannelName}` at {Index} of `{ChannelNames}`")]
    public static partial void StopReceiveChannelMessagesTaskAsyncReceiveCtsDisposeException(
        this ILogger logger, string channelName, int index, List<string> channelNames, Exception exception);

    [LoggerMessage(
        EventId = 9010,
        Level = LogLevel.Error,
        Message = "NotifyWebSocketConnectionState: publish to topic exception for topic `{Topic}` and state `{ConnectionState}`")]
    public static partial void NotifyWebSocketConnectionStatePublishToTopicException(
        this ILogger logger, string topic, string connectionState, Exception exception);

    [LoggerMessage(
        EventId = 10010,
        Level = LogLevel.Error,
        Message = "DisposeReceiveChannelMessagesTask: exception")]
    public static partial void DisposeReceiveChannelMessagesTaskException(
        this ILogger logger, Exception exception);
}
