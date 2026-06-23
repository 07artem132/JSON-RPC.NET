namespace WsRpcServer.Services;

/// <summary>
/// Позначає збірку як таку, для якої source-генератор має згенерувати <see cref="IRpcServiceCatalog"/>
/// (compile-time виявлення всіх реалізацій <see cref="IRpcService"/>) + DI-розширення
/// <c>AddGeneratedRpcServiceCatalog</c>.
/// </summary>
/// <remarks>
/// Opt-in: без цього атрибута генератор нічого не випромінює, а реєстр використовує рефлексійне
/// сканування (стара поведінка). Додай <c>[assembly: GenerateRpcServiceCatalog]</c> у будь-якому файлі
/// збірки споживача й виклич <c>services.AddGeneratedRpcServiceCatalog()</c>, щоб виявлення сервісів
/// стало reflection-free (trim/AOT-сумісним).
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class GenerateRpcServiceCatalogAttribute : Attribute;
