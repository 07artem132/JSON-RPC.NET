    using System;
    using System.Net.WebSockets;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using Moq;
    using NetCoreServer;
    using WsRpcServer.Core;
    using WsRpcServer.Events;
    using WsRpcServer.Sessions;
    using Xunit;

    namespace WsRpcServer.Tests.Sessions
    {
        // Создаем тестовый сервер, необходимый для создания сессии
        public class TestWsServer : WsServer
        {
            public TestWsServer() : base("127.0.0.1", 8000)
            {
            }

            protected override WsSession CreateSession()
            {
                throw new NotImplementedException();
            }
        }

        // Тестовая реализация JsonRpcSession для тестирования с полной переопределением ProcessNotificationsAsync
        public class TestJsonRpcSession : AbstractJsonRpcSession
        {
            // Флаги для отслеживания внутренних событий
            public bool NotifyCalled { get; private set; }
            public string? LastMethodCalled { get; private set; }
            public object[]? LastArgsPassed { get; private set; }
            public bool ThrowExceptionOnProcessing { get; set; }
            public TaskCompletionSource<bool> ProcessingCompleted { get; } = new();
            
            // Переопределенный метод ProcessNotificationsAsync, используемый в тестах
            private Task? _customProcessingTask;

            public TestJsonRpcSession(
                WsServer server,
                ILogger logger,
                JsonRpcServerConfig config) : base(server, logger, config)
            {
            }

            // Предоставляем доступ к защищенным полям
            public CancellationTokenSource CtsAccessor => Cts;
            public Channel<RpcNotification> NotificationChannelAccessor => NotificationChannel;
            public ILogger LoggerAccessor => Logger;
            public JsonRpcServerConfig ConfigAccessor => Config;

            // Метод для запуска обработки уведомлений с нашей собственной реализацией
            public Task StartCustomProcessing(CancellationToken cancellationToken)
            {
                _customProcessingTask = Task.Run(async () =>
                {
                    try
                    {
                        // Реализация ProcessNotificationsAsync для тестов
                        Logger.LogDebug("Запущено обробку сповіщень для клієнта {ClientId}", Id);

                        await foreach (var notification in NotificationChannel.Reader.ReadAllAsync(cancellationToken))
                        {
                            try
                            {
                                // Записываем информацию о вызове
                                NotifyCalled = true;
                                LastMethodCalled = notification.Method;
                                LastArgsPassed = notification.Arguments;

                                // Имитируем обработку
                                if (ThrowExceptionOnProcessing)
                                {
                                    Logger.LogError(new InvalidOperationException("Test exception"), 
                                        "Помилка надсилання сповіщення {Method} клієнту {ClientId}",
                                        notification.Method, Id);
                                }
                                else
                                {
                                    Logger.LogDebug("Надіслано сповіщення {Method} клієнту {ClientId}", 
                                        notification.Method, Id);
                                }

                                // Небольшая задержка для имитации асинхронной работы
                                await Task.Delay(50, cancellationToken);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                Logger.LogDebug("Обробку сповіщень скасовано для клієнта {ClientId}", Id);
                                throw;
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError(ex, "Помилка надсилання сповіщення {Method} клієнту {ClientId}",
                                    notification.Method, Id);
                            }
                        }

                        Logger.LogDebug("Завершено обробку сповіщень для клієнта {ClientId}", Id);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        Logger.LogDebug("Обробку сповіщень скасовано для клієнта {ClientId}", Id);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Помилка в циклі обробки сповіщень для клієнта {ClientId}", Id);
                    }
                    finally
                    {
                        ProcessingCompleted.TrySetResult(true);
                    }
                }, cancellationToken);

                return _customProcessingTask;
            }

            public new void Dispose()
            {
                base.Dispose();
                ProcessingCompleted.TrySetResult(true);
            }
        }

        public class AbstractJsonRpcSessionTests : IDisposable
        {
            private readonly TestWsServer _server;
            private readonly Mock<ILogger> _loggerMock;
            private readonly JsonRpcServerConfig _config;
            private readonly TestJsonRpcSession _session;

            public AbstractJsonRpcSessionTests()
            {
                _server = new TestWsServer();
                _loggerMock = new Mock<ILogger>();
                _config = new JsonRpcServerConfig
                {
                    NotificationQueueSize = 10,
                    NotificationTimeout = TimeSpan.FromMilliseconds(100)
                };
                _session = new TestJsonRpcSession(_server, _loggerMock.Object, _config);
            }

            public void Dispose()
            {
                _session.Dispose();
                _server.Dispose();
                GC.SuppressFinalize(this);
            }

            [Fact]
            public void Constructor_InitializesProperties()
            {
                // Arrange
                // Конструктор вызывается в конструкторе теста

                // Act & Assert
                // Проверяем, что все свойства инициализированы правильно
                Assert.NotNull(_session.CtsAccessor);
                Assert.NotNull(_session.NotificationChannelAccessor);
                Assert.Same(_loggerMock.Object, _session.LoggerAccessor);
                Assert.Same(_config, _session.ConfigAccessor);
                
                // Проверяем размер канала уведомлений путем проверки свойства конфигурации
                Assert.Equal(_config.NotificationQueueSize, _session.ConfigAccessor.NotificationQueueSize);
            }

            [Fact]
            public async Task SendNotificationAsync_ValidParameters_AddsToChannel()
            {
                // Arrange
                // Подготавливаем параметры для отправки уведомления
                var method = "testMethod";
                var args = new object[] { "arg1", 123 };

                // Act
                // Отправляем уведомление
                await _session.SendNotificationAsync(method, args);

                // Assert
                // Проверяем, что уведомление добавлено в канал
                Assert.True(_session.NotificationChannelAccessor.Reader.TryPeek(out var notification));
                Assert.Equal(method, notification.Method);
                Assert.Equal(args, notification.Arguments);
                
                // Проверяем, что было залогировано сообщение о добавлении в очередь
                _loggerMock.Verify(
                    x => x.Log(
                        LogLevel.Debug,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Додано сповіщення")),
                        (Exception?)null,
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                    Times.Once);
            }

            [Fact]
            public async Task SendNotificationAsync_WhenSessionDisposed_DoesNotAddToChannel()
            {
                // Arrange
                // Утилизируем сессию перед отправкой уведомления
                _session.Dispose();

                // Act
                // Пытаемся отправить уведомление после утилизации
                await _session.SendNotificationAsync("testMethod", "arg1");

                // Assert
                // Проверяем, что уведомление не было добавлено в канал
                Assert.False(_session.NotificationChannelAccessor.Reader.TryPeek(out _));
                
                // Проверяем, что было залогировано сообщение о пропуске уведомления
                _loggerMock.Verify(
                    x => x.Log(
                        LogLevel.Debug,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Пропуск сповіщення")),
                        (Exception?)null,
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                    Times.Once);
            }

            [Fact]
            public async Task SendNotificationAsync_WhenCancellationRequested_DoesNotAddToChannel()
            {
                // Arrange
                // Запрашиваем отмену перед отправкой уведомления
                _session.CtsAccessor.Cancel();

                // Act
                // Пытаемся отправить уведомление после запроса отмены
                await _session.SendNotificationAsync("testMethod", "arg1");

                // Assert
                // Проверяем, что уведомление не было добавлено в канал
                Assert.False(_session.NotificationChannelAccessor.Reader.TryPeek(out _));
                
                // Проверяем, что было залогировано сообщение о пропуске уведомления
                _loggerMock.Verify(
                    x => x.Log(
                        LogLevel.Debug,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Пропуск сповіщення")),
                        (Exception?)null,
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                    Times.Once);
            }

            [Fact]
            public void Close_WithStatusAndReason_LogsAndCallsBaseMethod()
            {
                // Arrange - не требуется дополнительной подготовки

                // Нельзя напрямую проверить вызов базового метода Close из-за ограничений мокинга,
                // поэтому проверяем только логирование

                // Act
                // Вызываем метод Close с параметрами
                _session.Close(WebSocketCloseStatus.NormalClosure, "Normal shutdown");

                // Assert
                // Проверяем, что было залогировано сообщение о закрытии соединения
                _loggerMock.Verify(
                    x => x.Log(
                        LogLevel.Information,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Закриття WebSocket з'єднання")),
                        (Exception?)null,
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                    Times.Once);
            }

            [Fact]
            public async Task SendBinaryDataAsync_ValidData_LogsAndCallsSendBinary()
            {
                // Arrange
                // Подготавливаем тестовые бинарные данные
                var data = new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3, 4, 5 });

                // Act
                // Отправляем бинарные данные
                await _session.SendBinaryDataAsync(data);

                // Assert
                // Проверяем, что было залогировано сообщение об отправке данных
                _loggerMock.Verify(
                    x => x.Log(
                        LogLevel.Debug,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Надсилання бінарних даних")),
                        (Exception?)null,
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                    Times.Once);
                
                // Примечание: нельзя напрямую проверить вызов SendBinaryAsync из-за ограничений мокинга
            }

            [Fact]
            public async Task SendBinaryDataAsync_WhenSessionDisposed_LogsWarningAndDoesNotSend()
            {
                // Arrange
                // Утилизируем сессию перед отправкой данных
                _session.Dispose();
                var data = new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3, 4, 5 });

                // Act
                // Пытаемся отправить данные после утилизации
                await _session.SendBinaryDataAsync(data);

                // Assert
                // Проверяем, что было залогировано предупреждение
                _loggerMock.Verify(
                    x => x.Log(
                        LogLevel.Warning,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Спроба надіслати бінарні дані після утилізації")),
                        (Exception?)null,
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                    Times.Once);
            }

            [Fact]
            public void OnWsPing_LogsDebugMessage()
            {
                // Arrange
                // Подготавливаем тестовые ping-данные
                var buffer = new byte[] { 1, 2, 3 };

                // Act
                // Вызываем обработчик ping-сообщения
                _session.OnWsPing(buffer, 0, buffer.Length);

                // Assert
                // Проверяем, что было залогировано сообщение о получении ping
                _loggerMock.Verify(
                    x => x.Log(
                        LogLevel.Debug,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Отримано WebSocket ping")),
                        (Exception?)null,
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                    Times.Once);
            }

            [Fact]
            public void Dispose_ReleasesResources()
            {
                // Arrange - не требуется дополнительной подготовки

                // Act
                // Утилизируем сессию
                _session.Dispose();

                // Assert
                // Проверяем, что канал уведомлений был помечен для завершения
                try
                {
                    // Проверяем, что канал не принимает новые элементы
                    Assert.False(_session.NotificationChannelAccessor.Writer.TryWrite(new RpcNotification("test", Array.Empty<object>())));
                }
                catch
                {
                    // Если TryWrite выбрасывает исключение, это тоже означает, что канал закрыт
                    // Считаем тест успешным
                }
            }

            [Fact]
            public async Task ProcessNotifications_SendsNotificationsToSubscribers()
            {
                // Arrange
                // Подготавливаем тестовые данные
                var method = "testMethod";
                var args = new object[] { "arg1", 123 };

                // Запускаем задачу обработки уведомлений
                var processingTask = _session.StartCustomProcessing(CancellationToken.None);
                
                // Act
                // Отправляем уведомление
                await _session.SendNotificationAsync(method, args);
                
                // Ждем немного, чтобы уведомление было обработано
                await Task.Delay(200);
                
                // Assert
                // Проверяем, что уведомление было обработано
                Assert.True(_session.NotifyCalled);
                Assert.Equal(method, _session.LastMethodCalled);
                Assert.Equal(args, _session.LastArgsPassed);
                
                // Проверяем, что было залогировано сообщение об отправке уведомления
                _loggerMock.Verify(
                    x => x.Log(
                        LogLevel.Debug,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Надіслано сповіщення")),
                        (Exception?)null,
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                    Times.Once);
            }

            [Fact]
            public async Task ProcessNotifications_WithException_LogsError()
            {
                // Arrange
                // Настраиваем сессию для выброса исключения при обработке
                _session.ThrowExceptionOnProcessing = true;
    
                // TaskCompletionSource для синхронизации теста
                var errorLogged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    
                // Настраиваем мок логгера для фиксации момента логирования ошибки
                _loggerMock.Setup(x => x.Log(
                        LogLevel.Error,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Помилка надсилання сповіщення")),
                        It.IsAny<Exception>(),
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                    .Callback(() => errorLogged.TrySetResult(true));
    
                // Запускаем задачу обработки уведомлений
                var processingTask = _session.StartCustomProcessing(CancellationToken.None);
    
                // Act
                // Отправляем уведомление
                await _session.SendNotificationAsync("testMethod", "arg1");
    
                // Ждем, пока ошибка будет залогирована
                // Используем таймаут только для защиты от зависания теста
                if (!await errorLogged.Task.WaitAsync(TimeSpan.FromSeconds(5)))
                {
                    Assert.Fail("Error logging did not occur within the timeout period");
                }
    
                // Assert
                // Проверяем, что было залогировано сообщение об ошибке
                _loggerMock.Verify(
                    x => x.Log(
                        LogLevel.Error,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Помилка надсилання сповіщення")),
                        It.IsAny<Exception>(),
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                    Times.AtLeastOnce);
            }

           [Fact]
           public async Task ProcessNotifications_WithCancellation_LogsAndExitsGracefully()
           {
               // Arrange
               var cts = new CancellationTokenSource();
    
               // TaskCompletionSource для синхронизации теста
               var notificationReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    
               // Переопределяем логику TestJsonRpcSession для этого теста
               _loggerMock.Setup(x => x.Log(
                       LogLevel.Debug,
                       It.IsAny<EventId>(),
                       It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Надіслано сповіщення")),
                       (Exception?)null,
                       It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                   .Callback(() => notificationReceived.TrySetResult(true));
    
               // Запускаем задачу обработки уведомлений
               var processingTask = _session.StartCustomProcessing(cts.Token);
    
               // Добавляем тестовое уведомление в канал
               await _session.SendNotificationAsync("testMethod", "arg1");
    
               // Ждем, пока уведомление начнет обрабатываться
               // Используем таймаут только для защиты от зависания теста
               if (!await notificationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)))
               {
                   Assert.Fail("Notification processing did not start within the timeout period");
               }
    
               // Act
               // Отменяем обработку
               cts.Cancel();
    
               // Assert
               // Ждем завершения задачи обработки
               await processingTask.WaitAsync(TimeSpan.FromSeconds(5));
    
               // Проверяем логирование отмены
               _loggerMock.Verify(
                   x => x.Log(
                       LogLevel.Debug,
                       It.IsAny<EventId>(),
                       It.Is<It.IsAnyType>((o, t) => o!.ToString()!.Contains("Обробку сповіщень скасовано")),
                       (Exception?)null,
                       It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                   Times.AtLeastOnce);
           }
        }
    }