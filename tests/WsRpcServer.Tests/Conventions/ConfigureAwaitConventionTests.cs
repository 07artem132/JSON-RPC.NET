using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace WsRpcServer.Tests.Conventions;

/// <summary>
/// Guard для R2-M1: кожен <c>await</c> у бібліотечному коді (<c>src/WsRpcServer/**</c>) МУСИТЬ
/// завершуватися <c>.ConfigureAwait(false)</c> — бібліотека постачається як NuGet-пакет і не сміє
/// захоплювати <see cref="System.Threading.SynchronizationContext"/> споживача (conventions.md).
/// </summary>
/// <remarks>
/// Roslyn-парсинг (а не line-based grep), щоб коректно обробляти багаторядкові await-вирази
/// (напр. <c>await X().WaitAsync(t).ConfigureAwait(false)</c>) та <c>await foreach</c>.
/// </remarks>
public class ConfigureAwaitConventionTests
{
    [Fact]
    public void EveryLibraryAwait_EndsWithConfigureAwait()
    {
        var srcRoot = LocateLibrarySource();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Пропускаємо згенеровані артефакти build (obj/bin).
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text);
            var root = tree.GetRoot();

            // 1) Звичайні await-вирази: awaited-вираз має бути викликом ...ConfigureAwait(...).
            foreach (var awaitExpr in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
            {
                if (!EndsWithConfigureAwait(awaitExpr.Expression))
                {
                    violations.Add($"{Path.GetFileName(file)}:{LineOf(awaitExpr)} — {Squash(awaitExpr.ToString())}");
                }
            }

            // 2) await foreach: колекційний вираз має завершуватися ...ConfigureAwait(...).
            foreach (var forEach in root.DescendantNodes().OfType<CommonForEachStatementSyntax>())
            {
                if (forEach.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) &&
                    !EndsWithConfigureAwait(forEach.Expression))
                {
                    violations.Add($"{Path.GetFileName(file)}:{LineOf(forEach)} — await foreach {Squash(forEach.Expression.ToString())}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Бібліотечні await без .ConfigureAwait(false) (R2-M1):\n  " + string.Join("\n  ", violations));
    }

    private static bool EndsWithConfigureAwait(ExpressionSyntax expression) =>
        expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } &&
        member.Name.Identifier.ValueText == "ConfigureAwait";

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string Squash(string s)
    {
        var collapsed = string.Join(' ', s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 80 ? collapsed[..80] + "…" : collapsed;
    }

    private static string LocateLibrarySource()
    {
        // Піднімаємось від каталогу тестової збірки до кореня репо (де лежить src/WsRpcServer).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "WsRpcServer");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Не вдалося знайти src/WsRpcServer від каталогу тестової збірки.");
    }
}
