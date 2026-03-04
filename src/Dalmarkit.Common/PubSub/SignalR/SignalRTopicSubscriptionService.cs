using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Dalmarkit.Common.PubSub.SignalR;

public class SignalRTopicSubscriptionService(
    ILogger<SignalRTopicSubscriptionService> logger) : ITopicSubscriptionService
{
    public const int SubscriberTopicsInitialCapacity = 100003;
    public const int TopicsByPrefixInitialCapacity = 8209;

    private static readonly ImmutableHashSet<string> EmptyHashSet = [];

    private readonly ILogger<SignalRTopicSubscriptionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly Lock _mutationLock = new();
    private readonly ConcurrentDictionary<string, ImmutableHashSet<string>> _subscriberTopics = new(Environment.ProcessorCount, SubscriberTopicsInitialCapacity, StringComparer.Ordinal);
#pragma warning disable IDE0028 // Simplify collection initialization
    private readonly Dictionary<string, int> _topicNumSubscribers = new(StringComparer.Ordinal);
#pragma warning restore IDE0028 // Simplify collection initialization
    private readonly ConcurrentDictionary<string, ImmutableHashSet<string>> _topicsByPrefix = new(Environment.ProcessorCount, TopicsByPrefixInitialCapacity, StringComparer.Ordinal);

    public virtual ImmutableHashSet<string> GetSubscriberTopics(string subscriberId)
    {
        _ = _subscriberTopics.TryGetValue(subscriberId, out ImmutableHashSet<string>? subscriberTopics);
        return subscriberTopics ?? EmptyHashSet;
    }

    public virtual ImmutableHashSet<string> GetTopicsByPrefix(string prefix)
    {
        _ = _topicsByPrefix.TryGetValue(prefix, out ImmutableHashSet<string>? topicsByPrefix);
        return topicsByPrefix ?? EmptyHashSet;
    }

    public virtual ImmutableHashSet<string> RemoveSubscriber(string subscriberId, Func<string, string>? GetTopicPrefix)
    {
        ImmutableHashSet<string> topicsToRemove;
        lock (_mutationLock)
        {
            topicsToRemove = GetSubscriberTopics(subscriberId);
            foreach (IGrouping<string, string> topicPrefixToRemove in topicsToRemove.GroupBy(t => GetTopicPrefix == null ? t : GetTopicPrefix(t)))
            {
                foreach (string topicName in topicPrefixToRemove)
                {
                    _ = TopicsByPrefixRemove(subscriberId, topicName, topicPrefixToRemove.Key);
                }
            }

            _ = _subscriberTopics.TryRemove(subscriberId, out _);
        }

        _logger.RemoveSubscriberInfo(subscriberId, topicsToRemove);
        return topicsToRemove;
    }

    public virtual bool SubscribeTopic(string subscriberId, string topicName, Func<string, string>? GetTopicPrefix)
    {
        string topicPrefix = GetTopicPrefix == null ? topicName : GetTopicPrefix(topicName);
        if (string.IsNullOrWhiteSpace(topicPrefix))
        {
            _logger.SubscribeTopicPrefixNullError(subscriberId, topicName);
            return false;
        }

        bool isSubscribed;
        lock (_mutationLock)
        {
            isSubscribed = SubscribeTopic(subscriberId, topicName, topicPrefix);
        }

        if (isSubscribed)
        {
            _logger.SubscribeTopicInfo(subscriberId, topicName);
        }
        return isSubscribed;
    }

    public virtual bool UnsubscribeTopic(string subscriberId, string topicName, Func<string, string>? GetTopicPrefix)
    {
        string topicPrefix = GetTopicPrefix == null ? topicName : GetTopicPrefix(topicName);
        if (string.IsNullOrWhiteSpace(topicPrefix))
        {
            _logger.UnsubscribeTopicPrefixNullError(subscriberId, topicName);
            return false;
        }

        bool isUnsubscribed;
        lock (_mutationLock)
        {
            isUnsubscribed = UnsubscribeTopic(subscriberId, topicName, topicPrefix);
        }

        if (isUnsubscribed)
        {
            _logger.UnsubscribeTopicInfo(subscriberId, topicName);
        }
        return isUnsubscribed;
    }

    protected virtual bool SubscribeTopic(string subscriberId, string topicName, string topicPrefix)
    {
        return SubscriberTopicsAdd(subscriberId, topicName) && TopicsByPrefixAdd(subscriberId, topicName, topicPrefix);
    }

    protected virtual bool SubscriberTopicsAdd(string subscriberId, string topicName)
    {
        _ = _subscriberTopics.TryGetValue(subscriberId, out ImmutableHashSet<string>? subscriberTopics);
        if (subscriberTopics?.Contains(topicName) == true)
        {
            _logger.SubscribeTopicsAddDuplicateError(subscriberId, topicName);
            return false;
        }

        _subscriberTopics[subscriberId] = subscriberTopics == null ? [topicName] : subscriberTopics.Add(topicName);
        return true;
    }

    protected virtual bool SubscriberTopicsRemove(string subscriberId, string topicName)
    {
        _ = _subscriberTopics.TryGetValue(subscriberId, out ImmutableHashSet<string>? subscriberTopics);
        if (subscriberTopics?.Contains(topicName) == true)
        {
            ImmutableHashSet<string> updatedSubscriberTopics = subscriberTopics.Remove(topicName);
            if (updatedSubscriberTopics.Count > 0)
            {
                _subscriberTopics[subscriberId] = updatedSubscriberTopics;
            }
            else
            {
                _ = _subscriberTopics.TryRemove(subscriberId, out _);
            }

            return true;
        }

        return false;
    }

    protected virtual bool TopicsByPrefixAdd(string subscriberId, string topicName, string topicPrefix)
    {
        _ = _topicNumSubscribers.TryGetValue(topicName, out int count);
        _topicNumSubscribers[topicName] = count + 1;

        if (count == 0)
        {
            _ = _topicsByPrefix.TryGetValue(topicPrefix, out ImmutableHashSet<string>? topicsByPrefix);
            _topicsByPrefix[topicPrefix] = topicsByPrefix == null ? [topicName] : topicsByPrefix.Add(topicName);
        }

        return true;
    }

    protected virtual bool TopicsByPrefixRemove(string subscriberId, string topicName, string topicPrefix)
    {
        if (!_topicNumSubscribers.TryGetValue(topicName, out int count) || count <= 0)
        {
            return false;
        }

        if (count == 1)
        {
            if (!_topicNumSubscribers.Remove(topicName))
            {
                _logger.TopicsByPrefixRemoveTopicNumSubscribersNotRemovedWarning(subscriberId, topicName, topicPrefix);
            }

            _ = _topicsByPrefix.TryGetValue(topicPrefix, out ImmutableHashSet<string>? topicsByPrefix);
            if (topicsByPrefix == null)
            {
                _logger.TopicsByPrefixRemoveTopicsByPrefixNullError(subscriberId, topicName, topicPrefix);
                return false;
            }

            ImmutableHashSet<string> updatedTopicsByPrefix = topicsByPrefix.Remove(topicName);
            if (updatedTopicsByPrefix.Count > 0)
            {
                _topicsByPrefix[topicPrefix] = updatedTopicsByPrefix;
            }
            else if (!_topicsByPrefix.TryRemove(topicPrefix, out _))
            {
                _logger.TopicsByPrefixRemoveTopicsByPrefixNotRemovedWarning(subscriberId, topicName, topicPrefix);
            }
        }
        else
        {
            _topicNumSubscribers[topicName] = count - 1;
        }

        return true;
    }

    protected virtual bool UnsubscribeTopic(string subscriberId, string topicName, string topicPrefix)
    {
        return SubscriberTopicsRemove(subscriberId, topicName) && TopicsByPrefixRemove(subscriberId, topicName, topicPrefix);
    }
}

public static partial class SignalRTopicSubscriptionServiceLogs
{
    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "RemoveSubscriber: removed from all topics for subscriber `{SubscriberId}`: {TopicNames}")]
    public static partial void RemoveSubscriberInfo(
        this ILogger logger, string subscriberId, ImmutableHashSet<string> topicNames);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Error,
        Message = "SubscribeTopic: topic prefix null for topic `{TopicName}`: {SubscriberId}")]
    public static partial void SubscribeTopicPrefixNullError(
        this ILogger logger, string subscriberId, string topicName);

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Information,
        Message = "SubscribeTopic: subscribed for topic `{TopicName}`: {SubscriberId}")]
    public static partial void SubscribeTopicInfo(
        this ILogger logger, string subscriberId, string topicName);

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Error,
        Message = "UnsubscribeTopic: topic prefix null for topic `{TopicName}`: {SubscriberId}")]
    public static partial void UnsubscribeTopicPrefixNullError(
        this ILogger logger, string subscriberId, string topicName);

    [LoggerMessage(
        EventId = 50,
        Level = LogLevel.Information,
        Message = "UnsubscribeTopic: unsubscribed for topic `{TopicName}`: {SubscriberId}")]
    public static partial void UnsubscribeTopicInfo(
        this ILogger logger, string subscriberId, string topicName);

    [LoggerMessage(
        EventId = 60,
        Level = LogLevel.Error,
        Message = "SubscribeTopicsAdd: duplicate for topic `{TopicName}`: {SubscriberId}")]
    public static partial void SubscribeTopicsAddDuplicateError(
        this ILogger logger, string subscriberId, string topicName);

    [LoggerMessage(
        EventId = 70,
        Level = LogLevel.Warning,
        Message = "TopicsByPrefixRemove: topic number of subscribers not removed for topic `{TopicName}` with prefix `{topicPRefix}`: {SubscriberId}")]
    public static partial void TopicsByPrefixRemoveTopicNumSubscribersNotRemovedWarning(
        this ILogger logger, string subscriberId, string topicName, string topicPRefix);

    [LoggerMessage(
        EventId = 80,
        Level = LogLevel.Error,
        Message = "TopicsByPrefixRemove: topics by prefix null for topic `{TopicName}` with prefix `{topicPRefix}`: {SubscriberId}")]
    public static partial void TopicsByPrefixRemoveTopicsByPrefixNullError(
        this ILogger logger, string subscriberId, string topicName, string topicPRefix);

    [LoggerMessage(
        EventId = 90,
        Level = LogLevel.Error,
        Message = "TopicsByPrefixRemove: topics by prefix not removed for topic `{TopicName}` with prefix `{topicPRefix}`: {SubscriberId}")]
    public static partial void TopicsByPrefixRemoveTopicsByPrefixNotRemovedWarning(
        this ILogger logger, string subscriberId, string topicName, string topicPRefix);
}
