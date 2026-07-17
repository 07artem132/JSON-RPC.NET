using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetCoreServer;
using WsRpcServer.Core;
using WsRpcServer.Sessions;
using Xunit;

namespace WsRpcServer.Tests.Sessions;

/// <summary>
/// Guard для capability `browser-ws-interop` (пункт 1): хук <c>NegotiateSubprotocol</c> + перебудова
/// 101-відповіді у <see cref="AbstractJsonRpcSession.OnWsConnecting"/> echo-ять узгоджений субпротокол
/// БЕЗ сміття-кадру (обхід дефекту NetCoreServer 8.0.7, де <c>OnWsConnecting</c> викликається після
/// <c>SetBody()</c>). Реальний loopback (`ClientWebSocket`) — доказ на справжньому 101-upgrade seam'і.
/// </summary>
public sealed class SubprotocolNegotiationTests
{
    private const string OfferedProtocol = "jsonrpc";

    // Відомий перший кадр, який сесія шле одразу після open — доказ, що жоден заголовок не «протік»
    // у потік як сміття-кадр (клієнт має отримати САМЕ ці байти як валідний перший кадр).
    private static readonly byte[] FirstFrame = [0x7B, 0x22, 0x6F, 0x6B, 0x22, 0x7D]; // {"ok"}

    /// <summary>Сесія, що echo-ить <c>jsonrpc</c>, якщо клієнт його запропонував.</summary>
    private sealed class EchoingSession(WsServer server)
        : AbstractJsonRpcSession(server, NullLogger.Instance, new JsonRpcServerConfig())
    {
        protected override string? NegotiateSubprotocol(IReadOnlyList<string> offeredSubprotocols) =>
            offeredSubprotocols.Contains(OfferedProtocol) ? OfferedProtocol : null;

        public override void OnWsConnected(HttpRequest request)
        {
            base.OnWsConnected(request);
            // Шлемо відомий кадр одразу після рукостискання (перевірка «перший кадр валідний»).
            SendBinaryAsync(FirstFrame);
        }
    }

    /// <summary>Сесія, що НЕ override-ить хук (дефолт <c>null</c>) — пін незмінної поведінки.</summary>
    private sealed class DefaultSession(WsServer server)
        : AbstractJsonRpcSession(server, NullLogger.Instance, new JsonRpcServerConfig());

    private sealed class TestServer(IPAddress address, int port, IServiceProvider sp, bool echoing)
        : AbstractJsonRpcServer(address, port, sp, NullLogger.Instance)
    {
        protected override WsSession CreateJsonRpcSession() =>
            echoing ? new EchoingSession(this) : new DefaultSession(this);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static ServiceProvider EmptyProvider() => new ServiceCollection().BuildServiceProvider();

    private static async Task<bool> PollAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    [Fact]
    public async Task NegotiateSubprotocol_EchoesOfferedProtocol_ConnectionOpensWithValidFirstFrame()
    {
        int port = FreePort();
        using var provider = EmptyProvider();
        using var server = new TestServer(IPAddress.Loopback, port, provider, echoing: true);
        Assert.True(server.Start());

        try
        {
            using var client = new ClientWebSocket();
            client.Options.AddSubProtocol(OfferedProtocol);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), cts.Token);

            // 1) Сервер echo-нув узгоджений субпротокол у 101-заголовках (а не «протік» у потік).
            Assert.Equal(WebSocketState.Open, client.State);
            Assert.Equal(OfferedProtocol, client.SubProtocol);

            // 2) Перший кадр після upgrade — валідний і рівно наш маркер (жодного сміття-кадру).
            var buffer = new byte[64];
            var result = await client.ReceiveAsync(buffer, cts.Token);
            Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
            Assert.True(result.EndOfMessage);
            Assert.Equal(FirstFrame, buffer[..result.Count]);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task NegotiateSubprotocol_DefaultHook_DoesNotEchoProtocol()
    {
        int port = FreePort();
        using var provider = EmptyProvider();
        using var server = new TestServer(IPAddress.Loopback, port, provider, echoing: false);
        Assert.True(server.Start());

        try
        {
            using var client = new ClientWebSocket();
            client.Options.AddSubProtocol(OfferedProtocol);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), cts.Token);

            // Дефолтний хук (null) → сервер НЕ echo-ить субпротокол (поведінка як раніше).
            Assert.True(await PollAsync(() => client.State == WebSocketState.Open));
            Assert.True(string.IsNullOrEmpty(client.SubProtocol));
        }
        finally
        {
            server.Stop();
        }
    }
}
