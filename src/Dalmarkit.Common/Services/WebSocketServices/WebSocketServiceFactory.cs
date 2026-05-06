using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Dalmarkit.Common.Services.WebSocketServices;

public class WebSocketServiceFactory<TService> : IWebSocketServiceFactory<TService>
    where TService : class, IClientWebSocketPubService
{
    private const int DisposeLockWaitMilliseconds = 5000;

    /// <summary>
    /// should not be divisible by a small prime and larger than WebSocketServiceFactoryOptions.MaxConcurrentUsersLimit
    /// </summary>
    private const int ServiceEntriesInitialCapacity = 2053;

    private sealed class ServiceEntry
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public IServiceScope? Scope;
        public TService? Service;

        /// <summary>
        /// Set under Lock by the thread that decides to remove the entry from the dictionary
        /// Any waiter that subsequently acquires Lock observes the tombstone and retries with a fresh entry
        /// </summary>
        public bool Evicted;
    }

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly Type _concreteType;
    private readonly int _maxConcurrentUsers;
    private readonly ILogger<WebSocketServiceFactory<TService>> _logger;
    private readonly ConcurrentDictionary<string, ServiceEntry> _serviceEntries = new(Environment.ProcessorCount, ServiceEntriesInitialCapacity, StringComparer.Ordinal);

    private int _activeServiceCount;

    private volatile int _isDisposed;

    public WebSocketServiceFactory(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<WebSocketServiceFactoryOptions> options,
        ILogger<WebSocketServiceFactory<TService>> logger,
        Type concreteType)
    {
        Debug.Assert(ServiceEntriesInitialCapacity > WebSocketServiceFactoryOptions.MaxConcurrentUsersLimit);

        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _concreteType = concreteType ?? throw new ArgumentNullException(nameof(concreteType));

        if (!typeof(TService).IsAssignableFrom(concreteType))
        {
            throw new ArgumentException(
                $"Concrete type `{concreteType.FullName}` does not implement `{typeof(TService).FullName}`",
                nameof(concreteType));
        }

        WebSocketServiceFactoryOptions opts = options?.Value ?? throw new ArgumentNullException(nameof(options));
        if (opts.MaxConcurrentUsers <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(opts.MaxConcurrentUsers)} must be positive");
        }
        if (opts.MaxConcurrentUsers > WebSocketServiceFactoryOptions.MaxConcurrentUsersLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(opts.MaxConcurrentUsers)} must not exceed {WebSocketServiceFactoryOptions.MaxConcurrentUsersLimit}");
        }

        _maxConcurrentUsers = opts.MaxConcurrentUsers;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        if (!disposing)
        {
            return;
        }

        // Drain the dictionary repeatedly
        // Concurrent ConnectAsync that won race on `_isDisposed` can still GetOrAdd a fresh entry before observing flipped flag
        // Loop until empty
        List<KeyValuePair<string, ServiceEntry>> drainedServiceEntries = [];
        while (!_serviceEntries.IsEmpty)
        {
            foreach (string key in _serviceEntries.Keys)
            {
                if (_serviceEntries.TryRemove(key, out ServiceEntry? serviceEntry))
                {
                    drainedServiceEntries.Add(new KeyValuePair<string, ServiceEntry>(key, serviceEntry));
                }
            }
        }

        foreach (KeyValuePair<string, ServiceEntry> kvp in drainedServiceEntries)
        {
            ServiceEntry serviceEntry = kvp.Value;

            // Best-effort: take the per-id lock so we don't race a concurrent op already inside it
            // Use a bounded wait so a stuck operation cannot deadlock shutdown
            bool enteredLock = false;
            try
            {
                enteredLock = serviceEntry.Lock.Wait(TimeSpan.FromMilliseconds(DisposeLockWaitMilliseconds));
            }
            catch (Exception ex)
            {
                _logger.WebSocketServiceFactoryDisposeLockWaitException(kvp.Key, ex);
            }

            try
            {
                serviceEntry.Evicted = true;

                TService? service = serviceEntry.Service;
                IServiceScope? scope = serviceEntry.Scope;
                serviceEntry.Service = null;
                serviceEntry.Scope = null;

                if (service != null)
                {
                    try
                    {
                        service.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.WebSocketServiceFactoryDisposeServiceDisposeException(kvp.Key, ex);
                    }
                    _ = Interlocked.Decrement(ref _activeServiceCount);
                }

                if (scope != null)
                {
                    try
                    {
                        scope.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.WebSocketServiceFactoryDisposeScopeDisposeException(kvp.Key, ex);
                    }
                }
            }
            finally
            {
                if (enteredLock)
                {
                    try
                    {
                        _ = serviceEntry.Lock.Release();
                    }
                    catch (Exception ex)
                    {
                        _logger.WebSocketServiceFactoryDisposeLockReleaseException(kvp.Key, ex);
                    }
                }

                try
                {
                    serviceEntry.Lock.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.WebSocketServiceFactoryDisposeLockDisposeException(kvp.Key, ex);
                }
            }
        }
    }

    public async Task<TService> ConnectAsync(string webSocketId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(webSocketId);

        // Retry loop
        // Queued waiter may discover, after acquiring the lock, that a racer (a failed concurrent Connect or a Disconnect) evicted the entry from the dictionary while it was waiting
        // In that case the entry is a tombstone and we must `GetOrAdd` a fresh one and queue again
        while (true)
        {
            ServiceEntry serviceEntry = _serviceEntries.GetOrAdd(webSocketId, static _ => new ServiceEntry());

            try
            {
                await serviceEntry.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.WebSocketServiceFactoryConnectAsyncSemaphoreWaitCanceledInfo(webSocketId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.WebSocketServiceFactoryConnectAsyncSemaphoreWaitException(webSocketId, ex);
                throw;
            }

            try
            {
                ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

                if (serviceEntry.Evicted)
                {
                    _logger.WebSocketServiceFactoryConnectAsyncEvictedEntryRetryInfo(webSocketId);
                    continue;
                }

                if (serviceEntry.Service != null)
                {
                    return serviceEntry.Service;
                }

                int newCount = Interlocked.Increment(ref _activeServiceCount);
                if (newCount > _maxConcurrentUsers)
                {
                    _ = Interlocked.Decrement(ref _activeServiceCount);
                    EvictServiceEntryUnderLock(webSocketId, serviceEntry);
                    _logger.WebSocketServiceFactoryConnectAsyncMaxConcurrentUsersReachedWarning(webSocketId, _maxConcurrentUsers);
                    throw new InvalidOperationException($"Maximum concurrent users ({_maxConcurrentUsers}) reached");
                }

                IServiceScope scope = _serviceScopeFactory.CreateScope();
                TService service;
                try
                {
                    service = WebSocketServiceBuilder.Build<TService>(scope.ServiceProvider, _concreteType);
                }
                catch (Exception ex)
                {
                    scope.Dispose();
                    _ = Interlocked.Decrement(ref _activeServiceCount);
                    EvictServiceEntryUnderLock(webSocketId, serviceEntry);
                    _logger.WebSocketServiceFactoryConnectAsyncBuildServiceException(webSocketId, ex);
                    throw;
                }

                try
                {
                    await service.ConnectAsync(webSocketId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.WebSocketServiceFactoryConnectAsyncServiceConnectCanceledInfo(webSocketId);
                    DisposeFailedService(webSocketId, service, scope);
                    _ = Interlocked.Decrement(ref _activeServiceCount);
                    EvictServiceEntryUnderLock(webSocketId, serviceEntry);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.WebSocketServiceFactoryConnectAsyncServiceConnectException(webSocketId, ex);
                    DisposeFailedService(webSocketId, service, scope);
                    _ = Interlocked.Decrement(ref _activeServiceCount);
                    EvictServiceEntryUnderLock(webSocketId, serviceEntry);
                    throw;
                }

                serviceEntry.Scope = scope;
                serviceEntry.Service = service;
                return service;
            }
            finally
            {
                _ = serviceEntry.Lock.Release();
            }
        }
    }

    public async Task DisconnectAsync(string webSocketId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(webSocketId);

        if (!_serviceEntries.TryGetValue(webSocketId, out ServiceEntry? serviceEntry))
        {
            return;
        }

        try
        {
            await serviceEntry.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.WebSocketServiceFactoryDisconnectAsyncSemaphoreWaitCanceledInfo(webSocketId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.WebSocketServiceFactoryDisconnectAsyncSemaphoreWaitException(webSocketId, ex);
            throw;
        }

        try
        {
            // The entry might have been evicted (e.g. a parallel failed Connect) or may have been re-used by a fresh Connect after a previous Disconnect
            // Either way: nothing live to tear down here
            if (serviceEntry.Evicted || serviceEntry.Service is null)
            {
                return;
            }

            TService service = serviceEntry.Service;
            IServiceScope scope = serviceEntry.Scope!;
            serviceEntry.Service = null;
            serviceEntry.Scope = null;

            try
            {
                await service.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.WebSocketServiceFactoryDisconnectAsyncServiceDisconnectCanceledInfo(webSocketId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.WebSocketServiceFactoryDisconnectAsyncServiceDisconnectException(webSocketId, ex);
                throw;
            }
            finally
            {
                try
                {
                    service.Dispose();
                }
                catch (Exception disposeEx)
                {
                    _logger.WebSocketServiceFactoryDisconnectAsyncDisposeDisconnectedServiceException(webSocketId, disposeEx);
                }

                try
                {
                    scope.Dispose();
                }
                catch (Exception disposeEx)
                {
                    _logger.WebSocketServiceFactoryDisconnectAsyncDisposeScopeException(webSocketId, disposeEx);
                }

                _ = Interlocked.Decrement(ref _activeServiceCount);
                EvictServiceEntryUnderLock(webSocketId, serviceEntry);
            }
        }
        finally
        {
            _ = serviceEntry.Lock.Release();
        }
    }

    public TService GetService(string webSocketId)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(webSocketId);

        return _serviceEntries.TryGetValue(webSocketId, out ServiceEntry? serviceEntry) && serviceEntry.Service is TService service
            ? service
            : throw new KeyNotFoundException($"No connected websocket service for websocket id `{webSocketId}`");
    }

    public bool TryGetService(string webSocketId, out TService? service)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(webSocketId);

        if (_serviceEntries.TryGetValue(webSocketId, out ServiceEntry? serviceEntry) && serviceEntry.Service != null)
        {
            service = serviceEntry.Service;
            return true;
        }

        service = null;
        return false;
    }

    private void DisposeFailedService(string webSocketId, TService service, IServiceScope scope)
    {
        try
        {
            service.Dispose();
        }
        catch (Exception ex)
        {
            _logger.WebSocketServiceFactoryDisposeFailedServiceDisposeServiceException(webSocketId, ex);
        }

        try
        {
            scope.Dispose();
        }
        catch (Exception disposeEx)
        {
            _logger.WebSocketServiceFactoryDisposeFailedServiceDisposeScopeException(webSocketId, disposeEx);
        }
    }

    /// <summary>
    /// Tombstones the entry under its Lock and removes it from the dictionary only if the dictionary's current value is still this exact instance
    /// The caller must hold serviceEntry.Lock
    /// </summary>
    /// <param name="webSocketId">Web socket identifier</param>
    /// <param name="serviceEntry">Service entry</param>
    private void EvictServiceEntryUnderLock(string webSocketId, ServiceEntry serviceEntry)
    {
        serviceEntry.Evicted = true;
        _ = _serviceEntries.TryRemove(KeyValuePair.Create(webSocketId, serviceEntry));
    }
}

public static partial class WebSocketServiceFactoryLogs
{
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryDispose: lock wait exception for websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisposeLockWaitException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryDispose: service dispose exception for websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisposeServiceDisposeException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryDispose: scope dispose exception for websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisposeScopeDisposeException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 1040,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryDispose: lock release exception for websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisposeLockReleaseException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 1050,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryDispose: lock dispose exception for websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisposeLockDisposeException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Information,
        Message = "WebSocketServiceFactoryConnectAsync: semaphore wait canceled when connecting websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryConnectAsyncSemaphoreWaitCanceledInfo(
        this ILogger logger, string webSocketId);

    [LoggerMessage(
        EventId = 2020,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryConnectAsync: semaphore wait exception when connecting websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryConnectAsyncSemaphoreWaitException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 2030,
        Level = LogLevel.Warning,
        Message = "WebSocketServiceFactoryConnectAsync: maximum concurrent users reached when connecting websocket id `{WebSocketId}`: {MaxConcurrentUsers}")]
    public static partial void WebSocketServiceFactoryConnectAsyncMaxConcurrentUsersReachedWarning(
        this ILogger logger, string webSocketId, int maxConcurrentUsers);

    [LoggerMessage(
        EventId = 2040,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryConnectAsync: build service exception when connecting websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryConnectAsyncBuildServiceException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 2050,
        Level = LogLevel.Information,
        Message = "WebSocketServiceFactoryConnectAsync: service connect canceled when connecting websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryConnectAsyncServiceConnectCanceledInfo(
        this ILogger logger, string webSocketId);

    [LoggerMessage(
        EventId = 2060,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryConnectAsync: service connect exception when connecting websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryConnectAsyncServiceConnectException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 2070,
        Level = LogLevel.Information,
        Message = "WebSocketServiceFactoryConnectAsync: evicted entry observed for websocket id `{WebSocketId}`, retrying with a fresh entry")]
    public static partial void WebSocketServiceFactoryConnectAsyncEvictedEntryRetryInfo(
        this ILogger logger, string webSocketId);

    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Information,
        Message = "WebSocketServiceFactoryDisconnectAsync: semaphore wait canceled when disconnecting websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisconnectAsyncSemaphoreWaitCanceledInfo(
        this ILogger logger, string webSocketId);

    [LoggerMessage(
        EventId = 3020,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryDisconnectAsync: semaphore wait exception when disconnecting websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisconnectAsyncSemaphoreWaitException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 3030,
        Level = LogLevel.Information,
        Message = "WebSocketServiceFactoryDisconnectAsync: service disconnect canceled when disconnecting websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisconnectAsyncServiceDisconnectCanceledInfo(
        this ILogger logger, string webSocketId);

    [LoggerMessage(
        EventId = 3040,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryDisconnectAsync: service disconnect exception when disconnecting websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisconnectAsyncServiceDisconnectException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 3050,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryDisconnectAsync: dispose disconnected service exception for websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisconnectAsyncDisposeDisconnectedServiceException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 3060,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryDisconnectAsync: dispose scope exception for websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisconnectAsyncDisposeScopeException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryDisposeFailedService: dispose service exception for websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisposeFailedServiceDisposeServiceException(
        this ILogger logger, string webSocketId, Exception exception);

    [LoggerMessage(
        EventId = 4020,
        Level = LogLevel.Error,
        Message = "WebSocketServiceFactoryDisposeFailedService: dispose scope exception for websocket id `{WebSocketId}`")]
    public static partial void WebSocketServiceFactoryDisposeFailedServiceDisposeScopeException(
        this ILogger logger, string webSocketId, Exception exception);
}
