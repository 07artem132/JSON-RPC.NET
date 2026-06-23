using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using WsRpcServer.Core;
using WsRpcServer.Sessions;
using WsRpcServer.Transport;
using Xunit;
using WebSocketMessageHandler = WsRpcServer.Transport.WebSocketMessageHandler;

namespace WsRpcServer.Tests.Transport
{
    /// <summary>
    /// Regression guard для H2 (parse-failure-throttle): нескінченний цикл відновлення на
    /// невалідному JSON — CPU-burn DoS-вектор. Після N послідовних помилок з'єднання має
    /// закриватися зі статусом ProtocolError; успішний розбір скидає лічильник.
    /// </summary>
    public class WebSocketMessageHandlerParseThrottleTests
    {
        private readonly Mock<IJsonRpcSession> _mockSession = new();
        private readonly Mock<IJsonRpcMessageFormatter> _mockFormatter = new();
        private readonly Mock<ILogger<WebSocketMessageHandler>> _mockLogger = new();

        public WebSocketMessageHandlerParseThrottleTests()
        {
            _mockSession.Setup(s => s.Id).Returns(Guid.NewGuid());
        }

        private static readonly MethodInfo ReadCore = typeof(WebSocketMessageHandler).GetMethod(
            "ReadCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static async Task<JsonRpcMessage?> ReadAsync(WebSocketMessageHandler handler) =>
            await (ValueTask<JsonRpcMessage>)ReadCore.Invoke(handler, new object[] { CancellationToken.None })!;

        [Fact]
        public async Task ReadCoreAsync_ConsecutiveParseFailuresExceedLimit_ClosesWithProtocolError()
        {
            // Arrange — низький поріг, форматер завжди кидає JsonException.
            var config = new JsonRpcServerConfig { MaxConsecutiveParseFailures = 3 };
            var handler = new WebSocketMessageHandler(
                _mockSession.Object, _mockFormatter.Object, _mockLogger.Object, config);

            _mockFormatter.Setup(f => f.Deserialize(It.IsAny<ReadOnlySequence<byte>>()))
                .Throws(() => new JsonException("bad", null, null, 0));

            // Act — кожна ітерація подає окремий невалідний фрейм і читає його.
            for (int i = 0; i < 2; i++)
            {
                await handler.ProcessReceivedDataAsync(Encoding.UTF8.GetBytes("garbage"));
                await ReadAsync(handler);
            }

            // До перевищення ліміту з'єднання ще НЕ закрите.
            _mockSession.Verify(s => s.Close(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string>()), Times.Never);

            // Третя послідовна помилка перевищує поріг.
            await handler.ProcessReceivedDataAsync(Encoding.UTF8.GetBytes("garbage"));
            var result = await ReadAsync(handler);

            // Assert
            Assert.Null(result);
            _mockSession.Verify(
                s => s.Close(WebSocketCloseStatus.ProtocolError, It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task ReadCoreAsync_SuccessfulParse_ResetsFailureCounter()
        {
            // Arrange — поріг 3; шаблон: fail, fail, success, fail → лічильник не має досягти 3.
            var config = new JsonRpcServerConfig { MaxConsecutiveParseFailures = 3 };
            var handler = new WebSocketMessageHandler(
                _mockSession.Object, _mockFormatter.Object, _mockLogger.Object, config);

            int call = 0;
            _mockFormatter.Setup(f => f.Deserialize(It.IsAny<ReadOnlySequence<byte>>()))
                .Returns((ReadOnlySequence<byte> _) =>
                {
                    call++;
                    if (call == 3)
                    {
                        // Третій виклик — успішний розбір, що скидає лічильник.
                        return new JsonRpcRequest { RequestId = new RequestId(1), Method = "ok", Version = "2.0" };
                    }
                    throw new JsonException("bad", null, null, 0);
                });

            // Act — 4 read-цикли: fail, fail, success(reset), fail.
            for (int i = 0; i < 4; i++)
            {
                await handler.ProcessReceivedDataAsync(Encoding.UTF8.GetBytes("frame"));
                await ReadAsync(handler);
            }

            // Assert — успішний розбір скинув лічильник, тож порога не досягнуто → без закриття.
            _mockSession.Verify(s => s.Close(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string>()), Times.Never);
        }
    }
}
