using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetCoreServer;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Extensions;
using WsRpcServer.Services;
using WsRpcServer.Tests.Events;
using WsRpcServer.Tests.Sessions;
using WsRpcServer.Tests.Subscriptions;
using Xunit;

namespace WsRpcServer.Tests.Extensions
{
    // DI-дружній тестовий реєстр (конструктор резолвиться контейнером).
    public sealed class CompositionTestRegistry(IServiceProvider serviceProvider, ILogger<CompositionTestRegistry> logger)
        : AbstractRpcServiceRegistry(serviceProvider, logger)
    {
        protected override IEnumerable<string> GetAdditionalAssemblyPrefixes() => Array.Empty<string>();
    }

    // DI-дружній тестовий сервер (будується фабрикою композиційного кореня).
    public sealed class CompositionTestServer(
        IPAddress address,
        int port,
        IServiceProvider serviceProvider,
        ILogger<CompositionTestServer> logger)
        : AbstractJsonRpcServer(address, port, serviceProvider, logger)
    {
        protected override WsSession CreateJsonRpcSession() => throw new NotSupportedException();
    }

    /// <summary>
    /// Regression guard для H1: узагальнений <c>AddJsonRpcCore&lt;…&gt;</c> реєструє всі п'ять
    /// основних сервісів + конкретний сервер, дотримується lifetime'ів ("один екземпляр — дві ролі"
    /// для обробника подій / менеджера підписок), і є ідемпотентним при повторному виклику.
    /// </summary>
    public sealed class AddJsonRpcCoreCompositionTests
    {
        private static ServiceCollection NewServices()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            // Тестові двійники приймають неузагальнений ILogger — робимо його резолвабельним.
            services.AddSingleton<ILogger>(NullLogger.Instance);
            return services;
        }

        private static IServiceCollection AddCore(IServiceCollection services, Action<JsonRpcServerConfig>? configure = null)
            => services.AddJsonRpcCore<
                CompositionTestServer,
                TestJsonRpcSession,
                TestEventProcessor,
                TestSubscriptionManager,
                CompositionTestRegistry,
                string,
                object>(configure);

        [Fact]
        public void RegistersAllCoreServices_AllResolvable()
        {
            var services = NewServices();
            AddCore(services, c => c.Host = "127.0.0.1");
            using var provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetRequiredService<JsonRpcServerConfig>());
            Assert.NotNull(provider.GetRequiredService<IEventProcessor>());
            Assert.NotNull(provider.GetRequiredService<ISubscriptionManager<string, object>>());
            Assert.NotNull(provider.GetRequiredService<IRpcServiceRegistry>());
            Assert.NotNull(provider.GetRequiredService<CompositionTestServer>());
        }

        [Fact]
        public void EventProcessor_ConcreteAndInterface_ResolveToSameSingleton()
        {
            var services = NewServices();
            AddCore(services);
            using var provider = services.BuildServiceProvider();

            var concrete = provider.GetRequiredService<TestEventProcessor>();
            var viaInterface = provider.GetRequiredService<IEventProcessor>();

            Assert.Same(concrete, viaInterface);
            Assert.Same(viaInterface, provider.GetRequiredService<IEventProcessor>());
        }

        [Fact]
        public void SubscriptionManager_ConcreteAndInterface_ResolveToSameSingleton()
        {
            var services = NewServices();
            AddCore(services);
            using var provider = services.BuildServiceProvider();

            var concrete = provider.GetRequiredService<TestSubscriptionManager>();
            var viaInterface = provider.GetRequiredService<ISubscriptionManager<string, object>>();

            Assert.Same(concrete, viaInterface);
        }

        [Fact]
        public void Server_BuiltFromValidatedConfig_UsesConfiguredHostAndPort()
        {
            var services = NewServices();
            AddCore(services, c =>
            {
                c.Host = "127.0.0.1";
                c.Port = 8123;
            });
            using var provider = services.BuildServiceProvider();

            var server = provider.GetRequiredService<CompositionTestServer>();

            // NetCoreServer WsServer.Address — рядкове представлення адреси.
            Assert.Equal("127.0.0.1", server.Address);
            Assert.Equal(8123, server.Port);
        }

        [Fact]
        public void CoreServices_HaveExpectedLifetimes()
        {
            var services = NewServices();
            AddCore(services);

            Assert.Equal(ServiceLifetime.Singleton, services.Single(d => d.ServiceType == typeof(IEventProcessor)).Lifetime);
            Assert.Equal(ServiceLifetime.Singleton, services.Single(d => d.ServiceType == typeof(ISubscriptionManager<string, object>)).Lifetime);
            Assert.Equal(ServiceLifetime.Singleton, services.Single(d => d.ServiceType == typeof(IRpcServiceRegistry)).Lifetime);
            Assert.Equal(ServiceLifetime.Singleton, services.Single(d => d.ServiceType == typeof(CompositionTestServer)).Lifetime);
            // Сесія транзієнтна — новий екземпляр на кожне з'єднання.
            Assert.Equal(ServiceLifetime.Transient, services.Single(d => d.ServiceType == typeof(TestJsonRpcSession)).Lifetime);
        }

        [Fact]
        public void RepeatedRegistration_IsIdempotent_NoDuplicateDescriptors()
        {
            var services = NewServices();
            AddCore(services);
            var countAfterFirst = services.Count;

            AddCore(services);

            Assert.Equal(countAfterFirst, services.Count);
            Assert.Single(services, d => d.ServiceType == typeof(JsonRpcServerConfig));
            Assert.Single(services, d => d.ServiceType == typeof(IEventProcessor));
        }

        [Fact]
        public void ConfigOnlyOverload_RepeatedCall_IsIdempotent()
        {
            var services = NewServices();
            services.AddJsonRpcCore();
            var countAfterFirst = services.Count;

            services.AddJsonRpcCore(c => c.Port = 1234);

            Assert.Equal(countAfterFirst, services.Count);
        }
    }
}
