using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using WsRpcServer.Generated;
using WsRpcServer.Services;

// Opt-in: генератор випромінить IRpcServiceCatalog + IRpcMethodBinder (+ DI-розширення) для цієї збірки.
[assembly: GenerateRpcServiceCatalog]

namespace AotSmoke;

// Звичайний RPC-сервіс (резолвиться з DI). Має реальний метод — щоб binder згенерував AddLocalRpcMethod.
public interface IPingRpc : IRpcService
{
    string Echo(string message);
}

public sealed class PingRpc : IPingRpc
{
    public string Echo(string message) => "echo:" + message;
}

// Клієнт-залежний сервіс (інстанціюється на клієнта; ctor приймає clientId).
public interface ISessionRpc : IClientAwareRpcService
{
    string WhoAmI();
}

public sealed class SessionRpc(Guid clientId) : ISessionRpc
{
    public string WhoAmI() => clientId.ToString();
}

/// <summary>Конкретний реєстр для доказу (відкриває кількість виявлених сервісів).</summary>
internal sealed class SmokeRegistry(IServiceProvider serviceProvider, Microsoft.Extensions.Logging.ILogger logger)
    : AbstractRpcServiceRegistry(serviceProvider, logger)
{
    protected override IEnumerable<string> GetAdditionalAssemblyPrefixes() => [];

    public int DiscoveredCount()
    {
        var cache = GetServiceTypeCache();
        return cache.RegularServices.Count + cache.ClientAwareServices.Count;
    }
}

internal static class Program
{
    public static async Task<int> Main()
    {
        var services = new ServiceCollection();

        // Reflection-free виявлення (каталог) + reflection-free диспетч (binder) — обидва source-генеровані.
        services.AddGeneratedRpcServiceCatalog();
        services.AddGeneratedRpcMethodBinder();
        services.AddSingleton<IPingRpc, PingRpc>();

        await using var sp = services.BuildServiceProvider();
        var clientId = Guid.NewGuid();

        // 1) Виявлення через каталог (без рефлексії).
        var registry = new SmokeRegistry(sp, NullLogger.Instance);
        int discovered = registry.DiscoveredCount();
        Console.WriteLine($"AOT smoke: виявлено {discovered} RPC-сервіс(и) через source-генерований каталог.");

        // 2) Диспетч: source-генерований binder реєструє КОЖЕН метод через JsonRpc.AddLocalRpcMethod
        //    (делегат прив'язано на етапі компіляції) — БЕЗ рефлексійного AddLocalRpcTarget. Саме це й
        //    робить нашу частину диспетчу Native-AOT-чистою. Прив'язку + StartListening виконуємо на
        //    реальному JsonRpc, аби довести, що шлях працює в нативному бінарнику.
        //
        //    Примітка: повноцінний серіалізаційний round-trip тут НЕ виконуємо навмисно — серіалізація
        //    payload'ів усередині StreamJsonRpc (та її дефолтний форматтер) у 2.21.69 не AOT-чиста
        //    (IL3053 на StreamJsonRpc.dll). Це окремий upstream-блокер payload'ів, а не нашого диспетчу.
        var binder = sp.GetRequiredService<IRpcMethodBinder>();
        Console.WriteLine($"AOT smoke: binder = {binder.GetType().FullName}");

        using var serverStream = new MemoryStream();
        using var serverRpc =
            new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, new SystemTextJsonFormatter()));

        binder.Bind(serverRpc, sp, clientId, principal: null);   // ← AddLocalRpcMethod на кожен метод, без рефлексії
        serverRpc.StartListening();

        bool ok = discovered == 2
                  && binder.GetType().FullName == "WsRpcServer.Generated.RpcMethodBinder";
        Console.WriteLine(ok
            ? "AOT smoke: диспетч прив'язано через source-генерований AddLocalRpcMethod (без рефлексії). OK"
            : "AOT smoke: НЕОЧІКУВАНО — binder або кількість сервісів не збігаються.");
        return ok ? 0 : 1;
    }
}
