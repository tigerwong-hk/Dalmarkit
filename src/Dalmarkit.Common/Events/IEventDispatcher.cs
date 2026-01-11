namespace Dalmarkit.Common.Events;

public interface IEventDispatcher
{
    Task DispatchEventAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default);
}
