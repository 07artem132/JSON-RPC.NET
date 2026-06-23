using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace WsRpcServer.Tests.Logging;

/// <summary>
/// Regression-guard для capability `logger-message-migration` (2.1.0).
/// Пінує дві інваріанти: (1) у production-коді не лишилось жодного прямого
/// <c>ILogger.Log*("template", …)</c> виклику — усе логування йде через
/// source-generated <c>[LoggerMessage]</c> partial методи у <c>Logging/*Log.cs</c>;
/// (2) EventId у *Log.cs унікальні (блоки не перетинаються).
/// Без цього guard'а CA1848/CA1873 знову б тихо просочились після зняття NoWarn.
/// </summary>
public sealed class LoggerMessageMigrationTests
{
    /// <summary>
    /// Рівні логування, прямий виклик яких на ILogger заборонений у production-коді.
    /// </summary>
    private static readonly Regex DirectLogCall = new(
        @"\.(LogTrace|LogDebug|LogInformation|LogWarning|LogError|LogCritical|Log)\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void ProductionSource_HasNoDirectILoggerCalls()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateProductionSources())
        {
            // Самі *Log.cs містять [LoggerMessage]-декларації, не виклики — пропускаємо.
            if (file.EndsWith("Log.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (DirectLogCall.IsMatch(text))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Прямі ILogger.Log*(...) виклики заборонені — використовуй Logging/*Log.cs. Порушники: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void LoggerMessageEventIds_AreUnique()
    {
        var eventIdRegex = new Regex(@"EventId\s*=\s*(\d+)", RegexOptions.Compiled);
        var seen = new Dictionary<int, string>();
        var collisions = new List<string>();

        foreach (var file in EnumerateProductionSources())
        {
            if (!file.EndsWith("Log.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            foreach (Match m in eventIdRegex.Matches(File.ReadAllText(file)))
            {
                int id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (seen.TryGetValue(id, out var owner))
                {
                    collisions.Add($"{id} ({owner} ↔ {name})");
                }
                else
                {
                    seen[id] = name;
                }
            }
        }

        Assert.True(collisions.Count == 0, "Колізії EventId: " + string.Join(", ", collisions));
        // Sanity: міграція справді відбулась — є щонайменше один *Log.cs з EventId.
        Assert.True(seen.Count >= 30, $"Очікувалось ≥30 EventId, знайдено {seen.Count}.");
    }

    private static IEnumerable<string> EnumerateProductionSources()
    {
        var srcRoot = Path.Combine(RepoRoot(), "src", "WsRpcServer");
        return Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal));
    }

    /// <summary>
    /// Знаходить корінь репозиторію, піднімаючись від цього файлу до теки, що містить
    /// <c>src/WsRpcServer</c>. Стабільніше за обчислення відносно теки bin/.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile)!;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "WsRpcServer")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("Не знайдено корінь репозиторію (src/WsRpcServer).");
    }
}
