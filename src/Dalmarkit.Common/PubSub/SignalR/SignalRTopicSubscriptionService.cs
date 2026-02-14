using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Dalmarkit.Common.PubSub.SignalR;

public class SignalRTopicSubscriptionService(
    IConnectionMultiplexer redis,
    ILogger<SignalRTopicSubscriptionService> logger) : ITopicSubscriptionService
{
    /// <summary>
    /// TTL to auto-expire stale keys in case miss cleanup
    /// </summary>
    public static readonly TimeSpan AutoStaleKeyExpiry = TimeSpan.FromHours(24);

    public virtual string ConnectionKeyPrefix => _connectionKeyPrefix;
    public virtual string SubscriptionKey => _subscriptionKey;
    public virtual string TopicKeyPrefix => _topicKeyPrefix;

    /// <summary>
    /// SET - connectionId => {topics}
    /// </summary>
    private const string _connectionKeyPrefix = "sigr|conns|";
    private const string _subscriptionKey = "sigr|subs";
    /// <summary>
    /// SET - topic => {connectionIds}
    /// </summary>
    private const string _topicKeyPrefix = "sigr|topics|";

    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException(nameof(redis));
    private readonly ILogger<SignalRTopicSubscriptionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private IDatabase RedisDb => _redis.GetDatabase();

    public virtual async Task<IReadOnlyCollection<string>> GetAllSubscribedTopics()
    {
        RedisValue[] members = await RedisDb.SetMembersAsync(SubscriptionKey);
        return members.Select(m => m.ToString()).ToList().AsReadOnly();
    }

    public virtual async Task<IReadOnlyCollection<string>> GetSubscribedTopicsByConnectionIdAsync(string connectionId)
    {
        RedisValue[] members = await RedisDb.SetMembersAsync(ConnectionKeyPrefix + connectionId);
        return members.Select(m => m.ToString()).ToList().AsReadOnly();
    }

    public virtual async Task<IReadOnlyCollection<string>> GetSubscribedTopicsByPrefixAsync(string prefix)
    {
        IReadOnlyCollection<string> subscribedTopics = await GetAllSubscribedTopics();
        return subscribedTopics.Where(st => st.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList().AsReadOnly();
    }

    public virtual async Task<IReadOnlyCollection<string>> GetTopicSubscribersAsync(string topic)
    {
        RedisValue[] members = await RedisDb.SetMembersAsync(TopicKeyPrefix + topic);
        return members.Select(m => m.ToString()).ToList().AsReadOnly();
    }

    public virtual async Task<bool> IsTopicSubscribedAsync(string connectionId, string topic)
    {
        return await RedisDb.SetContainsAsync(TopicKeyPrefix + topic, connectionId);
    }

    public virtual async Task<IReadOnlyCollection<string>> RemoveConnectionAsync(string connectionId)
    {
        string connectionKey = ConnectionKeyPrefix + connectionId;

        IDatabase redisDb = RedisDb;

        RedisResult result = await redisDb.ScriptEvaluateAsync(
            RemoveConnectionLuaScript,
            keys: [connectionKey],
            values: [TopicKeyPrefix, connectionId, SubscriptionKey]
        );

        RedisResult[]? topics = (RedisResult[]?)result;
        if (topics == null)
        {
            _logger.RemoveConnectionTopicsEmptyWarning(connectionId);
            return [];
        }

        IReadOnlyCollection<string> removedTopics = topics.Select(t => t.ToString()).ToList().AsReadOnly();
        _logger.RemoveConnectionInfo(connectionId, removedTopics);
        return removedTopics;
    }

    public virtual async Task SubscribeTopicAsync(string connectionId, string topic)
    {
        string connectionKey = ConnectionKeyPrefix + connectionId;
        string topicKey = TopicKeyPrefix + topic;

        IDatabase redisDb = RedisDb;

        ITransaction transaction = redisDb.CreateTransaction();
        _ = transaction.SetAddAsync(topicKey, connectionId);
        _ = transaction.KeyExpireAsync(topicKey, AutoStaleKeyExpiry);
        _ = transaction.SetAddAsync(connectionKey, topic);
        _ = transaction.KeyExpireAsync(connectionKey, AutoStaleKeyExpiry);
        _ = transaction.SetAddAsync(SubscriptionKey, topic);

        bool isExecuted = await transaction.ExecuteAsync();
        if (!isExecuted)
        {
            _logger.SubscribeTopicNotExecutedWarning(connectionId, topic);
            return;
        }

        _logger.SubscribeTopicInfo(connectionId, topic);
    }

    public virtual async Task UnsubscribeTopicAsync(string connectionId, string topic)
    {
        string connectionKey = ConnectionKeyPrefix + connectionId;
        string topicKey = TopicKeyPrefix + topic;

        IDatabase redisDb = RedisDb;

        ITransaction transaction = redisDb.CreateTransaction();
        _ = transaction.SetRemoveAsync(topicKey, connectionId);
        _ = transaction.SetRemoveAsync(connectionKey, topic);

        bool isExecuted = await transaction.ExecuteAsync();
        if (!isExecuted)
        {
            _logger.UnsubscribeTopicNotExecutedWarning(connectionId, topic);
            return;
        }

        long numSubscribers = await redisDb.SetLengthAsync(topicKey);
        if (numSubscribers == 0)
        {
            bool isRemoved = await redisDb.SetRemoveAsync(SubscriptionKey, topic);
            if (isRemoved)
            {
                _logger.UnsubscribeTopicNoSubscribersRemovedInfo(connectionId, topic);
            }
            else
            {
                _logger.UnsubscribeTopicNoSubscribersNotRemovedWarning(connectionId, topic);
            }

            return;
        }

        _logger.UnsubscribeTopicInfo(connectionId, topic);
    }

    protected const string RemoveConnectionLuaScript = @"
        local connectionKey = KEYS[1]
        local topicKeyPrefix = ARGV[1]
        local connectionId = ARGV[2]
        local subscriptionKey = ARGV[3]

        local topics = redis.call('SMEMBERS', connectionKey)
        if #topics == 0 then return topics end

        for _, topic in ipairs(topics) do
            local topicKey = topicKeyPrefix .. topic
            redis.call('SREM', topicKey, connectionId)

            local numSubscribers = redis.call('SCARD', topicKey)
            if numSubscribers == 0 then
                redis.call('SREM', subscriptionKey, topic)
            end
        end

        redis.call('UNLINK', connectionKey)
        return topics
    ";
}

public static partial class SignalRTopicSubscriptionServiceLogs
{
    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "Remove connection topics empty for {ConnectionId}")]
    public static partial void RemoveConnectionTopicsEmptyWarning(
        this ILogger logger, string connectionId);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Information,
        Message = "Removed connection from all topics for {ConnectionId}: {Topics}")]
    public static partial void RemoveConnectionInfo(
        this ILogger logger, string connectionId, IReadOnlyCollection<string> topics);

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Warning,
        Message = "Subscribe topic not executed for topic `{Topic}`: {ConnectionId}")]
    public static partial void SubscribeTopicNotExecutedWarning(
        this ILogger logger, string connectionId, string topic);

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Information,
        Message = "Subscribed to topic `{Topic}`: {ConnectionId}")]
    public static partial void SubscribeTopicInfo(
        this ILogger logger, string connectionId, string topic);

    [LoggerMessage(
        EventId = 50,
        Level = LogLevel.Warning,
        Message = "Unsubscribe topic not executed for topic `{Topic}`: {ConnectionId}")]
    public static partial void UnsubscribeTopicNotExecutedWarning(
        this ILogger logger, string connectionId, string topic);

    [LoggerMessage(
        EventId = 60,
        Level = LogLevel.Information,
        Message = "Unsubscribe topic with no subscribers removed for `{Topic}`: {ConnectionId}")]
    public static partial void UnsubscribeTopicNoSubscribersRemovedInfo(
        this ILogger logger, string connectionId, string topic);

    [LoggerMessage(
        EventId = 70,
        Level = LogLevel.Warning,
        Message = "Unsubscribe topic with no subscribers not removed for `{Topic}`: {ConnectionId}")]
    public static partial void UnsubscribeTopicNoSubscribersNotRemovedWarning(
        this ILogger logger, string connectionId, string topic);

    [LoggerMessage(
        EventId = 80,
        Level = LogLevel.Information,
        Message = "Unsubscribed from topic `{Topic}`: {ConnectionId}")]
    public static partial void UnsubscribeTopicInfo(
        this ILogger logger, string connectionId, string topic);
}
