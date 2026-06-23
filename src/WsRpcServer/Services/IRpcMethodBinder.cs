using StreamJsonRpc;

namespace WsRpcServer.Services;

/// <summary>
/// Reflection-free прив'язувач RPC-методів до екземпляра <see cref="JsonRpc"/>.
/// </summary>
/// <remarks>
/// Реалізація генерується source-генератором у збірці споживача (коли та позначена
/// <see cref="GenerateRpcServiceCatalogAttribute"/>) і реєструється через <c>AddGeneratedRpcMethodBinder</c>.
/// Замість рефлексійного <c>JsonRpc.AddLocalRpcTarget(object)</c> (який сканує тип у рантаймі —
/// IL2026/IL3050) генерований binder викликає <c>JsonRpc.AddLocalRpcMethod(name, delegate)</c> на кожен
/// метод RPC-інтерфейсу, зв'язуючи делегат на етапі компіляції. Це робить ДИСПЕТЧ сумісним із Native AOT.
///
/// Якщо binder зареєстровано в DI, <see cref="AbstractRpcServiceRegistry.RegisterServices"/> використовує
/// його; інакше повертається до рефлексійного <c>AddLocalRpcTarget</c> (стара поведінка). Це окремий opt-in
/// від <see cref="IRpcServiceCatalog"/> (виявлення), бо delegate-шлях має інші компроміси, ніж
/// <c>AddLocalRpcTarget</c> (експонує лише методи інтерфейсу; без подій таргета / RpcMarshalable / тощо).
/// </remarks>
public interface IRpcMethodBinder
{
    /// <summary>
    /// Реєструє всі RPC-методи виявлених сервісів у наданому екземплярі <paramref name="jsonRpc"/>.
    /// </summary>
    /// <param name="jsonRpc">Екземпляр JSON-RPC, у якому реєструються методи.</param>
    /// <param name="serviceProvider">Постачальник сервісів для резолву звичайних сервісів та залежностей.</param>
    /// <param name="clientId">Ідентифікатор клієнта (для клієнт-залежних сервісів).</param>
    void Bind(JsonRpc jsonRpc, IServiceProvider serviceProvider, Guid clientId);
}
