namespace WsRpcServer.Services;

/// <summary>
/// Описує один RPC-сервіс для реєстрації: інтерфейс, його реалізація та чи є він клієнт-залежним.
/// Це reflection-free представлення результату виявлення сервісів — заповнюється або
/// source-генератором (compile-time, AOT-сумісно), або рефлексійним скануванням (fallback).
/// </summary>
/// <param name="InterfaceType">Тип RPC-інтерфейсу (нащадок <see cref="IRpcService"/>).</param>
/// <param name="ImplementationType">Конкретний клас, що реалізує <paramref name="InterfaceType"/>.</param>
/// <param name="IsClientAware">
/// <c>true</c>, якщо інтерфейс наслідується від <see cref="IClientAwareRpcService"/> — такий сервіс
/// створюється для кожного клієнта (через <c>ActivatorUtilities</c> з передачею clientId), а не
/// резолвиться як спільний екземпляр з DI.
/// </param>
public readonly record struct RpcServiceDescriptor(
    Type InterfaceType,
    Type ImplementationType,
    bool IsClientAware);
