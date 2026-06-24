using System.Security.Claims;

namespace WsRpcServer.Authorization;

/// <summary>
/// Політика авторизації RPC-викликів: чи дозволено даному principal'у виклик методу з даною вимогою.
/// </summary>
/// <remarks>
/// Типова реалізація <see cref="StaticRoleMapAuthorizationPolicy"/> зіставляє ідентичність вузла
/// зі статичною мапою «ідентичність → ролі» (з конфігурації споживача) і перевіряє членство в ролі.
/// Замінна, щоб споживач міг підключити власне джерело ролей.
/// </remarks>
public interface IRpcAuthorizationPolicy
{
    /// <summary>
    /// Чи задовольняє <paramref name="principal"/> вимогу <paramref name="requirement"/>.
    /// </summary>
    /// <param name="principal">Principal сесії (з mTLS-ідентичності), або <c>null</c>, якщо неавтентифікований.</param>
    /// <param name="requirement">Вимога авторизації з атрибута <see cref="RpcAuthorizeAttribute"/>.</param>
    /// <returns><c>true</c>, якщо виклик дозволено; інакше <c>false</c> (deny).</returns>
    bool IsAuthorized(ClaimsPrincipal? principal, RpcAuthorizeAttribute requirement);
}
