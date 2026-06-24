namespace WsRpcServer.Authorization;

/// <summary>
/// Позначає RPC-метод (або весь RPC-інтерфейс) як такий, що потребує авторизації.
/// </summary>
/// <remarks>
/// Авторизація — deny-by-default ДЛЯ ПОЗНАЧЕНИХ методів: principal без потрібної ролі отримує
/// <see cref="Exceptions.RpcErrorException"/> (код <c>-32001</c>) ще до виконання тіла методу.
/// Методи без цього атрибута лишаються необмеженими (можливість суто additive).
///
/// Атрибут можна ставити на інтерфейс (застосовується до всіх його методів) або на окремий метод.
/// Перевірка виконується на обох шляхах диспетчу: рефлексійному (<see cref="AuthorizingJsonRpc"/>)
/// та source-генерованому (<see cref="Services.IRpcMethodBinder"/>).
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Interface, AllowMultiple = false, Inherited = true)]
public sealed class RpcAuthorizeAttribute : Attribute
{
    /// <summary>
    /// Кома-розділений перелік ролей, БУДЬ-ЯКА з яких дозволяє виклик. Порожньо/<c>null</c> —
    /// вимагається лише автентифікований principal (будь-яка ідентичність).
    /// </summary>
    public string? Roles { get; set; }

    /// <summary>
    /// Розбирає <see cref="Roles"/> на нормалізований набір (без порожніх, trimmed).
    /// </summary>
    /// <returns>Масив необхідних ролей (можливо порожній).</returns>
    public string[] GetRequiredRoles() =>
        string.IsNullOrWhiteSpace(Roles)
            ? []
            : Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
