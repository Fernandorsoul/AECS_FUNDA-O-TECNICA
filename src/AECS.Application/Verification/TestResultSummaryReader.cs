using System.Globalization;
using System.Xml.Linq;

namespace AECS.Application.Verification;

internal sealed record TestResultSummary(
    bool DiscoveryCompleted,
    int Discovered,
    int Executed,
    int Passed,
    int Failed,
    int Skipped);

internal static class TestResultSummaryReader
{
    public static TestResultSummary Read(string resultsDirectory)
    {
        var trxFiles = Directory.Exists(resultsDirectory)
            ? Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList()
            : [];
        var discovered = 0;
        var executed = 0;
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        foreach (var trxPath in trxFiles)
        {
            var document = XDocument.Load(trxPath, LoadOptions.None);
            var counters = document.Descendants()
                .LastOrDefault(element => element.Name.LocalName == "Counters");
            if (counters is not null)
            {
                var total = Attribute(counters, "total");
                var currentExecuted = Attribute(counters, "executed");
                discovered += total;
                executed += currentExecuted;
                passed += Attribute(counters, "passed");
                failed += Attribute(counters, "failed") + Attribute(counters, "error") +
                    Attribute(counters, "timeout") + Attribute(counters, "aborted");
                skipped += Math.Max(
                    Attribute(counters, "notExecuted"),
                    Math.Max(0, total - currentExecuted));
                continue;
            }

            var results = document.Descendants()
                .Where(element => element.Name.LocalName == "UnitTestResult")
                .ToList();
            discovered += results.Count;
            foreach (var result in results)
            {
                var outcome = result.Attribute("outcome")?.Value ?? string.Empty;
                if (outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase))
                {
                    executed++;
                    passed++;
                }
                else if (outcome.Equals("NotExecuted", StringComparison.OrdinalIgnoreCase) ||
                         outcome.Equals("Skipped", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                }
                else
                {
                    executed++;
                    failed++;
                }
            }
        }

        return new TestResultSummary(
            trxFiles.Count > 0,
            discovered,
            executed,
            passed,
            failed,
            skipped);
    }

    private static int Attribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
