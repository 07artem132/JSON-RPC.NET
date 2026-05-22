using Microsoft.Extensions.Logging;
using WsRpcServer.Subscriptions;

namespace SimpleServer;

    public class DemoSubscriptionManager(
        ILogger<DemoSubscriptionManager> logger,
        DemoEventProcessor eventProcessor)
        : AbstractSubscriptionManager(logger, 10)
    {
        private readonly Dictionary<Guid, HashSet<ServerEventType>> _clientSubscriptions = new();
        private readonly DemoEventProcessor _eventProcessor = eventProcessor ?? throw new ArgumentNullException(nameof(eventProcessor));

        // Implementation of Subscribe method
        public override Task<int> Subscribe(
            Guid clientId,
            string account,
            object eventTypes,
            CancellationToken cancellationToken = default)
        {
            Logger.LogInformation("Client {ClientId} subscribing to {EventTypes}", 
                clientId, eventTypes);

            var subscriptionId = 0;

            if (eventTypes is ServerEventType eventType)
            {
                // Subscribe to a single event type
                if (!_clientSubscriptions.TryGetValue(clientId, out var events))
                {
                    events = new HashSet<ServerEventType>();
                    _clientSubscriptions[clientId] = events;
                }

                events.Add(eventType);
                subscriptionId = (int)eventType;
            }
            else if (eventTypes is ServerEventType[] eventTypesArray)
            {
                // Subscribe to multiple event types
                if (!_clientSubscriptions.TryGetValue(clientId, out var events))
                {
                    events = new HashSet<ServerEventType>();
                    _clientSubscriptions[clientId] = events;
                }

                foreach (var type in eventTypesArray)
                {
                    events.Add(type);
                }
                
                subscriptionId = 999; // Special ID for "all events"
            }

            return Task.FromResult(subscriptionId);
        }

        // Implementation of Unsubscribe method
        public override Task<bool> Unsubscribe(
            Guid clientId,
            int subscriptionId,
            CancellationToken cancellationToken = default)
        {
            Logger.LogInformation("Client {ClientId} unsubscribing from {SubscriptionId}", 
                clientId, subscriptionId);

            if (!_clientSubscriptions.TryGetValue(clientId, out var events))
            {
                return Task.FromResult(false);
            }

            if (subscriptionId == 999)
            {
                // Unsubscribe from all events
                _clientSubscriptions.Remove(clientId);
            }
            else if (Enum.IsDefined(typeof(ServerEventType), subscriptionId))
            {
                // Unsubscribe from a specific event type
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

        // Implementation of UpdateSubscription method
        public override Task<bool> UpdateSubscription(
            Guid clientId,
            int subscriptionId,
            object eventTypes,
            CancellationToken cancellationToken = default)
        {
            // For our simple demo, this is just the same as subscribing
            _ = Subscribe(clientId, "", eventTypes, cancellationToken);
            return Task.FromResult(true);
        }

        // Implementation of GetClientsForEvent method
        public override List<Guid> GetClientsForEvent(object args, object eventType)
        {
            if (eventType is not ServerEventType type)
            {
                return new List<Guid>();
            }

            return _clientSubscriptions
                .Where(kv => kv.Value.Contains(type))
                .Select(kv => kv.Key)
                .ToList();
        }
    }
