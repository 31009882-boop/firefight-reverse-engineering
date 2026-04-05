using System.Text;

namespace FirefightWorkbench;

internal static class ScenarioSnapshotFormatter
{
    public static IReadOnlyList<ScenarioDeployment> FilterDeployments(
        IReadOnlyList<ScenarioDeployment> deployments,
        string? filterLabel)
    {
        var choice = ResolveFilter(filterLabel);
        IEnumerable<ScenarioDeployment> filtered = deployments;

        if (choice.IsOtherBucket)
        {
            filtered = filtered.Where(item => item.GroupByte is not 0 and not 1);
        }
        else if (choice.GroupByte.HasValue)
        {
            filtered = filtered.Where(item => item.GroupByte == choice.GroupByte.Value);
        }

        if (choice.Flag4Only)
        {
            filtered = filtered.Where(item => item.FlagByte != 0);
        }

        if (choice.DelayedOnly)
        {
            filtered = filtered.Where(item => item.DelayGuess > 0);
        }

        return filtered.ToArray();
    }

    public static string NormalizeFilterLabel(string? filterLabel)
    {
        return ResolveFilter(filterLabel).Label;
    }

    public static string BuildMapSummary(MapBrowseData map, bool includeWorldDiagnostics = true)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Root: {map.RootName}");
        builder.AppendLine($"Map code: {map.MapCode}");
        builder.AppendLine($"Display name: {map.MapDisplayName}");
        if (!string.IsNullOrWhiteSpace(map.MapDescription))
        {
            builder.AppendLine();
            builder.AppendLine(map.MapDescription);
        }

        builder.AppendLine();
        builder.AppendLine($"Tile grid: {map.TileColumns} x {map.TileRows}");
        builder.AppendLine($"World size: {(map.WorldWidth is null || map.WorldHeight is null ? "n/a" : $"{map.WorldWidth} x {map.WorldHeight}")}");
        builder.AppendLine($"Has map image: {!string.IsNullOrWhiteSpace(map.MapImagePath)}");
        builder.AppendLine($"Has contours: {!string.IsNullOrWhiteSpace(map.ContoursPath)}");
        builder.AppendLine($"Has world data: {!string.IsNullOrWhiteSpace(map.WorldFilePath)}");
        builder.AppendLine($"Has scenario data: {map.ScenarioData is not null}");
        builder.AppendLine($"Scenario count: {map.ScenarioData?.Scenarios.Count ?? 0}");
        builder.AppendLine($"Deployment count: {map.ScenarioData?.Scenarios.Sum(item => item.Deployments.Count) ?? 0}");
        builder.AppendLine($"World prototype count: {map.WorldPrototypeTable?.Entries.Count ?? 0}");
        builder.AppendLine($"World instance count: {map.WorldInstanceProbe?.ParsedCount ?? 0}");
        if (includeWorldDiagnostics)
        {
            AppendWorldDiagnosticsSection(builder, map);
        }

        if (map.ScenarioData is not null)
        {
            var mapDeployments = map.ScenarioData.Scenarios
                .SelectMany(item => item.Deployments)
                .ToArray();
            var g0Count = mapDeployments.Count(item => item.GroupByte == 0);
            var g1Count = mapDeployments.Count(item => item.GroupByte == 1);
            var otherGroupCount = mapDeployments.Count(item => item.GroupByte is not 0 and not 1);
            var flag4Count = mapDeployments.Count(item => item.FlagByte != 0);
            var delayedCount = mapDeployments.Count(item => item.DelayGuess > 0);
            var specialRecordCount = map.ScenarioData.Scenarios.Sum(item => item.SpecialRecordCandidates.Count);
            builder.AppendLine($"Map deployment groups: g0={g0Count} / g1={g1Count} / other={otherGroupCount} / flag4={flag4Count} / delayed={delayedCount}");
            builder.AppendLine($"Special-record candidates: {specialRecordCount}");
            AppendScenarioScopedSampleSection(builder, "Map other-group samples", map.ScenarioData.Scenarios, item => item.GroupByte is not 0 and not 1, 3);
            AppendScenarioScopedSampleSection(builder, "Map flag4-active samples", map.ScenarioData.Scenarios, item => item.FlagByte != 0, 3);
            AppendScenarioScopedSampleSection(builder, "Map delayed samples", map.ScenarioData.Scenarios, item => item.DelayGuess > 0, 3);
            AppendScenarioScopedSpecialRecordSection(builder, "Map special-record samples", map.ScenarioData.Scenarios, 3);
        }

        if (map.LocalResourceDirectories.Count > 0)
        {
            builder.AppendLine($"Local resource dirs: {string.Join(", ", map.LocalResourceDirectories)}");
        }

        if (map.WorldPrototypeTable is not null)
        {
            builder.AppendLine($"World prototype sample: {string.Join(" / ", map.WorldPrototypeTable.Entries.Take(8).Select(item => item.Name))}");
        }
        else if (map.WorldTokenSample.Count > 0)
        {
            builder.AppendLine($"World token sample: {string.Join(" / ", map.WorldTokenSample.Take(8))}");
        }

        if (map.WorldTailTokenSample.Count > 0)
        {
            builder.AppendLine($"World tail sample: {string.Join(" / ", map.WorldTailTokenSample.Take(8))}");
        }

        if (map.WorldInstanceTable is not null)
        {
            builder.AppendLine($"World instance sample: {string.Join(" / ", map.WorldInstanceTable.Entries.Take(8).Select(item => $"{item.PrototypeName}@({item.X},{item.Y})"))}");
        }

        if (map.MapImagePath is not null)
        {
            builder.AppendLine($"Map image: {map.MapImagePath}");
        }
        if (map.ContoursPath is not null)
        {
            builder.AppendLine($"Contours: {map.ContoursPath}");
        }
        if (map.WorldFilePath is not null)
        {
            builder.AppendLine($"World file: {map.WorldFilePath}");
        }
        if (map.ScenarioFilePath is not null)
        {
            builder.AppendLine($"Scenario file: {map.ScenarioFilePath}");
        }

        return builder.ToString().TrimEnd();
    }

    public static string BuildWorldDiagnosticsReport(MapBrowseData map)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{map.MapCode} | World Diagnostics");
        builder.AppendLine(new string('=', Math.Max(24, map.MapCode.Length + 20)));
        builder.AppendLine($"Root: {map.RootName}");
        builder.AppendLine($"Display name: {map.MapDisplayName}");
        builder.AppendLine($"Map directory: {map.MapDirectory}");
        builder.AppendLine($"World file: {map.WorldFilePath ?? "n/a"}");
        builder.AppendLine($"World size: {(map.WorldWidth is null || map.WorldHeight is null ? "n/a" : $"{map.WorldWidth} x {map.WorldHeight}")}");
        builder.AppendLine($"World prototype count: {map.WorldPrototypeTable?.Entries.Count ?? 0}");
        builder.AppendLine($"World instance count: {map.WorldInstanceProbe?.ParsedCount ?? 0}");
        builder.AppendLine();
        AppendWorldDiagnosticsSection(builder, map);
        return builder.ToString().TrimEnd();
    }

    public static string BuildScenarioExportText(
        string assetRoot,
        MapBrowseData map,
        ParsedScenario scenario,
        IReadOnlyList<ScenarioDeployment> visibleDeployments,
        string? filterLabel)
    {
        var normalizedFilterLabel = NormalizeFilterLabel(filterLabel);
        var builder = new StringBuilder();
        builder.AppendLine("Firefight Desktop Scenario Snapshot");
        builder.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Assets root: {assetRoot}");
        builder.AppendLine($"Map directory: {map.MapDirectory}");
        if (!string.IsNullOrWhiteSpace(map.ScenarioFilePath))
        {
            builder.AppendLine($"Scenario file: {map.ScenarioFilePath}");
        }

        builder.AppendLine();
        builder.AppendLine(BuildScenarioSummary(map, scenario, visibleDeployments, normalizedFilterLabel));
        return builder.ToString().TrimEnd();
    }

    public static string BuildScenarioSummary(
        MapBrowseData map,
        ParsedScenario scenario,
        IReadOnlyList<ScenarioDeployment> visibleDeployments,
        string? filterLabel)
    {
        var normalizedFilterLabel = NormalizeFilterLabel(filterLabel);
        var g0Count = scenario.Deployments.Count(item => item.GroupByte == 0);
        var g1Count = scenario.Deployments.Count(item => item.GroupByte == 1);
        var otherGroupCount = scenario.Deployments.Count(item => item.GroupByte is not 0 and not 1);
        var flag4Count = scenario.Deployments.Count(item => item.FlagByte != 0);
        var delayedCount = scenario.Deployments.Count(item => item.DelayGuess > 0);
        var builder = new StringBuilder();
        builder.AppendLine($"{map.MapCode} | {scenario.Title}");
        builder.AppendLine(new string('=', Math.Max(12, scenario.Title.Length + map.MapCode.Length + 3)));
        builder.AppendLine($"Date: {scenario.Year:D4}-{scenario.Month:D2}");
        builder.AppendLine($"Side A code: {scenario.SideACode}");
        builder.AppendLine($"Side B code: {scenario.SideBCode}");
        builder.AppendLine($"Artillery HE: {scenario.ArtilleryHeSideA}/{scenario.ArtilleryHeSideB}");
        builder.AppendLine($"Artillery Smoke: {scenario.ArtillerySmokeSideA}/{scenario.ArtillerySmokeSideB}");
        builder.AppendLine($"Camera center: ({scenario.CameraCenterX:N1}, {scenario.CameraCenterY:N1})");
        builder.AppendLine($"Deployment count: {scenario.Deployments.Count}");
        builder.AppendLine($"Deployment groups: g0={g0Count} / g1={g1Count} / other={otherGroupCount} / flag4={flag4Count} / delayed={delayedCount}");
        builder.AppendLine($"Active filter: {normalizedFilterLabel} -> {visibleDeployments.Count}/{scenario.Deployments.Count} visible");
        builder.AppendLine($"Special-record candidates: {scenario.SpecialRecordCandidates.Count}");
        builder.AppendLine($"Special-record world links: {scenario.SpecialRecordCandidates.Sum(item => item.WorldPointLinks.Count)}");
        builder.AppendLine($"World instance count: {map.WorldInstanceProbe?.ParsedCount ?? 0}");
        builder.AppendLine($"Post-deployment tail bytes: {scenario.PostDeploymentTailBytes.Length}");
        builder.AppendLine($"Metadata tail: {scenario.MetadataTailHex}");
        AppendWorldDiagnosticsSection(builder, map);
        builder.AppendLine();
        builder.AppendLine(scenario.Description);
        AppendOriginalComparisonBaselineSection(builder, scenario);
        AppendDeploymentSampleSection(builder, scenario, $"Filtered sample [{normalizedFilterLabel}]", visibleDeployments, item => true, 8);
        AppendDeploymentSampleSection(builder, scenario, "Other-group samples", scenario.Deployments, item => item.GroupByte is not 0 and not 1, 8);
        AppendSpecialRecordSection(builder, scenario, 8);
        AppendDeploymentSampleSection(builder, scenario, "Flag4-active samples", scenario.Deployments, item => item.FlagByte != 0, 8);
        AppendDeploymentSampleSection(builder, scenario, "Delayed samples", scenario.Deployments, item => item.DelayGuess > 0, 8);

        return builder.ToString().TrimEnd();
    }

    public static string GetScenarioExportDirectory(string appBaseDirectory)
    {
        var cursor = new DirectoryInfo(appBaseDirectory);
        while (cursor is not null)
        {
            if (string.Equals(cursor.Name, "FirefightDesktop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cursor.Name, "FirefightRebuild.Workbench", StringComparison.OrdinalIgnoreCase))
            {
                var resultRoot = cursor.Parent?.FullName ?? cursor.FullName;
                var exportDirectory = Path.Combine(resultRoot, "ScenarioExports");
                Directory.CreateDirectory(exportDirectory);
                return exportDirectory;
            }

            cursor = cursor.Parent;
        }

        var fallbackDirectory = Path.Combine(appBaseDirectory, "ScenarioExports");
        Directory.CreateDirectory(fallbackDirectory);
        return fallbackDirectory;
    }

    public static string BuildScenarioSnapshotFileName(
        string mapCode,
        int scenarioIndex,
        ParsedScenario scenario,
        string? filterLabel)
    {
        var normalizedFilterLabel = NormalizeFilterLabel(filterLabel);
        return
            $"{SanitizeFileNameComponent(mapCode)}_{scenarioIndex:D2}_{scenario.Year:D4}{scenario.Month:D2}_{SanitizeFileNameComponent(scenario.Title)}_{SanitizeFileNameComponent(normalizedFilterLabel)}_ScenarioSnapshot.txt";
    }

    public static string SanitizeFileNameComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unnamed";
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(invalidCharacters.Contains(character) || char.IsWhiteSpace(character) ? '_' : character);
        }

        var sanitized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }

    internal static string BuildDeploymentReplicaComparisonNotes(
        ParsedScenario scenario,
        ScenarioDeployment deployment)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Replica comparison notes:");

        var specialCandidate = TryFindSpecialRecordCandidate(scenario, deployment);
        if (specialCandidate is null)
        {
            builder.AppendLine("raw deployment position remains the primary original spatial anchor for this record.");
            return builder.ToString().TrimEnd();
        }

        var digest = BuildSpecialRouteSpatialDigest(deployment, specialCandidate);
        builder.AppendLine($"special-route baseline: marker=0x{specialCandidate.MarkerWord:X4}, group=g{deployment.GroupByte}, points={digest.PointCount}, worldLinks={digest.WorldLinkCount}/{digest.PointCount}");
        builder.AppendLine($"raw deployment position: ({deployment.X:N0}, {deployment.Y:N0})");
        builder.AppendLine($"route start/end: ({digest.StartX:N0}, {digest.StartY:N0}) -> ({digest.EndX:N0}, {digest.EndY:N0})");
        builder.AppendLine($"route centroid: ({digest.CentroidX:N0}, {digest.CentroidY:N0})");
        builder.AppendLine($"route bounds: x[{digest.MinX:N0}, {digest.MaxX:N0}] y[{digest.MinY:N0}, {digest.MaxY:N0}]");
        builder.AppendLine($"route path length: {digest.PathLength:N1}");
        builder.AppendLine($"raw->route displacement: start={digest.RawToStartDistance:N1}, centroid={digest.RawToCentroidDistance:N1}");
        if (digest.WorldLinkCount > 0)
        {
            builder.AppendLine($"world-link distances: min/avg/max={digest.MinWorldLinkDistance:N1}/{digest.AverageWorldLinkDistance:N1}/{digest.MaxWorldLinkDistance:N1}");
            builder.AppendLine($"world-link sample: {BuildWorldLinkPrototypeSample(specialCandidate, 4)}");
        }
        else
        {
            builder.AppendLine("world-link distances: none");
        }

        builder.AppendLine("comparison rule: validate the replica against route/world-link geometry instead of the raw deployment origin.");
        if (ShouldSuppressRawDeploymentMarker(deployment, specialCandidate))
        {
            builder.AppendLine("desktop view rule: suppress the raw marker to avoid a false (0,0) anchor.");
        }

        return builder.ToString().TrimEnd();
    }

    internal static bool ShouldSuppressRawDeploymentMarker(
        ScenarioDeployment deployment,
        ScenarioSpecialRecordCandidate? specialRoute)
    {
        if (specialRoute is null || specialRoute.Points.Count == 0)
        {
            return false;
        }

        if (MathF.Abs(deployment.X) > 0.5f || MathF.Abs(deployment.Y) > 0.5f)
        {
            return false;
        }

        var start = specialRoute.Points[0];
        var dx = start.X - deployment.X;
        var dy = start.Y - deployment.Y;
        return MathF.Sqrt(dx * dx + dy * dy) >= 64f;
    }

    private static void AppendWorldDiagnosticsSection(StringBuilder builder, MapBrowseData map)
    {
        if (!string.IsNullOrWhiteSpace(map.WorldDiagnosticsSummary))
        {
            builder.AppendLine(map.WorldDiagnosticsSummary);
            return;
        }

        builder.AppendLine($"World format signature: {FirefightAssetInspector.BuildWorldFormatSignature(map.WorldHeader)}");
    }

    private static FilterDescriptor ResolveFilter(string? filterLabel)
    {
        var normalized = (filterLabel ?? "All").Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "all" => new FilterDescriptor("All", null),
            "g0" => new FilterDescriptor("g0", 0),
            "g1" => new FilterDescriptor("g1", 1),
            "other" => new FilterDescriptor("Other", null, IsOtherBucket: true),
            "flag4" or "flag4>0" or "flag4active" => new FilterDescriptor("Flag4>0", null, Flag4Only: true),
            "delay" or "delay>0" or "delayed" or "delayactive" => new FilterDescriptor("Delay>0", null, DelayedOnly: true),
            _ => throw new ArgumentException(
                $"Unsupported deployment filter '{filterLabel}'. Supported values: All, g0, g1, Other, Flag4>0, Delay>0.")
        };
    }

    private static void AppendScenarioScopedSampleSection(
        StringBuilder builder,
        string title,
        IReadOnlyList<ParsedScenario> scenarios,
        Func<ScenarioDeployment, bool> predicate,
        int maxSamples)
    {
        var samples = scenarios
            .SelectMany(
                scenario => scenario.Deployments
                    .Where(predicate)
                    .Select(deployment => $"{scenario.Title} :: {BuildDeploymentDigest(scenario, deployment)}"))
            .ToArray();

        builder.AppendLine($"{title}: {samples.Length}");
        if (samples.Length == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var sample in samples.Take(maxSamples))
        {
            builder.AppendLine($"- {sample}");
        }

        if (samples.Length > maxSamples)
        {
            builder.AppendLine($"- ... {samples.Length - maxSamples} more");
        }
    }

    private static void AppendDeploymentSampleSection(
        StringBuilder builder,
        ParsedScenario scenario,
        string title,
        IReadOnlyList<ScenarioDeployment> deployments,
        Func<ScenarioDeployment, bool> predicate,
        int maxSamples)
    {
        var samples = deployments
            .Where(predicate)
            .ToArray();

        builder.AppendLine();
        builder.AppendLine($"{title}: {samples.Length}");
        if (samples.Length == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var sample in samples.Take(maxSamples))
        {
            builder.AppendLine($"- {BuildDeploymentDigest(scenario, sample)}");
        }

        if (samples.Length > maxSamples)
        {
            builder.AppendLine($"- ... {samples.Length - maxSamples} more");
        }
    }

    private static void AppendOriginalComparisonBaselineSection(
        StringBuilder builder,
        ParsedScenario scenario)
    {
        builder.AppendLine();
        builder.AppendLine("Original comparison baseline:");

        var specialDeployments = scenario.Deployments
            .Select(deployment => new
            {
                Deployment = deployment,
                SpecialCandidate = TryFindSpecialRecordCandidate(scenario, deployment),
            })
            .Where(item => item.SpecialCandidate is not null)
            .ToArray();

        if (specialDeployments.Length == 0)
        {
            builder.AppendLine($"- raw deployment coordinates remain authoritative for all {scenario.Deployments.Count} records");
            return;
        }

        var rawAnchorCount = scenario.Deployments.Count - specialDeployments.Length;
        builder.AppendLine($"- raw coordinate anchors: {rawAnchorCount}/{scenario.Deployments.Count}");
        builder.AppendLine($"- special-route anchors: {specialDeployments.Length}/{scenario.Deployments.Count}");
        foreach (var item in specialDeployments)
        {
            builder.AppendLine($"- {BuildSpecialRouteComparisonDigest(item.Deployment, item.SpecialCandidate!)}");
        }

        builder.AppendLine("- rule: compare special-route records against route/world-link geometry, not the raw deployment coordinates");
    }

    private static void AppendScenarioScopedSpecialRecordSection(
        StringBuilder builder,
        string title,
        IReadOnlyList<ParsedScenario> scenarios,
        int maxSamples)
    {
        var samples = scenarios
            .SelectMany(
                scenario => scenario.SpecialRecordCandidates
                    .Select(candidate => $"{scenario.Title} :: {BuildSpecialRecordDigest(candidate)}"))
            .ToArray();

        builder.AppendLine($"{title}: {samples.Length}");
        if (samples.Length == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var sample in samples.Take(maxSamples))
        {
            builder.AppendLine($"- {sample}");
        }

        if (samples.Length > maxSamples)
        {
            builder.AppendLine($"- ... {samples.Length - maxSamples} more");
        }
    }

    private static void AppendSpecialRecordSection(
        StringBuilder builder,
        ParsedScenario scenario,
        int maxSamples)
    {
        builder.AppendLine();
        builder.AppendLine($"Special-record candidates: {scenario.SpecialRecordCandidates.Count}");
        if (scenario.SpecialRecordCandidates.Count == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var candidate in scenario.SpecialRecordCandidates.Take(maxSamples))
        {
            builder.AppendLine($"- {BuildSpecialRecordDigest(candidate)}");
        }

        if (scenario.SpecialRecordCandidates.Count > maxSamples)
        {
            builder.AppendLine($"- ... {scenario.SpecialRecordCandidates.Count - maxSamples} more");
        }
    }

    private static string BuildDeploymentDigest(ParsedScenario scenario, ScenarioDeployment deployment)
    {
        var definitionLabel = string.IsNullOrWhiteSpace(deployment.DefinitionPath)
            ? "-"
            : Path.GetFileNameWithoutExtension(deployment.DefinitionPath);
        var specialCandidate = TryFindSpecialRecordCandidate(scenario, deployment);
        var positionLabel = ShouldSuppressRawDeploymentMarker(deployment, specialCandidate)
            ? $"raw({deployment.X:N0}, {deployment.Y:N0})"
            : $"({deployment.X:N0}, {deployment.Y:N0})";
        var baseDigest =
            $"0x{deployment.RecordOffset:X4} | g{deployment.GroupByte} | flag4={deployment.FlagByte} | delay={deployment.DelayGuess} | {deployment.Category} | {deployment.UnitName} | pos={positionLabel} | def={definitionLabel}";
        return specialCandidate is null
            ? baseDigest
            : $"{baseDigest} | special=0x{specialCandidate.MarkerWord:X4}/{specialCandidate.Points.Count}pts";
    }

    private static ScenarioSpecialRecordCandidate? TryFindSpecialRecordCandidate(
        ParsedScenario scenario,
        ScenarioDeployment deployment)
    {
        return scenario.SpecialRecordCandidates
            .FirstOrDefault(item => item.DeploymentRecordOffset == deployment.RecordOffset);
    }

    private static string BuildSpecialRecordDigest(ScenarioSpecialRecordCandidate candidate)
    {
        var pointSample = string.Join(" -> ", candidate.Points.Take(4).Select(point => $"({point.X:N0}, {point.Y:N0})"));
        if (candidate.Points.Count > 4)
        {
            pointSample = $"{pointSample} -> ...";
        }

        var worldLinkSample = BuildWorldLinkSample(candidate);
        return $"0x{candidate.DeploymentRecordOffset:X4} | marker=0x{candidate.MarkerWord:X4} | points={candidate.Points.Count} | route={pointSample}{worldLinkSample}";
    }

    private static string BuildWorldLinkSample(ScenarioSpecialRecordCandidate candidate)
    {
        if (candidate.WorldPointLinks.Count == 0)
        {
            return string.Empty;
        }

        var sample = string.Join(
            " ; ",
            candidate.WorldPointLinks
                .Take(2)
                .Select(link => $"#{link.PointIndex + 1}->{link.PrototypeName}@{link.Distance:N0}"));
        return candidate.WorldPointLinks.Count > 2
            ? $" | world={sample} ; ..."
            : $" | world={sample}";
    }

    private static string BuildSpecialRouteComparisonDigest(
        ScenarioDeployment deployment,
        ScenarioSpecialRecordCandidate candidate)
    {
        var digest = BuildSpecialRouteSpatialDigest(deployment, candidate);
        var worldDistanceLabel = digest.WorldLinkCount == 0
            ? "world=none"
            : $"world={digest.WorldLinkCount}/{digest.PointCount}, d={digest.MinWorldLinkDistance:N0}/{digest.AverageWorldLinkDistance:N0}/{digest.MaxWorldLinkDistance:N0}";
        return
            $"0x{deployment.RecordOffset:X4} {deployment.UnitName} [g{deployment.GroupByte}] raw=({deployment.X:N0}, {deployment.Y:N0}) -> route={digest.PointCount}pts start=({digest.StartX:N0}, {digest.StartY:N0}) end=({digest.EndX:N0}, {digest.EndY:N0}) bbox=x[{digest.MinX:N0}, {digest.MaxX:N0}] y[{digest.MinY:N0}, {digest.MaxY:N0}] path={digest.PathLength:N0} {worldDistanceLabel} suppressRawMarker={(ShouldSuppressRawDeploymentMarker(deployment, candidate) ? "yes" : "no")}";
    }

    private static SpecialRouteSpatialDigest BuildSpecialRouteSpatialDigest(
        ScenarioDeployment deployment,
        ScenarioSpecialRecordCandidate candidate)
    {
        if (candidate.Points.Count == 0)
        {
            return new SpecialRouteSpatialDigest(
                PointCount: 0,
                StartX: deployment.X,
                StartY: deployment.Y,
                EndX: deployment.X,
                EndY: deployment.Y,
                CentroidX: deployment.X,
                CentroidY: deployment.Y,
                MinX: deployment.X,
                MinY: deployment.Y,
                MaxX: deployment.X,
                MaxY: deployment.Y,
                PathLength: 0f,
                RawToStartDistance: 0f,
                RawToCentroidDistance: 0f,
                WorldLinkCount: candidate.WorldPointLinks.Count,
                MinWorldLinkDistance: 0f,
                AverageWorldLinkDistance: 0f,
                MaxWorldLinkDistance: 0f);
        }

        var start = candidate.Points[0];
        var end = candidate.Points[^1];
        var minX = candidate.Points.Min(point => point.X);
        var minY = candidate.Points.Min(point => point.Y);
        var maxX = candidate.Points.Max(point => point.X);
        var maxY = candidate.Points.Max(point => point.Y);
        var centroidX = candidate.Points.Average(point => point.X);
        var centroidY = candidate.Points.Average(point => point.Y);
        var pathLength = 0f;
        for (var index = 1; index < candidate.Points.Count; index++)
        {
            var previous = candidate.Points[index - 1];
            var current = candidate.Points[index];
            var dx = current.X - previous.X;
            var dy = current.Y - previous.Y;
            pathLength += MathF.Sqrt(dx * dx + dy * dy);
        }

        var rawToStartX = start.X - deployment.X;
        var rawToStartY = start.Y - deployment.Y;
        var rawToCentroidX = (float)centroidX - deployment.X;
        var rawToCentroidY = (float)centroidY - deployment.Y;
        var worldLinkCount = candidate.WorldPointLinks.Count;
        var minWorldLinkDistance = worldLinkCount == 0 ? 0f : candidate.WorldPointLinks.Min(link => link.Distance);
        var averageWorldLinkDistance = worldLinkCount == 0 ? 0f : candidate.WorldPointLinks.Average(link => link.Distance);
        var maxWorldLinkDistance = worldLinkCount == 0 ? 0f : candidate.WorldPointLinks.Max(link => link.Distance);

        return new SpecialRouteSpatialDigest(
            PointCount: candidate.Points.Count,
            StartX: start.X,
            StartY: start.Y,
            EndX: end.X,
            EndY: end.Y,
            CentroidX: (float)centroidX,
            CentroidY: (float)centroidY,
            MinX: minX,
            MinY: minY,
            MaxX: maxX,
            MaxY: maxY,
            PathLength: pathLength,
            RawToStartDistance: MathF.Sqrt(rawToStartX * rawToStartX + rawToStartY * rawToStartY),
            RawToCentroidDistance: MathF.Sqrt(rawToCentroidX * rawToCentroidX + rawToCentroidY * rawToCentroidY),
            WorldLinkCount: worldLinkCount,
            MinWorldLinkDistance: minWorldLinkDistance,
            AverageWorldLinkDistance: (float)averageWorldLinkDistance,
            MaxWorldLinkDistance: maxWorldLinkDistance);
    }

    private static string BuildWorldLinkPrototypeSample(
        ScenarioSpecialRecordCandidate candidate,
        int maxEntries)
    {
        var samples = candidate.WorldPointLinks
            .Select(link => link.PrototypeName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxEntries)
            .ToArray();
        return samples.Length == 0 ? "none" : string.Join(" / ", samples);
    }

    private sealed record FilterDescriptor(
        string Label,
        int? GroupByte,
        bool IsOtherBucket = false,
        bool Flag4Only = false,
        bool DelayedOnly = false);

    private sealed record SpecialRouteSpatialDigest(
        int PointCount,
        float StartX,
        float StartY,
        float EndX,
        float EndY,
        float CentroidX,
        float CentroidY,
        float MinX,
        float MinY,
        float MaxX,
        float MaxY,
        float PathLength,
        float RawToStartDistance,
        float RawToCentroidDistance,
        int WorldLinkCount,
        float MinWorldLinkDistance,
        float AverageWorldLinkDistance,
        float MaxWorldLinkDistance);
}
