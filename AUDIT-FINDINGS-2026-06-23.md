# Audit Findings (Round 2) — WsRpcServer (JSON-RPC.NET)

**Date:** 2026-06-23
**Repo:** `07artem132/JSON-RPC.NET` @ commit `40d474c` (branch `claude/json-rpc-net-maturity-0zn187`)
**Scope:** `src/WsRpcServer/` (~2400 LOC) + `src/WsRpcServer.SourceGenerator/` (RpcServiceCatalogGenerator).
**Baseline:** `dotnet build` — 0 errors / **0 warnings** in lib+tests (`TreatWarningsAsErrors=true`); `dotnet test` — **128/128 passed**.
**Method:** static read of all key files (transport, sessions, server, services, generator, subscriptions, store, events, config, extensions, exceptions) + cross-check проти CLAUDE.md shipped-invariants та `.claude/rules/*`. No runtime/dynamic verification. All `file:line` references verified проти source at `40d474c`.

> This is the **second** audit pass. Round 1 (`AUDIT-FINDINGS.md`, 2026-05-27, commit `6c9cb6d`) enumerated 20
> findings — **all shipped/resolved** across `foundation-cluster-1` → `aot-rpc-dispatch` (2.5.0). This pass
> looks for *new* findings on the matured codebase; none of the Round-1 items are re-reported (they are guarded).

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

_(none)_ — no functional/security/corruption-class findings on this pass. The Round-1 HIGH band (H1-H4) is shipped+guarded.

---

## 🟡 MEDIUM

### R2-M1. Three library `await`s missing `.ConfigureAwait(false)` — captures consumer SynchronizationContext  ✅ SHIPPED

> **Статус:** виправлено. 3 сайти отримали `.ConfigureAwait(false)`; guard `ConfigureAwaitConventionTests`
> (Roslyn-парсинг кожного `.cs` у `src/WsRpcServer`) ловить весь клас регресії. Suite 128 → 129.

**Where:**
- `src/WsRpcServer/Transport/WebSocketMessageHandler.cs:272` — `await _session.SendBinaryDataAsync(writer.WrittenMemory);`
- `src/WsRpcServer/Sessions/AbstractJsonRpcSession.cs:166` — `await foreach (... NotificationChannel.Reader.ReadAllAsync(cancellationToken))`
- `src/WsRpcServer/Sessions/AbstractJsonRpcSession.cs:181-182` — `await JsonRpc.NotifyAsync(...).WaitAsync(timeoutCts.Token);`

**Що:** Три точки в бібліотечному коді (`src/WsRpcServer/**`) await'ять без `.ConfigureAwait(false)`. Решта файлу (рядки 121, 191, 255, 88, 91 у sibling-класах) застосовують його послідовно — це три пропуски, а не загальна відсутність патерну.

**Чому:** `conventions.md` прямо вимагає: *"Always `.ConfigureAwait(false)` in library code (`src/WsRpcServer/**`) — it ships as a NuGet package and must not capture a consumer's synchronization context."* Споживач із non-null `SynchronizationContext` (UI-застосунок, старий ASP.NET) отримує: (a) ризик deadlock'у на continuation, (b) зайвий re-scheduling на захоплений контекст у hot-path відправки (`WriteCoreAsync`) та у циклі fan-out сповіщень (`ProcessNotificationsAsync`). Це найгарячіші шляхи фреймворка — по одному await на кожне вихідне повідомлення/сповіщення.

> Примітка: `WebSocketMessageHandler.cs:82` (`return _writer.FlushAsync();`) **не** є дефектом — це безпосереднє повернення `ValueTask<FlushResult>`, до якого `.ConfigureAwait(false)` не застосовний (його застосовує той, хто await'ить).

**Fix:** додати `.ConfigureAwait(false)`:
- `await _session.SendBinaryDataAsync(writer.WrittenMemory).ConfigureAwait(false);`
- `await foreach (var n in NotificationChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))`
- `await JsonRpc.NotifyAsync(m, a).WaitAsync(timeoutCts.Token).ConfigureAwait(false);`

**Regression-guard:** reflection/Roslyn-тест, що сканує всі `AwaitExpression` у `src/WsRpcServer/**` і assert'ить, що кожен має `.ConfigureAwait(...)` (виняток — безпосередній `return` ValueTask). Малий синтаксичний guard, який ловить весь клас регресії — а не лише ці три сайти.

---

### R2-M2. `ProcessReceivedDataAsync` writes to the pipe without a disposal guard — teardown race vs. `Dispose`  ✅ SHIPPED

> **Статус:** виправлено. Додано явний dispose-guard, що кидає ідіоматичний `ObjectDisposedException`
> (симетрично до `WriteCoreAsync`) замість `InvalidOperationException`, який раніше випадково витікав із
> завершеного `PipeWriter`. Існуючий тест оновлено: `...ThrowsInvalidOperationException` →
> `...ThrowsObjectDisposedException`. **Уточнення проти первинного формулювання:** шлях не був повністю
> «незахищеним» — він кидав (і це тестувалося), але неідіоматичним типом; фікс — про коректний тип винятку.

**Where:** `src/WsRpcServer/Transport/WebSocketMessageHandler.cs:72-89` (запис у `_writer`) проти `:289-305` (`Dispose` → `_writer.Complete()`).

**Що:** `ProcessReceivedDataAsync` (вхідний шлях транспорту — NetCoreServer викликає його при отриманні WS-фрейму) робить `_writer.Write(buffer.Span)` + `_writer.FlushAsync()` **без** перевірки `_disposed`. Інші точки входу обробника її мають: `WriteCoreAsync:249` (`if (_disposed) throw`) і `DeserializationComplete:96` (`if (_disposed) return`). Вхідний шлях — виняток із цієї дисципліни.

**Чому:** під час teardown'у (клієнт від'єднався / сервер зупиняється) можлива гонка: фрейм приходить конкурентно з `Dispose`. Якщо `_writer.Complete()` (рядок 297) виконається між (або під час) `Write`/`FlushAsync`, `PipeWriter` кине `InvalidOperationException` ("writing is not allowed after writer was completed") у callback'у транспорту, або вхідні дані тихо губляться. Це той самий клас teardown-undefined-behavior, який Round-1 H4 закрив для сесій/процесора — лише на цьому шляху перевірки немає.

**Fix:** на початку `ProcessReceivedDataAsync` додати guard, симетричний до `WriteCoreAsync`:
```csharp
if (_disposed)
    return ValueTask.FromResult<FlushResult>(new FlushResult(isCanceled: false, isCompleted: true));
// або кинути ObjectDisposedException — узгоджено з WriteCoreAsync
```

**Regression-guard:** тест, що викликає `Dispose()` і потім `ProcessReceivedDataAsync(...)` — assert'ить детермінований результат (no-op/ObjectDisposedException), а не `InvalidOperationException` від завершеного `PipeWriter`.

---

### R2-M3. ~~`MaxSubscriptionsPerClient` + `ClientSubscriptionCounts` declared but never enforced by the base~~  ❌ ВІДКЛИКАНО (false positive)

> **Статус: ВІДКЛИКАНО при верифікації перед реалізацією.** Первинне формулювання спиралося на
> `grep` лише по `src/` цього репо й зробило висновок, що `ClientSubscriptionCounts`/`MaxSubscriptionsPerClient`
> «ніде не використовуються». Це **помилка неповного grep'у** (саме той патерн, від якого застерігає
> sibling-правило SignalCli.NET `audit-debt.md §0.5 «cite-and-read, not cite-and-trust»`).
>
> **Реальність:** обидва члени — навмисні `protected` будівельні блоки Template-Method, і **реальний
> downstream-споживач їх використовує**: `SignalCliNet.WsRpcServer/Subscriptions/SubscriptionManager.cs`
> читає `ClientSubscriptionCounts.GetValueOrDefault(clientId) >= MaxSubscriptionsPerClient` (рядок 46),
> кидає при перевищенні (50) і підтримує лічильник через `AddOrUpdate` (110, 152). Тобто база свідомо
> віддає enforcement похідному класу (як і документує `SubscribeCore`: *«похідні класи реалізують
> логіку перевірки обмеження»*), а тестовий дубль `TestSubscriptionManager.SubscribeCore` так само читає
> `MaxSubscriptionsPerClient` (рядок 43) і має тест `Subscribe_ExceedsMaxSubscriptions_ThrowsException`.
>
> Жодного дефекту немає: це валідний фреймворковий патерн (база надає storage+policy, похідний enforce'ить).
> Реалізація «фіксу» (enforce в базі або видалення членів) **зламала б** і реального споживача, і існуючий
> тест. Тому фінт не виконується. Урок занесено у підсумок нижче.

---

### R2-M4. Source generator doesn't detect methods that resolve to the same JSON-RPC name — emits duplicate `AddLocalRpcMethod` → throws at bind time  ✅ SHIPPED

> **Статус:** виправлено. Додано діагностику `WSRPC003`: `CollectMethods` групує за JSON-іменем, кожну
> колізію (overload'и / однаковий `[JsonRpcMethod]`) повідомляє Warning'ом і виключає всі методи групи з
> binder'а (рефлексійний `AddLocalRpcTarget` лишається для таких сервісів). Guard:
> `Generator_DuplicateJsonName_ReportsWsrpc003AndSkipsColliding`. Suite 130 → 131.

**Where:** `src/WsRpcServer.SourceGenerator/RpcServiceCatalogGenerator.cs:161-194` (`CollectMethods` — без дедуплікації за JSON-іменем) + `:196-219` (`UnsupportedReason` — не ловить колізії імен) + emit на `:388-394` (`EmitAddMethod` → `jsonRpc.AddLocalRpcMethod("<name>", …)`).

**Що:** `CollectMethods` збирає методи з `iface` + `iface.AllInterfaces`, мапить кожен на JSON-ім'я через `ResolveJsonName` (camelCase або `[JsonRpcMethod("…")]`), і додає **усі** як окремі `MethodBinding`. Дедуплікації/детекції колізій за `JsonName` немає. `UnsupportedReason` відсіює generic/ref-out-in/>16-params/byref, але **не** колізії імен. Два методи з однаковим JSON-іменем виникають через: (1) **overload'и** інтерфейсу (`Foo(int)` + `Foo(string)` → обидва `"foo"`), (2) дві сигнатури з однаковим явним `[JsonRpcMethod("x")]`, (3) метод базового інтерфейсу + його `new`-shadow.

**Чому:** binder емітить `jsonRpc.AddLocalRpcMethod("foo", delegateA)` **і** `jsonRpc.AddLocalRpcMethod("foo", delegateB)`. На відміну від рефлексійного `AddLocalRpcTarget` (який StreamJsonRpc вміє диспетчити за арністю/типами і **підтримує overload'и**), `AddLocalRpcMethod(name, delegate)` overload'ів не підтримує — повторна реєстрація того самого імені кидає на старті прив'язки. Тобто консюмер, який мав робочі overload'и через рефлексійний шлях і просто увімкнув `AddGeneratedRpcMethodBinder()`, отримує **runtime-збій реєстрації** замість тихої або діагностованої поведінки. Це звужена-поведінка binder'а, яку CLAUDE.md документує загально ("exposes only interface methods"), але колізія імен конкретно не діагностується.

**Fix:** у `CollectMethods` після збору згрупувати за `JsonName`; якщо група >1 — `context.ReportDiagnostic` нової діагностики (напр. `WSRPC003`, "кілька методів мапляться на однакове JSON-ім'я; binder не підтримує overload'и — лишіть рефлексійний шлях для цього сервісу") і **пропустити** колізійні (або весь сервіс), щоб не згенерувати код, що кине. Узгоджено з наявним `WSRPC002`-патерном (skip + diagnostic).

**Regression-guard:** generator-driver тест (як `RpcServiceCatalogGeneratorTests`) з інтерфейсом, що має overload'и (`Foo(int)`/`Foo(string)`), assert'ить наявність `WSRPC003` і відсутність двох `AddLocalRpcMethod("foo"…)` у згенерованому джерелі.

---

## 🟢 LOW

### R2-L1. Consecutive-failure circuit breaker uses a stale local count — concurrent success can still trip unregister  ✅ SHIPPED

> **Статус:** виправлено. Рішення про авто-відписку тепер атомарне:
> `_consecutiveFailures.TryRemove(KeyValuePair{clientId, failures})` знімає лічильник лише якщо він
> усе ще дорівнює знімку — конкурентний скид (success) скасовує відписку. Guard:
> `OnNotificationFailed_CounterResetBetweenIncrementAndCheck_DoesNotUnregister` (перевірено: падає на
> pre-fix, проходить post-fix). Suite 129 → 130.

**Where:** `src/WsRpcServer/Events/AbstractEventProcessor.cs:277-291` (`OnNotificationFailed`) проти `:260` (reset на успіх) — обидва біжать у `ContinueWith`-continuation'ах на `TaskScheduler.Default`.

**Що:** `OnNotificationFailed` бере `failures = _consecutiveFailures.AddOrUpdate(...)` (рядок 279), і **пізніше** перевіряє `failures >= _maxConsecutiveNotificationFailures` (рядок 284) проти **локального** snapshot'у. Між інкрементом і перевіркою конкурентна успішна доставка (`:260` `TryRemove`) може скинути лічильник до 0, але рішення на рядку 289 спирається на застаріле локальне `failures`.

**Чому:** fan-out запускає кілька handler-задач на клієнта одночасно (`NotifyClient:249`); сповіщення A (fail) і B (success) можуть завершитись у будь-якому порядку. При точному переплетінні на порозі клієнт, який щойно мав **успішну** доставку (тобто streak "послідовних" невдач перервано), все одно авто-відписується. Це суперечить документованій семантиці "**послідовних** невдач" (`:271-274`). Імовірність низька, наслідок доброякісний (відключається переважно-зламаний клієнт, який може перепідключитись), тому LOW.

**Fix:** прийняти рішення про unregister атомарно з лічильником — напр. перевіряти поточне значення через `_consecutiveFailures.TryGetValue` безпосередньо перед `UnregisterClient`, або реалізувати поріг як `TryUpdate`-CAS, що unregister'ить лише якщо лічильник усе ще `>= max`.

**Regression-guard:** тест, що конкурентно проганяє success+fail для одного клієнта на порозі та assert'ить, що success-after-fail скидає streak і клієнт НЕ відписується.

---

## Summary — counts + статус реалізації

| Severity | Count | IDs | Статус |
|---|---|---|---|
| 🔴 HIGH | 0 | — | — |
| 🟡 MEDIUM | 4 | R2-M1, R2-M2, R2-M4 ✅ shipped; R2-M3 ❌ відкликано | 3 shipped / 1 false-positive |
| 🟢 LOW | 1 | R2-L1 ✅ shipped | shipped |
| **Total** | **5** | **4 виправлено, 1 відкликано** | |

### Реалізація (2026-06-23, той самий день)

Усі валідні знахідки виправлено — по одному коміту на знахідку, кожен із regression-guard'ом
(правило репо: «fix without a guard is the next regression»). Suite **128 → 131** (+3 нові тести;
R2-M2 оновив існуючий тест). Build 0 warnings у lib+tests.

| Finding | Capability | Guard | Δ suite |
|---|---|---|---|
| R2-M1 | `configureawait-library-sweep` | `ConfigureAwaitConventionTests` (Roslyn-скан) | 128 → 129 |
| R2-M2 | `transport-dispose-guard` | `ProcessReceivedDataAsync_WhenDisposed_ThrowsObjectDisposedException` | 129 (оновлено) |
| R2-L1 | `eventprocessor-failure-cas` | `OnNotificationFailed_CounterResetBetweenIncrementAndCheck_DoesNotUnregister` | 129 → 130 |
| R2-M4 | `generator-jsonname-collision-diag` (WSRPC003) | `Generator_DuplicateJsonName_ReportsWsrpc003AndSkipsColliding` | 130 → 131 |
| R2-M3 | — | — (відкликано) | — |

### Урок (для майбутніх аудитів цього репо)

**R2-M3 був false positive через grep лише по `src/` цього репо.** Перед тим як оголосити
`protected`-член «мертвим», обов'язково перевір **downstream-споживачів** (тут — `SignalCliNet.WsRpcServer`):
`protected` API фреймворку існує саме для похідних класів, тож «невикористаний у `src/`» ≠ «невикористаний».
Це той самий урок, що sibling-правило SignalCli.NET `audit-debt.md §0.5` («cite-and-read, not cite-and-trust»).
Варто додати його у `.claude/rules/audit-debt.md` цього репо як prevention-checklist пункт.

### Не зловлено цим audit'ом (потребує live/dynamic аналізу)

- Реальна поведінка back-pressure pipe (`PipeThresholdBytes`) під навантаженням.
- WebSocket-фрагментація на межі великих повідомлень.
- Чи `AddLocalRpcMethod` дійсно кидає (а не warn'ить) на дубльованому імені — передбачено зі знання StreamJsonRpc API, не верифіковано рантаймом (R2-M4).
- Багатосерверна ізоляція стану в одному процесі.

---

**Generated:** 2026-06-23 by Claude (`claude-opus-4-8`).
**Method:** static analysis only; no runtime/dynamic verification.
**Citations:** all `file:line` references verified проти actual source at commit `40d474c`.
