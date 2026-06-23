using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NetCoreServer;
using StreamJsonRpc;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Logging;

namespace WsRpcServer.Sessions;

/// <summary>
/// Абстрактний базовий клас для сесій JSON-RPC WebSocket.
/// Реалізує базову логіку для обробки JSON-RPC повідомлень через WebSocket.
/// </summary>
/// <param name="server">Екземпляр WsServer, до якого належить сесія.</param>
/// <param name="logger">Логер для реєстрації подій сесії.</param>
/// <param name="config">Конфігурація JSON-RPC сервера.</param>
/// <remarks>
/// Цей клас поєднує функціональність NetCoreServer.WsSession для обробки WebSocket з'єднань
/// та StreamJsonRpc для обробки JSON-RPC повідомлень. Він забезпечує асинхронну, неблокуючу
/// обробку повідомлень з використанням каналів (System.Threading.Channels).
/// 
/// Використання каналів для черги сповіщень забезпечує:
/// 1. Асинхронну, неблокуючу обробку сповіщень
/// 2. Контроль потоку даних (backpressure) через обмеження розміру черги
/// 3. Надійну доставку сповіщень з можливістю скасування при таймауті
/// 
/// Базовий клас реалізує шаблон проектування "Template Method", де загальна логіка
/// визначена тут, а специфічні деталі мають бути реалізовані у похідних класах.
/// </remarks>
public abstract class AbstractJsonRpcSession(
    WsServer server,
    ILogger logger,
    JsonRpcServerConfig config)
    : WsSession(server), IJsonRpcSession
{
    /// <summary>
    /// Логер для реєстрації подій сесії.
    /// </summary>
    protected ILogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Джерело токенів скасування для контролю життєвого циклу сесії.
    /// </summary>
    /// <remarks>
    /// Використовується для координації зупинки фонових завдань при закритті сесії.
    /// </remarks>
    protected CancellationTokenSource Cts { get; } = new();

    /// <summary>
    /// Канал для черги сповіщень, які мають бути відправлені клієнту.
    /// </summary>
    /// <remarks>
    /// Використання Channel.CreateBounded з BoundedChannelFullMode.DropOldest забезпечує:
    /// - Обмеження максимального розміру черги для запобігання витоку пам'яті
    /// - Відкидання найстаріших сповіщень при переповненні, що забезпечує контроль потоку даних
    /// - Асинхронну, неблокуючу обробку сповіщень через модель producer-consumer
    ///
    /// SingleReader: true - гарантує, що лише один потік читає з каналу, що спрощує обробку
    /// SingleWriter: false - дозволяє багатьом потокам додавати сповіщення до черги
    /// </remarks>
    protected Channel<RpcNotification> NotificationChannel { get; } = Channel.CreateBounded<RpcNotification>(
        new BoundedChannelOptions(config.NotificationQueueSize)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    /// <summary>
    /// Конфігурація JSON-RPC сервера.
    /// </summary>
    protected JsonRpcServerConfig Config { get; } = config ?? throw new ArgumentNullException(nameof(config));

    /// <summary>
    /// Екземпляр JSON-RPC для обробки повідомлень.
    /// </summary>
    /// <remarks>
    /// Ініціалізується у похідному класі після встановлення WebSocket з'єднання.
    /// Null до моменту завершення рукостискання WebSocket та ініціалізації JSON-RPC.
    /// </remarks>
    protected JsonRpc? JsonRpc { get; set; }

    /// <summary>
    /// Завдання для фонової обробки сповіщень.
    /// </summary>
    /// <remarks>
    /// Зберігає посилання на асинхронне завдання, яке обробляє чергу сповіщень.
    /// Використовується для відстеження статусу обробки та коректного завершення.
    /// </remarks>
    protected Task? NotificationProcessingTask { get; set; }

    /// <summary>
    /// Надсилає сповіщення клієнту.
    /// </summary>
    /// <param name="method">Ім'я методу сповіщення.</param>
    /// <param name="args">Аргументи сповіщення.</param>
    /// <returns>Завдання, яке представляє асинхронну операцію відправки.</returns>
    /// <remarks>
    /// Замість безпосередньої відправки, сповіщення додається до черги (NotificationChannel),
    /// яка потім обробляється у фоновому режимі методом ProcessNotificationsAsync.
    /// 
    /// Це забезпечує:
    /// 1. Неблокуючу відправку сповіщень (метод повертається миттєво)
    /// 2. Буферизацію сповіщень, якщо клієнт не встигає їх обробляти
    /// 3. Контроль потоку даних через обмеження розміру черги
    /// 
    /// Якщо черга заповнена, найстаріші сповіщення відкидаються (BoundedChannelFullMode.DropOldest),
    /// що запобігає блокуванню потоків відправки при повільному клієнті.
    /// </remarks>
    public virtual Task SendNotificationAsync(string method, params object[] args)
    {
        if (IsDisposed || Cts.IsCancellationRequested)
        {
            AbstractJsonRpcSessionLog.NotificationSkippedClosedSession(Logger, method, Id);
            return Task.CompletedTask;
        }

        // Використовуємо TryWrite, щоб уникнути блокування, якщо канал заповнений
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
    /// Запускає обробку сповіщень у фоновому режимі.
    /// </summary>
    /// <param name="cancellationToken">Токен скасування для зупинки обробки.</param>
    /// <returns>Завдання, яке представляє асинхронну операцію обробки сповіщень.</returns>
    /// <remarks>
    /// Цей метод запускає цикл, який:
    /// 1. Чекає на нові сповіщення з каналу NotificationChannel
    /// 2. Відправляє їх клієнту через JsonRpc.NotifyAsync
    /// 3. Обробляє помилки та таймаути
    /// 
    /// Важливі аспекти реалізації:
    /// - Використання await foreach для асинхронного читання з каналу
    /// - Використання таймаутів для запобігання блокуванню при відправці
    /// - Коректна обробка помилок, включаючи ConnectionLostException
    /// - Продовження роботи навіть при помилках з окремими сповіщеннями
    /// 
    /// Метод завершується, коли:
    /// - Канал закривається (NotificationChannel.Writer.Complete())
    /// - Виникає ConnectionLostException (з'єднання з клієнтом втрачено)
    /// - Відбувається скасування через токен cancellationToken
    /// </remarks>
    protected async Task ProcessNotificationsAsync(CancellationToken cancellationToken)
    {
        AbstractJsonRpcSessionLog.NotificationProcessingStarted(Logger, Id);

        try
        {
            await foreach (var notification in NotificationChannel.Reader.ReadAllAsync(cancellationToken))
            {
                if (JsonRpc == null)
                {
                    AbstractJsonRpcSessionLog.NotificationNoJsonRpc(Logger, notification.Method, Id);
                    break;
                }

                try
                {
                    // Використовуємо таймаут для надсилання сповіщень
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(Config.NotificationTimeout);

                    // Використовуємо StreamJsonRpc для правильного форматування JSON-RPC сповіщень
                    await JsonRpc.NotifyAsync(notification.Method, notification.Arguments)
                        .WaitAsync(timeoutCts.Token);

                    AbstractJsonRpcSessionLog.NotificationSent(Logger, notification.Method, Id);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Сесія завершується, виходимо з методу
                    AbstractJsonRpcSessionLog.NotificationProcessingCanceled(Logger, Id);
                    break;
                }
                catch (Exception ex)
                {
                    AbstractJsonRpcSessionLog.NotificationSendError(Logger, ex, notification.Method, Id);

                    // Якщо отримано занадто багато помилок, вважаємо з'єднання втраченим
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
            // Нормальне скасування, логуємо на рівні відлагодження
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
    /// <param name="reason">Причина закриття з'єднання.</param>
    /// <remarks>
    /// Метод передає відповідний код статусу та причину клієнту при закритті з'єднання.
    /// Це дозволяє клієнту розуміти, чому з'єднання було закрито (наприклад, нормальне закриття,
    /// помилка протоколу, тощо).
    /// 
    /// Внутрішньо використовує метод Close з базового класу NetCoreServer.WsSession.
    /// </remarks>
    public virtual void Close(WebSocketCloseStatus status, string reason)
    {
        try
        {
            AbstractJsonRpcSessionLog.ClosingConnection(Logger, status, reason, Id);

            // Використовуємо NetCoreServer API для закриття з'єднання з правильним статусом
            Close((int)status, reason);
        }
        catch (Exception ex)
        {
            AbstractJsonRpcSessionLog.CloseError(Logger, ex, Id);
        }
    }

    /// <summary>
    /// Надсилає бінарні дані через WebSocket з'єднання.
    /// </summary>
    /// <param name="data">Дані для надсилання.</param>
    /// <returns>Завдання, яке представляє асинхронну операцію відправки.</returns>
    /// <remarks>
    /// Цей метод забезпечує низькорівневий доступ до відправки бінарних даних через WebSocket.
    /// Використовується для випадків, коли потрібна ефективна передача бінарних даних без 
    /// накладних витрат на серіалізацію JSON.
    /// 
    /// Внутрішньо використовує метод SendBinaryAsync з базового класу NetCoreServer.WsSession.
    /// </remarks>
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

            // Використовуємо вбудований метод NetCoreServer для надсилання бінарних даних
            SendBinaryAsync(data.Span);
        }
        catch (Exception ex)
        {
            AbstractJsonRpcSessionLog.BinarySendError(Logger, ex, data.Length, Id);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Обробляє WebSocket повідомлення ping.
    /// Викликається NetCoreServer при отриманні ping кадру.
    /// </summary>
    /// <param name="buffer">Буфер з даними ping повідомлення.</param>
    /// <param name="offset">Зміщення в буфері.</param>
    /// <param name="size">Розмір даних.</param>
    /// <remarks>
    /// Перевизначення методу OnWsPing з базового класу NetCoreServer.WsSession.
    /// Викликає базову реалізацію, яка автоматично надсилає pong відповідь.
    /// 
    /// Додатково логує отримання ping повідомлення для діагностики.
    /// 
    /// Використання ключового слова "new" замість "override" пов'язано з тим,
    /// що базовий клас (NetCoreServer.WsSession) не позначає цей метод як virtual.
    /// </remarks>
    public new void OnWsPing(byte[] buffer, long offset, long size)
    {
        AbstractJsonRpcSessionLog.PingReceived(Logger, Id);

        // Дозволяємо NetCoreServer автоматично надіслати pong відповідь
        base.OnWsPing(buffer, offset, size);
    }

    /// <summary>
    /// Звільняє ресурси, що використовуються сесією.
    /// </summary>
    /// <param name="disposingManagedResources">Вказує, чи викликається метод явно (true) чи з фіналізатора (false).</param>
    /// <remarks>
    /// Перевизначення методу Dispose з базового класу для забезпечення коректного звільнення ресурсів:
    /// - Скасовує всі асинхронні операції через Cts
    /// - Завершує канал сповіщень
    /// - Викликає базову реалізацію для звільнення ресурсів базового класу
    /// 
    /// Важливо для похідних класів викликати базову реалізацію при перевизначенні.
    /// </remarks>
    protected override void Dispose(bool disposingManagedResources)
    {
        if (disposingManagedResources)
        {
            // H4: спершу сигналізуємо скасування, потім завершуємо канал і дренуємо фонову задачу,
            // і лише після цього звільняємо Cts. Якщо звільнити Cts до скасування — фонова задача
            // лишається "сиротою", а linked-токени всередині неї кидають ObjectDisposedException.
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Повторний Dispose — ідемпотентність.
            }

            NotificationChannel.Writer.TryComplete();

            // Найкраще-зусилля: дочекатися завершення обробки сповіщень. Обмежуємо за часом, бо
            // WsSession.Dispose синхронний (NetCoreServer керує життєвим циклом) — після скасування
            // задача завершується майже миттєво; таймаут лише страхує від теоретичного зависання.
            try
            {
                NotificationProcessingTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException or ObjectDisposedException)
            {
                // Скасування очікуване; будь-які помилки самої задачі вже залоговані у циклі обробки.
            }

            Cts.Dispose();
        }

        base.Dispose(disposingManagedResources);
    }
}