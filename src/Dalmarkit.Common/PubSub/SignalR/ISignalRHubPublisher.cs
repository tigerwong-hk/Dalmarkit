namespace Dalmarkit.Common.PubSub.SignalR;

public interface ISignalRHubPublisher
{
    Task PublishToAll<TMessage>(TMessage message);
    Task PublishToConnection<TMessage>(string connectionId, TMessage message);
    Task PublishToGroup<TMessage>(string groupName, TMessage message);
}
