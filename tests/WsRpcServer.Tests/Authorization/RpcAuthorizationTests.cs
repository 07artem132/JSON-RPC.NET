using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc.Protocol;
using WsRpcServer.Authorization;
using WsRpcServer.Exceptions;
using Xunit;

namespace WsRpcServer.Tests.Authorization;

/// <summary>
/// Guard для `rpc-authorization`: deny-by-default для позначених методів через
/// <see cref="RpcAuthorizationEnforcer"/> + <see cref="StaticRoleMapAuthorizationPolicy"/>, а також
/// розв'язок <see cref="RpcAuthorizeAttribute"/> з інтерфейсу (<see cref="RpcAuthorizationMetadata"/>).
/// </summary>
public sealed class RpcAuthorizationTests
{
    private interface IGuardedRpc
    {
        [RpcAuthorize(Roles = "admin")]
        void Delete();

        void Ping();
    }

    private sealed class GuardedRpc : IGuardedRpc
    {
        public void Delete() { }
        public void Ping() { }
    }

    private static ClaimsPrincipal Principal(string name, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "mtls", ClaimTypes.Name, ClaimTypes.Role));
    }

    private static readonly RpcAuthorizeAttribute AdminRequired = new() { Roles = "admin" };

    [Fact]
    public void Enforce_NoPrincipal_ThrowsUnauthorized()
    {
        var ex = Assert.Throws<RpcErrorException>(() =>
            RpcAuthorizationEnforcer.Enforce(
                new StaticRoleMapAuthorizationPolicy(), principal: null, AdminRequired, "delete", NullLogger.Instance));

        Assert.Equal((JsonRpcErrorCode)RpcAuthorizationEnforcer.UnauthorizedErrorCode, ex.ErrorCode);
        Assert.Equal(-32001, (int)ex.ErrorCode);
    }

    [Fact]
    public void Enforce_PrincipalWithoutRole_ThrowsUnauthorized()
    {
        Assert.Throws<RpcErrorException>(() =>
            RpcAuthorizationEnforcer.Enforce(
                new StaticRoleMapAuthorizationPolicy(), Principal("svc", "reader"), AdminRequired, "delete"));
    }

    [Fact]
    public void Enforce_PrincipalWithRole_DoesNotThrow()
    {
        RpcAuthorizationEnforcer.Enforce(
            new StaticRoleMapAuthorizationPolicy(), Principal("svc", "admin"), AdminRequired, "delete");
    }

    [Fact]
    public void Enforce_NullPolicy_FailsClosed()
    {
        // Позначений метод без зареєстрованої політики → deny (fail-closed).
        Assert.Throws<RpcErrorException>(() =>
            RpcAuthorizationEnforcer.Enforce(policy: null, Principal("svc", "admin"), AdminRequired, "delete"));
    }

    [Fact]
    public void StaticRoleMap_GrantsRoleFromMap_ByIdentityName()
    {
        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["spiffe://acme/billing"] = ["admin"],
        };
        var policy = new StaticRoleMapAuthorizationPolicy(map);

        // Principal не має role-claim'а admin, але мапа за іменем додає його.
        Assert.True(policy.IsAuthorized(Principal("spiffe://acme/billing"), AdminRequired));
        Assert.False(policy.IsAuthorized(Principal("spiffe://acme/other"), AdminRequired));
    }

    [Fact]
    public void StaticRoleMap_EmptyRoles_RequiresOnlyAuthentication()
    {
        var policy = new StaticRoleMapAuthorizationPolicy();
        var noRoles = new RpcAuthorizeAttribute();

        Assert.True(policy.IsAuthorized(Principal("svc"), noRoles));
        Assert.False(policy.IsAuthorized(principal: null, noRoles));
    }

    [Fact]
    public void Metadata_ResolvesAuthorizeFromInterfaceMethod()
    {
        var deleteImpl = typeof(GuardedRpc).GetMethod(nameof(GuardedRpc.Delete))!;
        var pingImpl = typeof(GuardedRpc).GetMethod(nameof(GuardedRpc.Ping))!;

        var requirement = RpcAuthorizationMetadata.Resolve(deleteImpl);
        Assert.NotNull(requirement);
        Assert.Equal("admin", requirement!.Roles);

        // Непозначений метод → null (необмежений).
        Assert.Null(RpcAuthorizationMetadata.Resolve(pingImpl));
    }
}
