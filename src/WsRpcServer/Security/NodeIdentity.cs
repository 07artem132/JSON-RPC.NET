using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WsRpcServer.Security;

/// <summary>
/// Ідентичність вузла (machine-to-machine), виведена з валідованого клієнтського сертифіката.
/// </summary>
/// <param name="SpiffeId">SAN URI (SPIFFE-стиль), якщо присутній — основне ім'я principal'а.</param>
/// <param name="SpkiThumbprint">SHA-256 від <c>SubjectPublicKeyInfo</c> (hex) — стабільний fallback-id.</param>
/// <param name="Subject">Subject DN сертифіката (для діагностики/логів).</param>
/// <remarks>
/// <see cref="SpkiThumbprint"/> стабільний при переоформленні сертифіката з тим самим ключем, тож
/// слугує надійним ідентифікатором навіть без SAN URI. Це <c>readonly record struct</c> — порівнюється
/// за значенням і не алокує на купі.
/// </remarks>
public readonly record struct NodeIdentity(Uri? SpiffeId, string SpkiThumbprint, string Subject)
{
    /// <summary>
    /// Стабільне ім'я principal'а: <see cref="SpiffeId"/>, якщо є, інакше — <see cref="SpkiThumbprint"/>.
    /// </summary>
    public string Name => SpiffeId?.ToString() ?? SpkiThumbprint;

    /// <summary>
    /// Обчислює SHA-256 hex-відбиток <c>SubjectPublicKeyInfo</c> сертифіката (SPKI-пін).
    /// </summary>
    /// <param name="certificate">Сертифікат, з якого береться публічний ключ.</param>
    /// <returns>Hex-рядок SHA-256 (великими літерами, без розділювачів).</returns>
    /// <remarks>
    /// Хешується саме <c>SubjectPublicKeyInfo</c> (а не весь сертифікат), тож відбиток не змінюється
    /// при переоформленні з тим самим ключем — на відміну від <see cref="X509Certificate2.Thumbprint"/>.
    /// </remarks>
    public static string ComputeSpkiThumbprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);
        return Convert.ToHexString(hash);
    }
}
