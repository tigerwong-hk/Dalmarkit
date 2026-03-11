using Mediator;
using Microsoft.Extensions.Logging;

namespace Dalmarkit.Common.Events;

public class MediatorEventDispatcher(IMediator mediator, ILogger<MediatorEventDispatcher> logger) : IEventDispatcher
{
    private readonly IMediator _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
    private readonly ILogger<MediatorEventDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task DispatchEventAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
    {
        if (message is not INotification notification)
        {
            throw new InvalidOperationException($"Message type {typeof(TMessage).Name} is not supported. Expected INotification.");
        }

        try
        {
            await _mediator.Publish(notification, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.DispatchEventException(message.ToString() ?? string.Empty, ex.Message, ex.InnerException?.Message, ex.StackTrace);
            throw;
        }
    }
}

public static partial class MediatorEventDispatcherLogs
{
    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Error,
        Message = "Dispatch exception for event {Message} with message `{ExceptionMessage}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void DispatchEventException(
        this ILogger logger, string message, string exceptionMessage, string? innerException, string? stackTrace);
}
