namespace WsRpcServer.Services;

/// <summary>
/// Reflection-free каталог RPC-сервісів, виявлених на етапі компіляції.
/// </summary>
/// <remarks>
/// Реалізація цього інтерфейсу генерується source-генератором у збірці споживача, коли та позначена
/// <see cref="GenerateRpcServiceCatalogAttribute"/>. Якщо каталог зареєстровано в DI,
/// <see cref="AbstractRpcServiceRegistry"/> бере перелік сервісів саме з нього й НЕ виконує
/// рефлексійного сканування збірок (<c>GetExportedTypes</c>/<c>IsAssignableFrom</c> — IL2026/IL3050).
/// Це робить виявлення сервісів сумісним із trim/AOT.
///
/// Якщо каталог не зареєстровано, реєстр повертається до рефлексійного сканування (стара поведінка) —
/// тож наявні споживачі працюють без змін.
/// </remarks>
public interface IRpcServiceCatalog
{
    /// <summary>
    /// Перелік усіх виявлених RPC-сервісів (інтерфейс + реалізація + ознака клієнт-залежності).
    /// </summary>
    IReadOnlyList<RpcServiceDescriptor> Services { get; }
}
