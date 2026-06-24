using Microsoft.Extensions.Options;

namespace WsRpcServer.Security;

/// <summary>
/// Валідатор <see cref="TlsServerOptions"/>, згенерований на етапі компіляції.
/// </summary>
/// <remarks>
/// Як і <see cref="Core.JsonRpcServerConfigValidator"/>: <see cref="OptionsValidatorAttribute"/> на
/// порожньому partial-класі генерує reflection-free <see cref="IValidateOptions{TOptions}"/> на основі
/// DataAnnotations (<c>[Required]</c> на <see cref="TlsServerOptions.ServerCertificate"/>). AOT-сумісно.
///
/// Крос-польові правила (приватний ключ серверного сертифіката, узгодженість mTLS-довіри) додаються
/// через <c>.Validate(...)</c> у композиційному корені <see cref="Extensions.SecureJsonRpcCoreExtensions"/>.
/// </remarks>
[OptionsValidator]
internal sealed partial class TlsServerOptionsValidator : IValidateOptions<TlsServerOptions>;
