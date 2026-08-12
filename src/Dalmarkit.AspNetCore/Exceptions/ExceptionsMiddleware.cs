using Dalmarkit.Common.Api.Responses;
using Dalmarkit.Common.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Text.Json;

namespace Dalmarkit.AspNetCore.Exceptions;

public class ExceptionsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionsMiddleware> _logger;

    public ExceptionsMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = loggerFactory.CreateLogger<ExceptionsMiddleware>();
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        // The caller went away: not a fault, so no Error log, no stack trace and no response - the socket
        // is already closed. Ordinary traffic on a public API (closed tab, backgrounded app, LB timeout).
        //
        // The `when` clause is load-bearing. Only an OperationCanceledException raised while
        // RequestAborted is signalled means the CLIENT left. One from an internal timeout or a linked
        // token is a real fault and must keep the 500 below; an unfiltered catch here would bury it.
        // TaskCanceledException derives from OperationCanceledException, so this covers both.
        //
        // Deliberately not rethrown. Re-throwing hands the exception to whatever wrapped this middleware -
        // typically request logging - which reports it as a failed request, reintroducing the noise this
        // clause removes. No status code is set either: the response never reaches anyone, ASP.NET Core
        // has no client-closed status (499 is nginx-specific), and inventing one would misreport the
        // request to every consumer's audit trail.
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.RequestCanceledByClient(context.Request.Path);
            }
        }
        catch (DbUpdateConcurrencyException ex)
        {
            if (context.Response.HasStarted)
            {
                _logger.ResponseAlreadyStartedForException(ex.Message, ex.InnerException?.Message, ex.StackTrace);
                throw;
            }

            await HandleExceptionsAsync(ex, StatusCodes.Status409Conflict, ErrorTypes.ResourceConflict);
        }
        // catch PostgreSQL unique violation exception
        // https://www.postgresql.org/docs/current/errcodes-appendix.html
        catch (DbUpdateException ex) when (ex.InnerException != null
            && ex.InnerException.GetType() == typeof(PostgresException)
            && ((PostgresException)ex.InnerException).SqlState == "23505")
        {
            if (context.Response.HasStarted)
            {
                _logger.ResponseAlreadyStartedForException(ex.Message, ex.InnerException?.Message, ex.StackTrace);
                throw;
            }

            await HandleExceptionsAsync(ex, StatusCodes.Status409Conflict, ErrorTypes.ResourceConflict);
        }
        // catch PostgreSQL foreign key violation exception
        // https://www.postgresql.org/docs/current/errcodes-appendix.html
        catch (DbUpdateException ex) when (ex.InnerException != null
            && ex.InnerException.GetType() == typeof(PostgresException)
            && ((PostgresException)ex.InnerException).SqlState == "23503")
        {
            if (context.Response.HasStarted)
            {
                _logger.ResponseAlreadyStartedForException(ex.Message, ex.InnerException?.Message, ex.StackTrace);
                throw;
            }

            await HandleExceptionsAsync(ex, StatusCodes.Status422UnprocessableEntity, ErrorTypes.BadRequest);
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                _logger.ResponseAlreadyStartedForException(ex.Message, ex.InnerException?.Message, ex.StackTrace);
                throw;
            }

            await HandleExceptionsAsync(ex, StatusCodes.Status500InternalServerError, ErrorTypes.ServerError);
        }

        async Task HandleExceptionsAsync(Exception ex, int responseStatusCode, ErrorDetail errorDetail)
        {
            _logger.CaughtUnhandledException(ex.Message, ex.InnerException?.Message, ex.StackTrace);
            context.Response.Clear();
            context.Response.StatusCode = responseStatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                new DefaultResponse
                {
                    ErrorCode = errorDetail.Code,
                    ErrorMessage = errorDetail.Message
                }));
        }
    }
}

public static partial class ExceptionMiddlewareLogs
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Unhandled exception caught with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void CaughtUnhandledException(
        this ILogger logger, string message, string? innerException, string? stackTrace);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Request to `{RequestPath}` was canceled by the client; no response written")]
    public static partial void RequestCanceledByClient(this ILogger logger, string requestPath);

    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Information,
        Message = "Exception middleware will not be executed as response has already started for exception with message `{Message}` and inner exception `{InnerException}`: {StackTrace}")]
    public static partial void ResponseAlreadyStartedForException(
        this ILogger logger, string message, string? innerException, string? stackTrace);
}
