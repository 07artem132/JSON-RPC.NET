using WsRpcServer.Security;
using Xunit;

namespace WsRpcServer.Tests.Security;

/// <summary>
/// Guard для `mtls-node-identity`: <see cref="SpiffeNodeIdentityResolver"/> бере SAN URI як ім'я,
/// з SPKI-fallback; <see cref="NodeIdentityPrincipalFactory"/> будує автентифікований principal.
/// </summary>
public sealed class NodeIdentityResolverTests
{
    private readonly SpiffeNodeIdentityResolver _resolver = new();

    [Fact]
    public void Resolve_WithSanUri_UsesItAsSpiffeIdAndName()
    {
        using var ca = TestCertificates.CreateCa();
        const string spiffe = "spiffe://example.org/service/billing";
        using var leaf = TestCertificates.CreateLeaf(ca, sanUri: spiffe);

        var identity = _resolver.Resolve(leaf);

        Assert.Equal(spiffe, identity.SpiffeId?.ToString());
        Assert.Equal(spiffe, identity.Name);
        Assert.False(string.IsNullOrEmpty(identity.SpkiThumbprint));
    }

    [Fact]
    public void Resolve_WithoutSan_FallsBackToSpki()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.CreateLeaf(ca); // без SAN

        var identity = _resolver.Resolve(leaf);

        Assert.Null(identity.SpiffeId);
        Assert.Equal(identity.SpkiThumbprint, identity.Name);
        Assert.Equal(NodeIdentity.ComputeSpkiThumbprint(leaf), identity.SpkiThumbprint);
    }

    [Fact]
    public void PrincipalFactory_ProducesAuthenticatedPrincipal_WithSpiffeName()
    {
        using var ca = TestCertificates.CreateCa();
        const string spiffe = "spiffe://example.org/service/billing";
        using var leaf = TestCertificates.CreateLeaf(ca, sanUri: spiffe);

        var principal = NodeIdentityPrincipalFactory.Create(_resolver.Resolve(leaf));

        Assert.True(principal.Identity?.IsAuthenticated);
        Assert.Equal(spiffe, principal.Identity?.Name);
    }
}
