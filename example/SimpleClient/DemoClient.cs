using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace SimpleClient;

/// <summary>
/// Client for the JSON-RPC demo server.
/// Handles WebSocket connection, RPC calls, and event subscriptions.
/// </summary>
public sealed class DemoClient : IAsyncDisposable
{
    private readonly Uri _serverUri;
    private readonly ILogger _logger;
    private readonly ClientWebSocket _webSocket;
    private readonly CancellationTokenSource _connectionCts = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly Dictionary<string, List<Action<object>>> _eventHandlers = new();
    private readonly JsonSerializerOptions _jsonOptions;

    private JsonRpc? _jsonRpc;
    private ICalculatorService? _calculatorService;
    private ISubscriptionService? _subscriptionService;
    private bool _isDisposed;

    /// <summary>
    /// Gets the calculator service proxy.
    /// </summary>
    public ICalculatorService Calculator => _calculatorService ??
                                            throw new InvalidOperationException(
                                                "Client not connected. Call ConnectAsync first.");

    /// <summary>
    /// Gets the subscription service proxy.
    /// </summary>
    public ISubscriptionService Subscriptions => _subscriptionService ??
                                                 throw new InvalidOperationException(
                                                     "Client not connected. Call ConnectAsync first.");

    /// <summary>
    /// Gets a value indicating whether the client is connected to the server.
    /// </summary>
    public bool IsConnected => _webSocket.State == WebSocketState.Open && _jsonRpc?.IsDisposed == false;

    /// <summary>
    /// Event raised when system status notifications are received.
    /// </summary>
    public event EventHandler<SystemStatusEvent>? OnSystemStatus;

    /// <summary>
    /// Event raised when user activity notifications are received.
    /// </summary>
    public event EventHandler<UserActivityEvent>? OnUserActivity;

    /// <summary>
    /// Creates a new instance of the <see cref="DemoClient"/> class.
    /// </summary>
    /// <param name="serverUri">The URI of the server.</param>
    /// <param name="logger">Optional logger for client operations.</param>
    public DemoClient(Uri serverUri, ILogger? logger = null)
    {
        _serverUri = serverUri ?? throw new ArgumentNullException(nameof(serverUri));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        _webSocket = new ClientWebSocket();
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        // Configure JSON options to match the server
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };


        // Set up event handlers
        ConfigureEventHandlers();
    }

    /// <summary>
    /// Connects to the server and initializes the JSON-RPC protocol.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the connection.</param>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                _logger.LogDebug("Already connected to server {Uri}", _serverUri);
                return;
            }

            _logger.LogInformation("Connecting to server {Uri}", _serverUri);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _connectionCts.Token, cancellationToken);

            await _webSocket.ConnectAsync(_serverUri, linkedCts.Token).ConfigureAwait(false);

            // Create the JSON-RPC formatter and message handler
            var formatter = new SystemTextJsonFormatter();
            formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

            var handler = new WebSocketMessageHandler(_webSocket, formatter);
            _jsonRpc = new JsonRpc(handler, handler);

            // Set up notification handling
            _jsonRpc.AddLocalRpcTarget(new NotificationHandlers(this, _logger, _jsonOptions));

            // Start listening for messages
            _jsonRpc.StartListening();

            // Create service proxies
            _calculatorService = _jsonRpc.Attach<ICalculatorService>();
            _subscriptionService = _jsonRpc.Attach<ISubscriptionService>();

            _logger.LogInformation("Connected to server {Uri}", _serverUri);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to server {Uri}", _serverUri);

            // Clean up resources if connection fails
            await DisconnectAsync().ConfigureAwait(false);

            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Disconnects from the server and cleans up resources.
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_webSocket.State != WebSocketState.Open)
            {
                return;
            }

            _logger.LogInformation("Disconnecting from server {Uri}", _serverUri);

            // Dispose JSON-RPC
            if (_jsonRpc != null)
            {
                _jsonRpc.Dispose();
                _jsonRpc = null;
            }

            // Reset service proxies
            _calculatorService = null;
            _subscriptionService = null;

            // Close WebSocket
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client initiated disconnect",
                    _connectionCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during WebSocket close");
            }

            _logger.LogInformation("Disconnected from server {Uri}", _serverUri);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Subscribes to server events.
    /// </summary>
    /// <param name="eventTypes">Event types to subscribe to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Subscription ID.</returns>
    public async Task<int> SubscribeAsync(ServerEventType[] eventTypes, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Client is not connected");
        }

        _logger.LogInformation("Subscribing to events: {EventTypes}",
            string.Join(", ", eventTypes.Select(e => e.ToString())));

        // Call the subscription service
        return await Subscriptions.Subscribe("default", eventTypes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Unsubscribes from server events.
    /// </summary>
    /// <param name="subscriptionId">The subscription ID to unsubscribe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if unsubscription was successful, false otherwise.</returns>
    public async Task<bool> UnsubscribeAsync(int subscriptionId, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Client is not connected");
        }

        _logger.LogInformation("Unsubscribing from subscription {SubscriptionId}", subscriptionId);

        // Call the subscription service
        return await Subscriptions.Unsubscribe( subscriptionId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Configures event handlers for server notifications.
    /// </summary>
    private static void ConfigureEventHandlers()
    {
        // Add handlers for specific notifications if needed
    }

    /// <summary>
    /// Disposes the client and releases all resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            // Disconnect if connected
            await DisconnectAsync().ConfigureAwait(false);

            // Cancel ongoing operations
            _connectionCts.Cancel();
            _connectionCts.Dispose();

            // Dispose resources
            _connectionLock.Dispose();
            _webSocket.Dispose();

            _isDisposed = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during client disposal");
        }
    }

    internal void RaiseSystemStatus(SystemStatusEvent e) =>
        OnSystemStatus?.Invoke(this, e);

    internal void RaiseUserActivity(UserActivityEvent e) =>
        OnUserActivity?.Invoke(this, e);
}






