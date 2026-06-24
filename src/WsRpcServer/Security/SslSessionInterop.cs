using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using NetCoreServer;

namespace WsRpcServer.Security;

/// <summary>
/// Доступ до приватного <c>SslStream</c> сесії NetCoreServer — ключа кореляції з валідованою
/// ідентичністю (<see cref="SecureTransport.TryGetIdentity"/>).
/// </summary>
/// <remarks>
/// NetCoreServer тримає <c>SslSession._sslStream</c> приватним і НЕ дає публічного доступу до
/// презентованого клієнтського сертифіката чи власне потоку. Єдиний колбек валідації
/// (<c>RemoteCertificateValidationCallback</c>) отримує <c>SslStream</c> як <c>sender</c>, тож щоб
/// зіставити «валідація → сесія», сесії потрібен власний <c>SslStream</c>. Це транспортна «glue»-ланка,
/// а не диспетч-шлях: один кешований <see cref="FieldInfo"/>, читаний раз на з'єднання. Якщо майбутня
/// версія NetCoreServer перейменує поле, <see cref="TryGetSslStream"/> поверне <c>false</c>, і
/// ідентичність просто не встановиться (fail-closed для авторизації) — без падіння транспорту.
///
/// justification: рефлексія тут — вимушений обхід відсутнього публічного API NetCoreServer, поза
/// AOT-диспетч-шляхом; анотовано для чесності перед trimming.
/// </remarks>
internal static class SslSessionInterop
{
    private static readonly FieldInfo? SslStreamField =
        typeof(SslSession).GetField("_sslStream", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Дістає приватний <c>SslStream</c> сесії, якщо доступний.
    /// </summary>
    /// <param name="session">SSL-сесія NetCoreServer.</param>
    /// <param name="sslStream">Потік, якщо знайдено.</param>
    /// <returns><c>true</c>, якщо поле прочитано й містить значення.</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification =
            "Читання приватного поля NetCoreServer _sslStream — транспортна glue-ланка поза AOT-диспетчем; " +
            "за відсутності поля повертаємо false (fail-closed), не падаючи.")]
    public static bool TryGetSslStream(SslSession session, [NotNullWhen(true)] out object? sslStream)
    {
        sslStream = SslStreamField?.GetValue(session);
        return sslStream is not null;
    }
}
