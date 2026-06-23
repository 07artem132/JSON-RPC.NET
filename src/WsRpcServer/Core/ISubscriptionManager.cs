namespace WsRpcServer.Core;

/// <summary>
/// Інтерфейс для менеджерів підписок.
/// Визначає контракт для управління підписками клієнтів на події системи.
/// </summary>
/// <typeparam name="TEventType">
/// Тип, що ідентифікує вид події (наприклад, enum або рядок). Узагальнення прибирає
/// <c>object</c>-параметри й повертає типобезпеку (M4).
/// </typeparam>
/// <typeparam name="TEventArgs">
/// Тип аргументів події, за якими фільтруються підписки у <see cref="GetClientsForEvent"/>.
/// </typeparam>
/// <remarks>
/// Цей інтерфейс є ключовим для забезпечення реактивної взаємодії між сервером та клієнтами.
/// Він абстрагує логіку підписок, дозволяючи різним реалізаціям використовувати різні стратегії
/// зберігання та обробки підписок (наприклад, in-memory, база даних, розподілене сховище).
/// </remarks>
public interface ISubscriptionManager<TEventType, TEventArgs> : IDisposable
{
    /// <summary>
    /// Створює підписку на події для клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта, який підписується.</param>
    /// <param name="topic">
    /// Тема або сегмент підписки (наприклад, ідентифікатор каналу). Узагальнена назва замість
    /// доменно-специфічного «account» (M3).
    /// </param>
    /// <param name="eventTypes">Типи подій, на які клієнт бажає підписатися.</param>
    /// <param name="cancellationToken">Токен скасування для асинхронної операції.</param>
    /// <returns>Ідентифікатор нової підписки, який клієнт може використовувати для подальших операцій.</returns>
    /// <remarks>
    /// Метод повертає <see cref="Task{TResult}"/>, оскільки створення підписки може вимагати
    /// асинхронної взаємодії з зовнішніми системами.
    /// </remarks>
    Task<int> Subscribe(
        Guid clientId,
        string topic,
        IReadOnlyCollection<TEventType> eventTypes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Скасовує підписку клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта, який скасовує підписку.</param>
    /// <param name="subscriptionId">Ідентифікатор підписки для скасування.</param>
    /// <param name="cancellationToken">Токен скасування для асинхронної операції.</param>
    /// <returns>True, якщо скасування підписки було успішним, інакше False.</returns>
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
    Task<bool> UpdateSubscription(
        Guid clientId,
        int subscriptionId,
        IReadOnlyCollection<TEventType> eventTypes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Отримує список клієнтів, які повинні отримати подію конкретного типу.
    /// </summary>
    /// <param name="args">Аргументи події, які можуть впливати на фільтрацію підписок.</param>
    /// <param name="eventType">Тип події, який визначає, які підписки будуть враховані.</param>
    /// <returns>Список ідентифікаторів клієнтів, які підписані на цю подію.</returns>
    /// <remarks>
    /// Цей метод є ключовим для ефективної доставки подій. Він повинен швидко визначати,
    /// які клієнти зацікавлені в конкретній події, уникаючи непотрібних пересилань. Це гаряча
    /// дорога читання, тож реалізації мають покладатися на потокобезпечне сховище (наприклад,
    /// <see cref="AbstractSubscriptionStore{TSubscription,TEventArgs,TEventType}"/> з
    /// <see cref="System.Threading.ReaderWriterLockSlim"/>), а не на write-орієнтований
    /// <c>OperationLock</c> менеджера.
    /// </remarks>
    List<Guid> GetClientsForEvent(TEventArgs args, TEventType eventType);
}
