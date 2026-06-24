using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamJsonRpc;
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

        private const string SampleWithMethods = """
            using WsRpcServer.Services;
            using StreamJsonRpc;

            [assembly: GenerateRpcServiceCatalog]

            namespace Sample
            {
                public interface IMathRpc : IRpcService
                {
                    System.Threading.Tasks.Task<int> Add(int a, int b);
                    void Reset();
                    [JsonRpcMethod("custom.name")] string Named();
                    [JsonRpcIgnore] void Hidden();
                }

                public sealed class MathRpc : IMathRpc
                {
                    public System.Threading.Tasks.Task<int> Add(int a, int b) => System.Threading.Tasks.Task.FromResult(a + b);
                    public void Reset() { }
                    public string Named() => "x";
                    public void Hidden() { }
                }
            }
            """;

        [Fact]
        public void Generator_EmitsBinderWithAddLocalRpcMethod()
        {
            var (output, generated, diagnostics) = RunGenerator(SampleWithMethods);

            Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Binder реєструє кожен метод через AddLocalRpcMethod із делегатом, прив'язаним на компіляції.
            Assert.Contains("AddGeneratedRpcMethodBinder", generated);
            Assert.Contains(
                "jsonRpc.AddLocalRpcMethod(\"add\", new global::System.Func<int, int, global::System.Threading.Tasks.Task<int>>(",
                generated);
            Assert.Contains("jsonRpc.AddLocalRpcMethod(\"reset\", new global::System.Action(", generated);
            // [JsonRpcMethod] перевизначає ім'я; [JsonRpcIgnore] метод не реєструється.
            Assert.Contains("jsonRpc.AddLocalRpcMethod(\"custom.name\", ", generated);
            Assert.DoesNotContain("\"hidden\"", generated);

            // Згенерований binder валідний C# (компілюється проти реального StreamJsonRpc).
            Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        private const string SampleWithOverloads = """
            using WsRpcServer.Services;
            using StreamJsonRpc;

            [assembly: GenerateRpcServiceCatalog]

            namespace Sample
            {
                public interface IOverloadRpc : IRpcService
                {
                    // Обидва мапляться на JSON-ім'я "send" — AddLocalRpcMethod overload'ів не підтримує.
                    System.Threading.Tasks.Task Send(int a);
                    System.Threading.Tasks.Task Send(string a);
                    void Ping();
                }

                public sealed class OverloadRpc : IOverloadRpc
                {
                    public System.Threading.Tasks.Task Send(int a) => System.Threading.Tasks.Task.CompletedTask;
                    public System.Threading.Tasks.Task Send(string a) => System.Threading.Tasks.Task.CompletedTask;
                    public void Ping() { }
                }
            }
            """;

        [Fact]
        public void Generator_DuplicateJsonName_ReportsWsrpc003AndSkipsColliding()
        {
            // R2-M4: два методи з однаковим JSON-іменем («send») не можна прив'язати через
            // AddLocalRpcMethod (overload'и не підтримуються) — генератор має діагностувати WSRPC003
            // і ВИКЛЮЧИТИ обидва, а не випромінити дубль, що кине на старті прив'язки.
            var (output, generated, diagnostics) = RunGenerator(SampleWithOverloads);

            Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // WSRPC003 повідомлено для колізії.
            Assert.Contains(diagnostics, d => d.Id == "WSRPC003");

            // Жодного AddLocalRpcMethod("send", ...) — колізійні методи виключено.
            Assert.DoesNotContain("AddLocalRpcMethod(\"send\"", generated);

            // Неконфліктний метод лишається прив'язаним.
            Assert.Contains("AddLocalRpcMethod(\"ping\", ", generated);

            // Згенерований код валідний C#.
            Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        // Кілька форм колізій ОДНОЧАСНО: intra-interface overload ("send"), однаковий явний
        // [JsonRpcMethod("dup")] у межах інтерфейсу, та КРОС-СЕРВІСНЕ однакове ім'я ("shared") у двох
        // різних сервісах. Усі прив'язуються на один JsonRpc, тож імена мають бути глобально унікальні.
        private const string SampleWithEveryCollisionShape = """
            using WsRpcServer.Services;
            using StreamJsonRpc;

            [assembly: GenerateRpcServiceCatalog]

            namespace Sample
            {
                public interface IAlpha : IRpcService
                {
                    System.Threading.Tasks.Task Send(int a);                 // -> "send"
                    System.Threading.Tasks.Task Send(string a);              // -> "send" (overload)
                    [JsonRpcMethod("dup")] void A();                          // -> "dup"
                    [JsonRpcMethod("dup")] void B();                          // -> "dup" (explicit collision)
                    void Shared();                                           // -> "shared" (крос-сервіс)
                    void AlphaOnly();                                        // -> "alphaOnly" (чистий)
                }

                public interface IBeta : IRpcService
                {
                    void Shared();                                           // -> "shared" (крос-сервіс)
                    void BetaOnly();                                         // -> "betaOnly" (чистий)
                }

                public sealed class Alpha : IAlpha
                {
                    public System.Threading.Tasks.Task Send(int a) => System.Threading.Tasks.Task.CompletedTask;
                    public System.Threading.Tasks.Task Send(string a) => System.Threading.Tasks.Task.CompletedTask;
                    public void A() { }
                    public void B() { }
                    public void Shared() { }
                    public void AlphaOnly() { }
                }

                public sealed class Beta : IBeta
                {
                    public void Shared() { }
                    public void BetaOnly() { }
                }
            }
            """;

        [Fact]
        public void Generator_BinderMethodNames_AreGloballyUnique()
        {
            // R2-M4 (класовий guard): незалежно від форми колізії (overload / однаковий [JsonRpcMethod] /
            // те саме ім'я у різних сервісах), згенерований binder НІКОЛИ не реєструє одне JSON-ім'я двічі —
            // інакше AddLocalRpcMethod кине на старті прив'язки. Ловить і майбутні крос-сервісні колізії.
            var (output, generated, diagnostics) = RunGenerator(SampleWithEveryCollisionShape);

            Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Усі імена з AddLocalRpcMethod("<name>", ...) — глобально унікальні.
            var names = System.Text.RegularExpressions.Regex
                .Matches(generated, "AddLocalRpcMethod\\(\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.Equal(names.Count, names.Distinct().Count());

            // Колізійні імена виключено повністю; чисті — лишилися.
            Assert.DoesNotContain("send", names);
            Assert.DoesNotContain("dup", names);
            Assert.DoesNotContain("shared", names);   // крос-сервісне — теж знято з обох сервісів
            Assert.Contains("alphaOnly", names);
            Assert.Contains("betaOnly", names);

            // Кожна форма колізії повідомлена WSRPC003.
            Assert.Contains(diagnostics, d => d.Id == "WSRPC003");

            // Згенерований код валідний C#.
            Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        private const string SampleWithAuthorize = """
            using WsRpcServer.Services;
            using WsRpcServer.Authorization;

            [assembly: GenerateRpcServiceCatalog]

            namespace Sample
            {
                public interface ISecureRpc : IRpcService
                {
                    [RpcAuthorize(Roles = "admin")] System.Threading.Tasks.Task Delete(int id);
                    void Ping();
                }

                public sealed class SecureRpc : ISecureRpc
                {
                    public System.Threading.Tasks.Task Delete(int id) => System.Threading.Tasks.Task.CompletedTask;
                    public void Ping() { }
                }
            }
            """;

        [Fact]
        public void Generator_AuthorizedMethod_EmitsEnforceInBinderDelegate()
        {
            // rpc-authorization: позначений метод отримує перевірку політики у ГОЛОВІ делегата
            // (атрибут читається на компіляції → рантайм лишається reflection-free, AOT-clean).
            var (output, generated, diagnostics) = RunGenerator(SampleWithAuthorize);

            Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Bind приймає principal; політика резолвиться; для "delete" емітиться Enforce із роллю.
            Assert.Contains("ClaimsPrincipal? principal", generated);
            Assert.Contains("RpcAuthorizationEnforcer.Enforce(__policy, principal", generated);
            Assert.Contains("Roles = @\"admin\"", generated);
            Assert.Contains("AddLocalRpcMethod(\"delete\"", generated);

            // Непозначений "ping" лишається прямим method-group (без Enforce у його реєстрації).
            Assert.Contains("AddLocalRpcMethod(\"ping\", new global::System.Action(", generated);

            // Згенерований код валідний C# (компілюється проти реального StreamJsonRpc + WsRpcServer).
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
                typeof(JsonRpc).Assembly,                            // StreamJsonRpc (binder посилається на JsonRpc)
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
