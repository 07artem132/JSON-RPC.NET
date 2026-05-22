using Microsoft.Extensions.Logging;
using StreamJsonRpc.Protocol;
using WsRpcServer.Core;
using WsRpcServer.Exceptions;
using WsRpcServer.Services;

namespace SimpleServer;

public class DemoEventsRpcAdapter(
    ISubscriptionManager subscriptionManager,
    ILogger<DemoEventsRpcAdapter> logger,
    Guid clientId) : IDemoEventsRpc
{
    public async Task<int> Subscribe(string account, ServerEventType[] eventTypes, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("RPC: Client {ClientId} subscribing to {EventTypes}", clientId, eventTypes);
        try
        {
            return await subscriptionManager.Subscribe(clientId, account, eventTypes, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subscription failed");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Subscription failed", ex);
        }
    }

    public async Task<bool> Unsubscribe(int subscriptionId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("RPC: Client {ClientId} unsubscribing from {SubscriptionId}", clientId, subscriptionId);
        try
        {
            return await subscriptionManager.Unsubscribe(clientId, subscriptionId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unsubscription failed");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Unsubscription failed", ex);
        }
    }

    public async Task<bool> UpdateSubscription(int subscriptionId, ServerEventType[] eventTypes, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("RPC: Client {ClientId} updating subscription {SubscriptionId} to {EventTypes}", clientId, subscriptionId, eventTypes);
        try
        {
            return await subscriptionManager.UpdateSubscription(clientId, subscriptionId, eventTypes, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update subscription failed");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Update subscription failed", ex);
        }
    }
}
