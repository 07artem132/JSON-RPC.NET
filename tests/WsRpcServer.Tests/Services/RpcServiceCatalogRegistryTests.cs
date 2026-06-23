using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using WsRpcServer.Services;
using Xunit;

namespace WsRpcServer.Tests.Services
{
    // Зразкові RPC-інтерфейси/реалізації для каталогу (registry-sourcegen-discovery).
    public interface ICatalogRegularRpc : IRpcService { }
    public sealed class CatalogRegularRpc : ICatalogRegularRpc { }
    public interface ICatalogClientAwareRpc : IClientAwareRpcService { }
    public sealed class CatalogClientAwareRpc : ICatalogClientAwareRpc { }

    /// <summary>Простий in-memory каталог для тестів (імітує source-генерований).</summary>
    internal sealed class FakeRpcServiceCatalog(params RpcServiceDescriptor[] services) : IRpcServiceCatalog
    {
        public IReadOnlyList<RpcServiceDescriptor> Services { get; } = services;
    }

    /// <summary>
    /// Тестовий реєстр: якщо рефлексійний шлях буде задіяно — кине (доводить, що каталог його оминає).
    /// Відкриває вміст побудованого кешу.
    /// </summary>
    public sealed class CatalogTestRegistry(IServiceProvider serviceProvider, ILogger logger)
        : AbstractRpcServiceRegistry(serviceProvider, logger)
    {
        protected override IEnumerable<string> GetAdditionalAssemblyPrefixes() => [];

        protected override Assembly[] GetTargetAssemblies() =>
            throw new InvalidOperationException("Рефлексійне сканування не має викликатись за наявності каталогу.");

        public (IReadOnlyList<Type> Regular, IReadOnlyList<(Type Interface, Type Impl)> ClientAware) BuildCache()
        {
            var cache = GetServiceTypeCache();
            return (cache.RegularServices, cache.ClientAwareServices);
        }
    }

    /// <summary>
    /// Guard для `sourcegen-catalog`: за наявності <see cref="IRpcServiceCatalog"/> у DI реєстр будує кеш
    /// саме з нього й НЕ виконує рефлексійного сканування збірок.
    /// </summary>
    public sealed class RpcServiceCatalogRegistryTests
    {
        [Fact]
        public void BuildCache_WithInjectedCatalog_UsesCatalogAndSkipsReflection()
        {
            // Arrange — каталог із одним звичайним і одним клієнт-залежним сервісом.
            var catalog = new FakeRpcServiceCatalog(
                new RpcServiceDescriptor(typeof(ICatalogRegularRpc), typeof(CatalogRegularRpc), false),
                new RpcServiceDescriptor(typeof(ICatalogClientAwareRpc), typeof(CatalogClientAwareRpc), true));

            var sp = new Mock<IServiceProvider>();
            sp.Setup(p => p.GetService(typeof(IRpcServiceCatalog))).Returns(catalog);

            var logger = new Mock<ILogger>();
            logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var registry = new CatalogTestRegistry(sp.Object, logger.Object);

            // Act — побудова кешу не має кинути (GetTargetAssemblies кидає, отже рефлексію оминуто).
            var (regular, clientAware) = registry.BuildCache();

            // Assert — розподіл відповідає каталогу.
            Assert.Single(regular);
            Assert.Equal(typeof(ICatalogRegularRpc), regular[0]);

            Assert.Single(clientAware);
            Assert.Equal(typeof(ICatalogClientAwareRpc), clientAware[0].Interface);
            Assert.Equal(typeof(CatalogClientAwareRpc), clientAware[0].Impl);
        }

        [Fact]
        public void BuildCache_WithoutCatalog_FallsBackToReflection()
        {
            // Arrange — ServiceProvider не віддає каталог → реєстр має піти рефлексійним шляхом.
            var sp = new Mock<IServiceProvider>();
            sp.Setup(p => p.GetService(typeof(IRpcServiceCatalog))).Returns(null!);

            var registry = new CatalogTestRegistry(sp.Object, Mock.Of<ILogger>());

            // Act + Assert — рефлексійний шлях задіяно (GetTargetAssemblies кидає нашу сентинел-помилку).
            var ex = Assert.Throws<InvalidOperationException>(() => registry.BuildCache());
            Assert.Contains("Рефлексійне сканування", ex.Message);
        }
    }
}
