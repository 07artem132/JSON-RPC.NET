using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WsRpcServer.Extensions;
using WsRpcServer.Security;
using Xunit;

namespace WsRpcServer.Tests.Security;

/// <summary>
/// Guard для `tls-transport`: <see cref="TlsServerOptions"/> валідується fail-fast (немає серверного
/// сертифіката / немає приватного ключа / mTLS без CA → <see cref="OptionsValidationException"/>), а
/// зібраний <see cref="SecureTransport"/> узгоджує лише TLS 1.3/1.2.
/// </summary>
public sealed class TlsServerOptionsValidationTests
{
    private static ServiceProvider Build(Action<TlsServerOptions>? configureTls)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSecureTransport(configureTls);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Resolve_WithoutServerCertificate_Throws()
    {
        using var sp = Build(configureTls: null);

        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<TlsServerOptions>>().Value);
        Assert.Contains("ServerCertificate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_MtlsWithoutTrustedRoots_Throws()
    {
        using var server = TestCertificates.CreateServerCertificate();
        using var sp = Build(o =>
        {
            o.ServerCertificate = server;
            o.ClientCertificateRequired = true; // mTLS, але без TrustedRoots
        });

        Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<TlsServerOptions>>().Value);
    }

    [Fact]
    public void Resolve_WithValidServerCertificate_Succeeds()
    {
        using var server = TestCertificates.CreateServerCertificate();
        using var sp = Build(o =>
        {
            o.ServerCertificate = server;
            o.ClientCertificateRequired = false; // суто серверний TLS
        });

        var options = sp.GetRequiredService<IOptions<TlsServerOptions>>().Value;
        Assert.NotNull(options.ServerCertificate);
    }

    [Fact]
    public void SecureTransport_NegotiatesTls13And12_Only()
    {
        using var server = TestCertificates.CreateServerCertificate();
        using var ca = TestCertificates.CreateCa();
        using var sp = Build(o =>
        {
            o.ServerCertificate = server;
            o.TrustedRoots = [ca];
        });

        var transport = sp.GetRequiredService<SecureTransport>();

        // Точна рівність гарантує, що жоден старіший протокол не ввімкнено.
        Assert.Equal(SslProtocols.Tls13 | SslProtocols.Tls12, transport.Context.Protocols);
        Assert.True(transport.Context.ClientCertificateRequired);
    }
}
