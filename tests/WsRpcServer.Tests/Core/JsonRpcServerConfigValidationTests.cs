using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WsRpcServer.Core;
using WsRpcServer.Extensions;
using Xunit;

namespace WsRpcServer.Tests.Core
{
    /// <summary>
    /// Regression guard для M5: <see cref="JsonRpcServerConfig"/> має fail-fast валідацію через
    /// options-pattern. Невалідні значення (порт поза діапазоном, порожній host, недодатні розміри
    /// черг/буферів/лічильників, недодатній таймаут) мають кидати
    /// <see cref="OptionsValidationException"/> при резолві з контейнера.
    /// </summary>
    public sealed class JsonRpcServerConfigValidationTests
    {
        // Резолвимо валідовані опції — саме доступ до .Value запускає валідатори.
        private static JsonRpcServerConfig ResolveConfig(Action<JsonRpcServerConfig> configure)
        {
            var services = new ServiceCollection();
            services.AddJsonRpcCore(configure);
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IOptions<JsonRpcServerConfig>>().Value;
        }

        [Fact]
        public void DefaultConfig_PassesValidation()
        {
            var config = ResolveConfig(_ => { });

            Assert.Equal("0.0.0.0", config.Host);
            Assert.Equal(9000, config.Port);
            Assert.Equal(10, config.MaxConsecutiveParseFailures);
        }

        [Fact]
        public void DirectlyResolvedConfig_IsAlsoValidatedInstance()
        {
            var services = new ServiceCollection();
            services.AddJsonRpcCore(c => c.Port = 0);
            using var provider = services.BuildServiceProvider();

            // Пряма резолюція JsonRpcServerConfig теж проходить через валідовані опції.
            Assert.Throws<OptionsValidationException>(() =>
            {
                _ = provider.GetRequiredService<JsonRpcServerConfig>();
            });
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(65536)]
        [InlineData(100000)]
        public void InvalidPort_ThrowsOptionsValidationException(int port)
        {
            Assert.Throws<OptionsValidationException>(() => ResolveConfig(c => c.Port = port));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void EmptyOrNullHost_ThrowsOptionsValidationException(string? host)
        {
            Assert.Throws<OptionsValidationException>(() => ResolveConfig(c => c.Host = host!));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void NonPositiveNotificationQueueSize_ThrowsOptionsValidationException(int size)
        {
            Assert.Throws<OptionsValidationException>(() => ResolveConfig(c => c.NotificationQueueSize = size));
        }

        [Fact]
        public void NonPositiveMaxMessageSize_ThrowsOptionsValidationException()
        {
            Assert.Throws<OptionsValidationException>(() => ResolveConfig(c => c.MaxMessageSizeBytes = 0));
        }

        [Fact]
        public void NonPositivePipeThreshold_ThrowsOptionsValidationException()
        {
            Assert.Throws<OptionsValidationException>(() => ResolveConfig(c => c.PipeThresholdBytes = 0));
        }

        [Fact]
        public void NonPositiveMaxConsecutiveParseFailures_ThrowsOptionsValidationException()
        {
            Assert.Throws<OptionsValidationException>(() => ResolveConfig(c => c.MaxConsecutiveParseFailures = 0));
        }

        [Fact]
        public void NonPositiveNotificationTimeout_ThrowsOptionsValidationException()
        {
            Assert.Throws<OptionsValidationException>(() => ResolveConfig(c => c.NotificationTimeout = TimeSpan.Zero));
        }

        [Fact]
        public void ValidCustomConfig_PassesValidation()
        {
            var config = ResolveConfig(c =>
            {
                c.Host = "127.0.0.1";
                c.Port = 8443;
                c.NotificationQueueSize = 1;
                c.MaxConsecutiveParseFailures = 1;
                c.NotificationTimeout = TimeSpan.FromMilliseconds(1);
            });

            Assert.Equal("127.0.0.1", config.Host);
            Assert.Equal(8443, config.Port);
        }
    }
}
