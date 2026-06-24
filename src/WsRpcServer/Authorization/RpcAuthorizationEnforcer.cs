using System.Security.Claims;
using Microsoft.Extensions.Logging;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using WsRpcServer.Logging;

namespace WsRpcServer.Authorization;

/// <summary>
/// Єдина точка примусу авторизації для ОБОХ шляхів диспетчу (рефлексійного та source-генерованого
/// binder'а). Кидає <see cref="RpcErrorException"/> з кодом <c>-32001</c> при відмові — до запуску тіла методу.
/// </summary>
/// <remarks>
/// Тримати перевірку в одному місці критично: інакше два шляхи диспетчу могли б розійтися в семантиці
/// «дозволено/відмовлено». Binder викликає цей метод у голові згенерованого делегата, а рефлексійний
/// <see cref="AuthorizingJsonRpc"/> — у <c>DispatchRequestAsync</c>.
/// </remarks>
public static class RpcAuthorizationEnforcer
{
    /// <summary>Код помилки JSON-RPC для відмови в авторизації (рівень застосунку).</summary>
    public const int UnauthorizedErrorCode = -32001;

    /// <summary>
    /// Примушує авторизацію: якщо <paramref name="policy"/> відмовляє (або відсутній), кидає
    /// <see cref="RpcErrorException"/> (<c>-32001</c>).
    /// </summary>
    /// <param name="policy">Політика авторизації (з DI). <c>null</c> для позначеного методу = deny (fail-closed).</param>
    /// <param name="principal">Principal сесії.</param>
    /// <param name="requirement">Вимога авторизації з атрибута.</param>
    /// <param name="methodName">Ім'я методу (для діагностики/логів).</param>
    /// <param name="logger">Опційний логер.</param>
    /// <exception cref="RpcErrorException">Якщо авторизація відмовлена.</exception>
    public static void Enforce(
        IRpcAuthorizationPolicy? policy,
        ClaimsPrincipal? principal,
        RpcAuthorizeAttribute requirement,
        string methodName,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        // Fail-closed: позначений метод без зареєстрованої політики авторизувати неможливо → відмова.
        bool allowed = policy is not null && policy.IsAuthorized(principal, requirement);
        if (allowed)
        {
            return;
        }

        if (logger is not null)
        {
            RpcAuthorizationLog.AuthorizationDenied(logger, methodName, principal?.Identity?.Name ?? "(анонім)");
        }

        throw new RpcErrorException(
            (JsonRpcErrorCode)UnauthorizedErrorCode,
            $"Доступ до методу '{methodName}' заборонено: недостатньо прав.");
    }
}
