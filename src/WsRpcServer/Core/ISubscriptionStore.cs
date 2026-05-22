namespace WsRpcServer.Core;

/// <summary>
/// Інтерфейс для сховища підписок, який визначає основні операції для збереження та запиту даних про підписки.
/// </summary>
/// <typeparam name="TSubscription">Тип об'єкта підписки. Повинен бути класом.</typeparam>
/// <typeparam name="TEventArgs">Тип аргументів події. Повинен бути класом.</typeparam>
/// <typeparam name="TEventType">Тип події.</typeparam>
/// <remarks>
/// Цей інтерфейс є базовим будівельним блоком системи підписок. Він абстрагує сховище підписок,
/// дозволяючи різним реалізаціям використовувати різні стратегії зберігання (in-memory, база даних, тощо).
/// 
/// Використання узагальнених типів (generics) дозволяє:
/// 1. Типобезпечну роботу з різними моделями підписок
/// 2. Чітко визначену контрактну взаємодію між компонентами
/// 3. Можливість розширення функціоналу без зміни базового інтерфейсу
/// </remarks>
public interface ISubscriptionStore<TSubscription, in TEventArgs, in TEventType> : IDisposable
    where TSubscription : class
    where TEventArgs : class
{
    /// <summary>
    /// Додає нову підписку до сховища.
    /// </summary>
    /// <param name="subscription">Об'єкт підписки для додавання.</param>
    /// <param name="providerSubscriptionId">Ідентифікатор підписки у зовнішньому провайдері.</param>
    /// <remarks>
    /// Параметр providerSubscriptionId дозволяє зберігати зв'язок між внутрішніми підписками
    /// та їх відповідниками у зовнішніх системах, що є важливим для інтеграційних сценаріїв.
    /// </remarks>
    void AddSubscription(TSubscription subscription, int providerSubscriptionId);

    /// <summary>
    /// Отримує підписку за її ідентифікатором.
    /// </summary>
    /// <param name="subscriptionId">Ідентифікатор підписки.</param>
    /// <returns>Об'єкт підписки або null, якщо підписка не знайдена.</returns>
    TSubscription? GetSubscription(int subscriptionId);

    /// <summary>
    /// Видаляє підписку клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <param name="subscriptionId">Ідентифікатор підписки.</param>
    /// <returns>
    /// Кортеж, що містить:
    /// - Видалену підписку (або null)
    /// - Набір клієнтів, які все ще мають цю підписку (або null)
    /// - Ідентифікатор підписки у зовнішньому провайдері
    /// </returns>
    /// <remarks>
    /// Повернення кортежу з трьома значеннями дозволяє ефективно керувати життєвим циклом підписок.
    /// Зокрема, список залишкових клієнтів дозволяє визначити, чи потрібно видаляти підписку 
    /// у зовнішньому провайдері, якщо жоден клієнт більше не використовує її.
    /// </remarks>
    (TSubscription? Subscription, HashSet<Guid>? RemainingClients, int ProviderSubscriptionId)
        RemoveSubscription(Guid clientId, int subscriptionId);

    /// <summary>
    /// Оновлює існуючу підписку.
    /// </summary>
    /// <param name="subscription">Оновлений об'єкт підписки.</param>
    void UpdateSubscription(TSubscription subscription);

    /// <summary>
    /// Отримує список ідентифікаторів підписок клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <returns>Список ідентифікаторів підписок.</returns>
    /// <remarks>
    /// Корисно для відстеження всіх підписок клієнта, наприклад, для очищення при відключенні.
    /// </remarks>
    List<int> GetClientSubscriptionIds(Guid clientId);

    /// <summary>
    /// Отримує інформацію про підписки клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта.</param>
    /// <returns>Словник, де ключ - ідентифікатор підписки, значення - опис підписки.</returns>
    /// <remarks>
    /// Цей метод є розширенням GetClientSubscriptionIds, надаючи додаткову інформацію
    /// для відображення або діагностики.
    /// </remarks>
    Dictionary<int, string> GetClientSubscriptionsInfo(Guid clientId);

    /// <summary>
    /// Отримує список клієнтів, які повинні отримати подію.
    /// </summary>
    /// <param name="args">Аргументи події, які можуть впливати на фільтрацію підписок.</param>
    /// <param name="eventType">Тип події, який визначає, які підписки будуть враховані.</param>
    /// <returns>Список ідентифікаторів клієнтів, які підписані на цю подію.</returns>
    /// <remarks>
    /// Критичний метод для продуктивності системи підписок. Повинен ефективно фільтрувати
    /// підписки для мінімізації непотрібних сповіщень.
    /// </remarks>
    List<Guid> GetClientsForEvent(TEventArgs args, TEventType eventType);

    /// <summary>
    /// Генерує новий унікальний ідентифікатор підписки.
    /// </summary>
    /// <returns>Унікальний ідентифікатор підписки.</returns>
    /// <remarks>
    /// Виділено в окремий метод для забезпечення консистентної генерації ідентифікаторів
    /// та можливості перевизначення логіки в конкретних реалізаціях.
    /// </remarks>
    int GenerateSubscriptionId();

    /// <summary>
    /// Отримує інформацію про підписку у зовнішньому провайдері.
    /// </summary>
    /// <param name="subscriptionId">Ідентифікатор підписки.</param>
    /// <returns>Кортеж з інформацією про акаунт та ідентифікатор у зовнішньому провайдері.</returns>
    /// <remarks>
    /// Цей метод дозволяє встановити зв'язок між внутрішніми ідентифікаторами підписок
    /// та їх відповідниками у зовнішніх системах.
    /// </remarks>
    (string? Account, int? ProviderSubscriptionId) GetSubscriptionInfo(int subscriptionId);
}