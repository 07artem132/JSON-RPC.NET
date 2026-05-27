using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreServer;
using StreamJsonRpc;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Services;
using WsRpcServer.Sessions;
using WebSocketMessageHandler = WsRpcServer.Transport.WebSocketMessageHandler;

namespace SimpleServer;

/// <summary>
/// WebSocket JSON-RPC session implementation for the demo server.
/// Manages the lifecycle of a client connection.
/// </summary>
public sealed class DemoJsonRpcSession : AbstractJsonRpcSession
{
    private readonly ILogger<DemoJsonRpcSession> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRpcServiceRegistry _serviceRegistry;
    private readonly IEventProcessor _eventProcessor;
    private readonly DemoEventProcessor _demoEventProcessor;
    private WebSocketMessageHandler? _messageHandler;
    private Task? _processingTask;

    public DemoJsonRpcSession(
        WsServer server,
        ILogger<DemoJsonRpcSession> logger,
        IServiceProvider serviceProvider,
        IRpcServiceRegistry serviceRegistry,
        IEventProcessor eventProcessor,
        DemoEventProcessor demoEventProcessor,
        JsonRpcServerConfig config) : base(server, logger, config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _serviceRegistry = serviceRegistry ?? throw new ArgumentNullException(nameof(serviceRegistry));
        _eventProcessor = eventProcessor ?? throw new ArgumentNullException(nameof(eventProcessor));
        _demoEventProcessor = demoEventProcessor ?? throw new ArgumentNullException(nameof(demoEventProcessor));
        
        _logger.LogDebug("Created new WebSocket session: {Id}", Id);
    }

    /// <summary>
    /// Creates a JSON formatter with optimized settings for the demo
    /// </summary>
    private static SystemTextJsonFormatter CreateJsonFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        
        return formatter;
    }

    /// <summary>
    /// Called when a WebSocket connection is established
    /// </summary>
    public override void OnWsConnected(HttpRequest request)
    {
        _logger.LogInformation("WebSocket connection established: {ClientId}", Id);

        try
        {
            // Create a message formatter
            var formatter = CreateJsonFormatter();
            
            // Setup JsonRpc with our WebSocket handler
            _messageHandler = new WebSocketMessageHandler(
                this, 
                formatter, 
                _serviceProvider.GetRequiredService<ILogger<WebSocketMessageHandler>>(),
                Config);
            
            JsonRpc = new JsonRpc(_messageHandler, _messageHandler);
            
            // Configure JSON-RPC for better error handling
            JsonRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed = true;
            var jsonRpcTraceSource = new TraceSource("StreamJsonRpc")
            {
                Switch = { Level = SourceLevels.Information }
            };
            jsonRpcTraceSource.Listeners.Add(new ConsoleTraceListener());
            JsonRpc.TraceSource = jsonRpcTraceSource;
            JsonRpc.ActivityTracingStrategy = new ActivityTracingStrategy(); 

            // Register RPC services
            _serviceRegistry.RegisterServices(JsonRpc, Id);
            
            // Register event handling
            _eventProcessor.RegisterClient(Id, SendNotificationAsync);
            
            // Start notification processing in the background
            _processingTask = ProcessNotificationsAsync(Cts.Token);
            
            // Log user activity
            _demoEventProcessor.PublishUserActivity($"User-{Id}", "Connected");
            
            // Start the JSON-RPC handler
            JsonRpc.StartListening();
            
            _logger.LogDebug("JSON-RPC started listening for client {ClientId}", Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing JSON-RPC for session {ClientId}", Id);
            Close(WebSocketCloseStatus.InternalServerError, "Connection setup failed");
        }
    }

    /// <summary>
    /// Called when a WebSocket connection is closed
    /// </summary>
    public override void OnWsDisconnected()
    {
        _logger.LogInformation("WebSocket client disconnected: {ClientId}", Id);
        
        try
        {
            // Unregister from event processor
            _eventProcessor.UnregisterClient(Id);
            _logger.LogDebug("Client {ClientId} unregistered from event system", Id);
            
            // Log user activity
            _demoEventProcessor.PublishUserActivity($"User-{Id}", "Disconnected");
            
            // Cancel any ongoing operations
            Cts.Cancel();
            
            // Complete the notification channel
            NotificationChannel.Writer.TryComplete();
            _logger.LogDebug("Notification channel completed for client {ClientId}", Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during client disconnect cleanup: {ClientId}", Id);
        }
        
        base.OnWsDisconnected();
    }

    /// <summary>
    /// Processes incoming WebSocket data
    /// </summary>
    public override void OnWsReceived(byte[] buffer, long offset, long size)
    {
        try
        {
            if (IsDisposed || JsonRpc == null)
            {
                _logger.LogWarning("Received message after session disposal {ClientId}", Id);
                return;
            }

            // Check message size limit for DOS protection
            if (size > Config.MaxMessageSizeBytes)
            {
                _logger.LogWarning("Message exceeds maximum allowed size ({Size} > {MaxSize}) for client {ClientId}", 
                    size, Config.MaxMessageSizeBytes, Id);
                    
                Close(WebSocketCloseStatus.MessageTooBig, "Message exceeds size limit");
                return;
            }
            
            // Process the received message
            var segment = new ReadOnlyMemory<byte>(buffer, (int)offset, (int)size);
            
            _logger.LogDebug("Received message of size {Size} bytes for client {ClientId}", size, Id);
            
            // The WebSocketMessageHandler will process the data
            if (_messageHandler != null)
            {
                _ = _messageHandler.ProcessReceivedDataAsync(segment).AsTask();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WebSocket message of size {Size} for client {ClientId}", size, Id);
        }
    }

    /// <summary>
    /// Handles WebSocket connection closure
    /// </summary>
    public override void OnWsClose(byte[] buffer, long offset, long size, int status = 1000)
    {
        _logger.LogInformation("Received WebSocket close frame with status {Status} for client {ClientId}", status, Id);
        base.OnWsClose(buffer, offset, size, status);
    }

    protected override void Dispose(bool disposingManagedResources)
    {
        if (disposingManagedResources)
        {
            _logger.LogDebug("Disposing resources for client {ClientId}", Id);
        }

        base.Dispose(disposingManagedResources);
    }
}