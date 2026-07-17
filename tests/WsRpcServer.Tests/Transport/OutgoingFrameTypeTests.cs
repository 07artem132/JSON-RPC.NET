using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetCoreServer;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using WsRpcServer.Core;
using WsRpcServer.Sessions;
using WsRpcServer.Transport;
using Xunit;
using WebSocketMessageHandler = WsRpcServer.Transport.WebSocketMessageHandler;

namespace WsRpcServer.Tests.Transport;

/// <summary>
/// Guard для capability `browser-ws-interop` (пункт 2): вихідні JSON-RPC кадри йдуть Text-ом, коли
/// ввімкнено <see cref="JsonRpcServerConfig.UseTextFramesForOutgoingMessages"/>, і Binary-ом за
/// замовчуванням (пін поточної поведінки). Два рівні: (1) реальний loopback крізь
/// <c>WriteCoreAsync</c> → <c>SendText/SendBinaryDataAsync</c> → провід; (2) детермінований handler-тест,
/// що <c>WriteCoreAsync</c> роутить за прапорцем.
/// </summary>
public sealed class OutgoingFrameTypeTests
{
    // ---- Рівень 1: реальний loopback (провід) ----

    /// <summary>Сесія, що на open будує реальний транспорт і шле одне JSON-RPC сповіщення.</summary>
    private sealed class SendingSession(WsServer server, JsonRpcServerConfig config)
        : AbstractJsonRpcSession(server, NullLogger.Instance, config)
    {
        private WebSocketMessageHandler? _handler;

        public override void OnWsConnected(HttpRequest request)
        {
            base.OnWsConnected(request);

            var formatter = new SystemTextJsonFormatter();
            _handler = new WebSocketMessageHandler(this, formatter,
                NullLogger<WebSocketMessageHandler>.Instance, Config);
            JsonRpc = new JsonRpc(_handler, _handler);
            JsonRpc.StartListening();

            _ = SendPingAsync();
        }

        private async Task SendPingAsync()
        {
            try
            {
                if (JsonRpc != null)
                {
                    await JsonRpc.NotifyAsync("ping");
                }
            }
            catch (Exception)
            {
                // Тест: помилка відправки нецікава — перевіряємо лише тип кадру, який доходить.
            }
        }
    }

    private sealed class SendingServer(IPAddress address, int port, IServiceProvider sp, JsonRpcServerConfig config)
        : AbstractJsonRpcServer(address, port, sp, NullLogger.Instance)
    {
        protected override WsSession CreateJsonRpcSession() => new SendingSession(this, config);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<WebSocketMessageType> ReceiveFirstFrameTypeAsync(bool useTextFrames)
    {
        int port = FreePort();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var config = new JsonRpcServerConfig { UseTextFramesForOutgoingMessages = useTextFrames };
        using var server = new SendingServer(IPAddress.Loopback, port, provider, config);
        Assert.True(server.Start());

        try
        {
            using var client = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), cts.Token);

            var buffer = new byte[512];
            var result = await client.ReceiveAsync(buffer, cts.Token);
            return result.MessageType;
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task Loopback_TextFramesEnabled_ClientReceivesTextFrame()
    {
        var type = await ReceiveFirstFrameTypeAsync(useTextFrames: true);
        Assert.Equal(WebSocketMessageType.Text, type);
    }

    [Fact]
    public async Task Loopback_DefaultBinary_ClientReceivesBinaryFrame()
    {
        var type = await ReceiveFirstFrameTypeAsync(useTextFrames: false);
        Assert.Equal(WebSocketMessageType.Binary, type);
    }

    // ---- Рівень 2: детермінований роутинг у WriteCoreAsync ----

    private static async Task InvokeWriteCoreAsync(WebSocketMessageHandler handler, JsonRpcMessage message)
    {
        var writeMethod = typeof(WebSocketMessageHandler).GetMethod(
            "WriteCoreAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await ((ValueTask)writeMethod!.Invoke(handler, [message, CancellationToken.None])!);
    }

    private static (Mock<IJsonRpcSession> session, WebSocketMessageHandler handler) BuildHandler(bool useTextFrames)
    {
        var session = new Mock<IJsonRpcSession>();
        session.Setup(s => s.Id).Returns(Guid.NewGuid());
        session.Setup(s => s.SendTextDataAsync(It.IsAny<ReadOnlyMemory<byte>>())).Returns(Task.CompletedTask);
        session.Setup(s => s.SendBinaryDataAsync(It.IsAny<ReadOnlyMemory<byte>>())).Returns(Task.CompletedTask);

        var formatter = new Mock<IJsonRpcMessageFormatter>();
        var logger = new Mock<ILogger<WebSocketMessageHandler>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var config = new JsonRpcServerConfig { UseTextFramesForOutgoingMessages = useTextFrames };
        var handler = new WebSocketMessageHandler(session.Object, formatter.Object, logger.Object, config);
        return (session, handler);
    }

    [Fact]
    public async Task WriteCoreAsync_TextFramesEnabled_CallsSendTextDataAsyncOnly()
    {
        var (session, handler) = BuildHandler(useTextFrames: true);
        var message = new JsonRpcRequest { RequestId = new RequestId(1), Method = "test", Version = "2.0" };

        await InvokeWriteCoreAsync(handler, message);

        session.Verify(s => s.SendTextDataAsync(It.IsAny<ReadOnlyMemory<byte>>()), Times.Once);
        session.Verify(s => s.SendBinaryDataAsync(It.IsAny<ReadOnlyMemory<byte>>()), Times.Never);
    }

    [Fact]
    public async Task WriteCoreAsync_DefaultBinary_CallsSendBinaryDataAsyncOnly()
    {
        var (session, handler) = BuildHandler(useTextFrames: false);
        var message = new JsonRpcRequest { RequestId = new RequestId(1), Method = "test", Version = "2.0" };

        await InvokeWriteCoreAsync(handler, message);

        session.Verify(s => s.SendBinaryDataAsync(It.IsAny<ReadOnlyMemory<byte>>()), Times.Once);
        session.Verify(s => s.SendTextDataAsync(It.IsAny<ReadOnlyMemory<byte>>()), Times.Never);
    }
}
