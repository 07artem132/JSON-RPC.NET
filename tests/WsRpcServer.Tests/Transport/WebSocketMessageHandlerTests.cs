using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;
using WsRpcServer.Core;
using WsRpcServer.Sessions;
using WsRpcServer.Transport;
using Xunit;
using WebSocketMessageHandler = WsRpcServer.Transport.WebSocketMessageHandler;

namespace WsRpcServer.Tests.Transport
{
    public class EnhancedWebSocketMessageHandlerTests
    {
        private static readonly string[] _subscriptionEventTypes = ["Create", "Update", "Delete"];
        private static readonly string[] _expectedCancellationExceptions = [nameof(OperationCanceledException), nameof(TaskCanceledException)];

        private readonly Mock<IJsonRpcSession> _mockSession;
        private readonly Mock<IJsonRpcMessageFormatter> _mockFormatter;
        private readonly Mock<ILogger<WebSocketMessageHandler>> _mockLogger;
        private readonly JsonRpcServerConfig _config;
        private readonly Guid _sessionId = Guid.NewGuid();

        public EnhancedWebSocketMessageHandlerTests()
        {
            _mockSession = new Mock<IJsonRpcSession>();
            _mockSession.Setup(s => s.Id).Returns(_sessionId);

            _mockFormatter = new Mock<IJsonRpcMessageFormatter>();
            _mockLogger = new Mock<ILogger<WebSocketMessageHandler>>();
            // [LoggerMessage]-генеровані методи перевіряють IsEnabled перед Log — за замовчуванням
            // Mock<ILogger>.IsEnabled повертає false, тож без цього Log ніколи б не викликався.
            _mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            _config = new JsonRpcServerConfig
            {
                PipeThresholdBytes = 1024 * 1024
            };
        }

        [Fact]
        public async Task ProcessReceivedDataAsync_LargeMessage_HandlesCorrectly()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Create a large JSON message (~100KB)
            var largeArray = Enumerable.Range(0, 10000).ToArray();
            var largeObject = new { data = largeArray };
            var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(largeObject));

            // Act
            await handler.ProcessReceivedDataAsync(data);

            // Assert - verify log was written
            _mockLogger.Verify(
                l => l.Log(
                    LogLevel.Debug,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v!.ToString()!.Contains("Обробка отриманих даних")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task ProcessReceivedDataAsync_FragmentedMessage_AccumulatesData()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Setup for incomplete and complete JSON detection
            bool hasCompleteJson = false;
            _mockFormatter.Setup(f => f.Deserialize(It.IsAny<ReadOnlySequence<byte>>()))
                .Returns((ReadOnlySequence<byte> buffer) => 
                {
                    var json = Encoding.UTF8.GetString(buffer.ToArray());
                    if (json.Contains("\"method\":\"test\"") && json.Contains("\"id\":1"))
                    {
                        hasCompleteJson = true;
                        return new JsonRpcRequest
                        {
                            RequestId = new RequestId(1),
                            Method = "test",
                            Version = "2.0"
                        };
                    }
                    throw new JsonException("Incomplete JSON");
                });

            // Get the ReadCoreAsync method via reflection
            var readMethod = typeof(WebSocketMessageHandler).GetMethod(
                "ReadCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Send fragmented JSON message
            var fragment1 = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"");
            var fragment2 = Encoding.UTF8.GetBytes(",\"method\":\"test\",\"id\":1}");

            // Act & Assert - Handling first fragment
            await handler.ProcessReceivedDataAsync(fragment1);
            
            var readTask1 = Task.Run(async () => 
            {
                try
                {
                    return await ((ValueTask<JsonRpcMessage>)readMethod!.Invoke(
                        handler,
                        new object[] { CancellationToken.None })!);
                }
                catch (JsonException)
                {
                    return null;
                }
            });

            // Let's give a short time for the task to process
            await Task.Delay(100);
            
            // At this point, we should not have a complete JSON message
            Assert.False(hasCompleteJson);
            
            // Send second fragment
            await handler.ProcessReceivedDataAsync(fragment2);
            
            // Now read should complete successfully
            var readTask2 = Task.Run(async () =>
            {
                return await ((ValueTask<JsonRpcMessage>)readMethod!.Invoke(
                    handler,
                    new object[] { CancellationToken.None })!);
            });

            var result = await readTask2;

            // Verify we got a complete message
            Assert.True(hasCompleteJson);
            Assert.NotNull(result);
            Assert.IsType<JsonRpcRequest>(result);
            Assert.Equal("test", ((JsonRpcRequest)result).Method);
            Assert.Equal(new RequestId(1), ((JsonRpcRequest)result).RequestId);
        }

        [Theory]
        [InlineData(10)]  // Error near the beginning
        [InlineData(50)]  // Error in the middle
        [InlineData(90)]  // Error near the end
        public async Task ReadCoreAsync_JsonErrorAtDifferentPositions_RecoversProperly(int errorPosition)
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Create a valid JSON followed by invalid JSON to simulate corruption
            var validJson = "{\"jsonrpc\":\"2.0\",\"method\":\"test\",\"id\":1}";
            var invalidJson = "{\"jsonrpc\":\"2.0\",\"method\":ERROR,\"id\":2}";
            
            // Send the data
            await handler.ProcessReceivedDataAsync(Encoding.UTF8.GetBytes(validJson));
            
            // Setup the formatter to first return a valid message, then throw an exception
            var callCount = 0;
            _mockFormatter.Setup(f => f.Deserialize(It.IsAny<ReadOnlySequence<byte>>()))
                .Returns((ReadOnlySequence<byte> buffer) => 
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        // First call - return valid message
                        return new JsonRpcRequest
                        {
                            RequestId = new RequestId(1),
                            Method = "test",
                            Version = "2.0"
                        };
                    }
                    // Second call - throw exception with specified position
                    throw new JsonException("Invalid JSON", null, null, errorPosition);
                });

            // Get the ReadCoreAsync method via reflection
            var readMethod = typeof(WebSocketMessageHandler).GetMethod(
                "ReadCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act - first read should succeed
            var firstResult = await ((ValueTask<JsonRpcMessage>)readMethod!.Invoke(
                handler,
                new object[] { CancellationToken.None })!);
            
            // Send the invalid JSON
            await handler.ProcessReceivedDataAsync(Encoding.UTF8.GetBytes(invalidJson));
            
            // Second read should fail but recover
            var secondResult = await ((ValueTask<JsonRpcMessage>)readMethod!.Invoke(
                handler,
                new object[] { CancellationToken.None })!);

            // Assert
            Assert.NotNull(firstResult);
            Assert.Null(secondResult); // The handler should return null when deserialization fails
            
            // Verify the parse failure was logged at Warning with the correct position.
            // (H2 / parse-failure-throttle: malformed-JSON is an expected client-side error class,
            //  logged at Warning — not Error — so a flood doesn't spam the error channel.)
            _mockLogger.Verify(
                l => l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.Is<JsonException>(ex => ex.BytePositionInLine == errorPosition),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task ReadCoreAsync_CancellationRequested_HandlesGracefully()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Send some valid data
            var validJson = "{\"jsonrpc\":\"2.0\",\"method\":\"test\",\"id\":1}";
            await handler.ProcessReceivedDataAsync(Encoding.UTF8.GetBytes(validJson));

            // Get the ReadCoreAsync method via reflection
            var readMethod = typeof(WebSocketMessageHandler).GetMethod(
                "ReadCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Create a cancellation token that's already canceled
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act - should handle cancellation gracefully (may return null or throw)
            try 
            {
                var result = await ((ValueTask<JsonRpcMessage>)readMethod!.Invoke(
                    handler,
                    new object[] { cts.Token })!);
                
                // If it doesn't throw, verify result is null
                Assert.Null(result);
            }
            catch (Exception ex)
            {
                // If it throws, verify it's due to cancellation
                // We need to check the inner exception (from reflection)
                Assert.Contains(ex.InnerException?.GetType().Name,
                    _expectedCancellationExceptions);
            }

            // Verify cancellation was logged appropriately
            _mockLogger.Verify(
                l => l.Log(
                    LogLevel.Debug,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v!.ToString()!.Contains("Операцію") ||
                                                 v!.ToString()!.Contains("скасовано")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task WriteCoreAsync_SessionDisconnected_LogsWarningAndThrows()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Setup session to throw when sending data
            _mockSession.Setup(s => s.SendBinaryDataAsync(It.IsAny<ReadOnlyMemory<byte>>()))
                .Throws(new InvalidOperationException("Connection closed"));

            var testMessage = new JsonRpcRequest
            {
                RequestId = new RequestId(1),
                Method = "test",
                Version = "2.0"
            };

            // Get the WriteCoreAsync method via reflection
            var writeMethod = typeof(WebSocketMessageHandler).GetMethod(
                "WriteCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await ((ValueTask)writeMethod!.Invoke(
                    handler,
                    new object[] { testMessage, CancellationToken.None })!);
            });

            // Verify that the error was logged
            _mockLogger.Verify(
                l => l.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v!.ToString()!.Contains("Помилка надсилання JSON-RPC повідомлення")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task WriteCoreAsync_SerializationError_LogsAndRethrows()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Setup formatter to throw during serialization
            _mockFormatter.Setup(f => f.Serialize(It.IsAny<IBufferWriter<byte>>(), It.IsAny<JsonRpcMessage>()))
                .Throws(new InvalidOperationException("Serialization error"));

            var testMessage = new JsonRpcRequest
            {
                RequestId = new RequestId(1),
                Method = "test",
                Version = "2.0"
            };

            // Get the WriteCoreAsync method via reflection
            var writeMethod = typeof(WebSocketMessageHandler).GetMethod(
                "WriteCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await ((ValueTask)writeMethod!.Invoke(
                    handler,
                    new object[] { testMessage, CancellationToken.None })!);
            });

            // Verify error was logged
            _mockLogger.Verify(
                l => l.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v!.ToString()!.Contains("Помилка надсилання JSON-RPC повідомлення")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task ProcessReceivedDataAsync_WhenDisposed_ThrowsInvalidOperationException()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Dispose the handler
#pragma warning disable CS0618 // MessageHandlerBase.Dispose() is obsolete in newer StreamJsonRpc; intentional sync-dispose test.
            handler.Dispose();
#pragma warning restore CS0618

            // Create test data
            var data = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"test\",\"id\":1}");

            // Act & Assert - Should throw InvalidOperationException with "Writing is not allowed after writer was completed"
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await handler.ProcessReceivedDataAsync(data);
            });
            
            Assert.Contains("Writing is not allowed", exception.Message);
        }

        [Fact]
        public void DeserializationComplete_WithNullMessage_HandlesGracefully()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Act - shouldn't throw
            ((IJsonRpcMessageBufferManager)handler).DeserializationComplete(null!);

            // Assert - nothing to verify, just making sure it doesn't throw
        }

        [Fact] 
        public async Task WriteCoreAsync_WithCancellation_HandlesGracefully()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Setup session to delay sending
            var sendTcs = new TaskCompletionSource<bool>();
            _mockSession.Setup(s => s.SendBinaryDataAsync(It.IsAny<ReadOnlyMemory<byte>>()))
                .Returns(async (ReadOnlyMemory<byte> data) => 
                {
                    await Task.Delay(100);
                    return; // Quick return instead of waiting for 5s
                });

            var testMessage = new JsonRpcRequest
            {
                RequestId = new RequestId(1),
                Method = "test",
                Version = "2.0"
            };

            // Get the WriteCoreAsync method via reflection
            var writeMethod = typeof(WebSocketMessageHandler).GetMethod(
                "WriteCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Create a cancellation token source with short timeout
            var cts = new CancellationTokenSource(10);

            // Act - Since cancellation handling can vary, we'll just verify it completes
            // without hanging (either by throwing or by returning)
            try
            {
                await ((ValueTask)writeMethod!.Invoke(
                    handler,
                    new object[] { testMessage, cts.Token })!);
                // If it completed without throwing, that's fine
            }
            catch (Exception)
            {
                // If it threw, it should be due to cancellation, but we won't make
                // strict assertions about the exact exception type
                _mockLogger.Verify(
                    l => l.Log(
                        LogLevel.Error,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((v, t) => v!.ToString()!.Contains("Помилка надсилання JSON-RPC повідомлення")),
                        It.IsAny<Exception>(),
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                    Times.AtLeastOnce);
            }
        }

        [Fact]
        public async Task ProcessRealWorldJsonRpcMessage_ValidRequest_ProcessesCorrectly()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Real-world JSON-RPC request
            var jsonRpcRequest = @"{
                ""jsonrpc"": ""2.0"",
                ""method"": ""subscriptions.subscribe"",
                ""params"": [""account123"", [""Create"", ""Update"", ""Delete""]],
                ""id"": 12345
            }";

            _mockFormatter.Setup(f => f.Deserialize(It.IsAny<ReadOnlySequence<byte>>()))
                .Returns(new JsonRpcRequest
                {
                    RequestId = new RequestId(12345),
                    Method = "subscriptions.subscribe",
                    ArgumentsList = new object[] { "account123", _subscriptionEventTypes },
                    Version = "2.0"
                });

            // Act
            await handler.ProcessReceivedDataAsync(Encoding.UTF8.GetBytes(jsonRpcRequest));

            // Get the ReadCoreAsync method via reflection
            var readMethod = typeof(WebSocketMessageHandler).GetMethod(
                "ReadCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var result = await ((ValueTask<JsonRpcMessage>)readMethod!.Invoke(
                handler,
                new object[] { CancellationToken.None })!);

            // Assert
            Assert.NotNull(result);
            Assert.IsType<JsonRpcRequest>(result);
            var request = (JsonRpcRequest)result;
            Assert.Equal("subscriptions.subscribe", request.Method);
            Assert.Equal(new RequestId(12345), request.RequestId);

            // Verify deserialization was called
            _mockFormatter.Verify(f => f.Deserialize(It.IsAny<ReadOnlySequence<byte>>()), Times.Once);
        }

        [Fact]
        public async Task ProcessRealWorldJsonRpcMessage_ValidResponse_ProcessesCorrectly()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Real-world JSON-RPC response
            var jsonRpcResponse = @"{
                ""jsonrpc"": ""2.0"",
                ""result"": 42,
                ""id"": 12345
            }";

            _mockFormatter.Setup(f => f.Deserialize(It.IsAny<ReadOnlySequence<byte>>()))
                .Returns(new JsonRpcResult()
                {
                    RequestId = new RequestId(12345),
                    Result = 42,
                    Version = "2.0"
                });

            // Act
            await handler.ProcessReceivedDataAsync(Encoding.UTF8.GetBytes(jsonRpcResponse));

            // Get the ReadCoreAsync method via reflection
            var readMethod = typeof(WebSocketMessageHandler).GetMethod(
                "ReadCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var result = await ((ValueTask<JsonRpcMessage>)readMethod!.Invoke(
                handler,
                new object[] { CancellationToken.None })!);

            // Assert
            Assert.NotNull(result);
            Assert.IsType<JsonRpcResult>(result);
            var response = (JsonRpcResult)result;
            Assert.Equal(42, response.Result);
            Assert.Equal(new RequestId(12345), response.RequestId);

            // Verify deserialization was called
            _mockFormatter.Verify(f => f.Deserialize(It.IsAny<ReadOnlySequence<byte>>()), Times.Once);
        }

        [Fact]
        public async Task ProcessRealWorldJsonRpcMessage_Notification_ProcessesCorrectly()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Real-world JSON-RPC notification (no id)
            var jsonRpcNotification = @"{
                ""jsonrpc"": ""2.0"",
                ""method"": ""accounts.update"",
                ""params"": [{ ""id"": ""account123"", ""name"": ""Updated Account"" }]
            }";

            _mockFormatter.Setup(f => f.Deserialize(It.IsAny<ReadOnlySequence<byte>>()))
                .Returns(new JsonRpcRequest
                {
                    // No RequestId for notifications
                    Method = "accounts.update",
                    ArgumentsList = new object[] { new Dictionary<string, string> { { "id", "account123" }, { "name", "Updated Account" } } },
                    Version = "2.0"
                });

            // Act
            await handler.ProcessReceivedDataAsync(Encoding.UTF8.GetBytes(jsonRpcNotification));

            // Get the ReadCoreAsync method via reflection
            var readMethod = typeof(WebSocketMessageHandler).GetMethod(
                "ReadCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var result = await ((ValueTask<JsonRpcMessage>)readMethod!.Invoke(
                handler,
                new object[] { CancellationToken.None })!);

            // Assert
            Assert.NotNull(result);
            Assert.IsType<JsonRpcRequest>(result);
            var request = (JsonRpcRequest)result;
            Assert.Equal("accounts.update", request.Method);
            Assert.Null(request.RequestId.Number); // Should be null for notifications

            // Verify deserialization was called
            _mockFormatter.Verify(f => f.Deserialize(It.IsAny<ReadOnlySequence<byte>>()), Times.Once);
        }

        [Fact]
        public async Task ProcessRealWorldJsonRpcMessage_ErrorResponse_ProcessesCorrectly()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Real-world JSON-RPC error response
            var jsonRpcError = @"{
                ""jsonrpc"": ""2.0"",
                ""error"": {
                    ""code"": -32600,
                    ""message"": ""Invalid Request"",
                    ""data"": ""Missing required parameter""
                },
                ""id"": 12345
            }";

            _mockFormatter.Setup(f => f.Deserialize(It.IsAny<ReadOnlySequence<byte>>()))
                .Returns(new JsonRpcError
                {
                    RequestId = new RequestId(12345),
                    Error = new JsonRpcError.ErrorDetail
                    {
                        Code = JsonRpcErrorCode.InvalidRequest,
                        Message = "Invalid Request",
                        Data = "Missing required parameter"
                    },
                    Version = "2.0"
                });

            // Act
            await handler.ProcessReceivedDataAsync(Encoding.UTF8.GetBytes(jsonRpcError));

            // Get the ReadCoreAsync method via reflection
            var readMethod = typeof(WebSocketMessageHandler).GetMethod(
                "ReadCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var result = await ((ValueTask<JsonRpcMessage>)readMethod!.Invoke(
                handler,
                new object[] { CancellationToken.None })!);

            // Assert
            Assert.NotNull(result);
            Assert.IsType<JsonRpcError>(result);
            var error = (JsonRpcError)result;
            Assert.Equal(JsonRpcErrorCode.InvalidRequest, error.Error!.Code);
            Assert.Equal("Invalid Request", error.Error!.Message);
            Assert.Equal("Missing required parameter", error.Error!.Data);
            Assert.Equal(new RequestId(12345), error.RequestId);

            // Verify deserialization was called
            _mockFormatter.Verify(f => f.Deserialize(It.IsAny<ReadOnlySequence<byte>>()), Times.Once);
        }

        [Fact]
        public async Task WriteCoreAsync_WithTracingCallback_CallsOnSerializationComplete()
        {
            // Arrange
            // Mock a formatter that also implements IJsonRpcFormatterTracingCallbacks
            var mockTracingFormatter = new Mock<IJsonRpcMessageFormatter>();
            mockTracingFormatter.As<IJsonRpcFormatterTracingCallbacks>();

            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                mockTracingFormatter.Object,
                _mockLogger.Object,
                _config);

            var testMessage = new JsonRpcRequest
            {
                RequestId = new RequestId(1),
                Method = "test",
                Version = "2.0"
            };

            // Setup the session to accept the data
            _mockSession.Setup(s => s.SendBinaryDataAsync(It.IsAny<ReadOnlyMemory<byte>>()))
                .Returns(Task.CompletedTask);

            // Setup the tracer callback
            mockTracingFormatter.As<IJsonRpcFormatterTracingCallbacks>()
                .Setup(t => t.OnSerializationComplete(It.IsAny<JsonRpcMessage>(), It.IsAny<ReadOnlySequence<byte>>()));

            // Get the WriteCoreAsync method via reflection
            var writeMethod = typeof(WebSocketMessageHandler).GetMethod(
                "WriteCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act
            await ((ValueTask)writeMethod!.Invoke(
                handler,
                new object[] { testMessage, CancellationToken.None })!);

            // Assert
            mockTracingFormatter.As<IJsonRpcFormatterTracingCallbacks>()
                .Verify(t => t.OnSerializationComplete(
                    It.Is<JsonRpcMessage>(m => m == testMessage),
                    It.IsAny<ReadOnlySequence<byte>>()),
                    Times.Once);
        }

        [Fact]
        public async Task WriteCoreAsync_MultipleConcurrentWrites_SerializesCorrectly()
        {
            // Arrange
            var handler = new WebSocketMessageHandler(
                _mockSession.Object,
                _mockFormatter.Object,
                _mockLogger.Object,
                _config);

            // Setup session to capture sent messages
            var sentMessages = new List<string>();
            _mockSession.Setup(s => s.SendBinaryDataAsync(It.IsAny<ReadOnlyMemory<byte>>()))
                .Returns(Task.CompletedTask)
                .Callback<ReadOnlyMemory<byte>>(data => 
                {
                    sentMessages.Add(Encoding.UTF8.GetString(data.Span));
                });

            // Create 5 different messages
            var messages = Enumerable.Range(1, 5)
                .Select(i => new JsonRpcRequest
                {
                    RequestId = new RequestId(i),
                    Method = $"test{i}",
                    Version = "2.0"
                })
                .ToList();

            // Get the WriteCoreAsync method via reflection
            var writeMethod = typeof(WebSocketMessageHandler).GetMethod(
                "WriteCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act - send all messages concurrently
            var tasks = messages.Select(message =>
                Task.Run(async () =>
                {
                    await ((ValueTask)writeMethod!.Invoke(
                        handler,
                        new object[] { message, CancellationToken.None })!);
                })).ToArray();

            // Wait for all tasks to complete
            await Task.WhenAll(tasks);

            // Assert
            // Verify all messages were sent
            Assert.Equal(5, sentMessages.Count);
            
            // Verify the formatter was called exactly 5 times
            _mockFormatter.Verify(f => f.Serialize(It.IsAny<IBufferWriter<byte>>(), It.IsAny<JsonRpcMessage>()), Times.Exactly(5));
        }
    }
}