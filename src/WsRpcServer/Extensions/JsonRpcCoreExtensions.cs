using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Services;
using WsRpcServer.Sessions;

namespace WsRpcServer.Extensions;

/// <summary>
/// Методи розширення для реєстрації JSON-RPC сервісів у контейнері DI.
/// </summary>
public static class JsonRpcCoreExtensions
{
    /// <summary>
    /// Sentinel-маркер для ідемпотентності: гарантує, що базова реєстрація конфігурації
    /// виконується рівно один раз навіть при повторних викликах <see cref="AddJsonRpcCore"/>
    /// (H1 — патерн маркера реєстрації, як у SignalCli.NET).
    /// </summary>
    private sealed class JsonRpcCoreMarker;

    /// <summary>
    /// Додає базову конфігурацію JSON-RPC сервера до колекції сервісів (через options-pattern
    /// з fail-fast валідацією). Реєстрація конкретних реалізацій сервісів виконується через
    /// узагальнену перевантажену версію
    /// <see cref="AddJsonRpcCore{TServer,TSession,TEventProcessor,TSubscriptionManager,TRegistry}"/>.
    /// </summary>
    /// <param name="services">Колекція сервісів.</param>
    /// <param name="configureOptions">Опціональна дія для налаштування сервера.</param>
    /// <returns>Колекція сервісів для ланцюжка викликів.</returns>
    /// <remarks>
    /// Конфігурація реєструється через <see cref="JsonRpcServerConfig"/> у двох ролях:
    /// (1) як <c>IOptions&lt;JsonRpcServerConfig&gt;</c> з валідацією
    /// (<see cref="JsonRpcServerConfigValidator"/> + крос-польове правило для
    /// <see cref="JsonRpcServerConfig.NotificationTimeout"/>); (2) як прямо резолвабельний
    /// <see cref="JsonRpcServerConfig"/> (значення з валідованих опцій) — для зворотної сумісності
    /// з кодом, що ін'єктить конфіг напряму. Повторний виклик ідемпотентний (M5/H1).
    /// </remarks>
    public static IServiceCollection AddJsonRpcCore(
        this IServiceCollection services,
        Action<JsonRpcServerConfig>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Ідемпотентність: повторний виклик не дублює реєстрації (H1).
        if (services.Any(d => d.ServiceType == typeof(JsonRpcCoreMarker)))
        {
            return services;
        }

        services.AddSingleton<JsonRpcCoreMarker>();

        // M5: конфігурація проходить через options-pipeline з fail-fast валідацією.
        services.AddOptionsWithValidateOnStart<JsonRpcServerConfig>()
            .Configure(options => configureOptions?.Invoke(options))
            .Validate(
                config => config.NotificationTimeout > TimeSpan.Zero,
                "NotificationTimeout має бути додатним.");

        // Source-gen валідатор DataAnnotations ([Range]/[Required]) — без рефлексії, AOT-сумісний.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<JsonRpcServerConfig>, JsonRpcServerConfigValidator>());

        // Back-compat: JsonRpcServerConfig лишається резолвабельним напряму (значення з валідованих опцій).
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<JsonRpcServerConfig>>().Value);

        return services;
    }

    /// <summary>
    /// Додає основні JSON-RPC сервіси разом із конкретними реалізаціями сервера, сесії, обробника
    /// подій, менеджера підписок та реєстру сервісів. Це повний композиційний корінь — споживачу
    /// більше не потрібно вручну зв'язувати п'ять сервісів і конструювати сервер (H1).
    /// </summary>
    /// <typeparam name="TServer">Конкретний тип сервера (нащадок <see cref="AbstractJsonRpcServer"/>).</typeparam>
    /// <typeparam name="TSession">Конкретний тип сесії (нащадок <see cref="AbstractJsonRpcSession"/>).</typeparam>
    /// <typeparam name="TEventProcessor">Конкретний тип обробника подій (<see cref="IEventProcessor"/>).</typeparam>
    /// <typeparam name="TSubscriptionManager">Конкретний тип менеджера підписок (<see cref="ISubscriptionManager"/>).</typeparam>
    /// <typeparam name="TRegistry">Конкретний тип реєстру RPC-сервісів (<see cref="IRpcServiceRegistry"/>).</typeparam>
    /// <param name="services">Колекція сервісів.</param>
    /// <param name="configureOptions">Опціональна дія для налаштування сервера.</param>
    /// <returns>Колекція сервісів для ланцюжка викликів.</returns>
    /// <remarks>
    /// Обробник подій та менеджер підписок реєструються за патерном "один екземпляр — дві ролі":
    /// конкретний тип як singleton, а інтерфейс резолвиться як той самий екземпляр. Усі реєстрації
    /// виконуються через <c>TryAdd*</c>, тож виклик ідемпотентний і не перетирає вже наявних
    /// реєстрацій (споживач може зареєструвати власну реалізацію до виклику цього методу).
    /// </remarks>
    public static IServiceCollection AddJsonRpcCore<TServer, TSession, TEventProcessor, TSubscriptionManager, TRegistry>(
        this IServiceCollection services,
        Action<JsonRpcServerConfig>? configureOptions = null)
        where TServer : AbstractJsonRpcServer
        where TSession : AbstractJsonRpcSession
        where TEventProcessor : class, IEventProcessor
        where TSubscriptionManager : class, ISubscriptionManager
        where TRegistry : class, IRpcServiceRegistry
    {
        ArgumentNullException.ThrowIfNull(services);

        // Базова реєстрація конфігурації + ідемпотентність.
        services.AddJsonRpcCore(configureOptions);

        // "Один екземпляр — дві ролі": конкретний тип і інтерфейс резолвляться як той самий singleton.
        services.TryAddSingleton<TEventProcessor>();
        services.TryAddSingleton<IEventProcessor>(sp => sp.GetRequiredService<TEventProcessor>());

        services.TryAddSingleton<TSubscriptionManager>();
        services.TryAddSingleton<ISubscriptionManager>(sp => sp.GetRequiredService<TSubscriptionManager>());

        services.TryAddSingleton<IRpcServiceRegistry, TRegistry>();

        services.TryAddTransient<TSession>();

        // Конкретний сервер: будуємо з валідованої конфігурації (Host → IPAddress).
        services.TryAddSingleton(sp =>
        {
            var config = sp.GetRequiredService<JsonRpcServerConfig>();
            var ipAddress = IPAddress.Parse(config.Host);
            var logger = sp.GetRequiredService<ILogger<TServer>>();
            return ActivatorUtilities.CreateInstance<TServer>(
                sp, ipAddress, config.Port, sp, logger);
        });

        return services;
    }
}
