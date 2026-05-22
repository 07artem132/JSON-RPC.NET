using Microsoft.Extensions.DependencyInjection;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Services;

namespace WsRpcServer.Extensions;

/// <summary>
/// Методи розширення для реєстрації JSON-RPC сервісів у контейнері DI.
/// </summary>
public static class JsonRpcCoreExtensions
{
    /// <summary>
    /// Додає основні JSON-RPC сервіси до колекції сервісів.
    /// </summary>
    /// <param name="services">Колекція сервісів.</param>
    /// <param name="configureOptions">Опціональна дія для налаштування сервера.</param>
    /// <returns>Колекція сервісів для ланцюжка викликів.</returns>
    public static IServiceCollection AddJsonRpcCore(
        this IServiceCollection services,
        Action<JsonRpcServerConfig>? configureOptions = null)
    {
        // Налаштування опцій
        var config = new JsonRpcServerConfig();
        configureOptions?.Invoke(config);
        services.AddSingleton(config);


        return services;
    }
}