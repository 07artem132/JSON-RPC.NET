using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WsRpcServer.Core;

namespace WsRpcServer.Subscriptions;

/// <summary>
/// Абстрактний базовий клас для менеджерів підписок.
/// Реалізує базову логіку для управління підписками клієнтів.
/// </summary>
/// <param name="logger">Логер для реєстрації подій менеджера.</param>
/// <param name="maxSubscriptionsPerClient">Максимальна кількість підписок на одного клієнта.</param>
/// <remarks>
/// Цей клас забезпечує базову інфраструктуру для менеджерів підписок, включаючи:
/// - Потокобезпечне управління кількістю підписок клієнтів
/// - Обмеження максимальної кількості підписок на клієнта
/// - Синхронізацію доступу до спільних ресурсів через SemaphoreSlim
/// 
/// Реалізує інтерфейс ISubscriptionManager, залишаючи конкретну логіку
/// управління підписками для реалізації у похідних класах.
/// </remarks>
public abstract class AbstractSubscriptionManager(ILogger logger, int maxSubscriptionsPerClient = 10)
    : ISubscriptionManager
{
    /// <summary>
    /// Логер для реєстрації подій менеджера підписок.
    /// </summary>
    protected readonly ILogger Logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Семафор для синхронізації операцій з підписками.
    /// </summary>
    /// <remarks>
    /// SemaphoreSlim використовується для синхронізації доступу до спільних ресурсів
    /// при операціях з підписками. Це дозволяє безпечно виконувати операції з різних потоків,
    /// уникаючи проблем з паралельним доступом.
    /// 
    /// Ініціалізується з початковою кількістю 1 та максимальною кількістю 1,
    /// що означає, що лише один потік може одночасно виконувати операції з підписками.
    /// </remarks>
    protected readonly SemaphoreSlim OperationLock = new(1, 1);

    /// <summary>
    /// Словник для відстеження кількості підписок кожного клієнта.
    /// </summary>
    /// <remarks>
    /// ConcurrentDictionary використовується для потокобезпечного зберігання кількості
    /// підписок кожного клієнта. Це дозволяє швидко перевіряти обмеження на кількість
    /// підписок без необхідності додаткової синхронізації.
    /// 
    /// Ключ: Guid - ідентифікатор клієнта
    /// Значення: int - кількість підписок клієнта
    /// </remarks>
    protected readonly ConcurrentDictionary<Guid, int> ClientSubscriptionCounts = new();

    /// <summary>
    /// Максимальна кількість підписок на одного клієнта.
    /// </summary>
    /// <remarks>
    /// Це обмеження запобігає зловживанню ресурсами сервера окремими клієнтами.
    /// За замовчуванням встановлено значення 10, яке можна змінити через конструктор.
    /// </remarks>
    protected readonly int MaxSubscriptionsPerClient = maxSubscriptionsPerClient;

    /// <summary>
    /// Прапорець, що вказує, чи був менеджер утилізований.
    /// </summary>
    protected bool IsDisposed;

    /// <summary>
    /// Створює підписку на події для клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <param name="account">Акаунт для підписки.</param>
    /// <param name="eventTypes">Типи подій для підписки.</param>
    /// <param name="cancellationToken">Токен скасування.</param>
    /// <returns>Ідентифікатор підписки.</returns>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Повинен забезпечувати:
    /// - Перевірку обмеження на кількість підписок клієнта
    /// - Створення та збереження підписки
    /// - Повернення унікального ідентифікатора підписки
    /// </remarks>
    public abstract Task<int> Subscribe(
        Guid clientId,
        string account,
        object eventTypes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Скасовує підписку клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <param name="subscriptionId">Ідентифікатор підписки.</param>
    /// <param name="cancellationToken">Токен скасування.</param>
    /// <returns>True, якщо скасування підписки було успішним, інакше False.</returns>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Повинен забезпечувати:
    /// - Видалення підписки зі сховища
    /// - Оновлення лічильника підписок клієнта
    /// - Звільнення пов'язаних ресурсів (наприклад, зовнішніх підписок)
    /// </remarks>
    public abstract Task<bool> Unsubscribe(
        Guid clientId,
        int subscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Оновлює підписку.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <param name="subscriptionId">Ідентифікатор підписки.</param>
    /// <param name="eventTypes">Нові типи подій.</param>
    /// <param name="cancellationToken">Токен скасування.</param>
    /// <returns>True, якщо оновлення було успішним, інакше False.</returns>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Повинен забезпечувати:
    /// - Перевірку існування підписки
    /// - Перевірку прав доступу клієнта до підписки
    /// - Оновлення типів подій підписки
    /// </remarks>
    public abstract Task<bool> UpdateSubscription(
        Guid clientId,
        int subscriptionId,
        object eventTypes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Отримує клієнтів, які повинні отримати подію.
    /// </summary>
    /// <param name="args">Аргументи події.</param>
    /// <param name="eventType">Тип події.</param>
    /// <returns>Список ідентифікаторів клієнтів.</returns>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Повинен забезпечувати:
    /// - Фільтрацію підписок за типом події
    /// - Фільтрацію підписок за аргументами події (якщо потрібно)
    /// - Повернення списку клієнтів, які підписані на цю подію
    /// 
    /// Цей метод є критичним для продуктивності системи підписок.
    /// Він повинен бути оптимізований для швидкого визначення отримувачів події.
    /// </remarks>
    public abstract List<Guid> GetClientsForEvent(object args, object eventType);

    /// <summary>
    /// Звільняє ресурси, що використовуються менеджером підписок.
    /// </summary>
    /// <remarks>
    /// Звільняє SemaphoreSlim та встановлює прапорець IsDisposed,
    /// щоб уникнути повторної утилізації.
    /// 
    /// Віртуальний метод дозволяє похідним класам розширювати логіку утилізації,
    /// зберігаючи базову функціональність.
    /// </remarks>
    public virtual void Dispose()
    {
        if (!IsDisposed)
        {
            OperationLock.Dispose();
            IsDisposed = true;
        }
    }
}