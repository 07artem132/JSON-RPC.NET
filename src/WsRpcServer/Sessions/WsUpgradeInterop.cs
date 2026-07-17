using System.Security.Cryptography;
using System.Text;
using NetCoreServer;

namespace WsRpcServer.Sessions;

/// <summary>
/// Транспортна «glue»-логіка узгодження WebSocket-субпротоколу під час 101-upgrade.
/// Спільна для плейн-текст (<see cref="AbstractJsonRpcSession"/>) і захищеної
/// (<see cref="AbstractSecureJsonRpcSession"/>) сесій: обидві мають однакову ваду NetCoreServer 8.0.7,
/// але різні базові типи (окремі ієрархії NetCoreServer), тож логіку інкапсульовано тут, а не дубльовано.
/// </summary>
/// <remarks>
/// NetCoreServer 8.0.7 викликає <c>OnWsConnecting(request, response)</c> ПІСЛЯ <c>response.SetBody()</c>
/// (порожній рядок-роздільник уже дописано до відповіді). Тому додавання заголовка через
/// <c>response.SetHeader("Sec-WebSocket-Protocol", …)</c> у цій точці «протікає» у WebSocket-потік як
/// сміття-кадр (WHATWG-клієнт бачить «reserved bits set» і не досягає open). Щоб коректно echo-нути
/// субпротокол, відповідь ПОВНІСТЮ перебудовується (Clear → SetBegin(101) → заголовки → SetBody) за
/// RFC 6455 — цим фреймворк інкапсулює обхід, який раніше кожен споживач робив вручну.
/// </remarks>
internal static class WsUpgradeInterop
{
    /// <summary>RFC 6455 §4.2.2 — «магічний» GUID для обчислення <c>Sec-WebSocket-Accept</c>.</summary>
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private const string SecWebSocketKeyHeader = "Sec-WebSocket-Key";
    private const string SecWebSocketProtocolHeader = "Sec-WebSocket-Protocol";

    /// <summary>
    /// Парсить усі запропоновані клієнтом субпротоколи із запиту: підтримує як кілька окремих заголовків
    /// <c>Sec-WebSocket-Protocol</c>, так і один comma-list. Повертає їх у порядку появи (без порожніх).
    /// </summary>
    /// <param name="request">HTTP-запит рукостискання WebSocket.</param>
    /// <returns>Список запропонованих субпротоколів (може бути порожнім).</returns>
    public static IReadOnlyList<string> ParseOfferedSubprotocols(HttpRequest request)
    {
        List<string>? offered = null;

        long count = request.Headers;
        for (long i = 0; i < count; i++)
        {
            var (key, value) = request.Header((int)i);
            if (!string.Equals(key, SecWebSocketProtocolHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var part in value.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    (offered ??= []).Add(trimmed);
                }
            }
        }

        return offered ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Повністю перебудовує успішну 101-відповідь, додаючи <c>Sec-WebSocket-Protocol</c> серед
    /// заголовків (до тіла), і завершує її <c>SetBody</c>. Повертає <c>false</c>, якщо у запиті немає
    /// валідного <c>Sec-WebSocket-Key</c> (тоді викликач лишає стандартну поведінку без змін).
    /// </summary>
    /// <param name="request">HTTP-запит рукостискання (джерело <c>Sec-WebSocket-Key</c>).</param>
    /// <param name="response">HTTP-відповідь, що перебудовується на місці.</param>
    /// <param name="subprotocol">Узгоджений субпротокол для echo.</param>
    /// <returns><c>true</c>, якщо відповідь перебудовано; інакше <c>false</c>.</returns>
    public static bool TryWriteUpgradeResponse(HttpRequest request, HttpResponse response, string subprotocol)
    {
        var key = FindHeader(request, SecWebSocketKeyHeader);
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var accept = ComputeAccept(key);

        response.Clear();
        response.SetBegin(101);
        response.SetHeader("Connection", "Upgrade");
        response.SetHeader("Upgrade", "websocket");
        response.SetHeader("Sec-WebSocket-Accept", accept);
        response.SetHeader(SecWebSocketProtocolHeader, subprotocol);
        response.SetBody();
        return true;
    }

    private static string? FindHeader(HttpRequest request, string name)
    {
        long count = request.Headers;
        for (long i = 0; i < count; i++)
        {
            var (key, value) = request.Header((int)i);
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static string ComputeAccept(string secWebSocketKey)
    {
        // justification: SHA-1 тут — не криптографічний примітив безпеки, а обов'язкова частина
        // WebSocket-рукостискання (RFC 6455 §4.2.2: Base64(SHA1(key + GUID))). Заміні не підлягає.
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(secWebSocketKey + WebSocketGuid));
#pragma warning restore CA5350
        return Convert.ToBase64String(hash);
    }
}
