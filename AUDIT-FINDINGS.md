# Audit Findings — WsRpcServer (JSON-RPC.NET)

**Date:** 2026-05-27
**Repo:** `07artem132/JSON-RPC.NET` @ commit `6c9cb6d`
**Scope:** `src/WsRpcServer/` (18 files, ~2400 LOC) + `tests/` (7 files, ~2700 LOC) + `example/SimpleServer/`
**Baseline:** `dotnet build` — 0 errors / **108 warnings**; `dotnet test` — **83/83 passed**.
**Method:** static read of all key files + cross-check проти SignalCli.NET maturity benchmark (14+ archived OpenSpec changes, 9 regression guards, 506 tests, TreatWarningsAsErrors=true).

---

## Shipped status

- **`foundation-cluster-1` (1.1.0):** M6 (warnings → 0), M7 (README org refs).
- **`security-hardening` (1.2.0):** ✅ **H2** (parse-failure throttle), ✅ **H3** (registry thread-safety + multi-impl warning), ✅ **H4** (cancel-before-dispose), ✅ **M9** (CanRead/CanWrite on dispose), plus the transitive **MessagePack** advisory (GHSA-hv8m-jj95-wg3x) pinned out. Each HIGH finding has a regression-guard test; build now passes with the NuGet audit **enabled**. Unit suite 83 → 90.
- **`ci-bootstrap`:** ✅ **M8** (`.github/workflows/build.yml` — NuGet vulnerability-audit gate + warnings-as-errors build + 90-test suite on push/PR) and the **M7** broken build badge (→ `build.yml`).
- **`composition-and-config` (1.3.0):** ✅ **H1** (full composition root — generic `AddJsonRpcCore<…>` registers all 5 services + concrete server + idempotency marker) and ✅ **M5** (`JsonRpcServerConfig` DataAnnotations + source-gen `[OptionsValidator]` fail-fast validation). Each guarded by a test; suite 90 → 112.
- **`subscription-manager-cleanup` (2.0.0, BREAKING):** ✅ **M2** (base now uses `OperationLock` via template `*Core` methods), ✅ **M3** (`account` → `topic`), ✅ **M4** (`ISubscriptionManager<TEventType, TEventArgs>` generic, no `object`). Guarded by 2 new tests; suite 112 → 114.
- **`logger-message-migration` (2.1.0):** closed the deferred `CA1848;CA1873` suppression left over from `warnings-cleanup` — all ~51 `ILogger` call sites in `src/WsRpcServer` moved onto source-generated `[LoggerMessage]` partials (5 new `Logging/*Log.cs`, EventId block per type). Repo-wide `<NoWarn>` removed; both perf rules now active in the lib (suppressed only in test/example projects). Guarded by `LoggerMessageMigrationTests`; suite 114 → 116.
- **Still open:** M1, L1–L7. See the table below.

---

## Severity legend

| Tag | Meaning |
|---|---|
| 🔴 HIGH | Functional bug / production risk / security hole / silent corruption |
| 🟡 MEDIUM | Correctness smell / minor race / API foot-gun / breaks audit invariants if shipped |
| 🟢 LOW | Style / hygiene / convention / nice-to-have |

Each finding includes: **what**, **where** (`file:line`), **why**, **proposed fix**, **regression-guard suggestion**.

---

## 🔴 HIGH

### H1. `AddJsonRpcCore` composition root incomplete — consumers must manually register 4 core services + concrete server  ✅ SHIPPED (`composition-and-config`, 1.3.0)

**Where:** `src/WsRpcServer/Extensions/JsonRpcCoreExtensions.cs:19-30` + leaked into `example/SimpleServer/Program.cs:58-74` (17 lines of boilerplate).

**Що:** Extension method `AddJsonRpcCore(this IServiceCollection, Action<JsonRpcServerConfig>?)` реєструє **тільки** `JsonRpcServerConfig` singleton. Не реєструє `IRpcServiceRegistry`, `ISubscriptionManager`, `IEventProcessor`, jet/no concrete `AbstractJsonRpcServer` subtype.

**Чому:** Consumer code примушений руками виконувати:
```csharp
services.AddSingleton<DemoEventProcessor>();
services.AddSingleton<IEventProcessor>(sp => sp.GetRequiredService<DemoEventProcessor>());
services.AddSingleton<DemoSubscriptionManager>();
services.AddSingleton<ISubscriptionManager>(sp => sp.GetRequiredService<DemoSubscriptionManager>());
services.AddSingleton<IRpcServiceRegistry, DemoServiceRegistry>();
services.AddTransient<DemoJsonRpcSession>();
services.AddSingleton<DemoJsonRpcServer>(sp => /* manual IPAddress.Parse + ctor */);
```
Це — типова "one-instance-two-roles" pattern, яку SignalCli.NET документує у `.claude/rules/patterns.md § DI registration` і helper-method'ом ховає. Тут — leaked у consumer.

**Окремий side-effect:** жодного **idempotency-guard'у** — повторні виклики `AddJsonRpcCore` тихо реєструють дублікат config (через `AddSingleton`). У SignalCli.NET це закривається `SignalCliRegistrationMarker` sentinel-type.

**Fix:** розширити signature з generic-параметрами для concrete implementations:
```csharp
public static IServiceCollection AddJsonRpcCore<TServer, TSession, TEventProc, TSubMgr, TRegistry>(
    this IServiceCollection services,
    Action<JsonRpcServerConfig>? configureOptions = null)
    where TServer : AbstractJsonRpcServer
    where TSession : AbstractJsonRpcSession
    where TEventProc : class, IEventProcessor
    where TSubMgr : class, ISubscriptionManager
    where TRegistry : class, IRpcServiceRegistry
```
+ idempotency через private marker type.

**Regression-guard:** test `AddJsonRpcCore_RegistersAllCoreServices` що resolve'ить кожен з 5 типів і assert'ить non-null + singleton-lifetime + повторний виклик не дублює.

---

### H2. JSON-парсер CPU-burn vector — `FindNextJsonDelimiter` під malformed stream'ом  ✅ SHIPPED (`security-hardening`, 1.2.0)

**Where:** `src/WsRpcServer/Transport/WebSocketMessageHandler.cs:104-211` (ReadCoreAsync + recovery).

**Що:** При `JsonException` під час `Formatter.Deserialize(buffer)`, код намагається "recover'ити" — шукає наступний `{`, `}`, `[`, `]`, `,` через `FindNextJsonDelimiter` і consume'ить буфер до тієї позиції. Це повторюється у loop'і.

**Чому ризик:** Атакуючий може надсилати потоки невалідного JSON з лише delimiter'ами між мусором — кожен read-cycle: (a) renting `byte[]` 1024 з ArrayPool, (b) копіювання, (c) лінійний скан, (d) advance на 1+ байт. CPU-burn DoS — single connection може насичити core'у на CPU без rate-limit'у. Жодного per-connection bad-message counter'у.

**Fix:**
1. Після N consecutive parse-failures (наприклад, 10) — close connection з `WebSocketCloseStatus.ProtocolError`.
2. Логувати на `Warning` (а не `Error`) перші N failures, потім throttle до `Trace`.
3. Track метрику `parse_failures_total{client_id}` через Meter.

**Regression-guard:** integration test що шле 1000+ невалідних JSON-фреймів, asserts connection closed або throttled до того як CPU spike перевищить threshold.

---

### H3. Reflection-based service discovery — AOT-incompatible + thread-unsafe lazy cache  ✅ SHIPPED (thread-safety + multi-impl warning; `security-hardening`, 1.2.0 — AOT still open)

**Where:** `src/WsRpcServer/Services/AbstractRpcServiceRegistry.cs:60-72` (lazy init) + `:151-194` (assembly scan) + `:212-266` (cache build).

**Що:**
1. **AOT-блокери:** `AppDomain.CurrentDomain.GetAssemblies()` + `assembly.GetExportedTypes()` + `IsAssignableFrom` — все це IL2026/IL3050 vectors. AOT-publish видасть warnings/errors. SignalCli.NET закриває це через source-gen `JsonSerializerContext` + `[OptionsValidator]`.
2. **Race condition у lazy init:** `_serviceTypeCache ??= BuildServiceTypeCache();` — non-thread-safe; concurrent first-RegisterServices з 2+ потоків race'ять, builds twice, останній виграє. Per-connection RegisterServices виклик (`AbstractJsonRpcSession` startup для кожного нового client'а concurrent'но).
3. **Silent multi-impl loss:** `BuildServiceTypeCache` використовує `FirstOrDefault` коли знаходить implementations — якщо 2 класи implement той самий `IFooRpcService`, один тихо ігнорується.

**Fix:**
1. AOT: документувати reflection requirement у README (`<IsAotCompatible>` не enable; provide source-generated alternative для AOT consumers — окремий future change).
2. Thread-safety: `Lazy<ServiceTypeCache>(LazyThreadSafetyMode.ExecutionAndPublication)` або `Interlocked.CompareExchange`.
3. Multi-impl: log Warning якщо знайдено >1 impl per interface; consumer обирає через DI замість first-found.

**Regression-guard:** `Parallel.Invoke`-based test що паралельно викликає `RegisterServices(jsonRpc, guid)` × 100 — assert жодного duplicate-discovery; `ServiceTypeCache` built exactly once (track count через counter field).

---

### H4. `Dispose()` patterns lose in-flight tasks — no cancellation signaling before disposal  ✅ SHIPPED (`security-hardening`, 1.2.0)

**Where:**
- `src/WsRpcServer/Sessions/AbstractJsonRpcSession.cs:318-327` — `Dispose(bool)`: `Cts.Dispose()` без `Cts.Cancel()`.
- `src/WsRpcServer/Events/AbstractEventProcessor.cs:137-151` — `Dispose()`: те саме (Cts.Dispose тільки).
- `src/WsRpcServer/Subscriptions/AbstractSubscriptionManager.cs:159-166` — `Dispose()`: `OperationLock.Dispose()` без `WaitAsync()` first.

**Що:** Класи hold `CancellationTokenSource` (Cts) для координації background tasks (e.g. `ProcessNotificationsAsync`). Dispose не cancel'ить Cts перед Dispose, тому:
- Background task що awaits `JsonRpc.NotifyAsync(...).WaitAsync(timeoutCts.Token)` — не отримає cancellation сигнал. Task завершиться через ConnectionLostException або таймаут — або hang'не до timeout.
- Fire-and-forget tasks через `NotifyClient` ContinueWith — handler може запуститися ПІСЛЯ Cts.Dispose, з captured already-disposed token → `ObjectDisposedException` у handler.
- `OperationLock` (SemaphoreSlim) disposed без waiting — якщо хтось у lock'у під час Dispose, отримує `ObjectDisposedException` при release.

**Чому HIGH:** не функціональний bug у happy-path, але під shutdown / restart / connection-drop scenarios — undefined behavior, в логах ObjectDisposedException, інвестигація тяжка.

**Fix pattern (SignalCli.NET-style):**
```csharp
public async ValueTask DisposeAsync()
{
    if (IsDisposed) return;
    IsDisposed = true;
    Cts.Cancel();                       // signal before disposing
    if (NotificationProcessingTask is not null)
        try { await NotificationProcessingTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
    Cts.Dispose();
    NotificationChannel.Writer.TryComplete();
}
```
+ implement `IAsyncDisposable` (вже patternу у SignalCli.NET — `IJsonRpcClient` is IAsyncDisposable-only).

**Regression-guard:** test `Dispose_DuringInFlightNotification_CancelsAndWaits` — створює session, починає notification, відразу Dispose, assert background task завершується протягом X ms без exception у логах.

---

## 🟡 MEDIUM

### M1. Fire-and-forget tasks — exceptions ловляться, але втрачаються

**Where:** `src/WsRpcServer/Events/AbstractEventProcessor.cs:169-199` (`NotifyClient`).

**Що:** `_ = handler(method, ...).ContinueWith(t => { if (t.IsFaulted) log; HandleClientFailure; })` — fire-and-forget з error-logging. Але:
- `HandleClientFailure(clientId)` — virtual no-op у base; derived class має implement'ити failure-counting / disconnect logic. Без implementation — broken client отримує infinite notifications, кожна failed.
- `_ = task` — task не trackається. Дослідити lifetime у тестах неможливо без instrumentation.

**Fix:** додати built-in failure-counter (наприклад, `ConcurrentDictionary<Guid, int>` з threshold = 5 → auto-unregister) як default behavior у base. Existing virtual hook залишається для customization.

**Regression-guard:** test з handler що always throws — assert клієнт unregister'нутий after threshold + logged warning per failure.

---

### M2. `AbstractSubscriptionManager` — operation-lock declared but never used in base  ✅ SHIPPED (`subscription-manager-cleanup`, 2.0.0)

**Where:** `src/WsRpcServer/Subscriptions/AbstractSubscriptionManager.cs:41` (declared) + `:159-166` (disposed).

**Що:** `protected readonly SemaphoreSlim OperationLock = new(1, 1);` — XMLDoc detalmente описує "синхронізація доступу до спільних ресурсів". Але base class сам **ніде** його не використовує — abstract methods `Subscribe`/`Unsubscribe`/`UpdateSubscription`/`GetClientsForEvent` повністю abstract. Derived classes МУСЯТЬ використовувати, але це socially-enforced.

**Чому MEDIUM:** API foot-gun. Junior developer бачить semaphore у base, припускає що base координує — implement'ить derived без власної синхронізації — гонки.

**Fix:** або (a) винести OperationLock у derived classes (видалити з base), або (b) реалізувати `protected async Task<T> WithLockAsync<T>(Func<Task<T>> action)` template-method і enforce у abstract signatures (`override Subscribe` calls `WithLockAsync(() => SubscribeCore(...))`).

---

### M3. `Subscribe(string account, ...)` — `account` param leaks domain-specific concept у generic framework  ✅ SHIPPED (`subscription-manager-cleanup`, 2.0.0)

**Where:** `src/WsRpcServer/Core/ISubscriptionManager.cs` + `AbstractSubscriptionManager:85-89`.

**Що:** Метод `Subscribe(Guid clientId, string account, object eventTypes, ...)` — другий аргумент `string account`. WsRpcServer — generic WebSocket JSON-RPC framework, не Signal-specific. Чому "account"? Leakage з якогось специфічного use-case у abstract API.

**Fix:** rename `account` → `topic` або `scope` (generic concept "канал/сегмент підписки"). Або зробити optional generic-typed `TContext context` через generic type param.

---

### M4. `eventTypes` параметр typed as `object` — lost type safety  ✅ SHIPPED (`subscription-manager-cleanup`, 2.0.0)

**Where:** `ISubscriptionManager.Subscribe(..., object eventTypes, ...)`, `UpdateSubscription(..., object eventTypes, ...)`, `GetClientsForEvent(object args, object eventType)`.

**Що:** `object`-typed parameters — нульова type-safety. Consumer передає що завгодно; runtime cast у derived class.

**Fix:** generic-ify interface:
```csharp
public interface ISubscriptionManager<TEventType, TArgs>
{
    Task<int> Subscribe(Guid clientId, string topic, TEventType eventTypes, CancellationToken ct = default);
    List<Guid> GetClientsForEvent(TArgs args, TEventType eventType);
    // ...
}
```

---

### M5. `JsonRpcServerConfig` — no validation, no `[Range]`/`[Required]` attributes  ✅ SHIPPED (`composition-and-config`, 1.3.0)

**Where:** `src/WsRpcServer/Core/JsonRpcServerConfig.cs:17-82`.

**Що:** Record з public mutable properties, no DataAnnotations:
- `Port` — int, no `[Range(1, 65535)]`. Negative/0/65536+ accepted.
- `MaxMessageSizeBytes` — int, no upper bound, default 100MB.
- `NotificationQueueSize` — int, no `[Range(1, _)]`. Zero/negative → channel-creation throw at runtime.
- `Host` — string, no IP/hostname validation.

**Fix:** Add `[Required(AllowEmptyStrings=false)]`, `[Range(...)]` per property; integrate with `IOptions<T>` pattern + `[OptionsValidator]` source-gen (SignalCli.NET pattern).

**Regression-guard:** validation test для кожного field — assert OptionsValidationException на host start with invalid config.

---

### M6. 108 build warnings — TreatWarningsAsErrors disabled

**Where:** All 4 `*.csproj` — no `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. No `Directory.Build.props` (shared MSBuild canon відсутній).

**Що:** 108 warnings ходять у production builds:
- ~80% CS86xx (CS8602/CS8605/CS8620) — nullability mismatches у тестах (Moq formatters з non-nullable Exception → nullable mismatch).
- 2 xUnit1031 у `AbstractSubscriptionStoreTests.cs:515,556` — `Task.WaitAll(tasks)` blocking у `[Fact]`-test'ах.
- CS0168 — unused `ex` variable.

**Чому MEDIUM:** не функціональні bugs, але:
- Real CS86xx у production code могли б замаскуватися — analyzer noise створює "blindness".
- xUnit1031 — реальний deadlock vector у test (хоча тут tasks безпечні).

**Fix:** покапітально:
1. Створити `Directory.Build.props` з `TreatWarningsAsErrors=true` + `AnalysisLevel=latest-recommended` + `<Nullable>enable</Nullable>` (вже є у csproj).
2. Виправити nullability mismatches у tests (формально через Moq overloads з nullable params).
3. Перетворити blocking `Task.WaitAll` → `await Task.WhenAll`.
4. Усунути unused `ex`.

**Regression-guard:** CI build assert `0 warnings`.

---

### M7. README — broken org references, non-existent CI badges  ✅ SHIPPED (org refs: `foundation-cluster-1`; build badge → `build.yml`: `ci-bootstrap`)

**Where:** `README.md:7` (build status badge) + `:60` (NuGet feed).

**Що:**
- Build badge: `https://github.com/mil-development/JSON-RPC.NET/actions/workflows/dotnet-desktop.yml/badge.svg` — посилається на `mil-development` org (інша organization!); цей repo — `07artem132/JSON-RPC.NET`. Badge показуватиме "missing workflow" або "broken".
- NuGet feed: `https://nuget.pkg.github.com/mil-development/index.json` — publishing target не `07artem132/`. Якщо consumer follow інструкцію — пакету немає.
- Coverage badges (`lines.svg`, `methods.svg`, `branches.svg`) — посилаються на `.github/badges/` яких **не існує** (немає `.github/` папки в repo взагалі).

**Fix:** Replace `mil-development` → `07artem132` через grep+sed; OR підтвердити що `mil-development` дійсно planned publish target і додати opener-explanation. Coverage badges generation потребує CI (separate change).

---

### M8. No CI/CD — `.github/workflows/` повністю відсутній  ✅ SHIPPED (`ci-bootstrap`: `build.yml` — audit gate + warnings-as-errors + tests)

**Where:** `git ls-files .github/` — empty.

**Що:** Жодних workflows: no build-on-push, no test runner, no coverage upload, no security scan, no NuGet publish on tag. README badge references `dotnet-desktop.yml` що НЕ існує.

**Fix:** Окремий future change `ci-bootstrap`:
- `.github/workflows/build.yml` — build + test на push/PR (ubuntu+windows+macos × Debug/Release).
- `.github/workflows/coverage.yml` — collect coverlet output → auto-commit badges (як SignalCli.NET).
- (Optionally) `.github/workflows/nuget-publish.yml` — manual-trigger publish on tag.

---

### M9. `WebSocketMessageHandler.CanRead`/`CanWrite` hardcoded `true` — survive Dispose  ✅ SHIPPED (`security-hardening`, 1.2.0)

**Where:** `src/WsRpcServer/Transport/WebSocketMessageHandler.cs:51-52`.

**Що:** Properties hardcoded:
```csharp
public override bool CanRead => true;
public override bool CanWrite => true;
```
Залишаються `true` навіть після `Dispose()` що sets `_disposed = true`. StreamJsonRpc internals можуть продовжувати call'ати `ReadCoreAsync`/`WriteCoreAsync` — отримуючи `ObjectDisposedException` (через `WriteCoreAsync:219`).

**Fix:**
```csharp
public override bool CanRead => !_disposed;
public override bool CanWrite => !_disposed;
```

---

## 🟢 LOW

### L1. `RegisterClient` overwrites silent — no warning on duplicate registration

**Where:** `AbstractEventProcessor.cs:105-110`.

**Що:** `ClientHandlers[clientId] = handler` — replaces silently. Якщо client двічі реєструється (наприклад через race), old handler dropped without log.

**Fix:** Use `TryAdd`; log Warning if already present + opt-in overwrite via separate method.

---

### L2. `Subscriptions` list (in EventProcessor) — not thread-safe

**Where:** `AbstractEventProcessor.cs:55`: `protected readonly List<IDisposable> Subscriptions = new();`.

**Що:** `List<T>` — not concurrent. `Subscriptions.Add(...)` з кількох потоків → undefined.

**Fix:** `ConcurrentBag<IDisposable>` OR document single-thread invariant via XMLDoc.

---

### L3. `OnWsPing` uses `new` instead of `override` — fragile if base method becomes virtual

**Where:** `AbstractJsonRpcSession.cs:298-304`.

**Що:** XMLDoc explicitly notes: "Використання ключового слова 'new' замість 'override' пов'язано з тим, що базовий клас (NetCoreServer.WsSession) не позначає цей метод як virtual." If upstream NetCoreServer makes it virtual у новій версії — `new` ховає override-intent, polymorphic dispatch через base reference bypasses цей `new`-method.

**Fix:** документально OK на сьогодні; додати CI-check що `OnWsPing` base-method signature не змінилася у dependency bump.

---

### L4. `SendNotificationAsync` returns `Task` але body sync — misleading signature

**Where:** `AbstractJsonRpcSession.cs:111-131`, `SendBinaryDataAsync:257-280`.

**Що:** Метод returns `Task.CompletedTask` після sync-write у channel. Caller'и можуть `await SendNotificationAsync(...)` thinking it awaits send-completion — насправді awaits nothing.

**Fix:** rename → `EnqueueNotification` (sync, returns `bool` для success/dropped) OR зробити return type `void`/`ValueTask` з clear docs.

---

### L5. `JsonRpcCoreExtensions.AddJsonRpcCore` — extra blank line при кінці body

**Where:** `JsonRpcCoreExtensions.cs:28`.

Trivial style nit — extra blank line `services.AddSingleton(config);\n\n\nreturn services;`.

---

### L6. `RpcErrorException` — no parameterless ctor, no serialization support

**Where:** `Exceptions/RpcErrorException.cs:19`.

**Що:** Class не sealed; немає `protected RpcErrorException()` ctor. Cross-AppDomain serialization deprecated у .NET 8+, тож likely OK; але some test frameworks expect it.

**Fix:** consider `sealed` per SignalCli.NET pattern (typed exception leaves).

---

### L7. No CLAUDE.md / OpenSpec / `.claude/rules/` infrastructure

**Where:** repo root.

**Що:** Жодного agent-instruction layer'у. Майбутні Claude/Copilot сесії стартуватимуть з чистого аркуша на цьому codebase'і.

**Fix:** окремий future change `claude-md-scaffold` (per SignalCli.NET 2.1.0 → 4.0.3 evolution).

---

## Summary — counts + capability suggestions

| Severity | Count |
|---|---|
| 🔴 HIGH | 4 (H1-H4) |
| 🟡 MEDIUM | 9 (M1-M9) |
| 🟢 LOW | 7 (L1-L7) |
| **Total** | **20 findings** |

### Запропонована roadmap (capability-shape, ordered low-risk first)

| Order | Capability | Findings | Files | Risk |
|---|---|---|---|---|
| 1 | `readme-org-fix` | M7 | README.md | none |
| 2 | `directory-build-props` | (part of M6) | new Directory.Build.props | none |
| 3 | `warnings-cleanup` | M6 | tests/* (~108 warnings) | low |
| 4 | `treat-warnings-errors` | M6 | csproj × 4 | none (post-#3) |
| 5 | `config-validation` | M5 | JsonRpcServerConfig + Options pattern | low |
| 6 | `composition-root-complete` | H1 | JsonRpcCoreExtensions + idempotency marker | low |
| 7 | `dispose-async-pattern` | H4 | 3 abstract classes | medium |
| 8 | `service-registry-thread-safety` | H3 | AbstractRpcServiceRegistry | low |
| 9 | `parse-failure-throttle` | H2 | WebSocketMessageHandler + Meter | medium |
| 10 | `subscription-manager-cleanup` | M2/M3/M4 | ISubscriptionManager + base | medium |
| 11 | `claude-md-scaffold` | L7 | CLAUDE.md + .claude/rules/ | none |
| 12 | `ci-bootstrap` | M8 | .github/workflows/ | low |

12 capabilities — chunkable у 2-3 OpenSpec changes (per SignalCli.NET historical pattern `address-audit-findings` + `address-audit-findings-2`).

**Не зловлено цим audit'ом** (потребує live testing або dynamic analysis):
- Performance under load (NotificationQueueSize=1000 + DropOldest behavior).
- Real WebSocket fragmentation edge cases.
- AOT publish actually producing IL2026/IL3050 errors (predicted, not verified).
- Multi-server inside one process — state isolation.

---

**Generated:** 2026-05-27 by Claude (Opus 4.7 1M context).
**Method:** static analysis only; no runtime/dynamic verification.
**Citations:** all `file:line` references verified proti actual source at commit `6c9cb6d`.
