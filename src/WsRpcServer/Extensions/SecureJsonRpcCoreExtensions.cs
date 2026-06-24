using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WsRpcServer.Authorization;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Security;
using WsRpcServer.Services;
using WsRpcServer.Sessions;

namespace WsRpcServer.Extensions;

/// <summary>
/// Композиційний корінь для ЗАХИЩЕНОГО (TLS / mTLS) JSON-RPC сервера. Паралельний до
/// <see cref="JsonRpcCoreExtensions.AddJsonRpcCore"/> — плейн-текст шлях лишається незмінним.
/// </summary>
public static class SecureJsonRpcCoreExtensions
{
    private sealed class SecureJsonRpcCoreMarker;

    /// <summary>
    /// Реєструє валідовані <see cref="TlsServerOptions"/> + типові mTLS-сервіси (валідатор сертифіката,
    /// резолвер ідентичності, політику авторизації) та <see cref="SecureTransport"/>.
    /// </summary>
    /// <param name="services">Колекція сервісів.</param>
    /// <param name="configureTls">Дія налаштування TLS-опцій (серверний сертифікат обов'язковий).</param>
    /// <returns>Колекція сервісів для ланцюжка.</returns>
    /// <remarks>
    /// <see cref="TlsServerOptions"/> валідується fail-fast (source-gen <see cref="TlsServerOptionsValidator"/>
    /// для <c>[Required]</c> + крос-польове правило «серверний сертифікат має приватний ключ»). Невалідна
    /// конфігурація кидає <see cref="OptionsValidationException"/> на резолві, а не глибоко в рукостисканні.
    /// Усі реєстрації — <c>TryAdd*</c>, тож споживач може підмінити будь-який тип своїм до виклику.
    /// </remarks>
    public static IServiceCollection AddSecureTransport(
        this IServiceCollection services,
        Action<TlsServerOptions>? configureTls = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(SecureJsonRpcCoreMarker)))
        {
            return services;
        }

        services.AddSingleton<SecureJsonRpcCoreMarker>();

        services.AddOptionsWithValidateOnStart<TlsServerOptions>()
            .Configure(options => configureTls?.Invoke(options))
            .Validate(
                o => o.ServerCertificate is null || o.ServerCertificate.HasPrivateKey,
                "TlsServerOptions.ServerCertificate має містити приватний ключ.")
            .Validate(
                o => !o.ClientCertificateRequired || o.TrustedRoots.Count > 0,
                "Для mTLS (ClientCertificateRequired=true) потрібен хоча б один TrustedRoots (приватний CA).");

        // Source-gen валідатор DataAnnotations ([Required] на ServerCertificate) — без рефлексії.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<TlsServerOptions>, TlsServerOptionsValidator>());

        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<TlsServerOptions>>().Value);

        // Типові mTLS-сервіси (споживач може підмінити будь-який через попередню реєстрацію).
        services.TryAddSingleton<INodeIdentityResolver, SpiffeNodeIdentityResolver>();
        services.TryAddSingleton<INodeCertificateValidator>(sp =>
        {
            var opts = sp.GetRequiredService<TlsServerOptions>();
            var logger = sp.GetRequiredService<ILogger<CustomRootTrustValidator>>();
            return new CustomRootTrustValidator(opts.TrustedRoots, opts.SpkiPins, opts.RevocationMode, logger);
        });

        // Типова політика авторизації: порожня статична мапа (deny для будь-якого [RpcAuthorize] з ролями).
        services.TryAddSingleton<IRpcAuthorizationPolicy>(_ => new StaticRoleMapAuthorizationPolicy());

        // Зібраний транспорт (SslContext + кореляція ідентичності) — будується один раз.
        services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<TlsServerOptions>();
            var validator = sp.GetRequiredService<INodeCertificateValidator>();
            var resolver = sp.GetRequiredService<INodeIdentityResolver>();
            var logger = sp.GetRequiredService<ILogger<SecureTransport>>();
            return SecureTransport.Create(opts, validator, resolver, logger);
        });

        return services;
    }

    /// <summary>
    /// Повний композиційний корінь захищеного сервера: усі core-сервіси + захищений сервер із TLS/mTLS.
    /// </summary>
    /// <typeparam name="TServer">Конкретний захищений сервер (нащадок <see cref="AbstractSecureJsonRpcServer"/>).</typeparam>
    /// <typeparam name="TSession">Конкретна захищена сесія (нащадок <see cref="AbstractSecureJsonRpcSession"/>).</typeparam>
    /// <typeparam name="TEventProcessor">Обробник подій.</typeparam>
    /// <typeparam name="TSubscriptionManager">Менеджер підписок.</typeparam>
    /// <typeparam name="TRegistry">Реєстр RPC-сервісів.</typeparam>
    /// <typeparam name="TEventType">Тип події.</typeparam>
    /// <typeparam name="TEventArgs">Тип аргументів події.</typeparam>
    /// <param name="services">Колекція сервісів.</param>
    /// <param name="configureOptions">Налаштування <see cref="JsonRpcServerConfig"/>.</param>
    /// <param name="configureTls">Налаштування <see cref="TlsServerOptions"/>.</param>
    /// <returns>Колекція сервісів для ланцюжка.</returns>
    public static IServiceCollection AddSecureJsonRpcCore<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TServer,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSession,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TEventProcessor,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSubscriptionManager,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TRegistry,
        TEventType,
        TEventArgs>(
        this IServiceCollection services,
        Action<JsonRpcServerConfig>? configureOptions = null,
        Action<TlsServerOptions>? configureTls = null)
        where TServer : AbstractSecureJsonRpcServer
        where TSession : AbstractSecureJsonRpcSession
        where TEventProcessor : class, IEventProcessor
        where TSubscriptionManager : class, ISubscriptionManager<TEventType, TEventArgs>
        where TRegistry : class, IRpcServiceRegistry
    {
        ArgumentNullException.ThrowIfNull(services);

        // Базова конфігурація (config-only overload) + TLS-транспорт.
        services.AddJsonRpcCore(configureOptions);
        services.AddSecureTransport(configureTls);

        services.TryAddSingleton<TEventProcessor>();
        services.TryAddSingleton<IEventProcessor>(sp => sp.GetRequiredService<TEventProcessor>());

        services.TryAddSingleton<TSubscriptionManager>();
        services.TryAddSingleton<ISubscriptionManager<TEventType, TEventArgs>>(sp => sp.GetRequiredService<TSubscriptionManager>());

        services.TryAddSingleton<IRpcServiceRegistry, TRegistry>();

        services.TryAddTransient<TSession>();

        // Захищений сервер: будуємо з валідованої конфігурації + зібраного транспорту.
        services.TryAddSingleton(sp =>
        {
            var config = sp.GetRequiredService<JsonRpcServerConfig>();
            var transport = sp.GetRequiredService<SecureTransport>();
            var ipAddress = IPAddress.Parse(config.Host);
            var logger = sp.GetRequiredService<ILogger<TServer>>();
            return ActivatorUtilities.CreateInstance<TServer>(sp, transport, ipAddress, config.Port, sp, logger);
        });

        return services;
    }
}
