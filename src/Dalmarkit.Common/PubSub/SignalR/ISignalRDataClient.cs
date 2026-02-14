namespace Dalmarkit.Common.PubSub.SignalR;

public interface ISignalRDataClient
{
    Task ReceiveMessage<TMessage>(TMessage message);
}
