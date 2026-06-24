using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace WsRpcServer.SourceGenerator;

/// <summary>
/// Інкрементальний source-генератор. На етапі компіляції виявляє всі реалізації
/// <c>WsRpcServer.Services.IRpcService</c> у збірці споживача й випромінює:
/// (1) reflection-free <c>IRpcServiceCatalog</c> (+ <c>AddGeneratedRpcServiceCatalog</c>) — виявлення;
/// (2) reflection-free <c>IRpcMethodBinder</c> (+ <c>AddGeneratedRpcMethodBinder</c>) — диспетч через
/// <c>JsonRpc.AddLocalRpcMethod(name, delegate)</c> замість рефлексійного <c>AddLocalRpcTarget</c>.
/// Працює лише коли збірку позначено <c>[assembly: GenerateRpcServiceCatalog]</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RpcServiceCatalogGenerator : IIncrementalGenerator
{
    private const string MarkerAttribute = "WsRpcServer.Services.GenerateRpcServiceCatalogAttribute";
    private const string RpcServiceInterface = "WsRpcServer.Services.IRpcService";
    private const string ClientAwareInterface = "WsRpcServer.Services.IClientAwareRpcService";
    private const string JsonRpcMethodAttribute = "StreamJsonRpc.JsonRpcMethodAttribute";
    private const string JsonRpcIgnoreAttribute = "StreamJsonRpc.JsonRpcIgnoreAttribute";

    private static readonly DiagnosticDescriptor MultipleImplementations = new(
        id: "WSRPC001",
        title: "Кілька реалізацій одного RPC-інтерфейсу",
        messageFormat:
        "Інтерфейс '{0}' має кілька реалізацій; у каталог увійде '{1}', решту проігноровано. Уточни через DI.",
        category: "WsRpcServer",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedMethod = new(
        id: "WSRPC002",
        title: "RPC-метод не підтримується source-генерованим binder'ом",
        messageFormat:
        "Метод '{0}' пропущено в AOT-binder'і ({1}); він лишиться доступним лише через рефлексійний AddLocalRpcTarget",
        category: "WsRpcServer",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateJsonName = new(
        id: "WSRPC003",
        title: "Кілька RPC-методів мапляться на однакове JSON-ім'я",
        messageFormat:
        "Метод '{1}' мапиться на JSON-ім'я '{0}', яке вже використовує інший метод; AOT-binder ('AddLocalRpcMethod') не підтримує overload'и — усі методи з цим іменем пропущено (лишаться доступними через рефлексійний AddLocalRpcTarget)",
        category: "WsRpcServer",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>Один RPC-метод, готовий до прив'язки: JSON-ім'я + символ методу.</summary>
    private sealed class MethodBinding(string jsonName, IMethodSymbol method)
    {
        public string JsonName { get; } = jsonName;
        public IMethodSymbol Method { get; } = method;
    }

    /// <summary>Сервіс + його прив'язувані методи.</summary>
    private sealed class ServiceModel(
        INamedTypeSymbol @interface,
        INamedTypeSymbol impl,
        bool clientAware,
        IReadOnlyList<MethodBinding> methods)
    {
        public INamedTypeSymbol Interface { get; } = @interface;
        public INamedTypeSymbol Impl { get; } = impl;
        public bool ClientAware { get; } = clientAware;
        public IReadOnlyList<MethodBinding> Methods { get; } = methods;
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(context.CompilationProvider, Execute);
    }

    private static void Execute(SourceProductionContext context, Compilation compilation)
    {
        var markerSymbol = compilation.GetTypeByMetadataName(MarkerAttribute);
        var rpcServiceSymbol = compilation.GetTypeByMetadataName(RpcServiceInterface);

        // Без референсу на WsRpcServer або без opt-in атрибута — нічого не генеруємо.
        if (markerSymbol is null || rpcServiceSymbol is null)
        {
            return;
        }

        bool optedIn = compilation.Assembly.GetAttributes()
            .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, markerSymbol));
        if (!optedIn)
        {
            return;
        }

        var clientAwareSymbol = compilation.GetTypeByMetadataName(ClientAwareInterface);
        var jsonRpcMethodSymbol = compilation.GetTypeByMetadataName(JsonRpcMethodAttribute);
        var jsonRpcIgnoreSymbol = compilation.GetTypeByMetadataName(JsonRpcIgnoreAttribute);

        // interface → (impl, isClientAware); перша реалізація перемагає, на дублі — діагностика.
        var byInterface = new Dictionary<INamedTypeSymbol, (INamedTypeSymbol Impl, bool ClientAware)>(
            SymbolEqualityComparer.Default);

        foreach (var type in EnumerateNamedTypes(compilation.Assembly.GlobalNamespace))
        {
            if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic)
            {
                continue;
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface, rpcServiceSymbol) ||
                    (clientAwareSymbol is not null &&
                     SymbolEqualityComparer.Default.Equals(iface, clientAwareSymbol)))
                {
                    continue; // самі маркерні інтерфейси не реєструємо
                }

                if (!iface.AllInterfaces.Contains(rpcServiceSymbol, SymbolEqualityComparer.Default))
                {
                    continue;
                }

                bool isClientAware = clientAwareSymbol is not null &&
                                     iface.AllInterfaces.Contains(clientAwareSymbol, SymbolEqualityComparer.Default);

                if (byInterface.TryGetValue(iface, out var existing))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        MultipleImplementations, Location.None,
                        iface.ToDisplayString(), existing.Impl.ToDisplayString()));
                    continue;
                }

                byInterface.Add(iface, (type, isClientAware));
            }
        }

        var services = byInterface
            .Select(kvp => new ServiceModel(
                kvp.Key, kvp.Value.Impl, kvp.Value.ClientAware,
                CollectMethods(context, kvp.Key, rpcServiceSymbol, clientAwareSymbol, jsonRpcMethodSymbol, jsonRpcIgnoreSymbol)))
            .OrderBy(s => s.Interface.ToDisplayString(), System.StringComparer.Ordinal)
            .ToImmutableArray();

        context.AddSource("WsRpcServerRpcServiceCatalog.g.cs", SourceText.From(Emit(services), Encoding.UTF8));
    }

    /// <summary>Збирає прив'язувані методи інтерфейсу (з базових інтерфейсів теж), фільтруючи непідтримувані.</summary>
    private static List<MethodBinding> CollectMethods(
        SourceProductionContext context,
        INamedTypeSymbol iface,
        INamedTypeSymbol rpcServiceSymbol,
        INamedTypeSymbol? clientAwareSymbol,
        INamedTypeSymbol? jsonRpcMethodSymbol,
        INamedTypeSymbol? jsonRpcIgnoreSymbol)
    {
        var result = new List<MethodBinding>();

        // Інтерфейс + його базові інтерфейси, окрім маркерних (IRpcService / IClientAwareRpcService).
        var sources = new[] { iface }.Concat(iface.AllInterfaces)
            .Where(i => !SymbolEqualityComparer.Default.Equals(i, rpcServiceSymbol) &&
                        (clientAwareSymbol is null || !SymbolEqualityComparer.Default.Equals(i, clientAwareSymbol)));

        foreach (var src in sources)
        {
            foreach (var method in src.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.MethodKind != MethodKind.Ordinary)
                {
                    continue;
                }

                if (jsonRpcIgnoreSymbol is not null &&
                    method.GetAttributes().Any(a =>
                        SymbolEqualityComparer.Default.Equals(a.AttributeClass, jsonRpcIgnoreSymbol)))
                {
                    continue;
                }

                string? unsupported = UnsupportedReason(method);
                if (unsupported is not null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnsupportedMethod, method.Locations.FirstOrDefault() ?? Location.None,
                        method.ToDisplayString(), unsupported));
                    continue;
                }

                result.Add(new MethodBinding(ResolveJsonName(method, jsonRpcMethodSymbol), method));
            }
        }

        // R2-M4: JsonRpc.AddLocalRpcMethod(name, delegate) НЕ підтримує overload'и — два методи з
        // однаковим JSON-іменем згенерували б два виклики того самого імені, що кидає на старті
        // прив'язки. Групуємо за JSON-іменем; кожну колізію (overload'и / однаковий [JsonRpcMethod])
        // діагностуємо WSRPC003 і ВИКЛЮЧАЄМО всі методи групи з binder'а (рефлексійний AddLocalRpcTarget
        // лишається для таких сервісів).
        var keep = new List<MethodBinding>(result.Count);
        foreach (var group in result.GroupBy(m => m.JsonName, System.StringComparer.Ordinal))
        {
            if (group.Skip(1).Any())
            {
                foreach (var binding in group)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DuplicateJsonName, binding.Method.Locations.FirstOrDefault() ?? Location.None,
                        binding.JsonName, binding.Method.ToDisplayString()));
                }

                continue;
            }

            keep.Add(group.First());
        }

        return keep
            .OrderBy(m => m.JsonName, System.StringComparer.Ordinal)
            .ThenBy(m => m.Method.Parameters.Length)
            .ToList();
    }

    private static string? UnsupportedReason(IMethodSymbol method)
    {
        if (method.IsGenericMethod)
        {
            return "узагальнені методи не підтримуються";
        }

        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            return "повернення за посиланням не підтримується";
        }

        if (method.Parameters.Length > 16)
        {
            return "понад 16 параметрів (немає відповідного Func/Action)";
        }

        if (method.Parameters.Any(p => p.RefKind != RefKind.None))
        {
            return "параметри ref/out/in не підтримуються";
        }

        return null;
    }

    private static string ResolveJsonName(IMethodSymbol method, INamedTypeSymbol? jsonRpcMethodSymbol)
    {
        if (jsonRpcMethodSymbol is not null)
        {
            var attr = method.GetAttributes().FirstOrDefault(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, jsonRpcMethodSymbol));
            if (attr is not null && attr.ConstructorArguments.Length > 0 &&
                attr.ConstructorArguments[0].Value is string explicitName &&
                !string.IsNullOrEmpty(explicitName))
            {
                return explicitName;
            }
        }

        return CamelCase(method.Name);
    }

    private static string CamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name.Substring(1);

    private static string DelegateType(IMethodSymbol method)
    {
        var parts = method.Parameters.Select(p => p.Type.ToDisplayString(Fq)).ToList();
        if (method.ReturnsVoid)
        {
            return parts.Count == 0
                ? "global::System.Action"
                : "global::System.Action<" + string.Join(", ", parts) + ">";
        }

        parts.Add(method.ReturnType.ToDisplayString(Fq));
        return "global::System.Func<" + string.Join(", ", parts) + ">";
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
            {
                foreach (var nested in EnumerateNamedTypes(childNs))
                {
                    yield return nested;
                }
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in EnumerateNestedTypes(type))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNestedTypes(nested))
            {
                yield return deeper;
            }
        }
    }

    private static string Emit(ImmutableArray<ServiceModel> services)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace WsRpcServer.Generated");
        sb.AppendLine("{");

        EmitCatalog(sb, services);
        sb.AppendLine();
        EmitBinder(sb, services);
        sb.AppendLine();
        EmitExtensions(sb);

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitCatalog(StringBuilder sb, ImmutableArray<ServiceModel> services)
    {
        sb.AppendLine("    /// <summary>Source-генерований reflection-free каталог RPC-сервісів.</summary>");
        sb.AppendLine("    internal sealed class RpcServiceCatalog : global::WsRpcServer.Services.IRpcServiceCatalog");
        sb.AppendLine("    {");
        sb.AppendLine("        private static readonly global::System.Collections.Generic.IReadOnlyList<global::WsRpcServer.Services.RpcServiceDescriptor> _services =");
        sb.AppendLine("            new global::WsRpcServer.Services.RpcServiceDescriptor[]");
        sb.AppendLine("            {");
        foreach (var s in services)
        {
            sb.Append("                new global::WsRpcServer.Services.RpcServiceDescriptor(typeof(");
            sb.Append(s.Interface.ToDisplayString(Fq));
            sb.Append("), typeof(");
            sb.Append(s.Impl.ToDisplayString(Fq));
            sb.Append("), ");
            sb.Append(s.ClientAware ? "true" : "false");
            sb.AppendLine("),");
        }

        sb.AppendLine("            };");
        sb.AppendLine();
        sb.AppendLine("        public global::System.Collections.Generic.IReadOnlyList<global::WsRpcServer.Services.RpcServiceDescriptor> Services => _services;");
        sb.AppendLine("    }");
    }

    private static void EmitBinder(StringBuilder sb, ImmutableArray<ServiceModel> services)
    {
        sb.AppendLine("    /// <summary>Source-генерований reflection-free binder: AddLocalRpcMethod на кожен метод (AOT-диспетч).</summary>");
        sb.AppendLine("    internal sealed class RpcMethodBinder : global::WsRpcServer.Services.IRpcMethodBinder");
        sb.AppendLine("    {");
        sb.AppendLine("        public void Bind(global::StreamJsonRpc.JsonRpc jsonRpc, global::System.IServiceProvider serviceProvider, global::System.Guid clientId)");
        sb.AppendLine("        {");

        int index = 0;
        foreach (var s in services)
        {
            if (s.Methods.Count == 0)
            {
                index++;
                continue;
            }

            string var = "svc" + index;
            string ifaceType = s.Interface.ToDisplayString(Fq);
            sb.AppendLine($"            // {ifaceType}");
            sb.AppendLine("            {");

            if (s.ClientAware)
            {
                sb.Append("                var ").Append(var).Append(" = ");
                sb.Append(ClientAwareInstantiation(s.Impl));
                sb.AppendLine(";");
                foreach (var m in s.Methods)
                {
                    EmitAddMethod(sb, var, m);
                }
            }
            else
            {
                sb.Append("                var ").Append(var).Append(" = (").Append(ifaceType)
                    .Append("?)serviceProvider.GetService(typeof(").Append(ifaceType).AppendLine("));");
                sb.AppendLine($"                if ({var} != null)");
                sb.AppendLine("                {");
                foreach (var m in s.Methods)
                {
                    EmitAddMethod(sb, "                " + var, m, indent: "                    ");
                }

                sb.AppendLine("                }");
            }

            sb.AppendLine("            }");
            index++;
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static void EmitAddMethod(StringBuilder sb, string instanceVar, MethodBinding m, string indent = "                ")
    {
        // instanceVar може містити провідні пробіли для regular-гілки — приберемо їх для виразу.
        string v = instanceVar.Trim();
        sb.Append(indent).Append("jsonRpc.AddLocalRpcMethod(\"").Append(m.JsonName).Append("\", new ")
            .Append(DelegateType(m.Method)).Append('(').Append(v).Append('.').Append(m.Method.Name).AppendLine("));");
    }

    /// <summary>Прямий виклик конструктора для клієнт-залежного сервісу (AOT-безпечно): Guid→clientId, решта→DI.</summary>
    private static string ClientAwareInstantiation(INamedTypeSymbol impl)
    {
        var ctor = impl.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();

        string implType = impl.ToDisplayString(Fq);
        if (ctor is null || ctor.Parameters.Length == 0)
        {
            return "new " + implType + "()";
        }

        bool clientIdUsed = false;
        var args = new List<string>(ctor.Parameters.Length);
        foreach (var p in ctor.Parameters)
        {
            if (!clientIdUsed && p.Type.SpecialType == SpecialType.None &&
                p.Type.ToDisplayString(Fq) == "global::System.Guid")
            {
                args.Add("clientId");
                clientIdUsed = true;
            }
            else
            {
                args.Add(
                    "global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<" +
                    p.Type.ToDisplayString(Fq) + ">(serviceProvider)");
            }
        }

        return "new " + implType + "(" + string.Join(", ", args) + ")";
    }

    private static void EmitExtensions(StringBuilder sb)
    {
        sb.AppendLine("    /// <summary>DI-розширення для source-генерованих каталогу та binder'а RPC-сервісів.</summary>");
        sb.AppendLine("    public static class GeneratedRpcServiceCatalogExtensions");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>Реєструє згенерований <see cref=\"global::WsRpcServer.Services.IRpcServiceCatalog\"/> (reflection-free виявлення сервісів).</summary>");
        sb.AppendLine("        public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddGeneratedRpcServiceCatalog(");
        sb.AppendLine("            this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine("        {");
        sb.AppendLine("            global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<global::WsRpcServer.Services.IRpcServiceCatalog, global::WsRpcServer.Generated.RpcServiceCatalog>(services);");
        sb.AppendLine("            return services;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>Реєструє згенерований <see cref=\"global::WsRpcServer.Services.IRpcMethodBinder\"/> (reflection-free AOT-диспетч через AddLocalRpcMethod).</summary>");
        sb.AppendLine("        public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddGeneratedRpcMethodBinder(");
        sb.AppendLine("            this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine("        {");
        sb.AppendLine("            global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<global::WsRpcServer.Services.IRpcMethodBinder, global::WsRpcServer.Generated.RpcMethodBinder>(services);");
        sb.AppendLine("            return services;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }
}
