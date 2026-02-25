namespace Dalmarkit.Common.PubSub;

public class TopicMessage<TPayload>
{
    public required string Topic { get; set; }
    public required string Method { get; set; }
    public required TPayload? Payload { get; set; }
    public required DateTimeOffset PublishTimestamp { get; set; }
}
