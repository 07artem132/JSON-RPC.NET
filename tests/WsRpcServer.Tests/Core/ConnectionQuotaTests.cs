using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetCoreServer;
using WsRpcServer.Core;
using WsRpcServer.Diagnostics;
using Xunit;

namespace WsRpcServer.Tests.Core;

/// <summary>
/// Guard для `connection-resilience`: квота <see cref="JsonRpcServerConfig.MaxConcurrentConnections"/>
/// відхиляє (N+1)-ше з'єднання (метрика <c>wsrpc.connections.rejected</c>); дефолт <c>0</c> не відхиляє.
/// Реальний loopback-сервер — перевірка справжнього TCP-accept seam'у.
/// </summary>
[Collection("WsRpcServerMetrics")]
public sealed class ConnectionQuotaTests
{
    /// <summary>Мінімальна реальна сесія: NetCoreServer сам завершує WS-рукостискання.</summary>
    private sealed class QuotaTestSession(WsServer server) : WsSession(server);

    private sealed class QuotaTestServer(IPAddress address, int port, IServiceProvider sp)
        : AbstractJsonRpcServer(address, port, sp, NullLogger.Instance)
    {
        protected override WsSession CreateJsonRpcSession() => new QuotaTestSession(this);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static IServiceProvider ProviderWithConfig(int maxConnections)
    {
        var sp = new Mock<IServiceProvider>();
        sp.Setup(p => p.GetService(typeof(JsonRpcServerConfig)))
            .Returns(new JsonRpcServerConfig { MaxConcurrentConnections = maxConnections });
        return sp.Object;
    }

    private static long _rejected;

    private static MeterListener ListenRejected()
    {
        Interlocked.Exchange(ref _rejected, 0);
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WsRpcServerDiagnostics.SourceName &&
                instrument.Name == "wsrpc.connections.rejected")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _rejected, value));
        listener.Start();
        return listener;
    }

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

    private static async Task<ClientWebSocket?> TryConnectAsync(int port)
    {
        var client = new ClientWebSocket();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), cts.Token);
            return client;
        }
        catch (Exception)
        {
            client.Dispose();
            return null;
        }
    }

    [Fact]
    public async Task SecondConnection_OverLimitOfOne_IsRejected()
    {
        int port = FreePort();
        using var listener = ListenRejected();
        using var server = new QuotaTestServer(IPAddress.Loopback, port, ProviderWithConfig(maxConnections: 1));
        Assert.True(server.Start());

        try
        {
            var first = await TryConnectAsync(port);
            Assert.NotNull(first);
            Assert.True(await PollAsync(() => server.ConnectedSessions >= 1), "Перше з'єднання не прийнято.");

            // Друге з'єднання перевищує квоту → сервер його відхиляє на TCP-accept.
            var second = await TryConnectAsync(port);

            Assert.True(await PollAsync(() => Interlocked.Read(ref _rejected) >= 1),
                "Очікувалось відхилення другого з'єднання (wsrpc.connections.rejected).");

            second?.Dispose();
            first!.Dispose();
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task Default_ZeroLimit_RejectsNothing()
    {
        int port = FreePort();
        using var server = new QuotaTestServer(IPAddress.Loopback, port, ProviderWithConfig(maxConnections: 0));
        Assert.True(server.Start());

        try
        {
            // Без квоти (дефолт 0) обидва з'єднання мають прийнятись — це і є доказ, що квота не відхиляє.
            var a = await TryConnectAsync(port);
            var b = await TryConnectAsync(port);
            Assert.NotNull(a);
            Assert.NotNull(b);

            Assert.True(await PollAsync(() => server.ConnectedSessions >= 2), "Обидва з'єднання мали прийнятись.");

            a!.Dispose();
            b!.Dispose();
        }
        finally
        {
            server.Stop();
        }
    }
}
