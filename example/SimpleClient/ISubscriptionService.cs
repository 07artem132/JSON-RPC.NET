using StreamJsonRpc;

namespace SimpleClient;

/// <summary>
/// Interface for subscription management.
/// </summary>
public interface ISubscriptionService
{
    [JsonRpcMethod("subscribe")]
    Task<int> Subscribe(string account, ServerEventType[] eventTypes, CancellationToken cancellationToken = default);
    [JsonRpcMethod("unsubscribe")]
    Task<bool> Unsubscribe(int subscriptionId, CancellationToken cancellationToken = default);
    [JsonRpcMethod("updateSubscription")]
    Task<bool> UpdateSubscription(int subscriptionId, ServerEventType[] eventTypes, CancellationToken cancellationToken = default);

}
