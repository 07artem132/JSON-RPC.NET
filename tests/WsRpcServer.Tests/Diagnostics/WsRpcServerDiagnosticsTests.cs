using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using WsRpcServer.Diagnostics;
using Xunit;

namespace WsRpcServer.Tests.Diagnostics;

/// <summary>
/// Guard для `observability`: інструменти <see cref="WsRpcServerDiagnostics"/> емітять у <c>Meter</c>
/// "WsRpcServer", сповіщення тегуються <c>result</c>, а набір тег-ключів НЕ виходить за privacy-allowlist.
/// </summary>
[Collection("WsRpcServerMetrics")]
public sealed class WsRpcServerDiagnosticsTests
{
    private sealed record Captured(string Instrument, long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags);

    /// <summary>Збирає виміри з усіх інструментів meter'а "WsRpcServer" під час дії <paramref name="act"/>.</summary>
    private static List<Captured> Capture(Action act)
    {
        var captured = new List<Captured>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WsRpcServerDiagnostics.SourceName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (captured)
            {
                captured.Add(new Captured(instrument.Name, value, tags.ToArray()));
            }
        });
        listener.Start();

        act();

        listener.Dispose();
        return captured;
    }

    [Fact]
    public void Notification_TaggedByResult_QueuedAndDropped()
    {
        var captured = Capture(() =>
        {
            WsRpcServerDiagnostics.Notification(dropped: false);
            WsRpcServerDiagnostics.Notification(dropped: true);
        });

        var notifications = captured.Where(c => c.Instrument == "wsrpc.notifications").ToList();
        Assert.Contains(notifications, c => HasTag(c, "result", "queued"));
        Assert.Contains(notifications, c => HasTag(c, "result", "dropped"));
    }

    [Fact]
    public void ConnectionsActive_RecordsPlusAndMinusOne()
    {
        var captured = Capture(() =>
        {
            WsRpcServerDiagnostics.ConnectionOpened();
            WsRpcServerDiagnostics.ConnectionClosed();
        });

        var gauge = captured.Where(c => c.Instrument == "wsrpc.connections.active").ToList();
        Assert.Contains(gauge, c => c.Value == 1);
        Assert.Contains(gauge, c => c.Value == -1);
    }

    [Fact]
    public void RejectedParseAndAuth_AreCounted()
    {
        var captured = Capture(() =>
        {
            WsRpcServerDiagnostics.ConnectionRejected();
            WsRpcServerDiagnostics.ParseFailure();
            WsRpcServerDiagnostics.AuthorizationDenied();
        });

        Assert.Contains(captured, c => c.Instrument == "wsrpc.connections.rejected" && c.Value == 1);
        Assert.Contains(captured, c => c.Instrument == "wsrpc.parse_failures" && c.Value == 1);
        Assert.Contains(captured, c => c.Instrument == "wsrpc.authorization.denied" && c.Value == 1);
    }

    [Fact]
    public void AllCapturedTagKeys_AreWithinPrivacyAllowlist()
    {
        var captured = Capture(() =>
        {
            WsRpcServerDiagnostics.Notification(dropped: false);
            WsRpcServerDiagnostics.Notification(dropped: true);
            WsRpcServerDiagnostics.ConnectionOpened();
            WsRpcServerDiagnostics.ConnectionClosed();
            WsRpcServerDiagnostics.ConnectionRejected();
            WsRpcServerDiagnostics.ParseFailure();
            WsRpcServerDiagnostics.AuthorizationDenied();
        });

        var allowed = WsRpcServerDiagnostics.AllowedTagKeys;
        foreach (var c in captured)
        {
            foreach (var tag in c.Tags)
            {
                Assert.Contains(tag.Key, allowed);
            }
        }
    }

    private static bool HasTag(Captured c, string key, string value) =>
        c.Tags.Any(t => t.Key == key && (string?)t.Value == value);
}
