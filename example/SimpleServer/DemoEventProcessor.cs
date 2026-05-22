using Microsoft.Extensions.Logging;
using WsRpcServer.Events;

namespace SimpleServer;

public class DemoEventProcessor : AbstractEventProcessor
{
    private readonly Timer _systemStatusTimer;

    public DemoEventProcessor(ILogger<DemoEventProcessor> logger) : base(logger)
    {
        // Create a timer to generate system status events
        _systemStatusTimer = new Timer(_ => PublishSystemStatus(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting DemoEventProcessor");
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Stopping DemoEventProcessor");
        _systemStatusTimer.Change(Timeout.Infinite, Timeout.Infinite);
        return base.StopAsync(cancellationToken);
    }

    private void PublishSystemStatus()
    {
        var status = new SystemStatusEvent($"Server running. Active clients: {ClientHandlers.Count}", DateTime.UtcNow);
            
        Logger.LogDebug("Publishing system status: {Status}", status.Status);
            
        // Notify all connected clients
        foreach (var clientId in ClientHandlers.Keys)
        {
            NotifyClient(clientId, "onSystemStatus", status);
        }
    }

    public void PublishUserActivity(string username, string action)
    {
        var activity = new UserActivityEvent(username, action, DateTime.UtcNow);
            
        Logger.LogDebug("Publishing user activity: {Username} {Action}", username, action);
            
        // Notify all connected clients
        foreach (var clientId in ClientHandlers.Keys)
        {
            NotifyClient(clientId, "onUserActivity", activity);
        }
    }
}
