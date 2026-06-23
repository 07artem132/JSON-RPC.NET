using Microsoft.Extensions.Logging;
using WsRpcServer.Subscriptions;

namespace SimpleServer;

public class DemoSubscriptionManager(
    ILogger<DemoSubscriptionManager> logger,
    DemoEventProcessor eventProcessor)
    : AbstractSubscriptionManager<ServerEventType, object>(logger, 10)
{
    private readonly Dictionary<Guid, HashSet<ServerEventType>> _clientSubscriptions = new();
    private readonly DemoEventProcessor _eventProcessor = eventProcessor ?? throw new ArgumentNullException(nameof(eventProcessor));

    // Виконується під OperationLock (база серіалізує мутації) — реалізуємо лише бізнес-логіку.
    protected override Task<int> SubscribeCore(
        Guid clientId,
        string topic,
        IReadOnlyCollection<ServerEventType> eventTypes,
        CancellationToken cancellationToken)
    {
        Logger.LogInformation("Client {ClientId} subscribing to {EventTypes} on topic {Topic}",
            clientId, eventTypes, topic);

        if (!_clientSubscriptions.TryGetValue(clientId, out var events))
        {
            events = new HashSet<ServerEventType>();
            _clientSubscriptions[clientId] = events;
        }

        foreach (var type in eventTypes)
        {
            events.Add(type);
        }

        // Спеціальний ідентифікатор "усі підписані типи" для цього простого демо.
        return Task.FromResult(999);
    }

    protected override Task<bool> UnsubscribeCore(
        Guid clientId,
        int subscriptionId,
        CancellationToken cancellationToken)
    {
        Logger.LogInformation("Client {ClientId} unsubscribing from {SubscriptionId}",
            clientId, subscriptionId);

        if (!_clientSubscriptions.TryGetValue(clientId, out var events))
        {
            return Task.FromResult(false);
        }

        if (subscriptionId == 999)
        {
            _clientSubscriptions.Remove(clientId);
        }
        else if (Enum.IsDefined(typeof(ServerEventType), subscriptionId))
        {
            events.Remove((ServerEventType)subscriptionId);

            if (events.Count == 0)
            {
                _clientSubscriptions.Remove(clientId);
            }
        }
        else
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    protected override Task<bool> UpdateSubscriptionCore(
        Guid clientId,
        int subscriptionId,
        IReadOnlyCollection<ServerEventType> eventTypes,
        CancellationToken cancellationToken)
    {
        // Для цього демо оновлення = повторна підписка. Викликаємо *Core напряму (НЕ публічний
        // Subscribe), бо ми вже під OperationLock — інакше повторний захід у семафор = дедлок.
        _ = SubscribeCore(clientId, string.Empty, eventTypes, cancellationToken);
        return Task.FromResult(true);
    }

    public override List<Guid> GetClientsForEvent(object args, ServerEventType eventType)
    {
        return _clientSubscriptions
            .Where(kv => kv.Value.Contains(eventType))
            .Select(kv => kv.Key)
            .ToList();
    }
}
