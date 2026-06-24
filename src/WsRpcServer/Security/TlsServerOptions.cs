using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;

namespace WsRpcServer.Security;

/// <summary>
/// Конфігурація TLS-транспорту для <see cref="Core.AbstractSecureJsonRpcServer"/>.
/// Валідується fail-fast через options-pipeline (як <see cref="Core.JsonRpcServerConfig"/>).
/// </summary>
/// <remarks>
/// Жодного секретного матеріалу не «зашито» у код: серверний сертифікат постачає споживач.
/// За замовчуванням узгоджуються лише TLS 1.3 та TLS 1.2 (старіші протоколи відхиляються);
/// <see cref="ClientCertificateRequired"/> вмикає mTLS (вимога клієнтського сертифіката на рукостисканні).
///
/// Поля довіри для mTLS (<see cref="TrustedRoots"/>, <see cref="SpkiPins"/>,
/// <see cref="RevocationMode"/>) використовує <see cref="INodeCertificateValidator"/>; вони лишаються
/// порожніми/типовими для суто серверного TLS без клієнтських сертифікатів.
/// </remarks>
public record TlsServerOptions
{
    /// <summary>
    /// Серверний сертифікат (з приватним ключем) для TLS-рукостискання.
    /// Обов'язковий — без нього <c>OptionsValidationException</c> на резолві.
    /// </summary>
    /// <remarks>
    /// Має містити приватний ключ (<see cref="X509Certificate2.HasPrivateKey"/> = true), інакше
    /// сервер не зможе завершити рукостискання. Сертифікат будується один раз і перевикористовується
    /// (побудова ланцюга — CPU-затратна; перевикористання дозволяє відновлення TLS-сесій на Linux).
    /// </remarks>
    [Required]
    public X509Certificate2? ServerCertificate { get; set; }

    /// <summary>
    /// Дозволені версії протоколу TLS. За замовчуванням TLS 1.3 із fallback на TLS 1.2.
    /// </summary>
    /// <remarks>
    /// Старіші протоколи (TLS 1.0/1.1, SSL 3.0) свідомо не вмикаються — вони мають відомі вади.
    /// Не використовуй <see cref="SslProtocols.None"/> «щоб ОС обрала» без розуміння наслідків:
    /// явний перелік тут — частина безпекового контракту.
    /// </remarks>
    public SslProtocols SslProtocols { get; set; } = SslProtocols.Tls13 | SslProtocols.Tls12;

    /// <summary>
    /// Вимагати клієнтський сертифікат на рукостисканні (вмикає mTLS). За замовчуванням <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Коли <c>true</c>, сервер встановлює <c>SslContext.ClientCertificateRequired = true</c>, а
    /// презентований сертифікат проходить через <see cref="INodeCertificateValidator"/>.
    /// </remarks>
    public bool ClientCertificateRequired { get; set; } = true;

    /// <summary>
    /// Довірені корені (приватний CA) для валідації клієнтських сертифікатів через
    /// <see cref="X509ChainTrustMode.CustomRootTrust"/>. НІКОЛИ не машинне сховище.
    /// </summary>
    /// <remarks>
    /// Заповнюється лише для mTLS. Порожня колекція + <see cref="ClientCertificateRequired"/> = true
    /// означає, що жоден клієнтський сертифікат не пройде валідацію (fail-closed).
    /// </remarks>
    public IReadOnlyCollection<X509Certificate2> TrustedRoots { get; set; } = [];

    /// <summary>
    /// Необов'язковий allowlist SPKI-SHA-256 пінів (defense-in-depth поверх довіри до CA).
    /// </summary>
    /// <remarks>
    /// Кожен елемент — hex-рядок SHA-256 від <c>SubjectPublicKeyInfo</c> (див.
    /// <see cref="NodeIdentity.SpkiThumbprint"/>). Якщо непорожній, CA-валідний сертифікат,
    /// чий SPKI не у списку, відхиляється. Пін переживає переоформлення сертифіката з тим самим
    /// ключем (на відміну від піна за відбитком).
    /// </remarks>
    public IReadOnlyCollection<string> SpkiPins { get; set; } = [];

    /// <summary>
    /// Режим перевірки відкликання для клієнтських сертифікатів. За замовчуванням
    /// <see cref="X509RevocationMode.Offline"/> (кешований CRL).
    /// </summary>
    /// <remarks>
    /// <see cref="X509RevocationMode.Online"/> робить зовнішні OCSP/CRL/AIA-виклики під час
    /// рукостискання — вектор DoS, якщо респондер повільний. Поєднуй <c>Offline</c> з короткоживучими
    /// сертифікатами. <see cref="X509RevocationMode.NoCheck"/> допустимий лише зі свідомим
    /// обґрунтуванням (guarded Roslyn-тестом).
    /// </remarks>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.Offline;
}
