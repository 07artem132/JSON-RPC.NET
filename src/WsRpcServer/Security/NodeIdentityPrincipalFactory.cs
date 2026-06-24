using System.Security.Claims;

namespace WsRpcServer.Security;

/// <summary>
/// Будує <see cref="ClaimsPrincipal"/> з валідованої <see cref="NodeIdentity"/>.
/// </summary>
/// <remarks>
/// Ім'я principal'а — <see cref="NodeIdentity.Name"/> (SPIFFE-id або SPKI-fallback). Тип
/// автентифікації <c>"mtls"</c> робить <see cref="System.Security.Principal.IIdentity.IsAuthenticated"/>
/// = <c>true</c> (важливо для deny-by-default: неавтентифікований principal завжди deny). SPKI
/// додається окремим claim'ом <c>"spki"</c> для діагностики/політик.
/// </remarks>
public static class NodeIdentityPrincipalFactory
{
    /// <summary>Тип claim'у для SPKI-SHA-256 відбитка.</summary>
    public const string SpkiClaimType = "spki";

    /// <summary>Тип автентифікації, що позначає mTLS-ідентичність.</summary>
    public const string AuthenticationType = "mtls";

    /// <summary>
    /// Створює автентифікований <see cref="ClaimsPrincipal"/> для ідентичності вузла.
    /// </summary>
    /// <param name="identity">Валідована ідентичність вузла.</param>
    /// <returns>Principal із claim'ами name + spki (+ SPIFFE-id, якщо є).</returns>
    public static ClaimsPrincipal Create(NodeIdentity identity)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, identity.Name),
            new(SpkiClaimType, identity.SpkiThumbprint),
        };

        if (identity.SpiffeId is not null)
        {
            claims.Add(new Claim(ClaimTypes.Uri, identity.SpiffeId.ToString()));
        }

        // Непорожній authenticationType → IsAuthenticated == true.
        var claimsIdentity = new ClaimsIdentity(claims, AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(claimsIdentity);
    }
}
