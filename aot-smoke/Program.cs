using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WsRpcServer.Generated;
using WsRpcServer.Services;

// Opt-in: генератор випромінить IRpcServiceCatalog + AddGeneratedRpcServiceCatalog для цієї збірки.
[assembly: GenerateRpcServiceCatalog]

namespace AotSmoke;

// Демонстраційні RPC-сервіси — генератор виявляє їх на етапі компіляції (без рефлексії в рантаймі).
public interface IPingRpc : IRpcService;
public sealed class PingRpc : IPingRpc;

public interface ISessionRpc : IClientAwareRpcService;
public sealed class SessionRpc : ISessionRpc;

/// <summary>Конкретний реєстр, що відкриває кількість виявлених сервісів (для доказу).</summary>
internal sealed class SmokeRegistry(IServiceProvider serviceProvider, Microsoft.Extensions.Logging.ILogger logger)
    : AbstractRpcServiceRegistry(serviceProvider, logger)
{
    protected override IEnumerable<string> GetAdditionalAssemblyPrefixes() => [];

    // Тригерить побудову кешу. За наявності каталогу — суто catalog-шлях, БЕЗ рефлексійного сканування.
    public int DiscoveredCount()
    {
        var cache = GetServiceTypeCache();
        return cache.RegularServices.Count + cache.ClientAwareServices.Count;
    }
}

internal static class Program
{
    public static int Main()
    {
        var services = new ServiceCollection();

        // Source-генерований каталог (reflection-free виявлення).
        services.AddGeneratedRpcServiceCatalog();
        // Звичайний сервіс має бути резолвабельним із DI (як у реальному композиційному корені).
        services.AddSingleton<IPingRpc, PingRpc>();

        using var sp = services.BuildServiceProvider();

        var registry = new SmokeRegistry(sp, NullLogger.Instance);
        int discovered = registry.DiscoveredCount();

        Console.WriteLine(
            $"AOT smoke: виявлено {discovered} RPC-сервіс(и) через source-генерований каталог (без рефлексії).");

        // Очікуємо рівно 2: PingRpc (regular) + SessionRpc (client-aware).
        return discovered == 2 ? 0 : 1;
    }
}
