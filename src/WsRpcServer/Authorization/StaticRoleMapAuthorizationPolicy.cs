using System.Security.Claims;

namespace WsRpcServer.Authorization;

/// <summary>
/// Типова <see cref="IRpcAuthorizationPolicy"/>: ролі беруться зі статичної мапи
/// «ім'я ідентичності (SPIFFE-id / SPKI) → ролі», постаченої споживачем.
/// </summary>
/// <remarks>
/// Кодування ролей у мапі (а не в сертифікаті) розв'язує зміну ролей із переоформленням сертифіката.
/// Принцип авторизації:
/// <list type="bullet">
///   <item>немає principal'а (неавтентифікований) → завжди deny;</item>
///   <item>вимога без ролей → дозволено будь-якому автентифікованому principal'у;</item>
///   <item>вимога з ролями → дозволено, якщо principal має БУДЬ-ЯКУ з них (об'єднання
///         role-claim'ів principal'а та ролей з мапи за іменем).</item>
/// </list>
/// </remarks>
public sealed class StaticRoleMapAuthorizationPolicy : IRpcAuthorizationPolicy
{
    private readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> _roleMap;

    /// <summary>
    /// Створює політику зі статичною мапою ідентичність → ролі.
    /// </summary>
    /// <param name="roleMap">Мапа «ім'я ідентичності → набір ролей». Може бути порожньою.</param>
    public StaticRoleMapAuthorizationPolicy(IReadOnlyDictionary<string, IReadOnlyCollection<string>>? roleMap = null)
    {
        _roleMap = roleMap ?? new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool IsAuthorized(ClaimsPrincipal? principal, RpcAuthorizeAttribute requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        // Deny-by-default: без автентифікованої ідентичності виклик позначеного методу заборонено.
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return false;
        }

        var requiredRoles = requirement.GetRequiredRoles();
        if (requiredRoles.Length == 0)
        {
            // Достатньо бути автентифікованим.
            return true;
        }

        var effectiveRoles = CollectRoles(principal);
        return requiredRoles.Any(r => effectiveRoles.Contains(r));
    }

    /// <summary>
    /// Об'єднує ролі principal'а (role-claim'и) з ролями зі статичної мапи за його іменем.
    /// </summary>
    private HashSet<string> CollectRoles(ClaimsPrincipal principal)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var claim in principal.FindAll(ClaimTypes.Role))
        {
            roles.Add(claim.Value);
        }

        var name = principal.Identity?.Name;
        if (name is not null && _roleMap.TryGetValue(name, out var mapped))
        {
            foreach (var role in mapped)
            {
                roles.Add(role);
            }
        }

        return roles;
    }
}
