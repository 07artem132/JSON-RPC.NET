using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace WsRpcServer.Authorization;

/// <summary>
/// Підклас <see cref="JsonRpc"/>, що примушує <see cref="RpcAuthorizeAttribute"/> на РЕФЛЕКСІЙНОМУ
/// шляху диспетчу (коли сервіси зареєстровані через <c>AddLocalRpcTarget</c>).
/// </summary>
/// <remarks>
/// Перехоплює <see cref="JsonRpc.DispatchRequestAsync"/>: перед виконанням знайденого методу звіряє
/// principal сесії з політикою (через <see cref="RpcAuthorizationEnforcer"/>). При відмові кидає
/// <see cref="Exceptions.RpcErrorException"/> (<c>-32001</c>) — тіло методу не запускається.
///
/// Споживач, якому потрібна авторизація на рефлексійному шляху, конструює
/// <c>new AuthorizingJsonRpc(handler, principal, policy, logger)</c> замість <c>new JsonRpc(handler)</c>.
/// Source-генерований binder примушує ту саму політику без рефлексії (атрибут читається на компіляції).
/// </remarks>
public class AuthorizingJsonRpc : JsonRpc
{
    private readonly ClaimsPrincipal? _principal;
    private readonly IRpcAuthorizationPolicy? _policy;
    private readonly ILogger? _logger;

    /// <summary>
    /// Створює авторизуючий JSON-RPC.
    /// </summary>
    /// <param name="messageHandler">Обробник повідомлень (транспорт).</param>
    /// <param name="principal">Principal сесії (з mTLS-ідентичності).</param>
    /// <param name="policy">Політика авторизації.</param>
    /// <param name="logger">Опційний логер для відмов.</param>
    public AuthorizingJsonRpc(
        IJsonRpcMessageHandler messageHandler,
        ClaimsPrincipal? principal,
        IRpcAuthorizationPolicy? policy,
        ILogger? logger = null)
        : base(messageHandler)
    {
        _principal = principal;
        _policy = policy;
        _logger = logger;
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode(
        "Авторизація на рефлексійному шляху читає [RpcAuthorize] через рефлексію (RpcAuthorizationMetadata); " +
        "для AOT використовуй source-генерований binder.")]
    protected override ValueTask<JsonRpcMessage> DispatchRequestAsync(
        JsonRpcRequest request,
        TargetMethod targetMethod,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (targetMethod.TargetMethodInfo is { } methodInfo)
        {
            var requirement = RpcAuthorizationMetadata.Resolve(methodInfo);
            if (requirement is not null)
            {
                // Кидає RpcErrorException(-32001) при відмові — до виклику base (тіло не запускається).
                RpcAuthorizationEnforcer.Enforce(
                    _policy, _principal, requirement, request.Method ?? methodInfo.Name, _logger);
            }
        }

        return base.DispatchRequestAsync(request, targetMethod, cancellationToken);
    }
}
