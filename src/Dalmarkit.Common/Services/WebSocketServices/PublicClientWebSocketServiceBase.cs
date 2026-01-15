using Mediator;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Dalmarkit.Common.Services.WebSocketServices;

public abstract class PublicClientWebSocketServiceBase(
    IWebSocketClient webSocketClient,
    ILogger<PublicClientWebSocketServiceBase> logger) : IPublicClientWebSocketService,
        INotificationHandler<WebSocketClientEvents.OnTextMessageReceived>,
        INotificationHandler<WebSocketClientEvents.OnWebSocketConnected>
{
    public const int HeartbeatIntervalSeconds = 10;
    public const int SubscribedChannelsCurrencyLevel = 2;
    public const int SubscribedChannelsCapacity = 1024;

    private readonly IWebSocketClient _webSocketClient = webSocketClient ?? throw new ArgumentNullException(nameof(webSocketClient));
    private readonly ILogger<PublicClientWebSocketServiceBase> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private static readonly ConcurrentDictionary<string, byte> _subscribedChannels = new(SubscribedChannelsCurrencyLevel, SubscribedChannelsCapacity);
    private static volatile bool hasSubscribedOnConnected;

    public virtual async ValueTask Handle(WebSocketClientEvents.OnTextMessageReceived notification, CancellationToken cancellationToken = default)
    {
        _logger.ReceivedTextMessageInfo(notification.Message);

        if (string.IsNullOrWhiteSpace(notification.Message))
        {
            _logger.ReceivedNullOrWhitespaceTextMessageWarning();
            return;
        }

        JsonNode? messageJson;
        try
        {
            messageJson = JsonNode.Parse(notification.Message);
        }
        catch (Exception ex)
        {
            _logger.ParseTextMessageReceivedException(notification.Message, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            return;
        }

        if (messageJson == null)
        {
            _logger.ParseTextMessageReceivedError(notification.Message);
            return;
        }

        string? id = (string?)messageJson["id"];
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                await ProcessServerResponseAsync(id, notification.Message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ProcessServerResponseException(notification.Message, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            }

            return;
        }

        string? method = (string?)messageJson["method"];
        if (string.IsNullOrWhiteSpace(method))
        {
            _logger.MethodNullError(notification.Message);
            return;
        }

        try
        {
            await ProcessServerNotificationAsync(method, notification.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.ProcessServerNotificationException(notification.Message, ex.Message, ex.InnerException?.Message, ex.StackTrace);
        }
    }

    public virtual async ValueTask Handle(WebSocketClientEvents.OnWebSocketConnected notification, CancellationToken cancellationToken = default)
    {
        List<string> channels = [.. _subscribedChannels.Keys];

        try
        {
            await SetupServerHeartbeatAsync(cancellationToken).ConfigureAwait(false);

            _ = Interlocked.Exchange(ref hasSubscribedOnConnected, true);

            _logger.SubscribingChannels(channels);
            await SendSubscribeRequestAsync(channels, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.SubscribeOnConnectedException(channels, ex.Message, ex.InnerException?.Message, ex.StackTrace);
        }
    }

    public virtual async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _webSocketClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _webSocketClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task SendMessageAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
    {
        await _webSocketClient.SendMessageAsync(message, cancellationToken);
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
            catch (Exception ex)
            {
                _logger.UnsubscribeChannelsException(newlyRemovedChannels, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            }
        }
    }

    protected virtual async Task ProcessServerNotificationAsync(string method, string message, CancellationToken cancellationToken = default)
    {
    }

    protected virtual async Task ProcessServerResponseAsync(string id, string message, CancellationToken cancellationToken = default)
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
        Message = "Received text message: {TextMessage}")]
    public static partial void ReceivedTextMessageInfo(
        this ILogger logger, string textMessage);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Warning,
        Message = "Received null or whitespace text message")]
    public static partial void ReceivedNullOrWhitespaceTextMessageWarning(
        this ILogger logger);

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Error,
        Message = "Deserialize text message received exception for `{TextMessage}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ParseTextMessageReceivedException(
        this ILogger logger, string textMessage, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Error,
        Message = "Deserialize text message received error: {TextMessage}")]
    public static partial void ParseTextMessageReceivedError(
        this ILogger logger, string textMessage);

    [LoggerMessage(
        EventId = 50,
        Level = LogLevel.Error,
        Message = "Process server response exception for `{TextMessage}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ProcessServerResponseException(
        this ILogger logger, string textMessage, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 60,
        Level = LogLevel.Error,
        Message = "Process server notification exception for `{TextMessage}` with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ProcessServerNotificationException(
        this ILogger logger, string textMessage, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 70,
        Level = LogLevel.Error,
        Message = "Method null error: {TextMessage}")]
    public static partial void MethodNullError(
        this ILogger logger, string textMessage);

    [LoggerMessage(
        EventId = 80,
        Level = LogLevel.Warning,
        Message = "Method unknown: {TextMessage}")]
    public static partial void MethodUnknownWarning(
        this ILogger logger, string textMessage);

    [LoggerMessage(
        EventId = 90,
        Level = LogLevel.Information,
        Message = "Subscribing to channels: {Channels}")]
    public static partial void SubscribingChannels(
        this ILogger logger, List<string> channels);

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Error,
        Message = "Subscribe {Channels} on connected exception with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SubscribeOnConnectedException(
        this ILogger logger, List<string> channels, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 110,
        Level = LogLevel.Error,
        Message = "Subscribe {Channels} exception with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void SubscribeChannelsException(
        this ILogger logger, List<string> channels, string exceptionMessage, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 120,
        Level = LogLevel.Error,
        Message = "Unsubscribe {Channels} exception with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void UnsubscribeChannelsException(
        this ILogger logger, List<string> channels, string exceptionMessage, string? innerException, string? stackTrace);
}
