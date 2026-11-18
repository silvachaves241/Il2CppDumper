using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Il2CppDumper.Diff;

internal static class LegacyDiff
{
    internal static int Execute(string leftPath, string rightPath, string outputPath, TextWriter log)
    {
        try
        {
            var report = Compare(leftPath, rightPath);
            var target = outputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(outputPath)
                : Path.Combine(Path.GetFullPath(outputPath), "legacy-diff.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, JsonSerializer.Serialize(report, DumpDiffer.JsonOptions));

            var observation = report.HasDifferences ? "Differences found" : "No differences found in compared tokens";
            log.WriteLine($"{observation}. Coverage is partial ({report.comparedTokens} shared original type tokens).");
            log.WriteLine($"Legacy report written to: {target}");
            return report.HasDifferences ? 1 : 0;
        }
        catch (Exception ex)
        {
            log.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }
    }

    internal static LegacyDiffReport Compare(string leftPath, string rightPath)
    {
        var left = LegacyDiffInput.Read(leftPath, "left");
        var right = LegacyDiffInput.Read(rightPath, "right");
        if (left.Endpoint.format == "dump.json" && right.Endpoint.format == "dump.json")
            throw new InvalidDataException("Legacy mode requires at least one DummyDll input. Compare two JSON dumps with the default mode.");

        var report = new LegacyDiffReport { left = left.Endpoint, right = right.Endpoint };
        foreach (var image in left.Images.Keys.Union(right.Images.Keys).OrderBy(name => name, StringComparer.Ordinal))
        {
            left.Images.TryGetValue(image, out var before);
            right.Images.TryGetValue(image, out var after);
            var status = before == null ? "right-only-image" : after == null ? "left-only-image" :
                before.Tokens.Count == 0 || after.Tokens.Count == 0 ? "original-tokens-unavailable" : "compared";
            var comparison = new LegacyImageComparison
            {
                image = image, status = status,
                leftTypes = before?.TypeCount, rightTypes = after?.TypeCount,
                leftTypesWithoutOriginalToken = before?.WithoutToken, rightTypesWithoutOriginalToken = after?.WithoutToken
            };
            report.images.Add(comparison);
            if (before == null || after == null || status != "compared")
                continue;

            foreach (var (token, type) in before.Tokens.OrderBy(entry => entry.Key))
            {
                if (!after.Tokens.TryGetValue(token, out var other))
                    comparison.leftOnlyTokens.Add(type);
                else if (type.name == null || other.name == null)
                    comparison.namesUnavailable++;
                else if (type.name == other.name)
                    comparison.sameNames++;
                else
                    comparison.nameDifferences.Add(new LegacyTokenNameDifference(type.originalToken, type, other));
            }
            comparison.rightOnlyTokens.AddRange(after.Tokens.OrderBy(entry => entry.Key)
                .Where(entry => !before.Tokens.ContainsKey(entry.Key)).Select(entry => entry.Value));
        }
        if (report.comparedTokens == 0)
            throw new InvalidDataException("No shared original TypeDef tokens in common images. Use DummyDll with original TokenAttribute annotations and, for a JSON input, an export with image and typeToken.");
        return report;
    }
}
