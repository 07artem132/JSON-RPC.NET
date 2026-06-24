using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StreamJsonRpc;
using WsRpcServer.Services;
using Xunit;

namespace WsRpcServer.Tests.Services
{
    /// <summary>Записує факт виклику Bind — для перевірки, що реєстр обирає binder, а не рефлексію.</summary>
    internal sealed class RecordingRpcMethodBinder : IRpcMethodBinder
    {
        public bool BindCalled { get; private set; }
        public Guid ClientId { get; private set; }

        public void Bind(JsonRpc jsonRpc, IServiceProvider serviceProvider, Guid clientId, System.Security.Claims.ClaimsPrincipal? principal)
        {
            BindCalled = true;
            ClientId = clientId;
        }
    }

    /// <summary>
    /// Guard для `aot-rpc-dispatch`: за наявності <see cref="IRpcMethodBinder"/> у DI
    /// <see cref="AbstractRpcServiceRegistry.RegisterServices"/> викликає binder і НЕ виконує
    /// рефлексійного сканування збірок (AddLocalRpcTarget-шлях).
    /// </summary>
    public sealed class RpcMethodBinderRegistryTests
    {
        [Fact]
        public void RegisterServices_WithInjectedBinder_UsesBinderAndSkipsReflection()
        {
            // Arrange — DI віддає binder; рефлексійний шлях (GetTargetAssemblies) кинув би.
            var binder = new RecordingRpcMethodBinder();
            var sp = new Mock<IServiceProvider>();
            sp.Setup(p => p.GetService(typeof(IRpcMethodBinder))).Returns(binder);

            var registry = new CatalogTestRegistry(sp.Object, NullLogger.Instance);
            var clientId = Guid.NewGuid();

            using var jsonRpc = new JsonRpc(new HeaderDelimitedMessageHandler(new MemoryStream()));

            // Act — не має кинути (binder-шлях повертається до рефлексії).
            registry.RegisterServices(jsonRpc, clientId);

            // Assert — binder викликано саме з нашим clientId.
            Assert.True(binder.BindCalled);
            Assert.Equal(clientId, binder.ClientId);
        }

        [Fact]
        public void RegisterServices_WithoutBinder_FallsBackToReflection()
        {
            // Arrange — ні binder'а, ні каталогу → реєстр піде рефлексійним шляхом (GetTargetAssemblies кине).
            var sp = new Mock<IServiceProvider>();
            sp.Setup(p => p.GetService(typeof(IRpcMethodBinder))).Returns(null!);
            sp.Setup(p => p.GetService(typeof(IRpcServiceCatalog))).Returns(null!);

            var registry = new CatalogTestRegistry(sp.Object, NullLogger.Instance);
            using var jsonRpc = new JsonRpc(new HeaderDelimitedMessageHandler(new MemoryStream()));

            var ex = Assert.Throws<InvalidOperationException>(
                () => registry.RegisterServices(jsonRpc, Guid.NewGuid()));
            Assert.Contains("Рефлексійне сканування", ex.Message);
        }
    }
}
