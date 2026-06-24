using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace WsRpcServer.Authorization;

/// <summary>
/// Розв'язує вимогу <see cref="RpcAuthorizeAttribute"/> для методу реалізації — шукаючи атрибут на
/// самому методі, його типі, та на відповідному методі/типі RPC-інтерфейсу.
/// </summary>
/// <remarks>
/// Атрибут зазвичай ставлять на методи ІНТЕРФЕЙСУ, а диспетч має <c>MethodInfo</c> РЕАЛІЗАЦІЇ
/// (<c>TargetMethod.TargetMethodInfo</c>), тож пряме <c>GetCustomAttribute</c> на impl-методі його не
/// побачить. Тут зіставляємо impl-метод із інтерфейсним через мапу інтерфейсів типу.
///
/// Це рефлексійний шлях (для AOT використовуй source-генерований binder, який читає атрибут на
/// етапі компіляції) — анотовано відповідно.
/// </remarks>
public static class RpcAuthorizationMetadata
{
    /// <summary>
    /// Знаходить застосовну вимогу авторизації для методу реалізації, або <c>null</c>, якщо її немає.
    /// </summary>
    /// <param name="implementationMethod">Метод реалізації (з диспетчу).</param>
    /// <returns>Вимога авторизації або <c>null</c>.</returns>
    [RequiresUnreferencedCode(
        "Рефлексійний розв'язок [RpcAuthorize] через мапу інтерфейсів несумісний із trimming; " +
        "для AOT використовуй source-генерований binder (читає атрибут на компіляції).")]
    public static RpcAuthorizeAttribute? Resolve(MethodInfo implementationMethod)
    {
        ArgumentNullException.ThrowIfNull(implementationMethod);

        // 1) Безпосередньо на методі реалізації.
        var direct = implementationMethod.GetCustomAttribute<RpcAuthorizeAttribute>(inherit: true);
        if (direct is not null)
        {
            return direct;
        }

        var declaringType = implementationMethod.DeclaringType;
        if (declaringType is null)
        {
            return null;
        }

        // 2) На відповідному методі інтерфейсу (та на самому інтерфейсі).
        foreach (var iface in declaringType.GetInterfaces())
        {
            InterfaceMapping map;
            try
            {
                map = declaringType.GetInterfaceMap(iface);
            }
            catch (ArgumentException)
            {
                continue;
            }

            for (int i = 0; i < map.TargetMethods.Length; i++)
            {
                if (map.TargetMethods[i] != implementationMethod)
                {
                    continue;
                }

                var ifaceMethod = map.InterfaceMethods[i];
                var onMethod = ifaceMethod.GetCustomAttribute<RpcAuthorizeAttribute>(inherit: true);
                if (onMethod is not null)
                {
                    return onMethod;
                }

                var onInterface = iface.GetCustomAttribute<RpcAuthorizeAttribute>(inherit: true);
                if (onInterface is not null)
                {
                    return onInterface;
                }
            }
        }

        // 3) На типі реалізації.
        return declaringType.GetCustomAttribute<RpcAuthorizeAttribute>(inherit: true);
    }
}
