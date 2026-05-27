namespace WsRpcServer.Core;

/// <summary>
/// Абстрактний базовий клас для збереження та управління підписками.
/// Реалізує інтерфейс ISubscriptionStore та забезпечує базову логіку для операцій з підписками.
/// </summary>
/// <typeparam name="TSubscription">Тип об'єкта підписки</typeparam>
/// <typeparam name="TEventArgs">Тип аргументів події</typeparam>
/// <typeparam name="TEventType">Тип події</typeparam>
/// <remarks>
/// Клас використовує ReaderWriterLockSlim для забезпечення потокової безпеки при високій конкурентності.
/// Цей вибір обумовлений необхідністю підтримки багатьох одночасних операцій читання з рідкісними операціями запису,
/// що типово для сценаріїв з підписками (більше читань подій, ніж змін підписок).
/// </remarks>
public abstract class AbstractSubscriptionStore<TSubscription, TEventArgs, TEventType> :
    ISubscriptionStore<TSubscription, TEventArgs, TEventType>
    where TSubscription : class
    where TEventArgs : class
{
    /// <summary>
    /// Блокування для синхронізації доступу до сховища підписок.
    /// ReaderWriterLockSlim обрано замість звичайного lock для забезпечення більшої продуктивності
    /// при численних операціях читання з рідкісними операціями запису.
    /// </summary>
    private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

    /// <summary>
    /// Лічильник для генерації унікальних ідентифікаторів підписок.
    /// Використовується Interlocked.Increment для атомарного збільшення без додаткового блокування.
    /// </summary>
    private int _nextSubscriptionId = 1;

    /// <summary>
    /// Прапорець для відстеження стану утилізації об'єкта.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Додає нову підписку до сховища.
    /// </summary>
    /// <param name="subscription">Об'єкт підписки</param>
    /// <param name="providerSubscriptionId">Ідентифікатор підписки у провайдері</param>
    /// <remarks>
    /// Метод використовує WriteLock, оскільки він модифікує стан сховища.
    /// Реальна логіка делегується до абстрактного методу AddSubscriptionCore.
    /// </remarks>
    public virtual void AddSubscription(TSubscription subscription, int providerSubscriptionId)
    {
        _lock.EnterWriteLock();
        try
        {
            AddSubscriptionCore(subscription, providerSubscriptionId);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Отримує підписку за її ідентифікатором.
    /// </summary>
    /// <param name="subscriptionId">Ідентифікатор підписки</param>
    /// <returns>Об'єкт підписки або null, якщо підписка не знайдена</returns>
    /// <remarks>
    /// Метод використовує ReadLock, оскільки він тільки читає стан сховища.
    /// </remarks>
    public virtual TSubscription? GetSubscription(int subscriptionId)
    {
        _lock.EnterReadLock();
        try
        {
            return GetSubscriptionCore(subscriptionId);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Видаляє підписку клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта</param>
    /// <param name="subscriptionId">Ідентифікатор підписки</param>
    /// <returns>
    /// Кортеж, що містить:
    /// - Видалену підписку (або null)
    /// - Набір клієнтів, які все ще мають цю підписку (або null)
    /// - Ідентифікатор підписки у провайдері
    /// </returns>
    /// <remarks>
    /// Метод використовує WriteLock, оскільки він модифікує стан сховища.
    /// Повертає інформацію про клієнтів, які все ще підписані, що дозволяє
    /// ефективно керувати підписками у зовнішніх системах.
    /// </remarks>
    public virtual (TSubscription? Subscription, HashSet<Guid>? RemainingClients, int ProviderSubscriptionId)
        RemoveSubscription(Guid clientId, int subscriptionId)
    {
        _lock.EnterWriteLock();
        try
        {
            return RemoveSubscriptionCore(clientId, subscriptionId);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Оновлює існуючу підписку.
    /// </summary>
    /// <param name="subscription">Оновлений об'єкт підписки</param>
    /// <remarks>
    /// Метод використовує WriteLock, оскільки він модифікує стан сховища.
    /// </remarks>
    public virtual void UpdateSubscription(TSubscription subscription)
    {
        _lock.EnterWriteLock();
        try
        {
            UpdateSubscriptionCore(subscription);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Отримує список ідентифікаторів підписок для конкретного клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта</param>
    /// <returns>Список ідентифікаторів підписок</returns>
    /// <remarks>
    /// Метод використовує ReadLock, оскільки він тільки читає стан сховища.
    /// </remarks>
    public virtual List<int> GetClientSubscriptionIds(Guid clientId)
    {
        _lock.EnterReadLock();
        try
        {
            return GetClientSubscriptionIdsCore(clientId);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Отримує інформацію про підписки клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта</param>
    /// <returns>Словник, де ключ - ідентифікатор підписки, значення - опис підписки</returns>
    /// <remarks>
    /// Метод використовує ReadLock, оскільки він тільки читає стан сховища.
    /// Повертає словник для зручного доступу та відображення інформації про підписки.
    /// </remarks>
    public virtual Dictionary<int, string> GetClientSubscriptionsInfo(Guid clientId)
    {
        _lock.EnterReadLock();
        try
        {
            return GetClientSubscriptionsInfoCore(clientId);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Отримує список клієнтів, які повинні отримати подію певного типу з конкретними аргументами.
    /// </summary>
    /// <param name="args">Аргументи події</param>
    /// <param name="eventType">Тип події</param>
    /// <returns>Список ідентифікаторів клієнтів</returns>
    /// <remarks>
    /// Метод використовує ReadLock, оскільки він тільки читає стан сховища.
    /// Це критичний метод для роботи системи підписок, він визначає, які клієнти
    /// отримують сповіщення про конкретну подію.
    /// </remarks>
    public virtual List<Guid> GetClientsForEvent(TEventArgs args, TEventType eventType)
    {
        _lock.EnterReadLock();
        try
        {
            return GetClientsForEventCore(args, eventType);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Отримує інформацію про підписку та її відповідний ідентифікатор у провайдері.
    /// </summary>
    /// <param name="subscriptionId">Ідентифікатор підписки</param>
    /// <returns>Кортеж з інформацією про акаунт та ідентифікатор у провайдері</returns>
    /// <remarks>
    /// Метод використовує ReadLock, оскільки він тільки читає стан сховища.
    /// Ця інформація корисна для взаємодії з зовнішніми системами підписок.
    /// </remarks>
    public virtual (string? Account, int? ProviderSubscriptionId) GetSubscriptionInfo(int subscriptionId)
    {
        _lock.EnterReadLock();
        try
        {
            return GetSubscriptionInfoCore(subscriptionId);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Генерує новий унікальний ідентифікатор підписки.
    /// </summary>
    /// <returns>Унікальний ідентифікатор підписки</returns>
    /// <remarks>
    /// Використовує Interlocked.Increment для потокобезпечного інкременту лічильника.
    /// Це дозволяє генерувати унікальні ідентифікатори навіть при паралельних викликах.
    /// </remarks>
    public virtual int GenerateSubscriptionId()
    {
        return Interlocked.Increment(ref _nextSubscriptionId);
    }

    /// <summary>
    /// Звільняє ресурси, що використовуються сховищем підписок.
    /// </summary>
    /// <remarks>
    /// Звільняє ReaderWriterLockSlim та встановлює прапорець _disposed,
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
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _lock.Dispose();
        }

        _disposed = true;
    }

    /// <summary>
    /// Базова реалізація додавання підписки до сховища.
    /// </summary>
    /// <param name="subscription">Об'єкт підписки</param>
    /// <param name="providerSubscriptionId">Ідентифікатор підписки у провайдері</param>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Викликається в контексті WriteLock, тому не потребує додаткової синхронізації.
    /// </remarks>
    protected abstract void AddSubscriptionCore(TSubscription subscription, int providerSubscriptionId);

    /// <summary>
    /// Базова реалізація отримання підписки за її ідентифікатором.
    /// </summary>
    /// <param name="subscriptionId">Ідентифікатор підписки</param>
    /// <returns>Об'єкт підписки або null, якщо підписка не знайдена</returns>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Викликається в контексті ReadLock, тому не потребує додаткової синхронізації.
    /// </remarks>
    protected abstract TSubscription? GetSubscriptionCore(int subscriptionId);

    /// <summary>
    /// Базова реалізація видалення підписки клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта</param>
    /// <param name="subscriptionId">Ідентифікатор підписки</param>
    /// <returns>
    /// Кортеж, що містить:
    /// - Видалену підписку (або null)
    /// - Набір клієнтів, які все ще мають цю підписку (або null)
    /// - Ідентифікатор підписки у провайдері
    /// </returns>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Викликається в контексті WriteLock, тому не потребує додаткової синхронізації.
    /// </remarks>
    protected abstract (TSubscription? Subscription, HashSet<Guid>? RemainingClients, int ProviderSubscriptionId)
        RemoveSubscriptionCore(Guid clientId, int subscriptionId);

    /// <summary>
    /// Базова реалізація оновлення існуючої підписки.
    /// </summary>
    /// <param name="subscription">Оновлений об'єкт підписки</param>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Викликається в контексті WriteLock, тому не потребує додаткової синхронізації.
    /// </remarks>
    protected abstract void UpdateSubscriptionCore(TSubscription subscription);

    /// <summary>
    /// Базова реалізація отримання ідентифікаторів підписок клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта</param>
    /// <returns>Список ідентифікаторів підписок</returns>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Викликається в контексті ReadLock, тому не потребує додаткової синхронізації.
    /// </remarks>
    protected abstract List<int> GetClientSubscriptionIdsCore(Guid clientId);

    /// <summary>
    /// Базова реалізація отримання інформації про підписки клієнта.
    /// </summary>
    /// <param name="clientId">Ідентифікатор клієнта</param>
    /// <returns>Словник підписок клієнта</returns>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Викликається в контексті ReadLock, тому не потребує додаткової синхронізації.
    /// </remarks>
    protected abstract Dictionary<int, string> GetClientSubscriptionsInfoCore(Guid clientId);

    /// <summary>
    /// Базова реалізація отримання клієнтів для конкретної події.
    /// </summary>
    /// <param name="args">Аргументи події</param>
    /// <param name="eventType">Тип події</param>
    /// <returns>Список ідентифікаторів клієнтів</returns>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Викликається в контексті ReadLock, тому не потребує додаткової синхронізації.
    /// </remarks>
    protected abstract List<Guid> GetClientsForEventCore(TEventArgs args, TEventType eventType);

    /// <summary>
    /// Базова реалізація отримання інформації про підписку.
    /// </summary>
    /// <param name="subscriptionId">Ідентифікатор підписки</param>
    /// <returns>Інформація про підписку у формі кортежу</returns>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Викликається в контексті ReadLock, тому не потребує додаткової синхронізації.
    /// </remarks>
    protected abstract (string? Account, int? ProviderSubscriptionId) GetSubscriptionInfoCore(int subscriptionId);
}