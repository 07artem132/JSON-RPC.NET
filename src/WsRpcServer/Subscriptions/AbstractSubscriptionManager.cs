using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WsRpcServer.Core;

namespace WsRpcServer.Subscriptions;

/// <summary>
/// Абстрактний базовий клас для менеджерів підписок.
/// Реалізує базову логіку для управління підписками клієнтів.
/// </summary>
/// <typeparam name="TEventType">Тип, що ідентифікує вид події.</typeparam>
/// <typeparam name="TEventArgs">Тип аргументів події для фільтрації у GetClientsForEvent.</typeparam>
/// <param name="logger">Логер для реєстрації подій менеджера.</param>
/// <param name="maxSubscriptionsPerClient">Максимальна кількість підписок на одного клієнта.</param>
/// <remarks>
/// Цей клас забезпечує базову інфраструктуру для менеджерів підписок, включаючи:
/// - Потокобезпечне управління кількістю підписок клієнтів
/// - Обмеження максимальної кількості підписок на клієнта
/// - Серіалізацію мутаційних операцій (Subscribe/Unsubscribe/UpdateSubscription) через
///   <see cref="OperationLock"/>: базовий клас САМ використовує лок у шаблонних методах
///   (M2 — раніше лок лише оголошувався, але ніде не застосовувався, що було пасткою API).
///
/// Похідні класи реалізують лише <c>*Core</c>-методи з власною бізнес-логікою; синхронізацію
/// мутацій забезпечує база. Гаряча дорога читання (<see cref="GetClientsForEvent"/>) лишається
/// абстрактною й НЕ бере write-орієнтований <see cref="OperationLock"/> — конкурентність читань
/// має забезпечувати сховище підписок.
/// </remarks>
public abstract class AbstractSubscriptionManager<TEventType, TEventArgs>(ILogger logger, int maxSubscriptionsPerClient = 10)
    : ISubscriptionManager<TEventType, TEventArgs>
{
    /// <summary>
    /// Логер для реєстрації подій менеджера підписок.
    /// </summary>
    protected ILogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Семафор для серіалізації мутаційних операцій з підписками.
    /// </summary>
    /// <remarks>
    /// Ініціалізується з початковою кількістю 1 та максимальною 1, тож лише один потік одночасно
    /// виконує мутаційну операцію. База застосовує його у <see cref="WithLockAsync{TResult}"/>,
    /// який обгортає <c>Subscribe</c>/<c>Unsubscribe</c>/<c>UpdateSubscription</c>.
    /// </remarks>
    protected SemaphoreSlim OperationLock { get; } = new(1, 1);

    /// <summary>
    /// Словник для відстеження кількості підписок кожного клієнта.
    /// </summary>
    /// <remarks>
    /// ConcurrentDictionary використовується для потокобезпечного зберігання кількості
    /// підписок кожного клієнта.
    ///
    /// Ключ: Guid - ідентифікатор клієнта
    /// Значення: int - кількість підписок клієнта
    /// </remarks>
    protected ConcurrentDictionary<Guid, int> ClientSubscriptionCounts { get; } = new();

    /// <summary>
    /// Максимальна кількість підписок на одного клієнта.
    /// </summary>
    /// <remarks>
    /// Це обмеження запобігає зловживанню ресурсами сервера окремими клієнтами.
    /// За замовчуванням встановлено значення 10, яке можна змінити через конструктор.
    /// </remarks>
    protected int MaxSubscriptionsPerClient { get; } = maxSubscriptionsPerClient;

    /// <summary>
    /// Прапорець, що вказує, чи був менеджер утилізований.
    /// </summary>
    protected bool IsDisposed { get; set; }

    /// <summary>
    /// Виконує мутаційну операцію під <see cref="OperationLock"/> — серіалізує доступ
    /// (M2: база реально використовує лок, а не лише оголошує його).
    /// </summary>
    /// <typeparam name="TResult">Тип результату операції.</typeparam>
    /// <param name="action">Асинхронна дія, що виконується під локом.</param>
    /// <param name="cancellationToken">Токен скасування очікування лока.</param>
    /// <returns>Результат дії.</returns>
    /// <exception cref="ObjectDisposedException">Якщо менеджер уже утилізований.</exception>
    protected async Task<TResult> WithLockAsync<TResult>(
        Func<Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        await OperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            OperationLock.Release();
        }
    }

    /// <summary>
    /// Створює підписку на події для клієнта (серіалізовано через <see cref="OperationLock"/>).
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <param name="topic">Тема/сегмент підписки.</param>
    /// <param name="eventTypes">Типи подій для підписки.</param>
    /// <param name="cancellationToken">Токен скасування.</param>
    /// <returns>Ідентифікатор підписки.</returns>
    public Task<int> Subscribe(
        Guid clientId,
        string topic,
        IReadOnlyCollection<TEventType> eventTypes,
        CancellationToken cancellationToken = default)
        => WithLockAsync(() => SubscribeCore(clientId, topic, eventTypes, cancellationToken), cancellationToken);

    /// <summary>
    /// Скасовує підписку клієнта (серіалізовано через <see cref="OperationLock"/>).
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <param name="subscriptionId">Ідентифікатор підписки.</param>
    /// <param name="cancellationToken">Токен скасування.</param>
    /// <returns>True, якщо скасування було успішним, інакше False.</returns>
    public Task<bool> Unsubscribe(
        Guid clientId,
        int subscriptionId,
        CancellationToken cancellationToken = default)
        => WithLockAsync(() => UnsubscribeCore(clientId, subscriptionId, cancellationToken), cancellationToken);

    /// <summary>
    /// Оновлює підписку (серіалізовано через <see cref="OperationLock"/>).
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <param name="subscriptionId">Ідентифікатор підписки.</param>
    /// <param name="eventTypes">Нові типи подій.</param>
    /// <param name="cancellationToken">Токен скасування.</param>
    /// <returns>True, якщо оновлення було успішним, інакше False.</returns>
    public Task<bool> UpdateSubscription(
        Guid clientId,
        int subscriptionId,
        IReadOnlyCollection<TEventType> eventTypes,
        CancellationToken cancellationToken = default)
        => WithLockAsync(() => UpdateSubscriptionCore(clientId, subscriptionId, eventTypes, cancellationToken), cancellationToken);

    /// <summary>
    /// Базова реалізація створення підписки. Викликається під <see cref="OperationLock"/>.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <param name="topic">Тема/сегмент підписки.</param>
    /// <param name="eventTypes">Типи подій для підписки.</param>
    /// <param name="cancellationToken">Токен скасування.</param>
    /// <returns>Ідентифікатор підписки.</returns>
    /// <remarks>
    /// Похідні класи реалізують логіку перевірки обмеження на кількість підписок, створення та
    /// збереження підписки, повернення унікального ідентифікатора. Додаткова синхронізація мутацій
    /// не потрібна — метод уже виконується під локом.
    /// </remarks>
    protected abstract Task<int> SubscribeCore(
        Guid clientId,
        string topic,
        IReadOnlyCollection<TEventType> eventTypes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Базова реалізація скасування підписки. Викликається під <see cref="OperationLock"/>.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <param name="subscriptionId">Ідентифікатор підписки.</param>
    /// <param name="cancellationToken">Токен скасування.</param>
    /// <returns>True, якщо скасування було успішним, інакше False.</returns>
    protected abstract Task<bool> UnsubscribeCore(
        Guid clientId,
        int subscriptionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Базова реалізація оновлення підписки. Викликається під <see cref="OperationLock"/>.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <param name="subscriptionId">Ідентифікатор підписки.</param>
    /// <param name="eventTypes">Нові типи подій.</param>
    /// <param name="cancellationToken">Токен скасування.</param>
    /// <returns>True, якщо оновлення було успішним, інакше False.</returns>
    protected abstract Task<bool> UpdateSubscriptionCore(
        Guid clientId,
        int subscriptionId,
        IReadOnlyCollection<TEventType> eventTypes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Отримує клієнтів, які повинні отримати подію.
    /// </summary>
    /// <param name="args">Аргументи події.</param>
    /// <param name="eventType">Тип події.</param>
    /// <returns>Список ідентифікаторів клієнтів.</returns>
    /// <remarks>
    /// Гаряча дорога читання — НЕ бере <see cref="OperationLock"/>. Реалізації мають покладатися
    /// на потокобезпечне сховище підписок для конкурентного доступу.
    /// </remarks>
    public abstract List<Guid> GetClientsForEvent(TEventArgs args, TEventType eventType);

    /// <summary>
    /// Звільняє ресурси, що використовуються менеджером підписок.
    /// </summary>
    /// <remarks>
    /// Звільняє SemaphoreSlim та встановлює прапорець IsDisposed,
    /// щоб уникнути повторної утилізації.
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
            // H4: дренуємо семафор перед звільненням — чекаємо, поки поточний тримач відпустить
            // OperationLock, і лише тоді звільняємо. Інакше in-flight Release() кидає
            // ObjectDisposedException. Acquire без подальшого Release: ми володіємо локом до самого
            // Dispose, тож нові тримачі не з'являться у вікні між дренажем і звільненням.
            try
            {
                OperationLock.Wait(TimeSpan.FromSeconds(5));
            }
            catch (ObjectDisposedException)
            {
                // Повторний Dispose — ідемпотентність.
            }

            OperationLock.Dispose();
        }

        IsDisposed = true;
    }
}
