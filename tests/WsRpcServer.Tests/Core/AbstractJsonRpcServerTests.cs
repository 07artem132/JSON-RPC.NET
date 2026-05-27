using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Moq;
using NetCoreServer;
using WsRpcServer.Core;
using Xunit;

namespace WsRpcServer.Tests.Core
{
    public sealed class AbstractJsonRpcServerTests
    {
        private sealed class TestJsonRpcServer : AbstractJsonRpcServer
        {
            public bool CreateJsonRpcSessionCalled { get; private set; }
            public TestJsonRpcServer(IPAddress address, int port, IServiceProvider serviceProvider, ILogger logger)
                : base(address, port, serviceProvider, logger)
            {
            }

            protected override WsSession CreateJsonRpcSession()
            {
                CreateJsonRpcSessionCalled = true;
                return new Mock<WsSession>(this).Object;
            }
            
            public void TestOnError(SocketError error)
            {
                OnError(error);
            }
            
            public bool OnServerErrorCalled { get; private set; }
            public SocketError LastError { get; private set; }
            
            protected override void OnServerError(SocketError error)
            {
                OnServerErrorCalled = true;
                LastError = error;
                base.OnServerError(error);
            }
        }

        private readonly Mock<IServiceProvider> _mockServiceProvider;
        private readonly Mock<ILogger> _mockLogger;
        private readonly IPAddress _testIpAddress = IPAddress.Loopback;
        private readonly int _testPort = 9000;

        public AbstractJsonRpcServerTests()
        {
            _mockServiceProvider = new Mock<IServiceProvider>();
            _mockLogger = new Mock<ILogger>();
        }

        [Fact]
        public void Constructor_ValidParameters_InitializesCorrectly()
        {
            // Arrange & Act
            var server = new TestJsonRpcServer(
                _testIpAddress,
                _testPort,
                _mockServiceProvider.Object,
                _mockLogger.Object);

            // Assert
            Assert.NotNull(server);
        }

        [Fact]
        public void Constructor_NullServiceProvider_ThrowsArgumentNullException()
        {
            // Arrange, Act & Assert
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new TestJsonRpcServer(
                    _testIpAddress,
                    _testPort,
                    null!,
                    _mockLogger.Object));

            Assert.Equal("serviceProvider", exception.ParamName);
        }

        [Fact]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            // Arrange, Act & Assert
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new TestJsonRpcServer(
                    _testIpAddress,
                    _testPort,
                    _mockServiceProvider.Object,
                    null!));

            Assert.Equal("logger", exception.ParamName);
        }

        [Fact]
        public void CreateSession_Called_InvokesCreateJsonRpcSession()
        {
            // Arrange
            var server = new TestJsonRpcServer(
                _testIpAddress,
                _testPort,
                _mockServiceProvider.Object,
                _mockLogger.Object);

            // Act
            var session = typeof(WsServer)
                .GetMethod("CreateSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(server, null);

            // Assert
            Assert.True(server.CreateJsonRpcSessionCalled);
            Assert.NotNull(session);
        }

        [Fact]
        public void OnError_Called_LogsErrorAndCallsOnServerError()
        {
            // Arrange
            var server = new TestJsonRpcServer(
                _testIpAddress,
                _testPort,
                _mockServiceProvider.Object,
                _mockLogger.Object);

            // Set up logger verification
            _mockLogger.Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()));

            // Act
            var errorType = SocketError.ConnectionRefused;
            server.TestOnError(errorType);

            // Assert
            _mockLogger.Verify(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v!.ToString()!.Contains(errorType.ToString())),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
                Times.Once);
            
            Assert.True(server.OnServerErrorCalled);
            Assert.Equal(errorType, server.LastError);
        }
    }
}