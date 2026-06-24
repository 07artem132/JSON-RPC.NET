using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NetCoreServer;
using WsRpcServer.Logging;

namespace WsRpcServer.Security;

/// <summary>
/// Зібраний TLS-транспорт: побудований один раз <see cref="SslContext"/> плюс кореляція
/// «з'єднання → <see cref="NodeIdentity"/>», заповнювана під час mTLS-рукостискання.
/// </summary>
/// <remarks>
/// NetCoreServer ставить ОДИН <c>RemoteCertificateValidationCallback</c> на весь
/// <see cref="SslContext"/>; його <c>sender</c> — це <see cref="System.Net.Security.SslStream"/>
/// конкретної сесії. Тому валідовану ідентичність ми кладемо у
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> за ключем-потоком, а сесія дістає її у
/// <c>OnWsConnected</c> (після рукостискання). Слабкі ключі гарантують, що запис зникає разом із
/// потоком — без витоку пам'яті.
///
/// Сам <see cref="SslContext"/> + ланцюг сертифіката будуються РІВНО ОДИН раз у
/// <see cref="Create"/> (побудова — CPU-затратна).
/// </remarks>
public sealed class SecureTransport
{
    private readonly ConditionalWeakTable<object, StrongBox<NodeIdentity>> _identities;

    private SecureTransport(SslContext context, ConditionalWeakTable<object, StrongBox<NodeIdentity>> identities)
    {
        Context = context;
        _identities = identities;
    }

    /// <summary>Побудований <see cref="SslContext"/> для <see cref="Core.AbstractSecureJsonRpcServer"/>.</summary>
    public SslContext Context { get; }

    /// <summary>
    /// Будує <see cref="SecureTransport"/> з валідованих опцій: <see cref="SslContext"/> із колбеком
    /// валідації клієнтського сертифіката (mTLS) поверх <paramref name="validator"/> та
    /// <paramref name="resolver"/>.
    /// </summary>
    /// <param name="options">Валідовані TLS-опції (серверний сертифікат, протоколи, вимога клієнт-серта).</param>
    /// <param name="validator">Валідатор клієнтського сертифіката (mTLS).</param>
    /// <param name="resolver">Резолвер ідентичності вузла з валідованого сертифіката.</param>
    /// <param name="logger">Логер.</param>
    /// <returns>Готовий транспорт.</returns>
    public static SecureTransport Create(
        TlsServerOptions options,
        INodeCertificateValidator validator,
        INodeIdentityResolver resolver,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);

        if (options.ServerCertificate is null)
        {
            throw new ArgumentException("TlsServerOptions.ServerCertificate є обов'язковим.", nameof(options));
        }

        // Колбек і сесії читають/пишуть ОДНУ й ту саму таблицю (замикання + поле екземпляра).
        var identities = new ConditionalWeakTable<object, StrongBox<NodeIdentity>>();

        RemoteCertificateValidationCallback? callback = null;
        if (options.ClientCertificateRequired)
        {
            callback = (sender, certificate, chain, sslPolicyErrors) =>
            {
                var cert = certificate as X509Certificate2
                           ?? (certificate is not null
                               ? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData())
                               : null);

                if (!validator.Validate(cert, chain, sslPolicyErrors))
                {
                    return false;
                }

                var identity = resolver.Resolve(cert!);
                NodeCertificateValidatorLog.NodeIdentityResolved(logger, identity.Name, identity.SpkiThumbprint);

                if (sender is not null)
                {
                    identities.AddOrUpdate(sender, new StrongBox<NodeIdentity>(identity));
                }

                return true;
            };
        }

        var context = callback is not null
            ? new SslContext(options.SslProtocols, options.ServerCertificate, callback)
            : new SslContext(options.SslProtocols, options.ServerCertificate);

        context.ClientCertificateRequired = options.ClientCertificateRequired;

        return new SecureTransport(context, identities);
    }

    /// <summary>
    /// Спроба дістати валідовану ідентичність вузла для з'єднання за його SSL-потоком.
    /// </summary>
    /// <param name="sslStream">SSL-потік сесії (ключ кореляції).</param>
    /// <param name="identity">Виведена ідентичність, якщо знайдена.</param>
    /// <returns><c>true</c>, якщо ідентичність знайдено.</returns>
    public bool TryGetIdentity(object? sslStream, out NodeIdentity identity)
    {
        if (sslStream is not null && _identities.TryGetValue(sslStream, out var box))
        {
            identity = box.Value;
            return true;
        }

        identity = default;
        return false;
    }
}
