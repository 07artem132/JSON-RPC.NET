using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WsRpcServer.Services;
using WsRpcServer.SourceGenerator;
using Xunit;

namespace WsRpcServer.Tests.Services
{
    /// <summary>
    /// Guard для `sourcegen-catalog`: ганяємо <see cref="RpcServiceCatalogGenerator"/> через
    /// <see cref="CSharpGeneratorDriver"/> над зразковою компіляцією й перевіряємо, що згенерований
    /// каталог містить очікувані дескриптори ТА компілюється без помилок.
    /// </summary>
    public sealed class RpcServiceCatalogGeneratorTests
    {
        private const string SampleWithMarker = """
            using WsRpcServer.Services;

            [assembly: GenerateRpcServiceCatalog]

            namespace Sample
            {
                public interface IRegularRpc : IRpcService { }
                public sealed class RegularRpc : IRegularRpc { }

                public interface IClientAwareRpc : IClientAwareRpcService { }
                public sealed class ClientAwareRpc : IClientAwareRpc { }

                // Без маркерного інтерфейсу — не має потрапити в каталог.
                public interface INotAnRpc { }
                public sealed class NotAnRpc : INotAnRpc { }
            }
            """;

        [Fact]
        public void Generator_WithMarker_EmitsCatalogThatCompiles()
        {
            var (output, generated, diagnostics) = RunGenerator(SampleWithMarker);

            // Жодних помилок генерації.
            Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Каталог містить обидва RPC-сервіси з правильною ознакою client-aware, але не «не-RPC» тип.
            Assert.Contains("typeof(global::Sample.IRegularRpc), typeof(global::Sample.RegularRpc), false", generated);
            Assert.Contains("typeof(global::Sample.IClientAwareRpc), typeof(global::Sample.ClientAwareRpc), true", generated);
            Assert.DoesNotContain("INotAnRpc", generated);
            Assert.Contains("AddGeneratedRpcServiceCatalog", generated);

            // Найважливіше: згенерований код валідний C# (компілюється без помилок із реальними типами).
            Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        [Fact]
        public void Generator_WithoutMarker_EmitsNothing()
        {
            const string sampleNoMarker = """
                using WsRpcServer.Services;
                namespace Sample
                {
                    public interface IRegularRpc : IRpcService { }
                    public sealed class RegularRpc : IRegularRpc { }
                }
                """;

            var (_, generated, _) = RunGenerator(sampleNoMarker);

            // Без [assembly: GenerateRpcServiceCatalog] генератор мовчить.
            Assert.Equal(string.Empty, generated);
        }

        private static (Compilation Output, string Generated, System.Collections.Immutable.ImmutableArray<Diagnostic> Diagnostics)
            RunGenerator(string source)
        {
            // Збираємо референси з усіх завантажених складок ПЛЮС явно ті, що потрібні згенерованому коду
            // (WsRpcServer + DI.Abstractions), бо вони можуть бути ще не завантажені на момент знімка AppDomain.
            var needed = new[]
            {
                typeof(object).Assembly,
                typeof(System.Collections.Generic.IReadOnlyList<>).Assembly,
                typeof(IRpcServiceCatalog).Assembly,                 // WsRpcServer
                typeof(IServiceCollection).Assembly,                 // Microsoft.Extensions.DependencyInjection.Abstractions
                typeof(ServiceCollectionDescriptorExtensions).Assembly,
            };

            var locations = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => a.Location)
                .Concat(needed.Select(a => a.Location))
                .Where(l => !string.IsNullOrEmpty(l))
                .Distinct()
                .ToList();

            var references = locations
                .Select(l => MetadataReference.CreateFromFile(l))
                .Cast<MetadataReference>()
                .ToList();

            var compilation = CSharpCompilation.Create(
                "SampleConsumer",
                [CSharpSyntaxTree.ParseText(source)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(new RpcServiceCatalogGenerator());
            var runDriver = driver.RunGeneratorsAndUpdateCompilation(
                compilation, out var outputCompilation, out var diagnostics);

            var result = runDriver.GetRunResult();
            var generated = result.GeneratedTrees.Length == 0
                ? string.Empty
                : string.Concat(result.GeneratedTrees.Select(t => t.ToString()));

            return (outputCompilation, generated, diagnostics);
        }
    }
}
