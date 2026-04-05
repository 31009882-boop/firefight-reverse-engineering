using System.Text;

namespace FirefightWorkbench;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try
        {
            var options = CliOptions.Parse(args);
            var assetRoot = AssetLocator.FindAssetRoot(options.AssetRootOverride);
            var inspector = new FirefightAssetInspector(assetRoot);
            string report;
            string outputPath;

            if (options.EmitScenarioSnapshot)
            {
                if (string.IsNullOrWhiteSpace(options.MapCode))
                {
                    throw new ArgumentException("Use --map together with --scenario-snapshot.");
                }

                if (!options.ScenarioIndex.HasValue)
                {
                    throw new ArgumentException("Use --scenario-index together with --scenario-snapshot.");
                }

                var map = inspector.TryLoadMapBrowseData(options.MapCode)
                    ?? throw new ArgumentException($"Map '{options.MapCode}' could not be loaded.");
                if (map.ScenarioData is null || map.ScenarioData.Scenarios.Count == 0)
                {
                    throw new ArgumentException($"Map '{options.MapCode}' does not expose parsed scenario data.");
                }

                if (options.ScenarioIndex.Value < 0 || options.ScenarioIndex.Value >= map.ScenarioData.Scenarios.Count)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(options.ScenarioIndex),
                        options.ScenarioIndex,
                        $"Scenario index must be between 0 and {map.ScenarioData.Scenarios.Count - 1} for map {options.MapCode}.");
                }

                var scenario = map.ScenarioData.Scenarios[options.ScenarioIndex.Value];
                var filterLabel = ScenarioSnapshotFormatter.NormalizeFilterLabel(options.FilterLabel);
                var visibleDeployments = ScenarioSnapshotFormatter.FilterDeployments(scenario.Deployments, filterLabel);
                report = ScenarioSnapshotFormatter.BuildScenarioExportText(assetRoot, map, scenario, visibleDeployments, filterLabel);
                outputPath = options.OutputPath
                    ?? Path.Combine(
                        ScenarioSnapshotFormatter.GetScenarioExportDirectory(AppContext.BaseDirectory),
                        ScenarioSnapshotFormatter.BuildScenarioSnapshotFileName(map.MapCode, options.ScenarioIndex.Value, scenario, filterLabel));
            }
            else if (options.ListScenarios)
            {
                if (string.IsNullOrWhiteSpace(options.MapCode))
                {
                    throw new ArgumentException("Use --map together with --list-scenarios.");
                }

                var map = inspector.TryLoadMapBrowseData(options.MapCode)
                    ?? throw new ArgumentException($"Map '{options.MapCode}' could not be loaded.");
                report = BuildScenarioListReport(assetRoot, map);
                outputPath = options.OutputPath
                    ?? Path.Combine(
                        ScenarioSnapshotFormatter.GetScenarioExportDirectory(AppContext.BaseDirectory),
                        BuildScenarioListFileName(map.MapCode));
            }
            else
            {
                var defaultOutputName = options.EmitAnomalyReport
                    ? BuildAnomalyFileName(options.MapCode)
                    : "FirefightWorkbench_Report.txt";
                var defaultOutputDirectory = options.EmitAnomalyReport && !string.IsNullOrWhiteSpace(options.MapCode)
                    ? ScenarioSnapshotFormatter.GetScenarioExportDirectory(AppContext.BaseDirectory)
                    : AppContext.BaseDirectory;
                outputPath = options.OutputPath ?? Path.Combine(defaultOutputDirectory, defaultOutputName);
                report = options.EmitAnomalyReport
                    ? inspector.BuildAnomalyReport(options.MapCode)
                    : inspector.BuildReport(options.MapCode, options.UnitName, options.Month, options.Year);
            }

            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            File.WriteAllText(outputPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Console.WriteLine(report);
            Console.WriteLine();
            Console.WriteLine($"Report written: {outputPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FirefightWorkbench failed.");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
    private static string BuildScenarioListReport(string assetRoot, MapBrowseData map)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Firefight Workbench Scenario Index Listing");
        builder.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Assets root: {assetRoot}");
        builder.AppendLine($"Map code: {map.MapCode}");
        builder.AppendLine($"Map directory: {map.MapDirectory}");
        builder.AppendLine($"Scenario file: {map.ScenarioFilePath ?? "n/a"}");
        builder.AppendLine($"Scenario count: {map.ScenarioData?.Scenarios.Count ?? 0}");
        builder.AppendLine();

        if (map.ScenarioData is null || map.ScenarioData.Scenarios.Count == 0)
        {
            builder.AppendLine("No parsed scenarios available.");
            return builder.ToString().TrimEnd();
        }

        for (var index = 0; index < map.ScenarioData.Scenarios.Count; index++)
        {
            var scenario = map.ScenarioData.Scenarios[index];
            builder.AppendLine(
                $"[{index:D2}] {scenario.Title} | {scenario.Year:D4}-{scenario.Month:D2} | deployments={scenario.Deployments.Count} | g0={scenario.Deployments.Count(item => item.GroupByte == 0)} | g1={scenario.Deployments.Count(item => item.GroupByte == 1)} | other={scenario.Deployments.Count(item => item.GroupByte is not 0 and not 1)}");
        }

        builder.AppendLine();
        builder.AppendLine("Use this index with --scenario-snapshot --scenario-index <N>.");
        return builder.ToString().TrimEnd();
    }

    private static string BuildScenarioListFileName(string mapCode)
    {
        return $"{ScenarioSnapshotFormatter.SanitizeFileNameComponent(mapCode)}_ScenarioIndex.txt";
    }

    private static string BuildAnomalyFileName(string? mapCode)
    {
        return string.IsNullOrWhiteSpace(mapCode)
            ? "FirefightWorkbench_Anomalies.txt"
            : $"{ScenarioSnapshotFormatter.SanitizeFileNameComponent(mapCode)}_Anomalies.txt";
    }
}

internal sealed record CliOptions(
    string? AssetRootOverride,
    string? OutputPath,
    string? MapCode,
    string? UnitName,
    int? Year,
    int? Month,
    bool EmitAnomalyReport,
    bool EmitScenarioSnapshot,
    bool ListScenarios,
    int? ScenarioIndex,
    string? FilterLabel)
{
    public static CliOptions Parse(string[] args)
    {
        string? assetRoot = null;
        string? output = null;
        string? mapCode = null;
        string? unitName = null;
        string? filterLabel = null;
        int? year = null;
        int? month = null;
        int? scenarioIndex = null;
        var anomalies = false;
        var scenarioSnapshot = false;
        var listScenarios = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            if (arg.Equals("--assets", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                assetRoot = args[++index];
                continue;
            }

            if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                output = args[++index];
                continue;
            }

            if (arg.Equals("--map", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                mapCode = args[++index];
                continue;
            }

            if (arg.Equals("--unit", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                unitName = args[++index];
                continue;
            }

            if (arg.Equals("--filter", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                filterLabel = args[++index];
                continue;
            }

            if (arg.Equals("--month", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                month = ParseIntArgument(args[++index], "--month");
                continue;
            }

            if (arg.Equals("--year", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                year = ParseIntArgument(args[++index], "--year");
                continue;
            }

            if (arg.Equals("--anomalies", StringComparison.OrdinalIgnoreCase))
            {
                anomalies = true;
                continue;
            }

            if (arg.Equals("--scenario-snapshot", StringComparison.OrdinalIgnoreCase))
            {
                scenarioSnapshot = true;
                continue;
            }

            if (arg.Equals("--list-scenarios", StringComparison.OrdinalIgnoreCase))
            {
                listScenarios = true;
                continue;
            }

            if (arg.Equals("--scenario-index", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                scenarioIndex = ParseIntArgument(args[++index], "--scenario-index");
                continue;
            }
        }

        if (year.HasValue ^ month.HasValue)
        {
            throw new ArgumentException("Use --year and --month together when resolving dated unit aliases.");
        }

        if (month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month), month, "Month must be between 1 and 12.");
        }

        var enabledModeCount = (anomalies ? 1 : 0) + (scenarioSnapshot ? 1 : 0) + (listScenarios ? 1 : 0);
        if (enabledModeCount > 1)
        {
            throw new ArgumentException("Use only one mode at a time: --anomalies, --scenario-snapshot, or --list-scenarios.");
        }

        return new CliOptions(assetRoot, output, mapCode, unitName, year, month, anomalies, scenarioSnapshot, listScenarios, scenarioIndex, filterLabel);
    }

    private static int ParseIntArgument(string rawValue, string argumentName)
    {
        if (!int.TryParse(rawValue, out var value))
        {
            throw new ArgumentException($"Invalid integer value '{rawValue}' for {argumentName}.");
        }

        return value;
    }
}

internal static class AssetLocator
{
    public static string FindAssetRoot(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var normalized = Path.GetFullPath(overridePath);
            ValidateAssetRoot(normalized);
            return normalized;
        }

        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            var candidate = TryFindAssetRootUnder(cursor.FullName);
            if (candidate is not null)
            {
                return candidate;
            }

            var siblingCandidate = TryFindAssetRootUnder(Path.GetFullPath(Path.Combine(cursor.FullName, "..")));
            if (siblingCandidate is not null)
            {
                return siblingCandidate;
            }

            cursor = cursor.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the extracted Firefight assets directory. Use --assets.");
    }

    private static void ValidateAssetRoot(string assetRoot)
    {
        if (!LooksLikeAssetRoot(assetRoot))
        {
            throw new DirectoryNotFoundException($"Invalid assets root: {assetRoot}");
        }
    }

    private static string? TryFindAssetRootUnder(string parentDirectory)
    {
        if (!Directory.Exists(parentDirectory))
        {
            return null;
        }

        foreach (var directory in Directory.GetDirectories(parentDirectory))
        {
            var assetsPath = Path.Combine(directory, "assets");
            if (LooksLikeAssetRoot(assetsPath))
            {
                return assetsPath;
            }
        }

        return null;
    }

    private static bool LooksLikeAssetRoot(string assetRoot)
    {
        if (!Directory.Exists(assetRoot))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(assetRoot, "Data"))
            && Directory.Exists(Path.Combine(assetRoot, "Maps"));
    }
}
