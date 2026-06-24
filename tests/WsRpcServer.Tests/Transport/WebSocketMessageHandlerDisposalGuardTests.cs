using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace WsRpcServer.Tests.Transport;

/// <summary>
/// Класовий guard для R2-M2: БУДЬ-ЯКИЙ метод <c>WebSocketMessageHandler</c>, що пише у вхідний
/// <c>PipeWriter</c> (<c>_writer</c>), МУСИТЬ містити перевірку <c>_disposed</c>. Інакше під час
/// teardown'у запис у вже завершений (через <c>Dispose() → _writer.Complete()</c>) writer кидає
/// <see cref="System.InvalidOperationException"/> у callback'у транспорту / губить дані.
/// </summary>
/// <remarks>
/// На відміну від точкового тесту на конкретний метод, цей guard ловить і <b>новий</b> ingest-метод,
/// доданий у майбутньому без dispose-перевірки (Roslyn-аналіз тіл методів, не runtime-поведінки).
/// </remarks>
public class WebSocketMessageHandlerDisposalGuardTests
{
    private static readonly string[] _pipeWriteMethods = ["Write", "WriteAsync", "FlushAsync"];

    [Fact]
    public void EveryMethodWritingToReceivePipe_GuardsOnDisposed()
    {
        var source = File.ReadAllText(LocateHandlerSource());
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        var violations = new List<string>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            // Чи містить метод інвокацію виду _writer.Write(...) / _writer.WriteAsync(...) / _writer.FlushAsync(...)?
            bool writesPipe = method.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression)
                .OfType<MemberAccessExpressionSyntax>()
                .Any(ma => ma.Expression is IdentifierNameSyntax { Identifier.ValueText: "_writer" } &&
                           _pipeWriteMethods.Contains(ma.Name.Identifier.ValueText));

            if (!writesPipe)
            {
                continue;
            }

            // Метод має посилатися на _disposed (рання перевірка/throw). Якщо в коді з'явиться
            // хелпер на кшталт ThrowIfDisposed() — додай його ім'я сюди в тому ж PR.
            bool guardsDisposed = method.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Any(id => id.Identifier.ValueText == "_disposed");

            if (!guardsDisposed)
            {
                violations.Add(method.Identifier.ValueText);
            }
        }

        Assert.True(
            violations.Count == 0,
            "Методи WebSocketMessageHandler, що пишуть у _writer без перевірки _disposed (R2-M2): " +
            string.Join(", ", violations));
    }

    private static string LocateHandlerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "WsRpcServer", "Transport", "WebSocketMessageHandler.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Не вдалося знайти WebSocketMessageHandler.cs від каталогу тестової збірки.");
    }
}
