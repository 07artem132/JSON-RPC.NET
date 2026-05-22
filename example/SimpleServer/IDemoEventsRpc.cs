using WsRpcServer.Services;

namespace SimpleServer;

public interface IDemoEventsRpc :IClientAwareRpcService
{
    Task<int> Subscribe(string account, ServerEventType[] eventTypes, CancellationToken cancellationToken = default);
    Task<bool> Unsubscribe(int subscriptionId, CancellationToken cancellationToken = default);
    Task<bool> UpdateSubscription(int subscriptionId, ServerEventType[] eventTypes, CancellationToken cancellationToken = default);

}