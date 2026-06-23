using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace WsRpcServer.SourceGenerator;

/// <summary>
/// Інкрементальний source-генератор, що на етапі компіляції виявляє всі реалізації
/// <c>WsRpcServer.Services.IRpcService</c> у збірці споживача й випромінює reflection-free
/// <c>IRpcServiceCatalog</c> (+ DI-розширення <c>AddGeneratedRpcServiceCatalog</c>).
/// Працює лише коли збірку позначено <c>[assembly: GenerateRpcServiceCatalog]</c> — це робить
/// виявлення сервісів trim/AOT-сумісним (без <c>GetExportedTypes</c>/<c>IsAssignableFrom</c>).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RpcServiceCatalogGenerator : IIncrementalGenerator
{
    private const string MarkerAttribute = "WsRpcServer.Services.GenerateRpcServiceCatalogAttribute";
    private const string RpcServiceInterface = "WsRpcServer.Services.IRpcService";
    private const string ClientAwareInterface = "WsRpcServer.Services.IClientAwareRpcService";

    private static readonly DiagnosticDescriptor MultipleImplementations = new(
        id: "WSRPC001",
        title: "Кілька реалізацій одного RPC-інтерфейсу",
        messageFormat:
        "Інтерфейс '{0}' має кілька реалізацій; у каталог увійде '{1}', решту проігноровано. Уточни через DI.",
        category: "WsRpcServer",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var compilationProvider = context.CompilationProvider;
        context.RegisterSourceOutput(compilationProvider, Execute);
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

        // interface → (impl, isClientAware); перша реалізація перемагає, на дублі — діагностика (як рефлексія).
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

                bool derivesRpc = iface.AllInterfaces.Contains(rpcServiceSymbol, SymbolEqualityComparer.Default);
                if (!derivesRpc)
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

        var source = Emit(byInterface
            .Select(kvp => (Interface: kvp.Key, kvp.Value.Impl, kvp.Value.ClientAware))
            .OrderBy(d => d.Interface.ToDisplayString(), System.StringComparer.Ordinal)
            .ToImmutableArray());

        context.AddSource("WsRpcServerRpcServiceCatalog.g.cs", SourceText.From(source, Encoding.UTF8));
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

    private static string Emit(ImmutableArray<(INamedTypeSymbol Interface, INamedTypeSymbol Impl, bool ClientAware)> descriptors)
    {
        var fq = SymbolDisplayFormat.FullyQualifiedFormat;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace WsRpcServer.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Source-генерований reflection-free каталог RPC-сервісів.</summary>");
        sb.AppendLine("    internal sealed class RpcServiceCatalog : global::WsRpcServer.Services.IRpcServiceCatalog");
        sb.AppendLine("    {");
        sb.AppendLine("        private static readonly global::System.Collections.Generic.IReadOnlyList<global::WsRpcServer.Services.RpcServiceDescriptor> _services =");
        sb.AppendLine("            new global::WsRpcServer.Services.RpcServiceDescriptor[]");
        sb.AppendLine("            {");
        foreach (var d in descriptors)
        {
            string iface = d.Interface.ToDisplayString(fq);
            string impl = d.Impl.ToDisplayString(fq);
            string clientAware = d.ClientAware ? "true" : "false";
            sb.Append("                new global::WsRpcServer.Services.RpcServiceDescriptor(typeof(");
            sb.Append(iface);
            sb.Append("), typeof(");
            sb.Append(impl);
            sb.Append("), ");
            sb.Append(clientAware);
            sb.AppendLine("),");
        }

        sb.AppendLine("            };");
        sb.AppendLine();
        sb.AppendLine("        public global::System.Collections.Generic.IReadOnlyList<global::WsRpcServer.Services.RpcServiceDescriptor> Services => _services;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>DI-розширення для реєстрації source-генерованого каталогу RPC-сервісів.</summary>");
        sb.AppendLine("    public static class GeneratedRpcServiceCatalogExtensions");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>Реєструє згенерований <see cref=\"global::WsRpcServer.Services.IRpcServiceCatalog\"/> (reflection-free виявлення сервісів).</summary>");
        sb.AppendLine("        public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddGeneratedRpcServiceCatalog(");
        sb.AppendLine("            this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine("        {");
        sb.AppendLine("            global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<global::WsRpcServer.Services.IRpcServiceCatalog, global::WsRpcServer.Generated.RpcServiceCatalog>(services);");
        sb.AppendLine("            return services;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
