using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WsRpcServer.Services;
using Xunit;

namespace WsRpcServer.Tests.Services
{
    // Два конкуруючі implementation'и одного RPC-інтерфейсу — для перевірки multi-impl warning (H3).
    public interface IMultiImplRpc : IRpcService { }
    public sealed class MultiImplA : IMultiImplRpc { }
    public sealed class MultiImplB : IMultiImplRpc { }

    /// <summary>
    /// Тестовий реєстр: рахує кількість сканувань збірок (≡ кількість побудов кешу) і скеровує
    /// сканування лише на задані збірки.
    /// </summary>
    public sealed class CountingRpcServiceRegistry(
        IServiceProvider serviceProvider,
        ILogger logger,
        Assembly[] assemblies) : AbstractRpcServiceRegistry(serviceProvider, logger)
    {
        private int _targetAssemblyScans;
        public int TargetAssemblyScans => _targetAssemblyScans;

        protected override IEnumerable<string> GetAdditionalAssemblyPrefixes() => Array.Empty<string>();

        protected override Assembly[] GetTargetAssemblies()
        {
            Interlocked.Increment(ref _targetAssemblyScans);
            return assemblies;
        }

        protected override bool IsTargetAssembly(Assembly assembly) => true;

        // GetServiceTypeCache тепер protected — тригеримо побудову кешу без потреби в JsonRpc-інстансі.
        public void TriggerCacheBuild() => GetServiceTypeCache();
    }

    /// <summary>
    /// Regression guard для H3: lazy-кеш реєстру має будуватися рівно один раз навіть при
    /// конкурентному першому використанні; множинні реалізації одного інтерфейсу — з Warning.
    /// </summary>
    public class RpcServiceRegistryThreadSafetyTests
    {
        [Fact]
        public void GetServiceTypeCache_ConcurrentFirstUse_BuildsExactlyOnce()
        {
            // Arrange
            var registry = new CountingRpcServiceRegistry(
                Mock.Of<IServiceProvider>(),
                Mock.Of<ILogger>(),
                new[] { typeof(MultiImplA).Assembly });

            // Act — 64 потоки одночасно вперше тригерять побудову кешу.
            Parallel.For(0, 64, _ => registry.TriggerCacheBuild());

            // Assert — попри гонку, збірки скановано (кеш побудовано) рівно один раз.
            Assert.Equal(1, registry.TargetAssemblyScans);
        }

        [Fact]
        public void BuildServiceTypeCache_MultipleImplementations_LogsWarning()
        {
            // Arrange — збірка містить дві реалізації IMultiImplRpc.
            var logger = new Mock<ILogger>();
            // [LoggerMessage]-генеровані методи перевіряють IsEnabled перед Log — за замовчуванням
            // Mock<ILogger>.IsEnabled повертає false, тож без цього Warning ніколи б не викликався.
            logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var registry = new CountingRpcServiceRegistry(
                Mock.Of<IServiceProvider>(),
                logger.Object,
                new[] { typeof(MultiImplA).Assembly });

            // Act
            registry.TriggerCacheBuild();

            // Assert — реєстр попереджає про множинні реалізації, а не ігнорує тихо.
            logger.Verify(
                l => l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v!.ToString()!.Contains("реалізацій інтерфейсу")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }
    }
}
