using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WsRpcServer.Events;
using Xunit;

namespace WsRpcServer.Tests.Events
{
    // Test implementation that exposes protected members for testing
    public class TestEventProcessor(ILogger logger, int maxConsecutiveNotificationFailures = 5)
        : AbstractEventProcessor(logger, maxConsecutiveNotificationFailures)
    {
        // Expose protected methods for testing
        public void PublicNotifyClient(Guid clientId, string method, object eventArgs)
        {
            NotifyClient(clientId, method, eventArgs);
        }

        public void PublicHandleClientFailure(Guid clientId)
        {
            HandleClientFailure(clientId);
        }
        
        // Expose protected fields through public properties
        public CancellationTokenSource CtsAccessor => Cts;
        public ConcurrentDictionary<Guid, Func<string, object[], Task>> ClientHandlersAccessor => ClientHandlers;
        public ConcurrentBag<IDisposable> SubscriptionsAccessor => Subscriptions;
        public bool IsDisposedAccessor => IsDisposed;
    }

    public class AbstractEventProcessorTests : IDisposable
    {
        private readonly Mock<ILogger> _loggerMock;
        private readonly TestEventProcessor _eventProcessor;
        private readonly Guid _clientId = Guid.NewGuid();

        public AbstractEventProcessorTests()
        {
            _loggerMock = new Mock<ILogger>();
            // [LoggerMessage]-генеровані методи перевіряють IsEnabled перед Log — за замовчуванням
            // Mock<ILogger>.IsEnabled повертає false, тож без цього Log ніколи б не викликався.
            _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            _eventProcessor = new TestEventProcessor(_loggerMock.Object);
        }

        public void Dispose()
        {
            _eventProcessor.Dispose();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public async Task StartAsync_WithValidCancellationToken_LogsStartupAndReturnsCompletedTask()
        {
            // Arrange
            // Подготовка тестовой среды выполнена в конструкторе

            // Actы
            // Проверяем, что метод StartAsync выполняется без ошибок и логирует процесс запуска
            await _eventProcessor.StartAsync(CancellationToken.None);

            // Assert
            // Проверяем, что в лог записано сообщение о запуске обработчика событий
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Запуск обробника подій")),
                    (Exception?)null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task StopAsync_WithValidCancellationToken_CancelsTokenAndLogsShutdown()
        {
            // Arrange
            // Подготовка тестовой среды выполнена в конструкторе

            // Act
            // Проверяем, что метод StopAsync отменяет токен и логирует остановку
             await _eventProcessor.StopAsync(CancellationToken.None);

            // Assert
            // Проверяем, что токен отмены был установлен в отмененное состояние
            Assert.True(_eventProcessor.CtsAccessor.IsCancellationRequested);
            // Проверяем, что было залогировано сообщение о остановке обработчика событий
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Зупинка обробника подій")),
                    (Exception?)null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void RegisterClient_WithValidHandler_AddsClientToHandlersAndLogs()
        {
            // Arrange
            // Создаем тестовый обработчик уведомлений
            Func<string, object[], Task> handler = (method, args) => Task.CompletedTask;

            // Act
            // Регистрируем клиента с действительным обработчиком
            _eventProcessor.RegisterClient(_clientId, handler);

            // Assert
            // Проверяем, что клиент добавлен в словарь обработчиков
            Assert.True(_eventProcessor.ClientHandlersAccessor.ContainsKey(_clientId));
            // Проверяем, что обработчик сохранен правильно
            Assert.Same(handler, _eventProcessor.ClientHandlersAccessor[_clientId]);
            // Проверяем, что было залогировано информационное сообщение о регистрации
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("зареєстрований для отримання сповіщень")),
                    (Exception?)null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void RegisterClient_WithNullHandler_ThrowsArgumentNullException()
        {
            // Arrange
            // Используем null в качестве обработчика

            // Act & Assert
            // Проверяем, что при передаче null-обработчика выбрасывается исключение ArgumentNullException
            var exception = Assert.Throws<ArgumentNullException>(() =>
                _eventProcessor.RegisterClient(_clientId, null!));
            // Проверяем, что имя параметра в исключении соответствует ожидаемому
            Assert.Equal("notificationHandler", exception.ParamName);
        }

        [Fact]
        public void UnregisterClient_ExistingClient_RemovesClientAndLogs()
        {
            // Arrange
            // Сначала регистрируем клиента для последующей отмены регистрации
            _eventProcessor.RegisterClient(_clientId, (method, args) => Task.CompletedTask);

            // Act
            // Отменяем регистрацию существующего клиента
            _eventProcessor.UnregisterClient(_clientId);

            // Assert
            // Проверяем, что клиент удален из словаря обработчиков
            Assert.False(_eventProcessor.ClientHandlersAccessor.ContainsKey(_clientId));
            // Проверяем, что было залогировано сообщение об отписке клиента
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("відписаний від сповіщень")),
                    (Exception?)null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void UnregisterClient_NonExistingClient_DoesNotLogOrThrow()
        {
            // Arrange
            // Используем неизвестный идентификатор клиента
            var unknownClientId = Guid.NewGuid();

            // Act
            // Пытаемся отменить регистрацию несуществующего клиента, не должно быть исключений
            _eventProcessor.UnregisterClient(unknownClientId);

            // Assert
            // Проверяем, что сообщение об отписке не было залогировано, так как клиент не существует
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("відписаний від сповіщень")),
                    (Exception?)null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Never);
        }

        [Fact]
        public async Task NotifyClient_ExistingClient_CallsHandler()
        {
            // Arrange
            // Создаем TaskCompletionSource для отслеживания вызова обработчика
            var handlerCalled = new TaskCompletionSource<bool>();
            // Создаем тестовый объект для передачи в качестве аргумента события
            var testObject = new { TestProperty = "TestValue" };
            
            // Создаем обработчик, который проверяет правильность переданных аргументов
            Func<string, object[], Task> handler = (method, args) =>
            {
                try
                {
                    // Проверяем, что метод и аргументы переданы правильно
                    Assert.Equal("testMethod", method);
                    Assert.Single(args);
                    Assert.Same(testObject, args[0]);
                    handlerCalled.SetResult(true);
                }
                catch (Exception ex)
                {
                    handlerCalled.SetException(ex);
                }
                return Task.CompletedTask;
            };
            
            // Регистрируем клиента с тестовым обработчиком
            _eventProcessor.RegisterClient(_clientId, handler);

            // Act
            // Отправляем уведомление клиенту
            _eventProcessor.PublicNotifyClient(_clientId, "testMethod", testObject);
            
            // Assert
            // Ожидаем завершения обработчика (с таймаутом)
            var completedTask = await Task.WhenAny(handlerCalled.Task, Task.Delay(1000));
            // Проверяем, что именно обработчик завершился, а не таймаут
            Assert.Same(handlerCalled.Task, completedTask);
            // Проверяем, что обработчик вернул true, т.е. все проверки прошли успешно
            Assert.True(await handlerCalled.Task);
        }

        [Fact]
        public void NotifyClient_NonExistingClient_DoesNothing()
        {
            // Arrange
            // Используем неизвестный идентификатор клиента
            var unknownClientId = Guid.NewGuid();

            // Act
            // Пытаемся отправить уведомление несуществующему клиенту, не должно быть исключений
            _eventProcessor.PublicNotifyClient(unknownClientId, "testMethod", new object());
            
            // Assert
            // Метод не должен выбрасывать исключений и не должен пытаться вызвать обработчик
            // Так как никаких действий не происходит, нет явных утверждений для проверки
        }

        [Fact]
        public async Task NotifyClient_HandlerThrowsException_LogsErrorAndCallsHandleClientFailure()
        {
            // Arrange
            // Создаем TaskCompletionSource для отслеживания вызова обработчика
            var handlerCalled = new TaskCompletionSource<bool>();
            // Создаем исключение, которое будет выброшено обработчиком
            var exceptionThrown = new InvalidOperationException("Test exception");
            
            // Создаем обработчик, который выбрасывает исключение
            Func<string, object[], Task> handler = (method, args) =>
            {
                handlerCalled.SetResult(true);
                return Task.FromException(exceptionThrown);
            };
            
            // Регистрируем клиента с обработчиком, выбрасывающим исключение
            _eventProcessor.RegisterClient(_clientId, handler);

            // Act
            // Отправляем уведомление, которое вызовет исключение в обработчике
            _eventProcessor.PublicNotifyClient(_clientId, "testMethod", new object());
            
            // Ждем, пока обработчик будет вызван
            var result = await Task.WhenAny(handlerCalled.Task, Task.Delay(1000));
            // Проверяем, что обработчик был вызван
            Assert.Same(handlerCalled.Task, result);
            
            // Ждем, пока исполнится продолжение с обработкой ошибки
            await Task.Delay(100);

            // Assert
            // Проверяем, что ошибка была залогирована
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Помилка відправки сповіщення")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public void Dispose_DisposesResourcesAndSetsIsDisposedFlag()
        {
            // Arrange
            // Создаем мок объекта IDisposable и добавляем его в список подписок
            var disposableMock = new Mock<IDisposable>();
            _eventProcessor.SubscriptionsAccessor.Add(disposableMock.Object);

            // Act
            // Вызываем метод Dispose
            _eventProcessor.Dispose();

            // Assert
            // Проверяем, что флаг IsDisposed установлен в true
            Assert.True(_eventProcessor.IsDisposedAccessor);
            // Проверяем, что список подписок очищен
            Assert.Empty(_eventProcessor.SubscriptionsAccessor);
            // Проверяем, что Dispose был вызван для каждой подписки
            disposableMock.Verify(d => d.Dispose(), Times.Once);
        }

        [Fact]
        public void Dispose_WithoutStopAsync_CancelsCtsBeforeDisposing()
        {
            // Arrange — НЕ викликаємо StopAsync (імітуємо утилізацію через DI-контейнер).
            Assert.False(_eventProcessor.CtsAccessor.IsCancellationRequested);

            // Act
            _eventProcessor.Dispose();

            // Assert — H4: Dispose сам скасовує токен, щоб фонові задачі похідних класів не лишались
            // "сиротами". IsCancellationRequested читається безпечно навіть після Dispose CTS.
            Assert.True(_eventProcessor.CtsAccessor.IsCancellationRequested);
            Assert.True(_eventProcessor.IsDisposedAccessor);
        }

        [Fact]
        public void Dispose_CalledMultipleTimes_OnlyDisposesOnce()
        {
            // Arrange
            // Создаем мок объекта IDisposable и добавляем его в список подписок
            var disposableMock = new Mock<IDisposable>();
            _eventProcessor.SubscriptionsAccessor.Add(disposableMock.Object);

            // Act
            // Вызываем метод Dispose дважды
            _eventProcessor.Dispose();
            _eventProcessor.Dispose(); // Повторный вызов, который не должен повторно вызывать Dispose у подписок

            // Assert
            // Проверяем, что флаг IsDisposed установлен в true
            Assert.True(_eventProcessor.IsDisposedAccessor);
            // Проверяем, что список подписок очищен
            Assert.Empty(_eventProcessor.SubscriptionsAccessor);
            // Проверяем, что Dispose был вызван для каждой подписки только один раз,
            // несмотря на многократный вызов Dispose у _eventProcessor
            disposableMock.Verify(d => d.Dispose(), Times.Once);
        }

        [Fact]
        public async Task NotifyClient_HandlerFailsRepeatedly_AutoUnregistersClientAfterThreshold()
        {
            // Arrange — M1: окремий процесор з низьким порогом, обробник що завжди падає.
            using var processor = new TestEventProcessor(_loggerMock.Object, maxConsecutiveNotificationFailures: 3);
            var clientId = Guid.NewGuid();
            Func<string, object[], Task> failing = (method, args) =>
                Task.FromException(new InvalidOperationException("boom"));
            processor.RegisterClient(clientId, failing);

            // Act — стріляємо рівно поріг сповіщень; кожне планує faulting-continuation, що інкрементує лічильник.
            for (int i = 0; i < 3; i++)
            {
                processor.PublicNotifyClient(clientId, "testMethod", new object());
            }

            // Continuations виконуються на пулі потоків — чекаємо до авто-відписки (з таймаутом).
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (processor.ClientHandlersAccessor.ContainsKey(clientId) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            // Assert — клієнт автоматично відписаний + залоговано Warning про авто-відписку.
            Assert.False(processor.ClientHandlersAccessor.ContainsKey(clientId));
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("автоматично відписаний")),
                    (Exception?)null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public void RegisterClient_DuplicateId_LogsWarningAndOverwrites()
        {
            // Arrange — L1: дві реєстрації того самого clientId.
            Func<string, object[], Task> first = (method, args) => Task.CompletedTask;
            Func<string, object[], Task> second = (method, args) => Task.CompletedTask;

            // Act
            _eventProcessor.RegisterClient(_clientId, first);
            _eventProcessor.RegisterClient(_clientId, second);

            // Assert — ефективна "останній перемагає" семантика, але дубль залоговано Warning'ом (не безшумно).
            Assert.Same(second, _eventProcessor.ClientHandlersAccessor[_clientId]);
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("вже зареєстрований")),
                    (Exception?)null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void Subscriptions_IsThreadSafeConcurrentBag()
        {
            // L2: базова властивість Subscriptions має бути потокобезпечною колекцією (не List).
            var prop = typeof(AbstractEventProcessor).GetProperty(
                "Subscriptions", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(prop);
            Assert.Equal(typeof(ConcurrentBag<IDisposable>), prop!.PropertyType);
        }
    }
}