using StreamJsonRpc.Protocol;

namespace WsRpcServer.Exceptions;

/// <summary>
/// Спеціальний виняток для помилок RPC з підтримкою кодів помилок JSON-RPC.
/// Дозволяє передавати структуровану інформацію про помилки в RPC-відповідях.
/// </summary>
/// <remarks>
/// Цей клас розширює стандартний Exception, додаючи специфічну для JSON-RPC інформацію:
/// - Код помилки (згідно зі специфікацією JSON-RPC)
/// - Додаткові дані про помилку
/// 
/// Коли такий виняток виникає в RPC-методі, StreamJsonRpc автоматично перетворює його
/// на відповідне JSON-RPC повідомлення про помилку з правильним кодом та даними.
/// 
/// Це дозволяє клієнтам отримувати структуровані помилки та обробляти їх відповідно до типу.
/// </remarks>
public sealed class RpcErrorException : Exception
{
    /// <summary>
    /// Код помилки JSON-RPC.
    /// </summary>
    /// <remarks>
    /// Згідно зі специфікацією JSON-RPC 2.0, коди помилок в діапазоні від -32768 до -32000
    /// зарезервовані для стандартних помилок протоколу. Коди поза цим діапазоном
    /// можуть використовуватися для специфічних для застосунку помилок.
    /// 
    /// Стандартні коди:
    /// - -32700: Parse error (помилка розбору)
    /// - -32600: Invalid Request (неправильний запит)
    /// - -32601: Method not found (метод не знайдено)
    /// - -32602: Invalid params (неправильні параметри)
    /// - -32603: Internal error (внутрішня помилка)
    /// </remarks>
    public JsonRpcErrorCode ErrorCode { get; }

    /// <summary>
    /// Додаткові дані про помилку.
    /// </summary>
    /// <remarks>
    /// Можуть містити будь-який серіалізований об'єкт, який надає більше інформації
    /// про помилку. Наприклад, детальний опис помилки, трасування стеку,
    /// або специфічні для застосунку дані.
    /// 
    /// Ці дані будуть включені в поле "data" JSON-RPC відповіді про помилку.
    /// </remarks>
    public object? ErrorData { get; }

    /// <summary>
    /// Створює новий екземпляр винятку RPC помилки.
    /// </summary>
    /// <param name="errorCode">Код помилки JSON-RPC.</param>
    /// <param name="message">Повідомлення про помилку.</param>
    /// <param name="innerException">Внутрішній виняток, що спричинив цю помилку.</param>
    /// <remarks>
    /// Цей конструктор створює виняток без додаткових даних,
    /// використовуючи лише стандартні поля Exception.
    /// </remarks>
    public RpcErrorException(JsonRpcErrorCode errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Створює новий екземпляр винятку RPC помилки з додатковими даними.
    /// </summary>
    /// <param name="errorCode">Код помилки JSON-RPC.</param>
    /// <param name="message">Повідомлення про помилку.</param>
    /// <param name="errorData">Додаткові дані про помилку.</param>
    /// <remarks>
    /// Цей конструктор дозволяє включити в виняток структуровані дані,
    /// які будуть серіалізовані в JSON-RPC відповідь про помилку.
    /// 
    /// Приклад використання:
    /// ```csharp
    /// throw new RpcErrorException(
    ///     JsonRpcErrorCode.InvalidParams,
    ///     "Невірний формат дати",
    ///     new { Field = "birthDate", ExpectedFormat = "yyyy-MM-dd" }
    /// );
    /// ```
    /// </remarks>
    public RpcErrorException(JsonRpcErrorCode errorCode, string message, object? errorData)
        : base(message)
    {
        ErrorCode = errorCode;
        ErrorData = errorData;
    }
}