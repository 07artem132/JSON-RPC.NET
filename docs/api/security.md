# Безпека транспорту — TLS, mTLS node-identity, RPC-авторизація

Захищений (TLS / mTLS) транспорт + авторизація RPC-методів. Усе **opt-in і additive**: плейн-текст
`AddJsonRpcCore<…>` лишається незмінним (CLAUDE.md rule #11, `secure-transport-mtls` 2.6.0). Без жодної
нової NuGet-залежності — TLS через NetCoreServer `WssServer`, валідація через BCL
`System.Security.Cryptography.X509Certificates`, авторизація через `System.Security.Claims`.

Три капабіліті:

1. **`tls-transport`** — `AbstractSecureJsonRpcServer` обслуговує `wss://` з валідованих `TlsServerOptions`.
2. **`mtls-node-identity`** — клієнтський сертифікат вимагається й валідується проти приватного CA;
   виведена `NodeIdentity` стає `ClaimsPrincipal` сесії.
3. **`rpc-authorization`** — `[RpcAuthorize]` примушується deny-by-default на обох шляхах диспетчу.

---

## `tls-transport`

### `AbstractSecureJsonRpcServer`

```csharp
public abstract class AbstractSecureJsonRpcServer : NetCoreServer.WssServer
{
    protected abstract WssSession CreateJsonRpcSession();
    public bool TryResolveNodeIdentity(WssSession session, out NodeIdentity identity);
}
```

Дзеркалить `AbstractJsonRpcServer`, але над SSL-гілкою NetCoreServer (`WssServer`/`WssSession`), бо
NetCoreServer розводить плейн-текст і TLS в окремі ієрархії без спільного WS-базового типу. Той самий
розширювальний шов `CreateJsonRpcSession()`. Конструктор:
`(SecureTransport transport, IPAddress address, int port, IServiceProvider sp, ILogger logger)`.

### `TlsServerOptions`

`record`, валідується fail-fast через options-pipeline (як `JsonRpcServerConfig`).

| Властивість | Тип | Дефолт | Опис |
|---|---|---|---|
| `ServerCertificate` | `X509Certificate2?` | — (`[Required]`) | Серверний сертифікат із приватним ключем. Без нього/без ключа — `OptionsValidationException` на резолві |
| `SslProtocols` | `SslProtocols` | `Tls13 \| Tls12` | Дозволені версії; старіші свідомо не вмикаються |
| `ClientCertificateRequired` | `bool` | `true` | Вимога клієнт-серта (mTLS) |
| `TrustedRoots` | `IReadOnlyCollection<X509Certificate2>` | `[]` | Приватний CA для `CustomRootTrust`. Для mTLS обов'язковий (крос-польове правило) |
| `SpkiPins` | `IReadOnlyCollection<string>` | `[]` | Опційний allowlist SPKI-SHA-256 пінів |
| `RevocationMode` | `X509RevocationMode` | `Offline` | Перевірка відкликання; `Online` = DoS-вектор у рукостисканні |

### `SecureTransport`

```csharp
public sealed class SecureTransport
{
    public static SecureTransport Create(TlsServerOptions options, INodeCertificateValidator validator,
        INodeIdentityResolver resolver, ILogger logger);
    public SslContext Context { get; }
    public bool TryGetIdentity(object? sslStream, out NodeIdentity identity);
}
```

Зібраний транспорт: `SslContext` (+ ланцюг сертифіката) будується **рівно один раз** (CPU-затратно),
плюс кореляція «з'єднання → `NodeIdentity`» через `ConditionalWeakTable` за ключем-`SslStream` (NetCoreServer
ставить один колбек валідації на весь `SslContext`, а його `sender` — це потік сесії). `SecureTransport`
реєструється singleton'ом у DI.

---

## `mtls-node-identity`

### `INodeCertificateValidator` + `CustomRootTrustValidator`

```csharp
public interface INodeCertificateValidator
{
    bool Validate(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors);
}
```

Викликається з `SslContext.RemoteCertificateValidationCallback` під час рукостискання. Типовий
`CustomRootTrustValidator` будує **власний** `X509Chain` з
`ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust` + `CustomTrustStore` (приватний CA, **ніколи**
машинне сховище), вимагає EKU `clientAuth` (OID 1.3.6.1.5.5.7.3.2), відхиляє на будь-якій помилці
ланцюга, та (опційно) звіряє SPKI-пін. **Ніколи не повертає `true` наосліп** — це сенс колбека
(пінить Roslyn-guard `CertificateValidationConventionTests`). Режим відкликання — `Offline` за дефолтом,
`NoCheck` лише зі свідомим `// justification:`.

### `NodeIdentity`

```csharp
public readonly record struct NodeIdentity(Uri? SpiffeId, string SpkiThumbprint, string Subject)
{
    public string Name { get; }                                       // SpiffeId ?? SpkiThumbprint
    public static string ComputeSpkiThumbprint(X509Certificate2 cert); // SHA-256 від SubjectPublicKeyInfo
}
```

SPKI-відбиток стабільний при переоформленні сертифіката з тим самим ключем (на відміну від `Thumbprint`).

### `INodeIdentityResolver` + `SpiffeNodeIdentityResolver`

```csharp
public interface INodeIdentityResolver { NodeIdentity Resolve(X509Certificate2 certificate); }
```

Типовий `SpiffeNodeIdentityResolver` бере перший **SAN URI** (SPIFFE-стиль, напр.
`spiffe://example.org/service/billing`) як ім'я principal'а, а SPKI-SHA-256 — як стабільний fallback-id,
коли SAN URI відсутній. Замінний — споживач може вивести ідентичність з іншого поля (напр. OU) без форку.

### `NodeIdentityPrincipalFactory`

```csharp
public static class NodeIdentityPrincipalFactory
{
    public static ClaimsPrincipal Create(NodeIdentity identity);  // authType "mtls" → IsAuthenticated=true
}
```

Будує `ClaimsPrincipal` із claim'ами `name` (= `NodeIdentity.Name`), `spki`, та SPIFFE-URI (якщо є).

### `AbstractSecureJsonRpcSession`

Захищена сесія (нащадок `WssSession`), що дзеркалить інфраструктуру сповіщень
`AbstractJsonRpcSession` і додає `protected ClaimsPrincipal? Principal`. У `OnWsConnected` похідний клас
викликає `TryEstablishPrincipal()` (резолвить `NodeIdentity` через сервер → `Principal`) **до**
`RegisterServices`/`StartListening`. Невалідне рукостискання обривається на TLS-рівні — диспетч недосяжний.

> `Principal` також додано на `AbstractJsonRpcSession` (плейн-текст: завжди `null`).

---

## `rpc-authorization`

### `RpcAuthorizeAttribute`

```csharp
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Interface)]
public sealed class RpcAuthorizeAttribute : Attribute { public string? Roles { get; set; } }
```

Позначає RPC-метод (чи весь інтерфейс) як такий, що потребує авторизації. **Deny-by-default для
позначених**: principal без потрібної ролі отримує `RpcErrorException(-32001)` до тіла методу.
Непозначені методи лишаються необмеженими (additive). `Roles` — кома-розділений перелік (БУДЬ-ЯКА
дозволяє); порожньо = достатньо автентифікації.

### `IRpcAuthorizationPolicy` + `StaticRoleMapAuthorizationPolicy`

```csharp
public interface IRpcAuthorizationPolicy
{
    bool IsAuthorized(ClaimsPrincipal? principal, RpcAuthorizeAttribute requirement);
}
```

Типова `StaticRoleMapAuthorizationPolicy` зіставляє ім'я ідентичності (SPIFFE-id / SPKI) зі статичною
мапою «ідентичність → ролі» (з конфігурації споживача) і об'єднує її з role-claim'ами principal'а.
Кодування ролей у мапі (а не в сертифікаті) розв'язує зміну ролей із переоформленням сертифіката.

### `RpcAuthorizationEnforcer`

```csharp
public static class RpcAuthorizationEnforcer
{
    public const int UnauthorizedErrorCode = -32001;
    public static void Enforce(IRpcAuthorizationPolicy? policy, ClaimsPrincipal? principal,
        RpcAuthorizeAttribute requirement, string methodName, ILogger? logger = null);
}
```

Єдина точка примусу для **обох** шляхів диспетчу. При відмові (включно з `policy == null` — fail-closed)
кидає `RpcErrorException` (`-32001`).

### `AuthorizingJsonRpc` + `RpcAuthorizationMetadata` (рефлексійний шлях)

`AuthorizingJsonRpc : JsonRpc` перехоплює `DispatchRequestAsync` і примушує `[RpcAuthorize]` перед
виконанням методу (коли сервіси зареєстровані через `AddLocalRpcTarget`). Споживач конструює
`new AuthorizingJsonRpc(handler, principal, policy, logger)` замість `new JsonRpc(handler)`.
`RpcAuthorizationMetadata.Resolve(MethodInfo)` знаходить застосовну вимогу (на методі реалізації, методі
інтерфейсу, чи на типі/інтерфейсі) через мапу інтерфейсів.

### Binder-шлях (AOT-clean)

Source-генерований `IRpcMethodBinder.Bind(jsonRpc, sp, clientId, principal)` випромінює виклик
`RpcAuthorizationEnforcer.Enforce(...)` у **голові** делегата кожного позначеного методу — атрибут
читається на компіляції, тож рантайм-перевірка лишається reflection-free (зберігає AOT-чистоту диспетчу).
Ідентична політика на обох шляхах.

### `SecureJsonRpcCoreExtensions`

```csharp
public static IServiceCollection AddSecureTransport(this IServiceCollection services,
    Action<TlsServerOptions>? configureTls = null);

public static IServiceCollection AddSecureJsonRpcCore<
    TServer, TSession, TEventProcessor, TSubscriptionManager, TRegistry, TEventType, TEventArgs>(
    this IServiceCollection services,
    Action<JsonRpcServerConfig>? configureOptions = null,
    Action<TlsServerOptions>? configureTls = null)
    where TServer : AbstractSecureJsonRpcServer
    where TSession : AbstractSecureJsonRpcSession
    where TEventProcessor : class, IEventProcessor
    where TSubscriptionManager : class, ISubscriptionManager<TEventType, TEventArgs>
    where TRegistry : class, IRpcServiceRegistry;
```

Паралельний композиційний корінь захищеного сервера. `AddSecureTransport` реєструє валідовані
`TlsServerOptions` + типові mTLS-сервіси (`INodeCertificateValidator` = `CustomRootTrustValidator`,
`INodeIdentityResolver` = `SpiffeNodeIdentityResolver`, `IRpcAuthorizationPolicy` =
`StaticRoleMapAuthorizationPolicy`) + `SecureTransport`. Усе через `TryAdd*` — споживач може підмінити
будь-який тип. Плейн-текст `AddJsonRpcCore<…>` не торкається.

```csharp
services.AddSecureJsonRpcCore<
    MySecureServer, MySecureSession, MyEventProcessor, MySubscriptionManager, MyRegistry,
    MyEventType, object>(
    o => { o.Host = "0.0.0.0"; o.Port = 9443; },
    tls =>
    {
        tls.ServerCertificate = serverCert;          // із приватним ключем
        tls.TrustedRoots = [privateCaCert];          // приватний CA
        tls.SpkiPins = ["A1B2…"];                    // опційний пін
    });
```
