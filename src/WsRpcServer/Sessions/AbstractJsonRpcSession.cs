using System.Net.WebSockets;
using System.Security.Claims;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NetCoreServer;
using StreamJsonRpc;
using WsRpcServer.Core;
using WsRpcServer.Diagnostics;
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
    /// Principal сесії, виведений з mTLS-ідентичності вузла (захищений транспорт).
    /// </summary>
    /// <remarks>
    /// На плейн-текст транспорті лишається <c>null</c> (немає автентифікації). На захищеному
    /// (<see cref="Core.AbstractSecureJsonRpcServer"/>) встановлюється у похідній сесії до
    /// <c>RegisterServices</c>/<c>StartListening</c> і передається у примус <c>[RpcAuthorize]</c>.
    /// </remarks>
    protected ClaimsPrincipal? Principal { get; set; }

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
    /// Ставить сповіщення в чергу на доставку клієнту (НЕ чекає на фактичну відправку).
    /// </summary>
    /// <param name="method">Ім'я методу сповіщення.</param>
    /// <param name="args">Аргументи сповіщення.</param>
    /// <returns>
    /// Уже завершене <see cref="Task"/>: метод лише записує сповіщення в канал і повертається миттєво.
    /// <c>await</c> на ньому НЕ чекає на доставку клієнту — фактична відправка відбувається у фоновому
    /// циклі <see cref="ProcessNotificationsAsync"/> (L4: сигнатура асинхронна заради сумісності інтерфейсу,
    /// але тіло синхронне — це постановка в чергу, а не відправка).
    /// </returns>
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
            WsRpcServerDiagnostics.Notification(dropped: true);
        }
        else
        {
            AbstractJsonRpcSessionLog.NotificationQueued(Logger, method, Id);
            WsRpcServerDiagnostics.Notification(dropped: false);
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
            await foreach (var notification in NotificationChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
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
                        .WaitAsync(timeoutCts.Token).ConfigureAwait(false);

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
    /// Передає бінарні дані у вихідний буфер WebSocket (НЕ чекає на підтвердження відправки).
    /// </summary>
    /// <param name="data">Дані для надсилання.</param>
    /// <returns>
    /// Уже завершене <see cref="Task"/>: метод викликає синхронний <c>SendBinaryAsync</c> NetCoreServer
    /// і повертається миттєво. <c>await</c> на ньому НЕ гарантує, що дані вже залишили сокет (L4).
    /// </returns>
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
    /// Передає дані у вихідний буфер WebSocket ТЕКСТОВИМ кадром (симетрично <see cref="SendBinaryDataAsync"/>).
    /// </summary>
    /// <param name="data">Дані (UTF-8 текст) для надсилання.</param>
    /// <returns>
    /// Уже завершене <see cref="Task"/>: метод викликає синхронний <c>SendTextAsync</c> NetCoreServer
    /// і повертається миттєво (та сама L4-семантика, що й у <see cref="SendBinaryDataAsync"/>).
    /// </returns>
    /// <remarks>
    /// Використовується транспортом, коли ввімкнено
    /// <see cref="JsonRpcServerConfig.UseTextFramesForOutgoingMessages"/>: WHATWG-браузерний клієнт для
    /// JSON очікує текстовий кадр (<c>event.data</c> = <c>string</c>, а не <c>Blob</c>). Дефолт лишає
    /// бінарні кадри (<see cref="SendBinaryDataAsync"/>) для зворотної сумісності.
    /// </remarks>
    public virtual Task SendTextDataAsync(ReadOnlyMemory<byte> data)
    {
        if (IsDisposed)
        {
            AbstractJsonRpcSessionLog.TextSendAfterDispose(Logger, Id);
            return Task.CompletedTask;
        }

        try
        {
            AbstractJsonRpcSessionLog.SendingTextData(Logger, data.Length, Id);

            // Використовуємо вбудований метод NetCoreServer для надсилання текстового кадру
            SendTextAsync(data.Span);
        }
        catch (Exception ex)
        {
            AbstractJsonRpcSessionLog.TextSendError(Logger, ex, data.Length, Id);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Хук узгодження WebSocket-субпротоколу під час 101-upgrade (browser-interop, opt-in).
    /// </summary>
    /// <param name="offeredSubprotocols">Субпротоколи, запропоновані клієнтом у <c>Sec-WebSocket-Protocol</c>
    /// (кілька заголовків АБО comma-list; може бути порожнім).</param>
    /// <returns>
    /// Субпротокол для echo назад клієнту (зазвичай з-поміж <paramref name="offeredSubprotocols"/>),
    /// або <c>null</c> (за замовчуванням) — тоді 101-відповідь не змінюється (поведінка як раніше).
    /// </returns>
    /// <remarks>
    /// WHATWG-браузерний <c>WebSocket</c>, що передає субпротокол у конструкторі, вимагає, щоб сервер
    /// echo-нув узгоджений субпротокол у 101-відповіді, інакше з'єднання не досягне open. Споживач
    /// override-ить цей хук; базова реалізація повертає <c>null</c> (нічого не узгоджено) — тож без
    /// override поведінка фреймворку незмінна.
    /// </remarks>
    protected virtual string? NegotiateSubprotocol(IReadOnlyList<string> offeredSubprotocols) => null;

    /// <summary>
    /// Перехоплює завершення WebSocket-рукостискання (101-upgrade), щоб за потреби узгодити субпротокол
    /// і коректно echo-нути його у відповідь.
    /// </summary>
    /// <param name="request">HTTP-запит рукостискання.</param>
    /// <param name="response">HTTP-відповідь 101 Switching Protocols.</param>
    /// <returns><c>true</c>, якщо рукостискання дозволено (як і базова реалізація).</returns>
    /// <remarks>
    /// Якщо <see cref="NegotiateSubprotocol"/> повертає не-<c>null</c>, база ПОВНІСТЮ перебудовує
    /// 101-відповідь із заголовком <c>Sec-WebSocket-Protocol</c> серед заголовків (до тіла) за RFC 6455 —
    /// це інкапсулює обхід дефекту NetCoreServer 8.0.7, де <c>OnWsConnecting</c> викликається ПІСЛЯ
    /// <c>SetBody()</c>, тож наївний <c>SetHeader</c> «протік» би у потік як сміття-кадр. Якщо хук дав
    /// <c>null</c> — повертаємо <c>base.OnWsConnecting(...)</c> (поведінка незмінна).
    /// </remarks>
    public override bool OnWsConnecting(HttpRequest request, HttpResponse response)
    {
        var offered = WsUpgradeInterop.ParseOfferedSubprotocols(request);
        var negotiated = NegotiateSubprotocol(offered);

        if (!string.IsNullOrEmpty(negotiated) &&
            WsUpgradeInterop.TryWriteUpgradeResponse(request, response, negotiated))
        {
            AbstractJsonRpcSessionLog.SubprotocolNegotiated(Logger, negotiated, Id);
            return true;
        }

        return base.OnWsConnecting(request, response);
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
    /// L3: метод позначено "override" (а не "new") — у поточній версії NetCoreServer
    /// WsSession.OnWsPing віртуальний, тож фреймворк викликає САМЕ цю реалізацію через посилання
    /// на базовий тип. З "new" наш код мовчки оминався б на внутрішньому шляху диспетчеризації ping'а.
    /// Інваріант «база лишається virtual» пінить WsSessionOnWsPingGuardTests.
    /// </remarks>
    public override void OnWsPing(byte[] buffer, long offset, long size)
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