namespace WsRpcServer.Core;

using Microsoft.Extensions.Logging;
using NetCoreServer;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using WsRpcServer.Logging;
using WsRpcServer.Security;

/// <summary>
/// Абстрактний базовий клас для ЗАХИЩЕНИХ (TLS / mTLS) JSON-RPC WebSocket серверів.
/// Наслідується від <see cref="WssServer"/> (NetCoreServer) і обслуговує <c>wss://</c>.
/// </summary>
/// <remarks>
/// Дзеркалить <see cref="AbstractJsonRpcServer"/>: той самий розширювальний шов
/// <see cref="CreateJsonRpcSession"/>, лише з SSL-базою та <see cref="SslContext"/>. Плейн-текст
/// <see cref="AbstractJsonRpcServer"/> лишається незмінним — TLS це opt-in additive-можливість.
///
/// Сесія дістає валідовану <see cref="NodeIdentity"/> через <see cref="TryResolveNodeIdentity"/>
/// (кореляція за SSL-потоком, заповнена під час mTLS-рукостискання у <see cref="SecureTransport"/>).
/// </remarks>
public abstract class AbstractSecureJsonRpcServer : WssServer
{
    private readonly SecureTransport _transport;
    private readonly ILogger _logger;

    /// <summary>
    /// Створює захищений сервер.
    /// </summary>
    /// <param name="transport">Зібраний TLS-транспорт (<see cref="SslContext"/> + кореляція ідентичності).</param>
    /// <param name="address">IP-адреса прослуховування.</param>
    /// <param name="port">Порт.</param>
    /// <param name="serviceProvider">Постачальник сервісів (для створення сесій).</param>
    /// <param name="logger">Логер.</param>
    protected AbstractSecureJsonRpcServer(
        SecureTransport transport,
        IPAddress address,
        int port,
        IServiceProvider serviceProvider,
        ILogger logger)
        : base((transport ?? throw new ArgumentNullException(nameof(transport))).Context, address, port)
    {
        _transport = transport;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        AbstractSecureJsonRpcServerLog.SecureServerInitialized(
            _logger, transport.Context.ClientCertificateRequired, transport.Context.Protocols);
    }

    /// <summary>
    /// Постачальник сервісів для створення екземплярів залежностей.
    /// </summary>
    protected IServiceProvider ServiceProvider { get; }

    /// <inheritdoc />
    protected override SslSession CreateSession()
    {
        return CreateJsonRpcSession();
    }

    /// <summary>
    /// Створює нову спеціалізовану захищену сесію JSON-RPC (нащадок <see cref="WssSession"/>).
    /// </summary>
    /// <returns>Спеціалізований екземпляр <see cref="WssSession"/> для обробки JSON-RPC повідомлень.</returns>
    protected abstract WssSession CreateJsonRpcSession();

    /// <summary>
    /// Спроба дістати валідовану <see cref="NodeIdentity"/> для сесії (за її SSL-потоком).
    /// </summary>
    /// <param name="session">Захищена сесія.</param>
    /// <param name="identity">Виведена ідентичність, якщо знайдена.</param>
    /// <returns><c>true</c>, якщо ідентичність знайдено (валідне mTLS-рукостискання відбулося).</returns>
    public bool TryResolveNodeIdentity(WssSession session, out NodeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (SslSessionInterop.TryGetSslStream(session, out var sslStream))
        {
            return _transport.TryGetIdentity(sslStream, out identity);
        }

        identity = default;
        return false;
    }

    /// <inheritdoc />
    protected override void OnError(SocketError error)
    {
        AbstractSecureJsonRpcServerLog.ServerError(_logger, error);
        OnServerError(error);
    }

    /// <summary>
    /// Викликається при виникненні помилки сервера. Дозволяє похідним класам реагувати особливим чином.
    /// </summary>
    /// <param name="error">Помилка сокета.</param>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "C#-only project; renaming would break virtual-method overrides in consumer code.")]
    protected virtual void OnServerError(SocketError error)
    {
    }
}
