using System;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace WsRpcServer.Tests.Exceptions
{
    public sealed class RpcErrorExceptionTests
    {
        [Fact]
        public void Constructor_WithErrorCodeMessageAndInnerException_SetsPropertiesCorrectly()
        {
            // Arrange
            // Подготавливаем тестовые данные: код ошибки, сообщение и внутреннее исключение
            var errorCode = JsonRpcErrorCode.InternalError;
            var message = "Test error message";
            var innerException = new InvalidOperationException("Inner exception");

            // Act
            // Создаем экземпляр исключения с помощью первого конструктора
            var exception = new RpcErrorException(errorCode, message, innerException);

            // Assert
            // Проверяем, что свойства установлены правильно
            Assert.Equal(errorCode, exception.ErrorCode);
            Assert.Equal(message, exception.Message);
            Assert.Same(innerException, exception.InnerException);
            Assert.Null(exception.ErrorData);
        }

        [Fact]
        public void Constructor_WithErrorCodeAndMessage_SetsPropertiesCorrectly()
        {
            // Arrange
            // Подготавливаем тестовые данные: код ошибки и сообщение
            var errorCode = JsonRpcErrorCode.InvalidParams;
            var message = "Invalid parameters";

            // Act
            // Создаем экземпляр исключения с первым конструктором без внутреннего исключения
            var exception = new RpcErrorException(errorCode, message);

            // Assert
            // Проверяем, что свойства установлены правильно
            Assert.Equal(errorCode, exception.ErrorCode);
            Assert.Equal(message, exception.Message);
            Assert.Null(exception.InnerException);
            Assert.Null(exception.ErrorData);
        }

        [Fact]
        public void Constructor_WithErrorCodeMessageAndErrorData_SetsPropertiesCorrectly()
        {
            // Arrange
            // Подготавливаем тестовые данные: код ошибки, сообщение и дополнительные данные об ошибке
            var errorCode = JsonRpcErrorCode.ParseError;
            var message = "JSON parse error";
            var errorData = new { Line = 10, Position = 30, Details = "Unexpected token" };

            // Act
            // Создаем экземпляр исключения с помощью второго конструктора
            var exception = new RpcErrorException(errorCode, message, errorData);

            // Assert
            // Проверяем, что свойства установлены правильно
            Assert.Equal(errorCode, exception.ErrorCode);
            Assert.Equal(message, exception.Message);
            Assert.Null(exception.InnerException);
            Assert.Same(errorData, exception.ErrorData);
        }

        [Theory]
        [InlineData(JsonRpcErrorCode.InternalError, "Internal server error")]
        [InlineData(JsonRpcErrorCode.InvalidParams, "Invalid parameters")]
        [InlineData(JsonRpcErrorCode.InvalidRequest, "Invalid request")]
        [InlineData(JsonRpcErrorCode.MethodNotFound, "Method not found")]
        [InlineData(JsonRpcErrorCode.ParseError, "Parse error")]
        public void Constructor_WithDifferentErrorCodes_SetsErrorCodeCorrectly(JsonRpcErrorCode errorCode, string message)
        {
            // Arrange
            // Использование различных кодов ошибок из перечисления JsonRpcErrorCode
            // Данные подготовлены в параметрах [InlineData]

            // Act
            // Создаем экземпляр исключения с разными кодами ошибок
            var exception = new RpcErrorException(errorCode, message);

            // Assert
            // Проверяем, что код ошибки и сообщение установлены правильно
            Assert.Equal(errorCode, exception.ErrorCode);
            Assert.Equal(message, exception.Message);
        }

        [Fact]
        public void Constructor_WithNullErrorData_AcceptsNullValue()
        {
            // Arrange
            // Подготавливаем тестовые данные с null в качестве ErrorData
            var errorCode = JsonRpcErrorCode.InternalError;
            var message = "Test error with null data";
            object? errorData = null;

            // Act
            // Создаем экземпляр исключения с null в качестве errorData
            var exception = new RpcErrorException(errorCode, message, errorData);

            // Assert
            // Проверяем, что свойства установлены правильно, включая null в ErrorData
            Assert.Equal(errorCode, exception.ErrorCode);
            Assert.Equal(message, exception.Message);
            Assert.Null(exception.ErrorData);
        }

        [Fact]
        public void ErrorData_WithComplexObject_StoresReferenceCorrectly()
        {
            // Arrange
            // Создаем сложный объект для тестирования хранения ссылок
            var complexErrorData = new ComplexErrorData
            {
                ErrorCode = 1001,
                ErrorMessage = "Complex error",
                Timestamp = DateTime.Now,
                Details = new[] { "Detail 1", "Detail 2" }
            };

            // Act
            // Создаем экземпляр исключения со сложным объектом
            var exception = new RpcErrorException(
                JsonRpcErrorCode.InternalError, 
                "Complex error occurred", 
                complexErrorData);

            // Assert
            // Проверяем, что ссылка на объект сохранена правильно
            Assert.Same(complexErrorData, exception.ErrorData);
            
            // Дополнительно проверяем, что это тот же самый экземпляр объекта
            var retrievedData = (ComplexErrorData)exception.ErrorData!;
            Assert.Equal(complexErrorData.ErrorCode, retrievedData.ErrorCode);
            Assert.Equal(complexErrorData.ErrorMessage, retrievedData.ErrorMessage);
            Assert.Equal(complexErrorData.Timestamp, retrievedData.Timestamp);
            Assert.Equal(complexErrorData.Details, retrievedData.Details);
        }

        // Вспомогательный класс для тестирования сложных данных об ошибке
        private sealed class ComplexErrorData
        {
            public int ErrorCode { get; set; }
            public string ErrorMessage { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
            public string[] Details { get; set; } = Array.Empty<string>();
        }
    }
}