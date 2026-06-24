# Native-AOT / trim — стан і opt-in

Чи можна публікувати споживача `WsRpcServer` під `PublishAot` / з trim'ом без IL-ворнінгів? **Частково,
і це під твоїм контролем.** Discovery й dispatch вже AOT-clean (opt-in нижче); кінцевий блокер —
**upstream StreamJsonRpc**, не наш код.

> TL;DR: увімкни `[assembly: GenerateRpcServiceCatalog]` + зареєструй
> `AddGeneratedRpcServiceCatalog()` та `AddGeneratedRpcMethodBinder()` — і **наш** шар стане
> reflection-free. Але `<IsAotCompatible>true</IsAotCompatible>` на бібліотеці лишається **вимкненим**,
> бо StreamJsonRpc 2.25.29 серіалізує payload'и через рефлексію.

---

## Що вже AOT-clean (наш код)

| Шар | Рефлексійний дефолт | AOT-clean альтернатива (opt-in) | Версія |
|---|---|---|---|
| **Discovery** (які типи — RPC-сервіси) | сканування збірок `GetExportedTypes`/`IsAssignableFrom` | source-gen `IRpcServiceCatalog` | `registry-sourcegen-discovery` 2.3.0 / `aot-readiness` 2.4.0 |
| **Dispatch** (чіпляння методів до `JsonRpc`) | рефлексійний `AddLocalRpcTarget` | source-gen `IRpcMethodBinder` (`AddLocalRpcMethod` per-метод) | `aot-rpc-dispatch` 2.5.0 |

Деталі обох артефактів — [`api/services-and-registry.md`](api/services-and-registry.md). Обидва генерує
`WsRpcServer.SourceGenerator` (їде в nupkg як analyzer); вмикаються незалежно один від одного.

Виміряно (rule #4, `.claude/rules/patterns.md`):

- **0 IL-ворнінгів** під `-p:IsAotCompatible=true` на самому discovery-шляху (5 generic-параметрів
  `AddJsonRpcCore<…>` несуть `[DynamicallyAccessedMembers(PublicConstructors)]`; межа рефлексійного
  fallback'у чесно заглушена `[UnconditionalSuppressMessage]` з обґрунтуванням «catalog — це AOT-шлях»).
- Реальний `dotnet publish -p:PublishAot=true` нативний бінарник у `aot-smoke/` виконує
  catalog-based discovery + binder-based dispatch **без рефлексії** (exit 0).

Перевиміряти:

```bash
dotnet build src/WsRpcServer/WsRpcServer.csproj \
    -p:IsAotCompatible=true -p:TreatWarningsAsErrors=false
```

---

## Як увімкнути (споживач)

```csharp
// 1) Позначити збірку зі своїми RPC-сервісами:
[assembly: GenerateRpcServiceCatalog]

// 2) Зареєструвати обидва згенеровані артефакти поряд із композиційним коренем:
services.AddJsonRpcCore<DemoJsonRpcServer, DemoJsonRpcSession, DemoEventProcessor,
                        DemoSubscriptionManager, DemoServiceRegistry,
                        ServerEventType, object>(o => { o.Port = 9000; });

services.AddGeneratedRpcServiceCatalog();   // discovery без рефлексії
services.AddGeneratedRpcMethodBinder();      // dispatch без рефлексії
```

Якщо не реєструвати — все працює як раніше через рефлексію (зворотна сумісність). Реєстр сам обирає:
є каталог/binder → бере їх; нема → fallback.

---

## Чому `<IsAotCompatible>` досі вимкнено (upstream-блокер)

Навіть із AOT-clean discovery + dispatch, **StreamJsonRpc 2.25.29** серіалізує самі JSON-RPC envelope'и
/ payload'и через рефлексію: `dotnet publish` показує `IL3053`/`IL2104` на `StreamJsonRpc.dll` +
транзитивний `Newtonsoft.Json`. End-to-end AOT RPC-payload'и — **upstream-прогалина**, не наша. Тому
бібліотека свідомо **не** ставить `<IsAotCompatible>true</IsAotCompatible>` — щоб не давати хибну
обіцянку. Заміну StreamJsonRpc цілком досліджували (spike) і відхилили.

Повернутися до цього питання — коли StreamJsonRpc випустить AOT-safe formatter / target-attach шлях.

---

## Обмеження binder'а (trade-off)

`IRpcMethodBinder` **не** 1:1 з `AddLocalRpcTarget` (rule #4):

- виставляє лише **методи інтерфейсу**;
- **не** підтримує target-події, `RpcMarshalable`, `JsonRpcTargetOptions`;
- непідтримувані форми методів (generic / `ref` / `out` / `in` / >16 параметрів) → діагностика
  `WSRPC002`, метод пропускається.

Кому потрібні ці можливості — **не** реєструй binder (лиши рефлексійний `AddLocalRpcTarget`); discovery
через каталог при цьому можна лишити увімкненим. Тобто два opt-in незалежні: каталог без binder'а — ок.
