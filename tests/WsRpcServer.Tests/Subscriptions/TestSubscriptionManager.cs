using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WsRpcServer.Core;
using WsRpcServer.Subscriptions;
using Xunit;

namespace WsRpcServer.Tests.Subscriptions
{
    // Тестовая реализация абстрактного класса для целей тестирования
    public class TestSubscriptionManager : AbstractSubscriptionManager<string, object>
    {
        private readonly Dictionary<Guid, HashSet<int>> _clientSubscriptions = new();
        private readonly Dictionary<int, string> _subscriptionTopics = new();
        private readonly Dictionary<int, IReadOnlyCollection<string>> _subscriptionEventTypes = new();
        private int _nextSubscriptionId = 1;

        public TestSubscriptionManager(ILogger logger, int maxSubscriptionsPerClient = 10)
            : base(logger, maxSubscriptionsPerClient)
        {
        }

        // Реалізація *Core-методів (виконуються під OperationLock бази — M2)
        protected override Task<int> SubscribeCore(
            Guid clientId,
            string topic,
            IReadOnlyCollection<string> eventTypes,
            CancellationToken cancellationToken)
        {
            var subscriptionId = _nextSubscriptionId++;

            if (!_clientSubscriptions.TryGetValue(clientId, out var subscriptions))
            {
                subscriptions = new HashSet<int>();
                _clientSubscriptions[clientId] = subscriptions;
            }

            // Проверка на превышение максимального количества подписок
            if (subscriptions.Count >= MaxSubscriptionsPerClient)
            {
                throw new InvalidOperationException($"Client {clientId} has reached the maximum number of subscriptions");
            }

            subscriptions.Add(subscriptionId);
            _subscriptionTopics[subscriptionId] = topic;
            _subscriptionEventTypes[subscriptionId] = eventTypes;

            return Task.FromResult(subscriptionId);
        }

        protected override Task<bool> UnsubscribeCore(
            Guid clientId,
            int subscriptionId,
            CancellationToken cancellationToken)
        {
            if (!_clientSubscriptions.TryGetValue(clientId, out var subscriptions))
            {
                return Task.FromResult(false);
            }

            var result = subscriptions.Remove(subscriptionId);
            if (result)
            {
                _subscriptionTopics.Remove(subscriptionId);
                _subscriptionEventTypes.Remove(subscriptionId);
            }

            return Task.FromResult(result);
        }

        protected override Task<bool> UpdateSubscriptionCore(
            Guid clientId,
            int subscriptionId,
            IReadOnlyCollection<string> eventTypes,
            CancellationToken cancellationToken)
        {
            if (!_clientSubscriptions.TryGetValue(clientId, out var subscriptions) || 
                !subscriptions.Contains(subscriptionId))
            {
                return Task.FromResult(false);
            }

            _subscriptionEventTypes[subscriptionId] = eventTypes;
            return Task.FromResult(true);
        }

        public override List<Guid> GetClientsForEvent(object args, string eventType)
        {
            var result = new List<Guid>();

            foreach (var (clientId, subscriptions) in _clientSubscriptions)
            {
                foreach (var subscriptionId in subscriptions)
                {
                    if (_subscriptionEventTypes.TryGetValue(subscriptionId, out var subscriptionEventTypes)
                        && subscriptionEventTypes.Contains(eventType))
                    {
                        result.Add(clientId);
                        break; // Клієнта вже додано, інші підписки перевіряти не треба
                    }
                }
            }

            return result;
        }

        // Публичные методы для тестирования защищенных полей
        public SemaphoreSlim OperationLockAccessor => OperationLock;
        public ConcurrentDictionary<Guid, int> ClientSubscriptionCountsAccessor => ClientSubscriptionCounts;
        public bool IsDisposedAccessor => IsDisposed;
        public int GetMaxSubscriptionsPerClient() => MaxSubscriptionsPerClient;

        // Метод для подсчета текущего количества подписок
        public int GetSubscriptionCount(Guid clientId)
        {
            return _clientSubscriptions.TryGetValue(clientId, out var subscriptions) ? subscriptions.Count : 0;
        }
    }

    public class AbstractSubscriptionManagerTests : IDisposable
    {
        private static readonly string[] _eventsCreateUpdate = ["Create", "Update"];
        private static readonly string[] _eventsCreateOnly = ["Create"];
        private static readonly string[] _eventsUpdateOnly = ["Update"];
        private static readonly string[] _eventsDeleteOnly = ["Delete"];
        private static readonly string[] _eventsGenericEvent = ["Event"];
        private static readonly string[] _eventsCreateUpdateDelete = ["Create", "Update", "Delete"];

        private readonly Mock<ILogger> _loggerMock;
        private readonly TestSubscriptionManager _manager;
        private readonly Guid _clientId = Guid.NewGuid();
        private const string _testAccount = "test-account";
        private const int _maxSubscriptions = 3;

        public AbstractSubscriptionManagerTests()
        {
            _loggerMock = new Mock<ILogger>();
            _manager = new TestSubscriptionManager(_loggerMock.Object, _maxSubscriptions);
        }

        public void Dispose()
        {
            _manager.Dispose();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void Constructor_InitializesProperties()
        {
            // Arrange
            // Конструктор вызывается в конструкторе теста

            // Act & Assert
            // Проверяем, что свойства были инициализированы правильно
            Assert.NotNull(_manager.OperationLockAccessor);
            Assert.NotNull(_manager.ClientSubscriptionCountsAccessor);
            Assert.False(_manager.IsDisposedAccessor);
            Assert.Equal(_maxSubscriptions, _manager.GetMaxSubscriptionsPerClient());
        }

        [Fact]
        public async Task Subscribe_ValidParameters_ReturnsSubscriptionId()
        {
            // Arrange
            // Подготавливаем тестовые параметры подписки
            var eventTypes = _eventsCreateUpdate;

            // Act
            // Создаем подписку и получаем ее идентификатор
            var subscriptionId = await _manager.Subscribe(_clientId, _testAccount, eventTypes);

            // Assert
            // Проверяем, что идентификатор подписки возвращен и подписка создана
            Assert.True(subscriptionId > 0);
            Assert.Equal(1, _manager.GetSubscriptionCount(_clientId));
        }

        [Fact]
        public async Task Subscribe_MultipleSubscriptions_ReturnsUniqueIds()
        {
            // Arrange
            // Подготавливаем параметры для нескольких подписок
            var eventTypes1 = _eventsCreateOnly;
            var eventTypes2 = _eventsUpdateOnly;

            // Act
            // Создаем две подписки для одного клиента
            var subscriptionId1 = await _manager.Subscribe(_clientId, _testAccount, eventTypes1);
            var subscriptionId2 = await _manager.Subscribe(_clientId, _testAccount, eventTypes2);

            // Assert
            // Проверяем, что идентификаторы подписок уникальны
            Assert.NotEqual(subscriptionId1, subscriptionId2);
            Assert.Equal(2, _manager.GetSubscriptionCount(_clientId));
        }

        [Fact]
        public async Task Subscribe_ExceedsMaxSubscriptions_ThrowsException()
        {
            // Arrange
            // Подготавливаем параметры для максимального количества подписок + 1
            var eventTypes = _eventsGenericEvent;

            // Act & Assert
            // Создаем подписки до достижения максимума
            for (int i = 0; i < _maxSubscriptions; i++)
            {
                await _manager.Subscribe(_clientId, _testAccount, eventTypes);
            }

            // Попытка создать еще одну подписку должна вызвать исключение
            await Assert.ThrowsAsync<InvalidOperationException>(async () => 
                await _manager.Subscribe(_clientId, _testAccount, eventTypes));
        }

        [Fact]
        public async Task Unsubscribe_ExistingSubscription_ReturnsTrue()
        {
            // Arrange
            // Создаем подписку, которую затем отменим
            var eventTypes = _eventsCreateOnly;
            var subscriptionId = await _manager.Subscribe(_clientId, _testAccount, eventTypes);

            // Act
            // Отменяем существующую подписку
            var result = await _manager.Unsubscribe(_clientId, subscriptionId);

            // Assert
            // Проверяем, что подписка успешно отменена
            Assert.True(result);
            Assert.Equal(0, _manager.GetSubscriptionCount(_clientId));
        }

        [Fact]
        public async Task Unsubscribe_NonExistingSubscription_ReturnsFalse()
        {
            // Arrange
            // Используем несуществующий идентификатор подписки
            int nonExistingSubscriptionId = 9999;

            // Act
            // Пытаемся отменить несуществующую подписку
            var result = await _manager.Unsubscribe(_clientId, nonExistingSubscriptionId);

            // Assert
            // Проверяем, что результат операции отрицательный
            Assert.False(result);
        }

        [Fact]
        public async Task Unsubscribe_NonExistingClient_ReturnsFalse()
        {
            // Arrange
            // Используем существующую подписку, но для другого клиента
            var eventTypes = _eventsCreateOnly;
            var subscriptionId = await _manager.Subscribe(_clientId, _testAccount, eventTypes);
            var differentClientId = Guid.NewGuid();

            // Act
            // Пытаемся отменить подписку от имени другого клиента
            var result = await _manager.Unsubscribe(differentClientId, subscriptionId);

            // Assert
            // Проверяем, что результат операции отрицательный
            Assert.False(result);
            // Проверяем, что исходная подписка не была затронута
            Assert.Equal(1, _manager.GetSubscriptionCount(_clientId));
        }

        [Fact]
        public async Task UpdateSubscription_ExistingSubscription_ReturnsTrue()
        {
            // Arrange
            // Создаем подписку, которую затем обновим
            var originalEventTypes = _eventsCreateOnly;
            var newEventTypes = _eventsCreateUpdateDelete;
            var subscriptionId = await _manager.Subscribe(_clientId, _testAccount, originalEventTypes);

            // Act
            // Обновляем существующую подписку
            var result = await _manager.UpdateSubscription(_clientId, subscriptionId, newEventTypes);

            // Assert
            // Проверяем, что подписка успешно обновлена
            Assert.True(result);
        }

        [Fact]
        public async Task UpdateSubscription_NonExistingSubscription_ReturnsFalse()
        {
            // Arrange
            // Используем несуществующий идентификатор подписки
            int nonExistingSubscriptionId = 9999;
            var newEventTypes = _eventsUpdateOnly;

            // Act
            // Пытаемся обновить несуществующую подписку
            var result = await _manager.UpdateSubscription(_clientId, nonExistingSubscriptionId, newEventTypes);

            // Assert
            // Проверяем, что результат операции отрицательный
            Assert.False(result);
        }

        [Fact]
        public async Task GetClientsForEvent_MatchingSubscription_ReturnsClient()
        {
            // Arrange
            // Создаем подписки для разных клиентов с разными типами событий
            var clientId1 = Guid.NewGuid();
            var clientId2 = Guid.NewGuid();
            
            await _manager.Subscribe(clientId1, _testAccount, _eventsCreateUpdate);
            await _manager.Subscribe(clientId2, _testAccount, _eventsDeleteOnly);

            // Act
            // Получаем клиентов для события типа "Update"
            var clients = _manager.GetClientsForEvent(new { Id = 1 }, "Update");

            // Assert
            // Проверяем, что возвращен только клиент с подпиской на этот тип события
            Assert.Single(clients);
            Assert.Equal(clientId1, clients[0]);
        }

        [Fact]
        public async Task GetClientsForEvent_NoMatchingSubscriptions_ReturnsEmptyList()
        {
            // Arrange
            // Создаем подписки с типами событий, не соответствующими запрашиваемому
            await _manager.Subscribe(_clientId, _testAccount, _eventsCreateUpdate);

            // Act
            // Получаем клиентов для события типа, на который никто не подписан
            var clients = _manager.GetClientsForEvent(new { Id = 1 }, "Archive");

            // Assert
            // Проверяем, что возвращен пустой список
            Assert.Empty(clients);
        }

        [Fact]
        public async Task GetClientsForEvent_MultipleClients_ReturnsAllMatchingClients()
        {
            // Arrange
            // Создаем несколько клиентов с перекрывающимися подписками
            var clientId1 = Guid.NewGuid();
            var clientId2 = Guid.NewGuid();
            var clientId3 = Guid.NewGuid();
            
            await _manager.Subscribe(clientId1, _testAccount, _eventsCreateOnly);
            await _manager.Subscribe(clientId2, _testAccount, _eventsCreateUpdate);
            await _manager.Subscribe(clientId3, _testAccount, _eventsDeleteOnly);

            // Act
            // Получаем клиентов для события типа "Create"
            var clients = _manager.GetClientsForEvent(new { Id = 1 }, "Create");

            // Assert
            // Проверяем, что возвращены все клиенты с подписками на этот тип события
            Assert.Equal(2, clients.Count);
            Assert.Contains(clientId1, clients);
            Assert.Contains(clientId2, clients);
            Assert.DoesNotContain(clientId3, clients);
        }

        [Fact]
        public void Dispose_DisposesResourcesAndSetsFlag()
        {
            // Arrange
            // Объект создается в конструкторе теста

            // Act
            // Вызываем метод Dispose
            _manager.Dispose();

            // Assert
            // Проверяем, что флаг IsDisposed установлен
            Assert.True(_manager.IsDisposedAccessor);
        }

        [Fact]
        public async Task Dispose_WhileLockHeld_DrainsBeforeDisposingWithoutThrow()
        {
            // Arrange — зовнішній тримач захоплює OperationLock (імітує in-flight операцію).
            await _manager.OperationLockAccessor.WaitAsync();

            // Act — Dispose у фоні: має блокуватись на дренажі семафора, поки тримач не відпустить (H4).
            var disposeTask = Task.Run(() => _manager.Dispose());

            // Поки лок утримується, Dispose НЕ може завершитися (детерміновано: Wait блокується).
            var finished = await Task.WhenAny(disposeTask, Task.Delay(200));
            Assert.NotSame(disposeTask, finished);
            Assert.False(_manager.IsDisposedAccessor);

            // Відпускаємо лок — Dispose дренує й завершується без винятку.
            _manager.OperationLockAccessor.Release();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(_manager.IsDisposedAccessor);
        }

        [Fact]
        public void Dispose_CalledMultipleTimes_DisposesOnlyOnce()
        {
            // Arrange
            // Объект создается в конструкторе теста

            // Act
            // Вызываем метод Dispose дважды
            _manager.Dispose();
            _manager.Dispose();

            // Assert
            // Проверяем, что флаг IsDisposed установлен
            Assert.True(_manager.IsDisposedAccessor);
            // Дополнительных проверок нет, так как нет публичного доступа
            // к проверке состояния OperationLock после вызова Dispose
        }

        [Fact]
        public async Task Subscribe_WhileOperationLockHeld_DoesNotProceedUntilReleased()
        {
            // Arrange — M2 guard: база САМА серіалізує мутації через OperationLock. Зовнішній тримач
            // захоплює лок; Subscribe не має проходити, поки лок утримується.
            await _manager.OperationLockAccessor.WaitAsync();

            // Act
            var subscribeTask = _manager.Subscribe(_clientId, _testAccount, _eventsCreateOnly);

            // Поки лок утримується, Subscribe детерміновано НЕ завершується.
            var finished = await Task.WhenAny(subscribeTask, Task.Delay(200));
            Assert.NotSame(subscribeTask, finished);
            Assert.Equal(0, _manager.GetSubscriptionCount(_clientId));

            // Відпускаємо лок — мутація проходить.
            _manager.OperationLockAccessor.Release();
            var subscriptionId = await subscribeTask.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(subscriptionId > 0);
            Assert.Equal(1, _manager.GetSubscriptionCount(_clientId));
        }

        [Fact]
        public async Task Subscribe_AfterDispose_ThrowsObjectDisposedException()
        {
            // Arrange — типізована помилка стану замість гонки на вже-звільненому семафорі.
            _manager.Dispose();

            // Act & Assert
            await Assert.ThrowsAsync<ObjectDisposedException>(
                async () => await _manager.Subscribe(_clientId, _testAccount, _eventsCreateOnly));
        }
    }
}