using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WsRpcServer.Core;
using Xunit;

namespace WsRpcServer.Tests.Docs;

/// <summary>
/// Guard (analog RG09 із SignalCli.NET): кожен публічний тип збірки <c>WsRpcServer</c> МУСИТЬ бути
/// згаданий хоча б в одному файлі під <c>docs/api/*.md</c>. Новий публічний тип без запису в docs
/// валить білд тестів — anti-omission захист від drift'у docs ↔ public API surface.
/// </summary>
/// <remarks>
/// Це наявність-згадки (не правильність опису): word-boundary regex на ім'я типу. Семантичну
/// актуальність prose забезпечує PR-time review (як зафіксовано у SignalCli.NET
/// <c>audit-debt.md § docs/api/ prose drift</c>).
/// </remarks>
public class DocsApiCoverageTests
{
    [Fact]
    public void EveryPublicType_IsMentionedInDocsApi()
    {
        var docsText = ReadAllDocsApi(out var fileCount);
        Assert.True(fileCount > 0, "Не знайдено жодного docs/api/*.md.");

        var missing = new List<string>();

        foreach (var type in typeof(JsonRpcServerConfig).Assembly.GetExportedTypes())
        {
            // Вкладені/компілятором-згенеровані пропускаємо: документуємо верхньорівневий публічний surface.
            if (type.IsNested)
            {
                continue;
            }

            var name = SimpleName(type);
            var pattern = $@"\b{Regex.Escape(name)}\b";
            if (!Regex.IsMatch(docsText, pattern))
            {
                missing.Add(type.FullName ?? name);
            }
        }

        Assert.True(
            missing.Count == 0,
            "Публічні типи без згадки в docs/api/*.md (додай запис per docs/README.md convention):\n  "
                + string.Join("\n  ", missing));
    }

    // Прибираємо generic-arity (`1, `2) — у docs тип згадується простим іменем.
    private static string SimpleName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        return tick >= 0 ? name[..tick] : name;
    }

    private static string ReadAllDocsApi(out int fileCount)
    {
        var apiDir = LocateDocsApi();
        var files = Directory.EnumerateFiles(apiDir, "*.md", SearchOption.TopDirectoryOnly).ToList();
        fileCount = files.Count;
        return string.Join("\n", files.Select(File.ReadAllText));
    }

    private static string LocateDocsApi()
    {
        // Піднімаємось від каталогу тестової збірки до кореня репо (де лежить docs/api).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "api");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Не вдалося знайти docs/api від каталогу тестової збірки.");
    }
}
