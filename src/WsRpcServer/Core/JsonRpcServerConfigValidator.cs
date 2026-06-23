using Microsoft.Extensions.Options;

namespace WsRpcServer.Core;

/// <summary>
/// Валідатор конфігурації <see cref="JsonRpcServerConfig"/>, згенерований під час компіляції.
/// </summary>
/// <remarks>
/// Атрибут <see cref="OptionsValidatorAttribute"/> на порожньому partial-класі вказує
/// source-generator'у <c>Microsoft.Extensions.Options</c> згенерувати реалізацію
/// <see cref="IValidateOptions{TOptions}"/> на основі DataAnnotations-атрибутів
/// (<c>[Range]</c>/<c>[Required]</c>), що позначають властивості <see cref="JsonRpcServerConfig"/>.
/// Згенерований код не використовує рефлексію (на відміну від <c>ValidateDataAnnotations()</c>),
/// тож лишається AOT-сумісним (M5).
///
/// Крос-польові правила без DataAnnotation-атрибута (наприклад, додатний
/// <see cref="JsonRpcServerConfig.NotificationTimeout"/>, який є <see cref="System.TimeSpan"/>)
/// додаються через <c>.Validate(...)</c> у композиційному корені
/// <see cref="WsRpcServer.Extensions.JsonRpcCoreExtensions"/>.
/// </remarks>
[OptionsValidator]
internal sealed partial class JsonRpcServerConfigValidator : IValidateOptions<JsonRpcServerConfig>;
