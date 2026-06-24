using System.Net.WebSockets;
using System.Security.Claims;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NetCoreServer;
using StreamJsonRpc;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Logging;
using WsRpcServer.Security;

namespace WsRpcServer.Sessions;

/// <summary>
/// Абстрактний базовий клас для ЗАХИЩЕНИХ (TLS / mTLS) сесій JSON-RPC WebSocket.
/// Дзеркалить <see cref="AbstractJsonRpcSession"/>, але над <see cref="WssSession"/> (SSL-гілка
/// NetCoreServer), і додає <see cref="Principal"/> з mTLS-ідентичності.
/// </summary>
/// <param name="server">Захищений сервер, до якого належить сесія.</param>
/// <param name="logger">Логер.</param>
/// <param name="config">Конфігурація сервера.</param>
/// <remarks>
/// NetCoreServer розводить плейн-текст (<c>WsSession</c>) і TLS (<c>WssSession</c>) в окремі ієрархії
/// без спільного WS-базового типу, тож інфраструктура сповіщень тут навмисно дублює
/// <see cref="AbstractJsonRpcSession"/> (та сама семантика каналу/таймауту/відмови). Це транспортна
/// «glue»-ланка, яку фреймворк бере на себе, щоб споживач писав лише бізнес-логіку.
/// </remarks>
public abstract class AbstractSecureJsonRpcSession(
    AbstractSecureJsonRpcServer server,
    ILogger logger,
    JsonRpcServerConfig config)
    : WssSession(server), IJsonRpcSession
{
    /// <summary>Захищений сервер, що володіє сесією.</summary>
    protected AbstractSecureJsonRpcServer SecureServer { get; } =
        server ?? throw new ArgumentNullException(nameof(server));

    /// <summary>Логер сесії.</summary>
    protected ILogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Джерело токенів скасування для життєвого циклу сесії.</summary>
    protected CancellationTokenSource Cts { get; } = new();

    /// <summary>Канал черги сповіщень клієнту (DropOldest backpressure).</summary>
    protected Channel<RpcNotification> NotificationChannel { get; } = Channel.CreateBounded<RpcNotification>(
        new BoundedChannelOptions(config.NotificationQueueSize)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    /// <summary>Конфігурація сервера.</summary>
    protected JsonRpcServerConfig Config { get; } = config ?? throw new ArgumentNullException(nameof(config));

    /// <summary>Екземпляр JSON-RPC (ініціалізується у похідному класі після рукостискання).</summary>
    protected JsonRpc? JsonRpc { get; set; }

    /// <summary>Завдання фонової обробки сповіщень.</summary>
    protected Task? NotificationProcessingTask { get; set; }

    /// <summary>
    /// Principal сесії з валідованої mTLS-ідентичності. <c>null</c> до встановлення
    /// (<see cref="TryEstablishPrincipal"/>) або якщо ідентичність не виведено.
    /// </summary>
    protected ClaimsPrincipal? Principal { get; private set; }

    /// <summary>
    /// Резолвить валідовану <see cref="NodeIdentity"/> для цього з'єднання й будує <see cref="Principal"/>.
    /// Викликати в <c>OnWsConnected</c> ДО <c>RegisterServices</c>/<c>StartListening</c>.
    /// </summary>
    /// <returns><c>true</c>, якщо ідентичність виведено й <see cref="Principal"/> встановлено.</returns>
    protected bool TryEstablishPrincipal()
    {
        if (SecureServer.TryResolveNodeIdentity(this, out var identity))
        {
            Principal = NodeIdentityPrincipalFactory.Create(identity);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Ставить сповіщення в чергу на доставку клієнту (НЕ чекає на фактичну відправку).
    /// </summary>
    /// <param name="method">Ім'я методу сповіщення.</param>
    /// <param name="args">Аргументи сповіщення.</param>
    /// <returns>Уже завершене <see cref="Task"/> — лише постановка в чергу.</returns>
    public virtual Task SendNotificationAsync(string method, params object[] args)
    {
        if (IsDisposed || Cts.IsCancellationRequested)
        {
            AbstractJsonRpcSessionLog.NotificationSkippedClosedSession(Logger, method, Id);
            return Task.CompletedTask;
        }

        if (!NotificationChannel.Writer.TryWrite(new RpcNotification(method, args)))
        {
            AbstractJsonRpcSessionLog.NotificationChannelFull(Logger, method, Id);
        }
        else
        {
            AbstractJsonRpcSessionLog.NotificationQueued(Logger, method, Id);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Запускає фонову обробку черги сповіщень.
    /// </summary>
    /// <param name="cancellationToken">Токен скасування.</param>
    /// <returns>Завдання обробки сповіщень.</returns>
    protected async Task ProcessNotificationsAsync(CancellationToken cancellationToken)
    {
        AbstractJsonRpcSessionLog.NotificationProcessingStarted(Logger, Id);

        try
        {
            await foreach (var notification in NotificationChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (JsonRpc == null)
                {
                    AbstractJsonRpcSessionLog.NotificationNoJsonRpc(Logger, notification.Method, Id);
                    break;
                }

                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(Config.NotificationTimeout);

                    await JsonRpc.NotifyAsync(notification.Method, notification.Arguments)
                        .WaitAsync(timeoutCts.Token).ConfigureAwait(false);

                    AbstractJsonRpcSessionLog.NotificationSent(Logger, notification.Method, Id);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    AbstractJsonRpcSessionLog.NotificationProcessingCanceled(Logger, Id);
                    break;
                }
                catch (Exception ex)
                {
                    AbstractJsonRpcSessionLog.NotificationSendError(Logger, ex, notification.Method, Id);

                    if (ex is ConnectionLostException)
                    {
                        AbstractJsonRpcSessionLog.ConnectionLostStopping(Logger, Id);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbstractJsonRpcSessionLog.NotificationProcessingCanceled(Logger, Id);
        }
        catch (Exception ex)
        {
            AbstractJsonRpcSessionLog.NotificationLoopError(Logger, ex, Id);
        }

        AbstractJsonRpcSessionLog.NotificationProcessingFinished(Logger, Id);
    }

    /// <summary>
    /// Закриває WebSocket з'єднання з вказаним статусом та причиною.
    /// </summary>
    /// <param name="status">Статус закриття WebSocket.</param>
    /// <param name="reason">Причина закриття.</param>
    public virtual void Close(WebSocketCloseStatus status, string reason)
    {
        try
        {
            AbstractJsonRpcSessionLog.ClosingConnection(Logger, status, reason, Id);
            Close((int)status, reason);
        }
        catch (Exception ex)
        {
            AbstractJsonRpcSessionLog.CloseError(Logger, ex, Id);
        }
    }

    /// <summary>
    /// Передає бінарні дані у вихідний буфер WebSocket (НЕ чекає на підтвердження відправки).
    /// </summary>
    /// <param name="data">Дані для надсилання.</param>
    /// <returns>Уже завершене <see cref="Task"/>.</returns>
    public virtual Task SendBinaryDataAsync(ReadOnlyMemory<byte> data)
    {
        if (IsDisposed)
        {
            AbstractJsonRpcSessionLog.BinarySendAfterDispose(Logger, Id);
            return Task.CompletedTask;
        }

        try
        {
            AbstractJsonRpcSessionLog.SendingBinaryData(Logger, data.Length, Id);
            SendBinaryAsync(data.Span);
        }
        catch (Exception ex)
        {
            AbstractJsonRpcSessionLog.BinarySendError(Logger, ex, data.Length, Id);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Обробляє WebSocket ping (логує + надсилає pong через базову реалізацію).
    /// </summary>
    /// <param name="buffer">Буфер ping.</param>
    /// <param name="offset">Зміщення.</param>
    /// <param name="size">Розмір.</param>
    public override void OnWsPing(byte[] buffer, long offset, long size)
    {
        AbstractJsonRpcSessionLog.PingReceived(Logger, Id);
        base.OnWsPing(buffer, offset, size);
    }

    /// <summary>
    /// Звільняє ресурси сесії: спершу скасування, далі дренаж фонової задачі, потім <c>Cts.Dispose</c> (H4).
    /// </summary>
    /// <param name="disposingManagedResources">Чи звільняти керовані ресурси.</param>
    protected override void Dispose(bool disposingManagedResources)
    {
        if (disposingManagedResources)
        {
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            NotificationChannel.Writer.TryComplete();

            try
            {
                NotificationProcessingTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException or ObjectDisposedException)
            {
            }

            Cts.Dispose();
        }

        base.Dispose(disposingManagedResources);
    }
}
