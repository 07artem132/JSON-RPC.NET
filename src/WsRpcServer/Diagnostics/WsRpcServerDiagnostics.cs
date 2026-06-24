using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace WsRpcServer.Diagnostics;

/// <summary>
/// Спільні джерела телеметрії бібліотеки: <see cref="System.Diagnostics.Metrics.Meter"/> та
/// <see cref="System.Diagnostics.ActivitySource"/> з ім'ям <c>"WsRpcServer"</c>.
/// </summary>
/// <remarks>
/// Приватність — інваріант (дзеркало SignalCli.NET): теги вимірів походять ЛИШЕ з фіксованого безпечного
/// набору <see cref="AllowedTagKeys"/> (статуси/енум-літерали), НІКОЛИ не несуть тіл повідомлень, номерів,
/// чи секретів ідентичності. Інструментація інертна без підписників (pull/opt-in), тож не впливає на
/// поведінку чи продуктивність, якщо ніхто не слухає.
///
/// Імена інструментів — крапко-розділені (<c>wsrpc.*</c>), як радить OpenTelemetry-конвенція.
/// </remarks>
public static class WsRpcServerDiagnostics
{
    /// <summary>Ім'я <see cref="System.Diagnostics.Metrics.Meter"/> та <see cref="System.Diagnostics.ActivitySource"/>.</summary>
    public const string SourceName = "WsRpcServer";

    /// <summary>Ключ тегу для результату операції (єдиний дозволений тег-ключ).</summary>
    public const string ResultTagKey = "result";

    /// <summary>Фіксований allowlist тег-ключів — privacy-guard звіряє захоплені виміри з ним.</summary>
    public static IReadOnlyCollection<string> AllowedTagKeys { get; } = [ResultTagKey];

    private static readonly Meter Meter = new(SourceName);

    /// <summary>Джерело activity-span'ів (життєвий цикл з'єднання).</summary>
    public static ActivitySource ActivitySource { get; } = new(SourceName);

    private static readonly UpDownCounter<long> ConnectionsActive =
        Meter.CreateUpDownCounter<long>("wsrpc.connections.active", unit: "{connection}",
            description: "Кількість активних WebSocket-з'єднань.");

    private static readonly Counter<long> ConnectionsRejectedCounter =
        Meter.CreateCounter<long>("wsrpc.connections.rejected", unit: "{connection}",
            description: "Відхилені з'єднання (перевищено квоту MaxConcurrentConnections).");

    private static readonly Counter<long> NotificationsCounter =
        Meter.CreateCounter<long>("wsrpc.notifications", unit: "{notification}",
            description: "Сповіщення server→client за результатом постановки в чергу.");

    private static readonly Counter<long> ParseFailuresCounter =
        Meter.CreateCounter<long>("wsrpc.parse_failures", unit: "{failure}",
            description: "Невдалі розбори вхідного JSON (recovery-loop транспорту).");

    private static readonly Counter<long> AuthorizationDeniedCounter =
        Meter.CreateCounter<long>("wsrpc.authorization.denied", unit: "{call}",
            description: "Відмови авторизації RPC ([RpcAuthorize] deny).");

    /// <summary>Збільшує гейдж активних з'єднань (з'єднання зараховано).</summary>
    public static void ConnectionOpened() => ConnectionsActive.Add(1);

    /// <summary>Зменшує гейдж активних з'єднань (зараховане з'єднання розірвано).</summary>
    public static void ConnectionClosed() => ConnectionsActive.Add(-1);

    /// <summary>Лічить відхилене за квотою з'єднання.</summary>
    public static void ConnectionRejected() => ConnectionsRejectedCounter.Add(1);

    /// <summary>Лічить сповіщення з тегом <c>result</c> (<c>queued</c>/<c>dropped</c>).</summary>
    public static void Notification(bool dropped) =>
        NotificationsCounter.Add(1, new KeyValuePair<string, object?>(ResultTagKey, dropped ? "dropped" : "queued"));

    /// <summary>Лічить невдалий розбір JSON.</summary>
    public static void ParseFailure() => ParseFailuresCounter.Add(1);

    /// <summary>Лічить відмову авторизації.</summary>
    public static void AuthorizationDenied() => AuthorizationDeniedCounter.Add(1);

    /// <summary>Стартує span життєвого циклу з'єднання (або <c>null</c>, якщо немає слухачів).</summary>
    public static Activity? StartConnectionActivity() =>
        ActivitySource.StartActivity("wsrpc.connection", ActivityKind.Server);
}
