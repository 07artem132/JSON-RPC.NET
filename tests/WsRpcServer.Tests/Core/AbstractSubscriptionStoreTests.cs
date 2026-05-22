using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WsRpcServer.Core;
using Xunit;

namespace WsRpcServer.Tests.Core
{
    public class AbstractSubscriptionStoreTests
    {
        // Define simple test types for our generic implementation
        public class TestSubscription
        {
            public int Id { get; set; }
            public string Account { get; set; }
            public HashSet<Guid> ClientIds { get; set; } = new HashSet<Guid>();
            public TestEventType EventType { get; set; }
        }

        public class TestEventArgs
        {
            public string Account { get; set; }
            public TestEventType EventType { get; set; }
        }

        public enum TestEventType
        {
            Type1,
            Type2,
            Type3
        }

        // Test implementation of AbstractSubscriptionStore
        private class TestSubscriptionStore : AbstractSubscriptionStore<TestSubscription, TestEventArgs, TestEventType>
        {
            // Simple in-memory storage for testing
            private readonly Dictionary<int, TestSubscription> _subscriptions = new();
            private readonly Dictionary<int, int> _providerSubscriptionIds = new();
            private readonly Dictionary<Guid, List<int>> _clientSubscriptions = new();

            // Track method calls for verification
            public bool AddSubscriptionCoreCalled { get; private set; }
            public bool GetSubscriptionCoreCalled { get;  set; }
            public bool RemoveSubscriptionCoreCalled { get;  set; }
            public bool UpdateSubscriptionCoreCalled { get;  set; }
            public bool GetClientSubscriptionIdsCoreCalled { get;  set; }
            public bool GetClientSubscriptionsInfoCoreCalled { get;  set; }
            public bool GetClientsForEventCoreCalled { get;  set; }
            public bool GetSubscriptionInfoCoreCalled { get;  set; }
            
            // Used to simulate delays for concurrency testing
            public int DelayMilliseconds { get; set; }

            protected override void AddSubscriptionCore(TestSubscription subscription, int providerSubscriptionId)
            {
                if (DelayMilliseconds > 0)
                    Thread.Sleep(DelayMilliseconds);
                
                AddSubscriptionCoreCalled = true;
                _subscriptions[subscription.Id] = subscription;
                _providerSubscriptionIds[subscription.Id] = providerSubscriptionId;
                
                foreach (var clientId in subscription.ClientIds)
                {
                    if (!_clientSubscriptions.TryGetValue(clientId, out var subscriptions))
                    {
                        _clientSubscriptions[clientId] = new List<int>();
                    }
                    
                    if (!_clientSubscriptions[clientId].Contains(subscription.Id))
                    {
                        _clientSubscriptions[clientId].Add(subscription.Id);
                    }
                }
            }

            protected override TestSubscription GetSubscriptionCore(int subscriptionId)
            {
                if (DelayMilliseconds > 0)
                    Thread.Sleep(DelayMilliseconds);
                
                GetSubscriptionCoreCalled = true;
                return _subscriptions.TryGetValue(subscriptionId, out var subscription) ? subscription : null;
            }

            protected override (TestSubscription Subscription, HashSet<Guid> RemainingClients, int ProviderSubscriptionId) 
                RemoveSubscriptionCore(Guid clientId, int subscriptionId)
            {
                if (DelayMilliseconds > 0)
                    Thread.Sleep(DelayMilliseconds);
                
                RemoveSubscriptionCoreCalled = true;
                
                if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
                {
                    return (null, null, 0);
                }
                
                subscription.ClientIds.Remove(clientId);
                
                if (_clientSubscriptions.TryGetValue(clientId, out var clientSubscriptions))
                {
                    clientSubscriptions.Remove(subscriptionId);
                    if (clientSubscriptions.Count == 0)
                    {
                        _clientSubscriptions.Remove(clientId);
                    }
                }
                
                int providerSubscriptionId = _providerSubscriptionIds.ContainsKey(subscriptionId) 
                    ? _providerSubscriptionIds[subscriptionId] 
                    : 0;
                
                if (subscription.ClientIds.Count == 0)
                {
                    _subscriptions.Remove(subscriptionId);
                    _providerSubscriptionIds.Remove(subscriptionId);
                    return (subscription, null, providerSubscriptionId);
                }
                
                return (subscription, subscription.ClientIds, providerSubscriptionId);
            }

            protected override void UpdateSubscriptionCore(TestSubscription subscription)
            {
                if (DelayMilliseconds > 0)
                    Thread.Sleep(DelayMilliseconds);
                
                UpdateSubscriptionCoreCalled = true;
                if (_subscriptions.ContainsKey(subscription.Id))
                {
                    _subscriptions[subscription.Id] = subscription;
                }
            }

            protected override List<int> GetClientSubscriptionIdsCore(Guid clientId)
            {
                if (DelayMilliseconds > 0)
                    Thread.Sleep(DelayMilliseconds);
                
                GetClientSubscriptionIdsCoreCalled = true;
                return _clientSubscriptions.TryGetValue(clientId, out var subscriptions) 
                    ? new List<int>(subscriptions) 
                    : new List<int>();
            }

            protected override Dictionary<int, string> GetClientSubscriptionsInfoCore(Guid clientId)
            {
                if (DelayMilliseconds > 0)
                    Thread.Sleep(DelayMilliseconds);
                
                GetClientSubscriptionsInfoCoreCalled = true;
                var result = new Dictionary<int, string>();
                
                if (_clientSubscriptions.TryGetValue(clientId, out var subscriptionIds))
                {
                    foreach (var id in subscriptionIds)
                    {
                        if (_subscriptions.TryGetValue(id, out var subscription))
                        {
                            result[id] = subscription.Account;
                        }
                    }
                }
                
                return result;
            }

            protected override List<Guid> GetClientsForEventCore(TestEventArgs args, TestEventType eventType)
            {
                if (DelayMilliseconds > 0)
                    Thread.Sleep(DelayMilliseconds);
                
                GetClientsForEventCoreCalled = true;
                var result = new HashSet<Guid>();
                
                foreach (var subscription in _subscriptions.Values)
                {
                    if (subscription.Account == args.Account && subscription.EventType == eventType)
                    {
                        foreach (var clientId in subscription.ClientIds)
                        {
                            result.Add(clientId);
                        }
                    }
                }
                
                return result.ToList();
            }

            protected override (string Account, int? ProviderSubscriptionId) GetSubscriptionInfoCore(int subscriptionId)
            {
                if (DelayMilliseconds > 0)
                    Thread.Sleep(DelayMilliseconds);
                
                GetSubscriptionInfoCoreCalled = true;
                
                if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
                {
                    return (null, null);
                }
                
                return (subscription.Account, 
                    _providerSubscriptionIds.TryGetValue(subscriptionId, out var id) ? id : (int?)null);
            }
            
            // Helper for testing if disposed
            public bool IsDisposed { get; private set; }
            
            public override void Dispose()
            {
                base.Dispose();
                IsDisposed = true;
            }
        }

        [Fact]
        public void AddSubscription_ValidSubscription_CallsAddSubscriptionCore()
        {
            // Arrange
            var store = new TestSubscriptionStore();
            var subscription = new TestSubscription
            {
                Id = 1,
                Account = "test-account",
                ClientIds = new HashSet<Guid> { Guid.NewGuid() },
                EventType = TestEventType.Type1
            };
            
            // Act
            store.AddSubscription(subscription, 42);
            
            // Assert
            Assert.True(store.AddSubscriptionCoreCalled);
        }
        
        [Fact]
        public void GetSubscription_ExistingId_CallsGetSubscriptionCoreAndReturnsSubscription()
        {
            // Arrange
            var store = new TestSubscriptionStore();
            var subscription = new TestSubscription
            {
                Id = 1,
                Account = "test-account",
                ClientIds = new HashSet<Guid> { Guid.NewGuid() },
                EventType = TestEventType.Type1
            };
            store.AddSubscription(subscription, 42);
            store.GetSubscriptionCoreCalled = false; // Reset after setup
            
            // Act
            var result = store.GetSubscription(1);
            
            // Assert
            Assert.True(store.GetSubscriptionCoreCalled);
            Assert.NotNull(result);
            Assert.Equal(subscription.Id, result.Id);
            Assert.Equal(subscription.Account, result.Account);
        }
        
        [Fact]
        public void GetSubscription_NonExistingId_CallsGetSubscriptionCoreAndReturnsNull()
        {
            // Arrange
            var store = new TestSubscriptionStore();
            
            // Act
            var result = store.GetSubscription(999);
            
            // Assert
            Assert.True(store.GetSubscriptionCoreCalled);
            Assert.Null(result);
        }

        [Fact]
        public void RemoveSubscription_ExistingClientAndSubscription_CallsRemoveSubscriptionCore()
        {
            // Arrange
            var store = new TestSubscriptionStore();
            var clientId = Guid.NewGuid();
            var subscription = new TestSubscription
            {
                Id = 1,
                Account = "test-account",
                ClientIds = new HashSet<Guid> { clientId },
                EventType = TestEventType.Type1
            };
            store.AddSubscription(subscription, 42);
            store.RemoveSubscriptionCoreCalled = false; // Reset after setup
            
            // Act
            var result = store.RemoveSubscription(clientId, 1);
            
            // Assert
            Assert.True(store.RemoveSubscriptionCoreCalled);
            Assert.NotNull(result.Subscription);
            Assert.Null(result.RemainingClients); // No remaining clients
            Assert.Equal(42, result.ProviderSubscriptionId);
        }
        
        [Fact]
        public void RemoveSubscription_MultipleClients_RemovesOneClientAndKeepsOthers()
        {
            // Arrange
            var store = new TestSubscriptionStore();
            var clientId1 = Guid.NewGuid();
            var clientId2 = Guid.NewGuid();
            var subscription = new TestSubscription
            {
                Id = 1,
                Account = "test-account",
                ClientIds = new HashSet<Guid> { clientId1, clientId2 },
                EventType = TestEventType.Type1
            };
            store.AddSubscription(subscription, 42);
            store.RemoveSubscriptionCoreCalled = false; // Reset after setup
            
            // Act
            var result = store.RemoveSubscription(clientId1, 1);
            
            // Assert
            Assert.True(store.RemoveSubscriptionCoreCalled);
            Assert.NotNull(result.Subscription);
            Assert.NotNull(result.RemainingClients);
            Assert.Single(result.RemainingClients);
            Assert.Contains(clientId2, result.RemainingClients);
        }

        [Fact]
        public void UpdateSubscription_ExistingSubscription_CallsUpdateSubscriptionCore()
        {
            // Arrange
            var store = new TestSubscriptionStore();
            var subscription = new TestSubscription
            {
                Id = 1,
                Account = "test-account",
                ClientIds = new HashSet<Guid> { Guid.NewGuid() },
                EventType = TestEventType.Type1
            };
            store.AddSubscription(subscription, 42);
            store.UpdateSubscriptionCoreCalled = false; // Reset after setup
            
            // Make changes to subscription
            subscription.Account = "updated-account";
            
            // Act
            store.UpdateSubscription(subscription);
            
            // Assert
            Assert.True(store.UpdateSubscriptionCoreCalled);
        }

        [Fact]
        public void GetClientSubscriptionIds_ExistingClient_CallsGetClientSubscriptionIdsCore()
        {
            // Arrange
            var store = new TestSubscriptionStore();
            var clientId = Guid.NewGuid();
            var subscription = new TestSubscription
            {
                Id = 1,
                Account = "test-account",
                ClientIds = new HashSet<Guid> { clientId },
                EventType = TestEventType.Type1
            };
            store.AddSubscription(subscription, 42);
            store.GetClientSubscriptionIdsCoreCalled = false; // Reset after setup
            
            // Act
            var subscriptionIds = store.GetClientSubscriptionIds(clientId);
            
            // Assert
            Assert.True(store.GetClientSubscriptionIdsCoreCalled);
            Assert.Single(subscriptionIds);
            Assert.Contains(1, subscriptionIds);
        }

        [Fact]
        public void GetClientSubscriptionsInfo_ExistingClient_CallsGetClientSubscriptionsInfoCore()
        {
            // Arrange
            var store = new TestSubscriptionStore();
            var clientId = Guid.NewGuid();
            var subscription = new TestSubscription
            {
                Id = 1,
                Account = "test-account",
                ClientIds = new HashSet<Guid> { clientId },
                EventType = TestEventType.Type1
            };
            store.AddSubscription(subscription, 42);
            store.GetClientSubscriptionsInfoCoreCalled = false; // Reset after setup
            
            // Act
            var info = store.GetClientSubscriptionsInfo(clientId);
            
            // Assert
            Assert.True(store.GetClientSubscriptionsInfoCoreCalled);
            Assert.Single(info);
            Assert.Equal("test-account", info[1]);
        }

        [Fact]
        public void GetClientsForEvent_MatchingSubscription_CallsGetClientsForEventCore()
        {
            // Arrange
            var store = new TestSubscriptionStore();
            var clientId = Guid.NewGuid();
            var subscription = new TestSubscription
            {
                Id = 1,
                Account = "test-account",
                ClientIds = new HashSet<Guid> { clientId },
                EventType = TestEventType.Type1
            };
            store.AddSubscription(subscription, 42);
            store.GetClientsForEventCoreCalled = false; // Reset after setup
            
            // Act
            var args = new TestEventArgs { Account = "test-account", EventType = TestEventType.Type1 };
            var clients = store.GetClientsForEvent(args, TestEventType.Type1);
            
            // Assert
            Assert.True(store.GetClientsForEventCoreCalled);
            Assert.Single(clients);
            Assert.Contains(clientId, clients);
        }

        [Fact]
        public void GetSubscriptionInfo_ExistingSubscription_CallsGetSubscriptionInfoCore()
        {
            // Arrange
            var store = new TestSubscriptionStore();
            var subscription = new TestSubscription
            {
                Id = 1,
                Account = "test-account",
                ClientIds = new HashSet<Guid> { Guid.NewGuid() },
                EventType = TestEventType.Type1
            };
            store.AddSubscription(subscription, 42);
            store.GetSubscriptionInfoCoreCalled = false; // Reset after setup
            
            // Act
            var info = store.GetSubscriptionInfo(1);
            
            // Assert
            Assert.True(store.GetSubscriptionInfoCoreCalled);
            Assert.Equal("test-account", info.Account);
            Assert.Equal(42, info.ProviderSubscriptionId);
        }

        [Fact]
        public void GenerateSubscriptionId_CalledMultipleTimes_ReturnsIncrementingIds()
        {
            // Arrange
            var store = new TestSubscriptionStore();
            
            // Act
            int id1 = store.GenerateSubscriptionId();
            int id2 = store.GenerateSubscriptionId();
            int id3 = store.GenerateSubscriptionId();
            
            // Assert
            Assert.True(id1 > 0);
            Assert.Equal(id1 + 1, id2);
            Assert.Equal(id2 + 1, id3);
        }

        [Fact]
        public void Dispose_CallsBaseDispose()
        {
            // Arrange
            var store = new TestSubscriptionStore();
            
            // Act
            store.Dispose();
            
            // Assert
            Assert.True(store.IsDisposed);
        }

        [Fact]
        public void ThreadSafety_ConcurrentReads_SynchronizesAccess()
        {
            // Arrange
            var store = new TestSubscriptionStore { DelayMilliseconds = 50 };
            var clientId = Guid.NewGuid();
            var subscription = new TestSubscription
            {
                Id = 1,
                Account = "test-account",
                ClientIds = new HashSet<Guid> { clientId },
                EventType = TestEventType.Type1
            };
            store.AddSubscription(subscription, 42);
            
            // Act & Assert - Multiple concurrent reads should work correctly
            var tasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    var result = store.GetSubscription(1);
                    Assert.NotNull(result);
                    Assert.Equal("test-account", result.Account);
                }));
            }
            
            // All tasks should complete without exceptions
            Task.WaitAll(tasks.ToArray());
        }

        [Fact]
        public void ThreadSafety_ConcurrentReadWrite_SynchronizesAccess()
        {
            // Arrange
            var store = new TestSubscriptionStore { DelayMilliseconds = 50 };
            var clientId = Guid.NewGuid();
            var subscription = new TestSubscription
            {
                Id = 1,
                Account = "test-account",
                ClientIds = new HashSet<Guid> { clientId },
                EventType = TestEventType.Type1
            };
            store.AddSubscription(subscription, 42);
            
            // Act - Start a write operation
            var writeTask = Task.Run(() =>
            {
                var updatedSubscription = new TestSubscription
                {
                    Id = 1,
                    Account = "updated-account",
                    ClientIds = new HashSet<Guid> { clientId },
                    EventType = TestEventType.Type2
                };
                store.UpdateSubscription(updatedSubscription);
            });
            
            // Act - Concurrently start read operations
            var readTasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
            {
                var result = store.GetSubscription(1);
                // The result should be either the original or updated value, not corrupted
                Assert.NotNull(result);
                Assert.Contains(result.Account, new[] { "test-account", "updated-account" });
            })).ToList();
            
            // All tasks should complete without exceptions
            Task.WaitAll(readTasks.Concat(new[] { writeTask }).ToArray());
        }
    }
}