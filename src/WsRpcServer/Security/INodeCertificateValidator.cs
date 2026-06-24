using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace WsRpcServer.Security;

/// <summary>
/// Валідатор клієнтського сертифіката для mTLS — викликається з
/// <c>SslContext.RemoteCertificateValidationCallback</c> під час рукостискання.
/// </summary>
/// <remarks>
/// NetCoreServer автентифікує через <c>BeginAuthenticateAsServer</c>, тож декларативний
/// <c>SslServerAuthenticationOptions.CertificateChainPolicy</c> недоступний — уся кастомна валідація
/// (побудова <see cref="X509Chain"/>, <see cref="X509ChainTrustMode.CustomRootTrust"/>, EKU, SPKI-піни)
/// має відбуватися всередині callback'а. Реалізація НІКОЛИ не сміє повертати <c>true</c> «наосліп» —
/// це сенс існування callback'а (Roslyn-guard пінує заборону <c>=> true</c>).
/// </remarks>
public interface INodeCertificateValidator
{
    /// <summary>
    /// Валідує презентований клієнтський сертифікат.
    /// </summary>
    /// <param name="certificate">Презентований сертифікат (може бути <c>null</c>, якщо клієнт його не надіслав).</param>
    /// <param name="chain">Ланцюг, побудований середовищем (не використовується — будуємо власний).</param>
    /// <param name="sslPolicyErrors">Помилки політики SSL, виявлені середовищем.</param>
    /// <returns><c>true</c>, якщо сертифікат валідний і з'єднання можна продовжити; інакше <c>false</c>.</returns>
    bool Validate(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors);
}
