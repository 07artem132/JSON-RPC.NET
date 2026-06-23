# Spec — composition-root-complete

## ADDED Requirements

### Requirement: AddJsonRpcCore SHALL register the full core service set + concrete server

A generic overload
`AddJsonRpcCore<TServer, TSession, TEventProcessor, TSubscriptionManager, TRegistry>(this IServiceCollection, Action<JsonRpcServerConfig>?)`
SHALL register all core services so consumers no longer hand-wire them:
`TEventProcessor`/`TSubscriptionManager` as singletons that double as their interfaces
(`IEventProcessor`/`ISubscriptionManager` resolve to the same instance — "one instance, two roles"),
`IRpcServiceRegistry` → `TRegistry` as a singleton, `TSession` as transient, and `TServer` as a
singleton built from the validated config (`Host` parsed to `IPAddress`, `Port` applied). The generic
constraints SHALL bind `TServer : AbstractJsonRpcServer`, `TSession : AbstractJsonRpcSession`,
`TEventProcessor : IEventProcessor`, `TSubscriptionManager : ISubscriptionManager`,
`TRegistry : IRpcServiceRegistry`.

#### Scenario: All core services resolve

- **GIVEN** `services.AddJsonRpcCore<TServer, TSession, TEventProcessor, TSubscriptionManager, TRegistry>()`
- **WHEN** the provider is built
- **THEN** `JsonRpcServerConfig`, `IEventProcessor`, `ISubscriptionManager`, `IRpcServiceRegistry`, and
  `TServer` all resolve to non-null instances

#### Scenario: One instance, two roles

- **GIVEN** the registered composition root
- **WHEN** the concrete `TEventProcessor` and `IEventProcessor` are both resolved
- **THEN** they are the same singleton instance (likewise for the subscription manager)

#### Scenario: Server built from validated config

- **GIVEN** `AddJsonRpcCore<…>(c => { c.Host = "127.0.0.1"; c.Port = 8123; })`
- **WHEN** `TServer` is resolved
- **THEN** its `Address` is `"127.0.0.1"` and its `Port` is `8123`

### Requirement: AddJsonRpcCore SHALL be idempotent

Repeated calls to `AddJsonRpcCore` (either overload) SHALL NOT duplicate registrations. A private
sentinel marker SHALL guard the one-time config/options registration, and all service registrations
SHALL use `TryAdd*`, so a consumer that pre-registers a custom implementation is not overwritten.

#### Scenario: Repeated registration adds no duplicates

- **GIVEN** `AddJsonRpcCore<…>()` called once
- **WHEN** it is called a second time on the same `IServiceCollection`
- **THEN** the descriptor count is unchanged and exactly one descriptor exists for `JsonRpcServerConfig`
  and for `IEventProcessor`
