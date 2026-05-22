namespace WsRpcServer.Core;

/// <summary>
/// Інтерфейс для менеджерів підписок.
/// Визначає контракт для управління підписками клієнтів на події системи.
/// </summary>
/// <remarks>
/// Цей інтерфейс є ключовим для забезпечення реактивної взаємодії між сервером та клієнтами.
/// Він абстрагує логіку підписок, дозволяючи різним реалізаціям використовувати різні стратегії
/// зберігання та обробки підписок (наприклад, in-memory, база даних, розподілене сховище).
/// </remarks>
public interface ISubscriptionManager : IDisposable
{
    /// <summary>
    /// Створює підписку на події для клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта, який підписується.</param>
    /// <param name="account">Акаунт або ресурс для підписки (наприклад, ідентифікатор каналу або теми).</param>
    /// <param name="eventTypes">Типи подій, на які клієнт бажає підписатися. Використовується object для гнучкості.</param>
    /// <param name="cancellationToken">Токен скасування для асинхронної операції.</param>
    /// <returns>Ідентифікатор нової підписки, який клієнт може використовувати для подальших операцій.</returns>
    /// <remarks>
    /// Метод повертає Task&lt;int&gt;, оскільки створення підписки може вимагати асинхронної взаємодії
    /// з зовнішніми системами. Використання object для eventTypes дозволяє підтримувати різні моделі
    /// типів подій (enum, string, складні об'єкти) без зміни інтерфейсу.
    /// </remarks>
    Task<int> Subscribe(
        Guid clientId,
        string account,
        object eventTypes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Скасовує підписку клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта, який скасовує підписку.</param>
    /// <param name="subscriptionId">Ідентифікатор підписки для скасування.</param>
    /// <param name="cancellationToken">Токен скасування для асинхронної операції.</param>
    /// <returns>True, якщо скасування підписки було успішним, інакше False.</returns>
    /// <remarks>
    /// Повертає Task&lt;bool&gt; для індикації успіху операції, оскільки скасування
    /// підписки може не вдатися з різних причин (наприклад, підписка не існує або
    /// недостатньо прав для її скасування).
    /// </remarks>
    Task<bool> Unsubscribe(
        Guid clientId,
        int subscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Оновлює існуючу підписку.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта, який оновлює підписку.</param>
    /// <param name="subscriptionId">Ідентифікатор підписки для оновлення.</param>
    /// <param name="eventTypes">Нові типи подій для підписки.</param>
    /// <param name="cancellationToken">Токен скасування для асинхронної операції.</param>
    /// <returns>True, якщо оновлення було успішним, інакше False.</returns>
    /// <remarks>
    /// Цей метод дозволяє змінювати параметри підписки без необхідності її перестворення.
    /// Це особливо корисно для сценаріїв, де створення нової підписки може бути витратним.
    /// </remarks>
    Task<bool> UpdateSubscription(
        Guid clientId,
        int subscriptionId,
        object eventTypes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Отримує список клієнтів, які повинні отримати подію конкретного типу.
    /// </summary>
    /// <param name="args">Аргументи події, які можуть впливати на фільтрацію підписок.</param>
    /// <param name="eventType">Тип події, який визначає, які підписки будуть враховані.</param>
    /// <returns>Список ідентифікаторів клієнтів, які підписані на цю подію.</returns>
    /// <remarks>
    /// Цей метод є ключовим для ефективної доставки подій. Він повинен швидко визначати,
    /// які клієнти зацікавлені в конкретній події, уникаючи непотрібних пересилань.
    /// Використання object для args та eventType забезпечує гнучкість без прив'язки до конкретних типів.
    /// </remarks>
    List<Guid> GetClientsForEvent(object args, object eventType);
}