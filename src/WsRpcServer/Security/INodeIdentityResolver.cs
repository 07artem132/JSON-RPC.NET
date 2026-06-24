using System.Security.Cryptography.X509Certificates;

namespace WsRpcServer.Security;

/// <summary>
/// Перетворює валідований клієнтський сертифікат на <see cref="NodeIdentity"/>.
/// </summary>
/// <remarks>
/// Замінний, щоб споживач міг виводити ідентичність з іншого поля (наприклад OU) без форку фреймворку.
/// Типова реалізація <see cref="SpiffeNodeIdentityResolver"/> бере SAN URI (SPIFFE), з SPKI-fallback.
/// </remarks>
public interface INodeIdentityResolver
{
    /// <summary>
    /// Виводить <see cref="NodeIdentity"/> з валідованого сертифіката.
    /// </summary>
    /// <param name="certificate">Валідований клієнтський сертифікат.</param>
    /// <returns>Ідентичність вузла.</returns>
    NodeIdentity Resolve(X509Certificate2 certificate);
}
