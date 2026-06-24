# Помилки — `RpcErrorException` + транспортні захисти

Як повернути клієнту структуровану JSON-RPC помилку з RPC-методу, і які захисти від зловмисного/битого
трафіку вшиті в транспорт.

---

## `RpcErrorException`

```csharp
public sealed class RpcErrorException : Exception
{
    public JsonRpcErrorCode ErrorCode { get; }
    public object? ErrorData { get; }

    public RpcErrorException(JsonRpcErrorCode errorCode, string message, Exception? innerException = null);
    public RpcErrorException(JsonRpcErrorCode errorCode, string message, object? errorData);
}
```

Кидай із RPC-методу — StreamJsonRpc автоматично перетворює його на JSON-RPC error-відповідь із
правильним `code`, `message` і (за наявності) `data`. `sealed` (L6, `low-severity-polish` 2.2.0).

- **`ErrorCode`** — `StreamJsonRpc.Protocol.JsonRpcErrorCode`. Стандартні коди JSON-RPC 2.0
  (−32768…−32000 зарезервовані): `ParseError` (−32700), `InvalidRequest` (−32600), `MethodNotFound`
  (−32601), `InvalidParams` (−32602), `InternalError` (−32603), `InvocationError`. Коди поза
  зарезервованим діапазоном — для прикладних помилок.
- **`ErrorData`** — будь-який серіалізовний об'єкт; іде у поле `data` error-відповіді.

### Два конструктори — за призначенням

```csharp
// 1) З inner-винятком (огорнути нижчележачий збій, зберегти причину):
throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Subscription failed", ex);

// 2) Зі структурованими data (віддати клієнту машиночитані деталі):
throw new RpcErrorException(
    JsonRpcErrorCode.InvalidParams,
    "Невірний формат параметра",
    new { Field = "date", ExpectedFormat = "yyyy-MM-dd" });
```

> ⚠ Перевантаження розрізняються **типом третього аргументу** (`Exception?` vs `object?`). `null`
> літералом це неоднозначно — або вкажи тип (`(Exception?)null`), або користуйся дволастковою формою
> `(errorCode, message)`-варіанта через `Exception?`-перевантаження з дефолтом.

Типове застосування — в адаптері навколо бізнес-логіки (з `example/SimpleServer`):

```csharp
public async Task<int> Subscribe(string topic, ServerEventType[] types, CancellationToken ct = default)
{
    try { return await subscriptionManager.Subscribe(clientId, topic, types, ct); }
    catch (Exception ex)
    {
        logger.LogError(ex, "Subscription failed");
        throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Subscription failed", ex);
    }
}
```

---

## Транспортні захисти (не виняток, а закриття з'єднання)

Не кожна помилка стає `RpcErrorException` — деякі класи трафіку фреймворк гасить на рівні транспорту
(`WebSocketMessageHandler`, див. [`server-and-session.md`](server-and-session.md)):

- **Bounded parse-recovery** (rule #5, H2, `security-hardening` 1.2.0). На невалідний JSON handler
  пробує відновитися (знайти наступний роздільник, не втратити решту буфера). Але після
  `JsonRpcServerConfig.MaxConsecutiveParseFailures` поспіль (дефолт **10**) невдалих розборів з'єднання
  **примусово закривається** з `WebSocketCloseStatus.ProtocolError`. Без цього зловмисник міг би слати
  потік сміття й тримати CPU в нескінченному recovery-циклі (single-connection DoS). Лічильник скидається
  після кожного успішного розбору.
- **`ProcessReceivedDataAsync` / `WriteCoreAsync` після `Dispose` → `ObjectDisposedException`** (R2-M2),
  замість витоку внутрішнього `InvalidOperationException` завершеного `PipeWriter`.
- **`CanRead`/`CanWrite => !_disposed`** (M9): StreamJsonRpc перестає кликати у звільнений handler.

Поведінкові межі (поріг розборів, симетрія disposal-помилок) закріплені регрес-тестами в
`tests/WsRpcServer.Tests/Transport/`.
