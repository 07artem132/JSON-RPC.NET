using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using WsRpcServer.Core;

namespace WsRpcServer.Services;

/// <summary>
/// Абстрактний базовий клас для реєстрації RPC-сервісів.
/// Забезпечує базову логіку для виявлення та реєстрації RPC-сервісів у JSON-RPC системі.
/// </summary>
/// <remarks>
/// Цей клас реалізує IRpcServiceRegistry, використовуючи рефлексію для виявлення
/// RPC-сервісів у збірках застосунку. Він розподіляє сервіси на дві категорії:
/// - Звичайні RPC-сервіси (IRpcService) - отримуються з DI-контейнера
/// - Клієнт-залежні RPC-сервіси (IClientAwareRpcService) - створюються для кожного клієнта
/// 
/// Використання рефлексії дозволяє уникнути необхідності ручної реєстрації кожного сервісу,
/// що спрощує розробку та підтримку застосунку.
/// 
/// Результати сканування збірок кешуються для підвищення продуктивності, оскільки
/// сканування є ресурсоємною операцією.
/// </remarks>
public abstract class AbstractRpcServiceRegistry(
    IServiceProvider serviceProvider,
    ILogger logger) : IRpcServiceRegistry
{
    /// <summary>
    /// Постачальник сервісів для отримання екземплярів RPC-сервісів.
    /// </summary>
    protected readonly IServiceProvider ServiceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    /// <summary>
    /// Логер для реєстрації подій реєстру сервісів.
    /// </summary>
    protected readonly ILogger Logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Стандартні опції для цільових об'єктів JSON-RPC.
    /// Конфігурує перетворення імен методів у стиль camelCase для сумісності з JavaScript клієнтами.
    /// </summary>
    /// <remarks>
    /// Використання CommonMethodNameTransforms.CamelCase забезпечує сумісність з
    /// JavaScript-конвенціями іменування, де методи починаються з малої літери.
    /// Наприклад, метод C# "GetUser" буде доступний у клієнті як "getUser".
    /// </remarks>
    protected readonly JsonRpcTargetOptions StandardOptions = new()
    {
        MethodNameTransform = CommonMethodNameTransforms.CamelCase
    };

    /// <summary>
    /// Кеш типів сервісів, отриманих через рефлексію.
    /// Заповнюється при першому виклику GetServiceTypeCache.
    /// </summary>
    private ServiceTypeCache? _serviceTypeCache;

    /// <summary>
    /// Отримує кеш типів сервісів, створюючи його при першому виклику.
    /// </summary>
    /// <returns>Кеш типів сервісів.</returns>
    /// <remarks>
    /// Використовує ледачу ініціалізацію (lazy initialization) для створення кешу
    /// лише при необхідності, що дозволяє уникнути сканування збірок до першого використання.
    /// </remarks>
    private ServiceTypeCache GetServiceTypeCache()
    {
        _serviceTypeCache ??= BuildServiceTypeCache();
        return _serviceTypeCache;
    }

    /// <summary>
    /// Реєструє сервіси у екземплярі JSON-RPC.
    /// </summary>
    /// <param name="jsonRpc">Екземпляр JSON-RPC, в якому будуть зареєстровані сервіси.</param>
    /// <param name="clientId">Ідентифікатор клієнта, для якого реєструються сервіси.</param>
    /// <remarks>
    /// Цей метод виконує наступні дії:
    /// 1. Отримує кеш типів сервісів (звичайних та клієнт-залежних)
    /// 2. Для звичайних сервісів - отримує екземпляри з DI-контейнера
    /// 3. Для клієнт-залежних сервісів - створює нові екземпляри з ідентифікатором клієнта
    /// 4. Реєструє всі сервіси в наданому екземплярі JsonRpc
    /// 
    /// Використання StreamJsonRpc.JsonRpc.AddLocalRpcTarget дозволяє зробити
    /// методи сервісів доступними для виклику через JSON-RPC протокол.
    /// </remarks>
    public virtual void RegisterServices(JsonRpc jsonRpc, Guid clientId)
    {
        ArgumentNullException.ThrowIfNull(jsonRpc);

        Logger.LogDebug("Реєстрація RPC-сервісів для клієнта {ClientId}", clientId);

        var cache = GetServiceTypeCache();
        int successCount = 0;

        // Реєструємо звичайні сервіси з DI
        foreach (var interfaceType in cache.RegularServices)
        {
            try
            {
                var service = ServiceProvider.GetService(interfaceType);
                if (service != null)
                {
                    jsonRpc.AddLocalRpcTarget(service, StandardOptions);
                    successCount++;

                    Logger.LogDebug("Зареєстровано RPC-сервіс: {Type}", interfaceType.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Помилка при реєстрації RPC-сервісу {Type}", interfaceType.Name);
            }
        }

        // Реєструємо клієнт-специфічні сервіси
        foreach (var (interfaceType, implType) in cache.ClientAwareServices)
        {
            try
            {
                // Використовуємо ActivatorUtilities для створення екземпляра з 
                // передачею clientId у конструктор
                var service = ActivatorUtilities.CreateInstance(ServiceProvider, implType, clientId);
                jsonRpc.AddLocalRpcTarget(service, StandardOptions);
                successCount++;

                Logger.LogDebug("Зареєстровано клієнт-специфічний RPC-сервіс: {Type}", implType.Name);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Помилка при реєстрації клієнт-специфічного сервісу {Type}",
                    implType.Name);
            }
        }

        Logger.LogInformation("Зареєстровано {Count} RPC-сервісів для клієнта {ClientId}",
            successCount, clientId);
    }

    /// <summary>
    /// Отримує список збірок, що містять RPC-сервіси.
    /// </summary>
    /// <returns>Масив збірок для сканування.</returns>
    /// <remarks>
    /// За замовчуванням повертає всі збірки домену застосунку, крім динамічних.
    /// Може бути перевизначений у похідних класах для обмеження кількості збірок
    /// або додавання зовнішніх збірок.
    /// </remarks>
    protected virtual Assembly[] GetTargetAssemblies()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .ToArray();
    }

    /// <summary>
    /// Фільтрує збірки для пошуку RPC-сервісів.
    /// </summary>
    /// <param name="assembly">Збірка для перевірки.</param>
    /// <returns>True, якщо збірка містить RPC-сервіси; інакше False.</returns>
    /// <remarks>
    /// Метод фільтрує збірки за іменем, перевіряючи, чи збірка є:
    /// 1. Основною збіркою з інтерфейсом IRpcService
    /// 2. Збіркою, ім'я якої починається з одного з префіксів, визначених у GetAdditionalAssemblyPrefixes
    /// 
    /// Це дозволяє обмежити сканування лише релевантними збірками,
    /// що підвищує продуктивність запуску застосунку.
    /// </remarks>
    protected virtual bool IsTargetAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        return name == typeof(IRpcService).Assembly.GetName().Name ||
               GetAdditionalAssemblyPrefixes().Any(prefix => name?.StartsWith(prefix) == true);
    }

    /// <summary>
    /// Префікси для додаткових збірок, що містять RPC-сервіси.
    /// </summary>
    /// <returns>Колекція префіксів імен збірок.</returns>
    /// <remarks>
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// Визначає, які збірки, крім основної, будуть скановані на наявність RPC-сервісів.
    /// 
    /// Приклад реалізації:
    /// ```csharp
    /// protected override IEnumerable<string> GetAdditionalAssemblyPrefixes()
    /// {
    ///     return new[] { "MyCompany.Services", "MyCompany.Api" };
    /// }
    /// ```
    /// </remarks>
    protected abstract IEnumerable<string> GetAdditionalAssemblyPrefixes();

    /// <summary>
    /// Будує кеш типів сервісів, скануючи збірки застосунку.
    /// </summary>
    /// <returns>Кеш типів сервісів.</returns>
    /// <remarks>
    /// Цей метод використовує рефлексію для виявлення:
    /// 1. Інтерфейсів, що наслідуються від IRpcService або IClientAwareRpcService
    /// 2. Класів, що реалізують ці інтерфейси
    /// 
    /// Потім він розподіляє знайдені сервіси на дві категорії:
    /// - Звичайні RPC-сервіси (IRpcService)
    /// - Клієнт-залежні RPC-сервіси (IClientAwareRpcService)
    /// 
    /// Весь цей аналіз виконується один раз при створенні кешу,
    /// що підвищує продуктивність при подальших реєстраціях сервісів.
    /// </remarks>
    private ServiceTypeCache BuildServiceTypeCache()
    {
        var regularServices = new List<Type>();
        var clientAwareServices = new List<(Type InterfaceType, Type ImplType)>();

        var targetAssemblies = GetTargetAssemblies()
            .Where(assembly => IsTargetAssembly(assembly))
            .ToArray();

        try
        {
            // Знаходимо всі інтерфейси, що наслідуються від IRpcService
            var serviceInterfaces = targetAssemblies
                .SelectMany(a => a.GetExportedTypes())
                .Where(t => t.IsInterface &&
                            typeof(IRpcService).IsAssignableFrom(t) &&
                            t != typeof(IRpcService) &&
                            t != typeof(IClientAwareRpcService))
                .ToList();

            // Знаходимо всі класи, що реалізують ці інтерфейси
            var implementations = targetAssemblies
                .SelectMany(a => a.GetExportedTypes())
                .Where(t => !t.IsAbstract &&
                            !t.IsInterface &&
                            serviceInterfaces.Any(i => i.IsAssignableFrom(t)))
                .ToList();

            // Розподіляємо сервіси за категоріями
            foreach (var interfaceType in serviceInterfaces)
            {
                var implType = implementations.FirstOrDefault(t => interfaceType.IsAssignableFrom(t));

                if (implType != null)
                {
                    // Якщо інтерфейс наслідується від IClientAwareRpcService, додаємо до клієнт-залежних
                    if (typeof(IClientAwareRpcService).IsAssignableFrom(interfaceType))
                    {
                        clientAwareServices.Add((interfaceType, implType));
                    }
                    else
                    {
                        // Інакше додаємо до звичайних сервісів
                        regularServices.Add(interfaceType);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Помилка при скануванні типів RPC-сервісів");
        }

        return new ServiceTypeCache(regularServices, clientAwareServices);
    }

    /// <summary>
    /// Клас для зберігання кешу типів сервісів.
    /// </summary>
    /// <param name="regularServices">Список звичайних RPC-сервісів.</param>
    /// <param name="clientAwareServices">Список клієнт-залежних RPC-сервісів.</param>
    /// <remarks>
    /// Використовується для зберігання результатів сканування збірок
    /// та уникнення повторного сканування при кожній реєстрації сервісів.
    /// </remarks>
    protected class ServiceTypeCache(
        IReadOnlyList<Type> regularServices,
        IReadOnlyList<(Type InterfaceType, Type ImplType)> clientAwareServices)
    {
        /// <summary>
        /// Список інтерфейсів звичайних RPC-сервісів.
        /// </summary>
        /// <remarks>
        /// Ці сервіси отримуються з DI-контейнера під час реєстрації.
        /// </remarks>
        public IReadOnlyList<Type> RegularServices { get; } = regularServices;

        /// <summary>
        /// Список пар (інтерфейс, реалізація) для клієнт-залежних RPC-сервісів.
        /// </summary>
        /// <remarks>
        /// Для цих сервісів створюються нові екземпляри з передачею clientId
        /// у конструктор під час реєстрації.
        /// </remarks>
        public IReadOnlyList<(Type InterfaceType, Type ImplType)> ClientAwareServices { get; } = clientAwareServices;
    }
}