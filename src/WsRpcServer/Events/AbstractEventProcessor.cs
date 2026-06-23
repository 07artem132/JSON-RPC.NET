using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WsRpcServer.Core;
using WsRpcServer.Logging;

namespace WsRpcServer.Events;

/// <summary>
/// Абстрактний базовий клас для обробників подій.
/// Реалізує базову логіку для управління клієнтами та їх сповіщеннями про події.
/// </summary>
/// <param name="logger">Логер для реєстрації подій обробника.</param>
/// <param name="maxConsecutiveNotificationFailures">
/// Поріг послідовних невдач доставки сповіщень одному клієнту, після якого клієнт
/// автоматично відписується (M1). За замовчуванням 5. Має бути ≥ 1; щоб фактично вимкнути
/// авто-відписку, передай дуже велике значення.
/// </param>
/// <remarks>
/// Цей клас надає загальну інфраструктуру для обробки подій та управління клієнтами,
/// залишаючи специфічну логіку отримання та обробки подій для реалізації у похідних класах.
///
/// Використовує ConcurrentDictionary для потокобезпечного зберігання клієнтських обробників,
/// що дозволяє додавати/видаляти клієнтів з різних потоків без додаткової синхронізації.
///
/// Підтримує "fire and forget" модель для сповіщень, яка не блокує основний потік обробки подій,
/// але при цьому забезпечує належну обробку помилок: послідовні невдачі доставки рахуються, і після
/// досягнення порогу клієнт автоматично відписується (щоб «зламаний» клієнт не отримував нескінченний
/// потік failed-сповіщень).
/// </remarks>
public abstract class AbstractEventProcessor(ILogger logger, int maxConsecutiveNotificationFailures = 5)
    : IEventProcessor, IDisposable
{
    /// <summary>
    /// Логер для реєстрації подій обробника.
    /// </summary>
    protected ILogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Поріг послідовних невдач доставки сповіщень, після якого клієнт авто-відписується (M1).
    /// </summary>
    private readonly int _maxConsecutiveNotificationFailures =
        maxConsecutiveNotificationFailures >= 1
            ? maxConsecutiveNotificationFailures
            : throw new ArgumentOutOfRangeException(nameof(maxConsecutiveNotificationFailures),
                maxConsecutiveNotificationFailures, "Поріг невдач має бути ≥ 1.");

    /// <summary>
    /// Лічильник послідовних невдач доставки сповіщень на клієнта (M1).
    /// Скидається при успішній доставці та при (пере)реєстрації клієнта.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, int> _consecutiveFailures = new();

    /// <summary>
    /// Словник клієнтських обробників сповіщень, індексованих за ідентифікатором клієнта.
    /// Використовується ConcurrentDictionary для потокобезпечної роботи без додаткового блокування.
    /// </summary>
    /// <remarks>
    /// Кожен клієнт представлений своїм унікальним ідентифікатором (Guid) та функцією обробника,
    /// яка викликається для відправки сповіщень цьому клієнту.
    /// </remarks>
    protected ConcurrentDictionary<Guid, Func<string, object[], Task>> ClientHandlers { get; } = new();

    /// <summary>
    /// Джерело токенів скасування для контролю життєвого циклу обробника подій.
    /// </summary>
    /// <remarks>
    /// Використовується для координації зупинки фонових завдань при завершенні роботи обробника.
    /// Спільний токен скасування забезпечує узгоджену зупинку всіх асинхронних операцій.
    /// </remarks>
    protected CancellationTokenSource Cts { get; } = new();

    /// <summary>
    /// Список зовнішніх підписок, які необхідно звільнити при утилізації обробника.
    /// </summary>
    /// <remarks>
    /// Корисно для відстеження зовнішніх ресурсів (наприклад, підписок на події зовнішніх систем),
    /// які необхідно коректно звільнити при завершенні роботи обробника.
    ///
    /// <see cref="ConcurrentBag{T}"/> (а не <c>List</c>) — щоб похідні класи могли реєструвати
    /// підписки з кількох потоків без зовнішньої синхронізації (L2). Порядок звільнення не важливий.
    /// </remarks>
    protected ConcurrentBag<IDisposable> Subscriptions { get; } = new();

    /// <summary>
    /// Прапорець, що вказує, чи був обробник утилізований.
    /// </summary>
    protected bool IsDisposed { get; set; }

    /// <summary>
    /// Запускає процес обробки подій.
    /// </summary>
    /// <param name="cancellationToken">Токен скасування для зупинки обробки подій.</param>
    /// <returns>Завдання, яке представляє асинхронну операцію запуску.</returns>
    /// <remarks>
    /// Базова реалізація лише логує запуск. Похідні класи повинні перевизначити цей метод
    /// для ініціалізації своїх специфічних механізмів обробки подій.
    /// </remarks>
    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        AbstractEventProcessorLog.Starting(Logger);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Зупиняє процес обробки подій.
    /// </summary>
    /// <param name="cancellationToken">Токен скасування для контролю процесу зупинки.</param>
    /// <returns>Завдання, яке представляє асинхронну операцію зупинки.</returns>
    /// <remarks>
    /// Базова реалізація скасовує внутрішній токен, що має призвести до зупинки всіх
    /// асинхронних операцій, які використовують цей токен.
    /// 
    /// Важливо для похідних класів викликати базову реалізацію при перевизначенні.
    /// </remarks>
    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        AbstractEventProcessorLog.Stopping(Logger);
        Cts.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Реєструє клієнта для отримання сповіщень про події.
    /// </summary>
    /// <param name="clientId">Унікальний ідентифікатор клієнта.</param>
    /// <param name="notificationHandler">Функція обробник, яка буде викликана для відправки сповіщень клієнту.</param>
    /// <exception cref="ArgumentNullException">Якщо notificationHandler є null.</exception>
    /// <remarks>
    /// Зберігає функцію обробника у словнику ClientHandlers для подальшого використання
    /// при обробці подій, що відповідають підпискам клієнта.
    /// </remarks>
    public virtual void RegisterClient(Guid clientId, Func<string, object[], Task> notificationHandler)
    {
        ArgumentNullException.ThrowIfNull(notificationHandler);

        // L1: не перезаписуємо мовчки. TryAdd → якщо клієнт уже є (повторна реєстрація / гонка),
        // логуємо Warning і свідомо перезаписуємо (зберігаємо ефективну "останній перемагає"
        // семантику, але вже не безшумно).
        if (!ClientHandlers.TryAdd(clientId, notificationHandler))
        {
            AbstractEventProcessorLog.ClientAlreadyRegistered(Logger, clientId);
            ClientHandlers[clientId] = notificationHandler;
        }

        // (Пере)реєстрація скидає лічильник невдач — це «свіжий» клієнт (M1).
        _consecutiveFailures.TryRemove(clientId, out _);

        AbstractEventProcessorLog.ClientRegistered(Logger, clientId);
    }

    /// <summary>
    /// Скасовує реєстрацію клієнта та припиняє надсилання йому сповіщень.
    /// </summary>
    /// <param name="clientId">Унікальний ідентифікатор клієнта.</param>
    /// <remarks>
    /// Видаляє функцію обробника зі словника ClientHandlers, що припиняє надсилання
    /// сповіщень цьому клієнту. Безпечно працює, навіть якщо клієнт не був зареєстрований.
    /// </remarks>
    public virtual void UnregisterClient(Guid clientId)
    {
        // Прибираємо й лічильник невдач, щоб не лишати «осиротілий» запис (M1).
        _consecutiveFailures.TryRemove(clientId, out _);

        if (ClientHandlers.TryRemove(clientId, out _))
        {
            AbstractEventProcessorLog.ClientUnregistered(Logger, clientId);
        }
    }

    /// <summary>
    /// Звільняє ресурси, що використовуються обробником подій.
    /// </summary>
    /// <remarks>
    /// Звільняє CancellationTokenSource та всі зовнішні підписки, зареєстровані у списку Subscriptions.
    /// Позначає обробник як утилізований, щоб уникнути повторної утилізації.
    /// 
    /// Важливо для похідних класів викликати базову реалізацію при перевизначенні.
    /// </remarks>
    public virtual void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Внутрішня реалізація утилізації, що підтримує патерн Dispose(bool).
    /// </summary>
    /// <param name="disposing">true, якщо викликано з Dispose(); false з фіналізатора.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            // H4: скасовуємо токен ПЕРЕД звільненням, навіть якщо StopAsync не викликали (напр.
            // утилізація через DI-контейнер) — інакше фонові задачі похідних класів лишаються
            // "сиротами" з captured-токеном, який зараз буде звільнено.
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Повторний Dispose — ідемпотентність.
            }

            Cts.Dispose();

            foreach (var subscription in Subscriptions)
            {
                subscription?.Dispose();
            }

            Subscriptions.Clear();
        }

        IsDisposed = true;
    }

    /// <summary>
    /// Сповіщає конкретного клієнта про подію.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <param name="method">Ім'я методу сповіщення.</param>
    /// <param name="eventArgs">Аргументи події, які будуть передані як параметри методу.</param>
    /// <remarks>
    /// Використовує "fire and forget" патерн з правильною обробкою помилок через Task.ContinueWith.
    /// Це дозволяє не блокувати потік обробки подій, очікуючи на завершення відправки сповіщення.
    /// 
    /// При виникненні помилки під час відправки сповіщення, вона логується, і за необхідності
    /// може бути викликаний додатковий обробник помилок HandleClientFailure.
    /// 
    /// Важливо: Цей метод не очікує завершення відправки сповіщення. Якщо необхідно
    /// синхронізуватися з відправкою, потрібно реалізувати окремий метод.
    /// </remarks>
    protected virtual void NotifyClient(Guid clientId, string method, object eventArgs)
    {
        if (!ClientHandlers.TryGetValue(clientId, out var handler))
        {
            return;
        }

        try
        {
            // Fire and forget, але з правильною обробкою помилок
            _ = handler(method, new[] { eventArgs })
                .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            AbstractEventProcessorLog.NotifyClientError(Logger, t.Exception!, method, clientId);
                            OnNotificationFailed(clientId);
                        }
                        else
                        {
                            // Успішна доставка скидає лічильник послідовних невдач (M1).
                            _consecutiveFailures.TryRemove(clientId, out _);
                        }
                    },
                    TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            AbstractEventProcessorLog.EnqueueNotificationError(Logger, ex, method, clientId);
        }
    }

    /// <summary>
    /// Обробляє одну невдачу доставки сповіщення клієнту: інкрементує лічильник послідовних
    /// невдач, викликає хук <see cref="HandleClientFailure"/> і, після досягнення порогу
    /// <c>maxConsecutiveNotificationFailures</c>, автоматично відписує клієнта (M1).
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    private void OnNotificationFailed(Guid clientId)
    {
        int failures = _consecutiveFailures.AddOrUpdate(clientId, 1, (_, current) => current + 1);

        // Зберігаємо хук для кастомної логіки похідних класів (зворотна сумісність).
        HandleClientFailure(clientId);

        if (failures >= _maxConsecutiveNotificationFailures)
        {
            // Вбудований запобіжник: «зламаний» клієнт не отримуватиме нескінченний потік
            // failed-сповіщень. UnregisterClient ідемпотентний і сам прибере лічильник.
            AbstractEventProcessorLog.ClientAutoUnregistered(Logger, clientId, failures);
            UnregisterClient(clientId);
        }
    }

    /// <summary>
    /// Хук для кастомної обробки невдач доставки клієнту.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <remarks>
    /// Порожня реалізація за замовчуванням. Базовий клас уже веде вбудований лічильник послідовних
    /// невдач і авто-відписує клієнта після <c>maxConsecutiveNotificationFailures</c> (M1) — цей хук
    /// для ДОДАТКОВОЇ логіки (метрики, експоненційне відтермінування, тимчасове призупинення тощо),
    /// а не заміна авто-відписки. Викликається на КОЖНУ невдачу, перед перевіркою порогу.
    /// </remarks>
    protected virtual void HandleClientFailure(Guid clientId)
    {
        // Реалізація може відстежувати власні метрики; базова авто-відписка вже працює.
    }
}