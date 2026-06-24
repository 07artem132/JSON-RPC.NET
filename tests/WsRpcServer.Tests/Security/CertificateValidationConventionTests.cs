using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace WsRpcServer.Tests.Security;

/// <summary>
/// Структурний Roslyn-guard для `secure-transport-mtls` («ходьба по колу»): у бібліотечному коді
/// (<c>src/WsRpcServer/**</c>) ЗАБОРОНЕНО (а) lambda сертифікат-валідації, що сліпо повертає
/// <c>=&gt; true</c>, та (б) <c>X509RevocationMode.NoCheck</c> без сусіднього <c>// justification:</c>
/// коментаря. Це сенс існування <c>RemoteCertificateValidationCallback</c> — не нівелювати його.
/// </summary>
public sealed class CertificateValidationConventionTests
{
    private static readonly string[] CertParamNames =
        ["certificate", "cert", "chain", "sslpolicyerrors", "errors"];

    [Fact]
    public void NoBlindCertificateAcceptance_InLibrarySource()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateLibrarySources())
        {
            var text = File.ReadAllText(file);
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();

            foreach (var lambda in BlindAcceptLambdas(root))
            {
                violations.Add($"{Path.GetFileName(file)}: сліпе '=> true' у callback'у валідації сертифіката");
            }

            if (UnjustifiedNoCheck(root, text))
            {
                violations.Add($"{Path.GetFileName(file)}: X509RevocationMode.NoCheck без // justification:");
            }
        }

        Assert.True(violations.Count == 0, "Порушення безпеки валідації сертифіката:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Guard_IsNonVacuous_DetectsBadPatterns()
    {
        // Синтетичні «погані» зразки мають ловитися тим самим предикатом — інакше guard порожній.
        const string blind = "class C { void M() { System.Net.Security.RemoteCertificateValidationCallback cb = " +
                             "(sender, certificate, chain, sslPolicyErrors) => true; } }";
        var root = CSharpSyntaxTree.ParseText(blind).GetRoot();
        Assert.NotEmpty(BlindAcceptLambdas(root));

        const string noCheck = "class C { void M() { var m = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck; } }";
        Assert.True(UnjustifiedNoCheck(CSharpSyntaxTree.ParseText(noCheck).GetRoot(), noCheck));

        const string justified = "class C { void M() {\n// justification: офлайн-середовище без CRL\n" +
                                 "var m = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck; } }";
        Assert.False(UnjustifiedNoCheck(CSharpSyntaxTree.ParseText(justified).GetRoot(), justified));
    }

    /// <summary>Lambda-вирази, тіло яких — літерал <c>true</c>, із параметрами cert-валідації.</summary>
    private static IEnumerable<SyntaxNode> BlindAcceptLambdas(SyntaxNode root)
    {
        foreach (var lambda in root.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>())
        {
            if (lambda.ExpressionBody is LiteralExpressionSyntax { Token.ValueText: "true" } &&
                lambda.ParameterList.Parameters
                    .Any(p => CertParamNames.Contains(p.Identifier.ValueText.ToLowerInvariant())))
            {
                yield return lambda;
            }
        }
    }

    /// <summary>
    /// Чи є реальне ВИКОРИСТАННЯ (member-access) <c>X509RevocationMode.NoCheck</c> у коді (не в коментарі/doc)
    /// без сусіднього <c>justification:</c>-коментаря.
    /// </summary>
    private static bool UnjustifiedNoCheck(SyntaxNode root, string text)
    {
        var lines = text.Split('\n');

        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Name.Identifier.ValueText != "NoCheck" ||
                !access.Expression.ToString().EndsWith("X509RevocationMode", System.StringComparison.Ordinal))
            {
                continue;
            }

            int line = access.GetLocation().GetLineSpan().StartLinePosition.Line;
            bool justified =
                LineHasJustification(lines, line) ||
                LineHasJustification(lines, line - 1) ||
                LineHasJustification(lines, line + 1);

            if (!justified)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LineHasJustification(string[] lines, int index) =>
        index >= 0 && index < lines.Length && lines[index].Contains("justification:");

    private static IEnumerable<string> EnumerateLibrarySources()
    {
        var srcRoot = LocateLibrarySource();
        return Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    private static string LocateLibrarySource()
    {
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

        throw new DirectoryNotFoundException("Не вдалося знайти src/WsRpcServer.");
    }
}
