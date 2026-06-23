using System.Reflection;
using NetCoreServer;
using WsRpcServer.Sessions;

namespace WsRpcServer.Tests.Sessions;

/// <summary>
/// Regression-guard для L3. У поточному NetCoreServer <c>WsSession.OnWsPing</c> віртуальний, тож
/// <see cref="AbstractJsonRpcSession.OnWsPing"/> позначено <c>override</c> — інакше внутрішня
/// диспетчеризація ping'а у фреймворку (через посилання на базовий тип) оминала б нашу реалізацію.
/// Цей тест пінить ОБИДВІ половини інваріанта: база лишається virtual, а наш метод реально її
/// перевизначає (а не ховає через <c>new</c>). Якщо bump NetCoreServer зробить базовий метод
/// невіртуальним — <c>override</c> не скомпілюється (build впаде); якщо хтось поверне <c>new</c> —
/// впаде цей тест.
/// </summary>
public sealed class WsSessionOnWsPingGuardTests
{
    [Fact]
    public void AbstractJsonRpcSession_OnWsPing_OverridesVirtualBase()
    {
        var baseMethod = typeof(WsSession).GetMethod(
            nameof(WsSession.OnWsPing),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(byte[]), typeof(long), typeof(long)],
            modifiers: null);

        Assert.NotNull(baseMethod);
        Assert.True(baseMethod!.IsVirtual,
            "NetCoreServer.WsSession.OnWsPing став невіртуальним — поверни `new` у AbstractJsonRpcSession (L3).");

        var derivedMethod = typeof(AbstractJsonRpcSession).GetMethod(
            nameof(AbstractJsonRpcSession.OnWsPing),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(byte[]), typeof(long), typeof(long)],
            modifiers: null);

        Assert.NotNull(derivedMethod);
        // Справжній override: GetBaseDefinition підіймається до WsSession, а не лишається на нашому типі
        // (як було б при `new`).
        Assert.Equal(typeof(WsSession), derivedMethod!.GetBaseDefinition().DeclaringType);
    }
}
