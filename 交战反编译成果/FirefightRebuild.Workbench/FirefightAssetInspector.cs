using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FirefightWorkbench;

internal sealed class FirefightAssetInspector
{
    private static readonly Regex TagRegexTemplate = new("<{0}>(.*?)</{0}>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<int, string> KnownNationalityCodes = new Dictionary<int, string>
    {
        [0] = "United States?",
        [2] = "United Kingdom?",
        [6] = "France?",
        [7] = "Germany?",
        [12] = "Soviet Union?",
    };
    private readonly string assetRoot;
    private readonly Dictionary<string, DefinitionEntry> definitionPathIndex;
    private readonly Dictionary<string, DefinitionEntry> normalizedDefinitionIndex;
    private readonly Dictionary<string, List<DefinitionEntry>> normalizedDefinitionAliasIndex;

    public FirefightAssetInspector(string assetRoot)
    {
        this.assetRoot = assetRoot;
        definitionPathIndex = BuildDefinitionCatalog(assetRoot);
        normalizedDefinitionIndex = BuildDefinitionIndex(definitionPathIndex.Values);
        normalizedDefinitionAliasIndex = BuildDefinitionAliasIndex(definitionPathIndex.Values);
    }

    public string BuildReport(string? mapCode = null, string? unitName = null, int? month = null, int? year = null)
    {
        if (!string.IsNullOrWhiteSpace(unitName))
        {
            return BuildUnitReport(unitName, month, year);
        }

        if (!string.IsNullOrWhiteSpace(mapCode))
        {
            return BuildMapReport(mapCode);
        }

        var builder = new StringBuilder();
        builder.AppendLine("Firefight Workbench 资产分析报告");
        builder.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"assets 根目录：{assetRoot}");
        builder.AppendLine();

        WriteEngineSummary(builder);
        WriteModSummary(builder);
        WriteDataSummary(builder);
        WriteMapSummary(builder);
        WriteDeploymentPayloadStatistics(builder);
        WriteWorldInstanceStatistics(builder);
        WriteUpdatedNextSteps(builder);

        return builder.ToString().TrimEnd();
    }

    public string BuildAnomalyReport(string? mapCode = null)
    {
        var scenarioFiles = CollectParsedScenarioFiles(mapCode);
        var observations = CollectDeploymentObservations(scenarioFiles);
        var flag9Outliers = GetFlag9OutlierObservations(observations);
        var flag4Samples = GetFlag4ActiveObservations(observations);
        var worldObservations = CollectWorldMapObservations(mapCode);
        var worldAnomalies = GetWorldInstanceAnomalyObservations(worldObservations);

        var builder = new StringBuilder();
        builder.AppendLine("Firefight Workbench Deployment Anomaly Report");
        builder.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"assets root: {assetRoot}");
        if (!string.IsNullOrWhiteSpace(mapCode))
        {
            builder.AppendLine($"map filter: {mapCode}");
        }

        builder.AppendLine();
        builder.AppendLine($"scenario files: {scenarioFiles.Length}");
        builder.AppendLine($"deployments: {observations.Length}");
        builder.AppendLine($"flag9 outliers: {flag9Outliers.Length}");
        builder.AppendLine($"flag4 active deployments: {flag4Samples.Length}");
        builder.AppendLine($"world maps: {worldObservations.Length}");
        builder.AppendLine($"world instance anomalies: {worldAnomalies.Length}");
        builder.AppendLine($"world canonical 0xCD00 maps: {worldObservations.Count(item => item.WorldInstanceProbe is { ParseAccepted: true } probe && IsCanonicalWorldInstanceModel(probe))}");
        builder.AppendLine($"world variant-header maps: {worldObservations.Count(item => item.WorldInstanceProbe is { ParseAccepted: true } probe && IsVariantHeaderWorldInstanceModel(probe))}");
        builder.AppendLine($"world sentinel-coordinate maps: {worldObservations.Count(item => item.WorldInstanceProbe is { ParseAccepted: true } probe && HasSentinelCoordinateRisk(probe))}");
        builder.AppendLine($"world rejected probes: {worldObservations.Count(item => item.WorldInstanceProbe is { ParseAccepted: false })}");
        builder.AppendLine();

        builder.AppendLine("World Instance Anomalies");
        builder.AppendLine("========================");
        if (worldAnomalies.Length == 0)
        {
            builder.AppendLine("none");
        }
        else
        {
            foreach (var observation in worldAnomalies)
            {
                builder.AppendLine(FormatWorldObservationDetail(observation));
                builder.AppendLine();
            }
        }

        builder.AppendLine("Flag9 Outliers");
        builder.AppendLine("================");
        if (flag9Outliers.Length == 0)
        {
            builder.AppendLine("none");
        }
        else
        {
            foreach (var observation in flag9Outliers)
            {
                builder.AppendLine(FormatObservationDetail(observation));
                builder.AppendLine();
            }
        }

        builder.AppendLine("Flag4 Active Samples");
        builder.AppendLine("====================");
        if (flag4Samples.Length == 0)
        {
            builder.AppendLine("none");
        }
        else
        {
            foreach (var observation in flag4Samples.Take(80))
            {
                builder.AppendLine(FormatObservationDetail(observation));
                builder.AppendLine();
            }

            if (flag4Samples.Length > 80)
            {
                builder.AppendLine($"... truncated {flag4Samples.Length - 80} additional flag4-active deployments");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public IReadOnlyList<MapBrowseSummary> GetMapBrowseSummaries()
    {
        return EnumerateMapDirectoryLocations()
            .Select(BuildMapBrowseDataCore)
            .Select(item => new MapBrowseSummary(
                RootName: item.RootName,
                MapCode: item.MapCode,
                MapDirectory: item.MapDirectory,
                DisplayName: item.MapDisplayName,
                Description: item.MapDescription,
                ScenarioCount: item.ScenarioData?.Scenarios.Count ?? 0,
                DeploymentCount: item.ScenarioData?.Scenarios.Sum(scenario => scenario.Deployments.Count) ?? 0,
                TileRows: item.TileRows,
                TileColumns: item.TileColumns,
                WorldWidth: item.WorldWidth,
                WorldHeight: item.WorldHeight,
                HasMapImage: !string.IsNullOrWhiteSpace(item.MapImagePath),
                HasScenarioData: item.ScenarioData is not null,
                HasWorldData: item.WorldWidth.HasValue && item.WorldHeight.HasValue,
                LocalResourceDirectories: item.LocalResourceDirectories))
            .OrderBy(item => item.RootName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.MapCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public MapBrowseData? TryLoadMapBrowseData(string mapCode)
    {
        var location = TryFindMapLocation(mapCode);
        return location is null ? null : BuildMapBrowseDataCore(location);
    }

    public DefinitionBrowseData? TryLoadDefinitionBrowseData(string? definitionPath)
    {
        if (string.IsNullOrWhiteSpace(definitionPath) || !File.Exists(definitionPath))
        {
            return null;
        }

        var category = Path.GetFileName(Path.GetDirectoryName(definitionPath)) ?? "Unknown";
        var rawDefinitionText = FileEncoding.ReadAllTextWithFallback(definitionPath).TrimEnd();
        var squad = XmlSummaries.TryReadSquad(definitionPath);
        var weapon = squad is null ? XmlSummaries.TryReadWeapon(definitionPath) : null;

        return new DefinitionBrowseData(
            DefinitionPath: definitionPath,
            Category: category,
            DisplayName: squad?.LongName ?? weapon?.Name ?? Path.GetFileNameWithoutExtension(definitionPath),
            SecondaryName: squad?.ShortName ?? string.Empty,
            Nationality: squad?.Nationality ?? string.Empty,
            Type: squad?.Type ?? weapon?.Type ?? category,
            AvailabilitySummary: squad is null ? string.Empty : FormatAvailabilitySummary(squad.Availability),
            RawDefinitionText: rawDefinitionText);
    }

    private string BuildUnitReport(string unitName, int? month, int? year)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Firefight Workbench 单位定点分析");
        builder.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"查询单位：{unitName}");
        builder.AppendLine();

        if (IsUsableScenarioDate(month, year))
        {
            builder.AppendLine($"date context: {year:D4}-{month:D2}");
            builder.AppendLine();
        }

        var resolution = ResolveDefinition(unitName, month, year);
        var definition = resolution.SelectedDefinition;
        var definitionPath = definition?.FilePath;
        if (definitionPath is null)
        {
            builder.AppendLine("未找到对应定义文件。");
            builder.AppendLine("可尝试使用完整单位名，例如 `German-Panther Ausf G` 或 `WEAPON_LMG_MG34`。");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine($"定义文件：{definitionPath}");
        builder.AppendLine($"定义类别：{Path.GetFileName(Path.GetDirectoryName(definitionPath))}");
        builder.AppendLine($"match source: {resolution.MatchSource}");
        if (!string.IsNullOrWhiteSpace(resolution.SelectionNote))
        {
            builder.AppendLine($"selection note: {resolution.SelectionNote}");
        }
        if (resolution.CandidateSummaries.Count > 1)
        {
            builder.AppendLine("candidate set:");
            foreach (var candidateSummary in resolution.CandidateSummaries.Take(8))
            {
                builder.AppendLine($"- {candidateSummary}");
            }
        }
        builder.AppendLine($"resolve label: {BuildResolutionLabel(resolution)}");


        if (Path.GetDirectoryName(definitionPath)?.EndsWith("Weapons", StringComparison.OrdinalIgnoreCase) == true)
        {
            var weapon = definition is null
                ? XmlSummaries.TryReadWeapon(definitionPath)
                : new WeaponSummary(definition.DisplayName, definition.Type);
            if (weapon is not null)
            {
                builder.AppendLine($"名称：{weapon.Name}");
                builder.AppendLine($"类型：{weapon.Type}");
            }
        }
        else
        {
            var squad = definition is null
                ? XmlSummaries.TryReadSquad(definitionPath)
                : new SquadSummary(definition.DisplayName, definition.ShortName, definition.Nationality, definition.Type, definition.Availability);
            if (squad is not null)
            {
                builder.AppendLine($"长名：{squad.LongName}");
                builder.AppendLine($"国家：{squad.Nationality}");
                builder.AppendLine($"类型：{squad.Type}");
                if (squad.Availability.Count > 0)
                {
                    builder.AppendLine($"Availability: {FormatAvailabilitySummary(squad.Availability)}");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("原始定义：");
        builder.AppendLine(FileEncoding.ReadAllTextWithFallback(definitionPath).TrimEnd());
        return builder.ToString().TrimEnd();
    }

    private string BuildMapReport(string mapCode)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Firefight Workbench 地图定点分析");
        builder.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"查询地图：{mapCode}");
        builder.AppendLine();

        var mapDirectory = TryFindMapDirectory(mapCode);
        if (mapDirectory is null)
        {
            builder.AppendLine("未找到对应地图目录。");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine($"地图目录：{mapDirectory}");
        var tileFiles = Directory.GetFiles(mapDirectory, "*-tile*.jpg", SearchOption.TopDirectoryOnly);
        var tileGrid = InferTileGrid(tileFiles);
        var worldFile = Directory.GetFiles(mapDirectory, "*-world.dat", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var scenarioFile = Directory.GetFiles(mapDirectory, "*-scenarios.dat", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var worldHeader = worldFile is null ? null : BinaryParsers.TryReadWorldHeader(worldFile);
        var worldTokens = worldFile is null ? Array.Empty<string>() : BinaryParsers.TryExtractInterestingWorldTokens(worldFile);
        var worldTailTokens = worldFile is null ? Array.Empty<string>() : BinaryParsers.TryExtractTailInterestingWorldTokens(worldFile);
        var (worldPrototypeTable, worldInstanceProbe, worldInstanceTable) = ResolveWorldData(worldFile, worldHeader);
        var scenarioHeader = scenarioFile is null ? null : BinaryParsers.TryReadScenarioHeader(scenarioFile);
        var scenarioParse = scenarioFile is null ? null : TryParseScenarioFile(scenarioFile);
        if (scenarioParse is not null && worldInstanceTable is not null)
        {
            scenarioParse = AttachWorldRouteLinks(scenarioParse, worldInstanceTable);
        }

        builder.AppendLine($"瓦片数量：{tileFiles.Length}");
        builder.AppendLine($"推定瓦片网格：{tileGrid.Columns}x{tileGrid.Rows}");
        builder.AppendLine($"world.dat：{worldFile ?? "缺失"}");
        builder.AppendLine($"scenarios.dat：{scenarioFile ?? "缺失"}");

        if (worldHeader is not null)
        {
            builder.AppendLine($"world 头部：unknown={worldHeader.UnknownHeader}, size={worldHeader.PixelWidth}x{worldHeader.PixelHeight}, marker={worldHeader.PayloadMarker}");
            builder.AppendLine($"world format signature: {BuildWorldFormatSignature(worldHeader)}");
        }

        if (worldPrototypeTable is not null)
        {
            builder.AppendLine(
                $"world 原型表：count={worldPrototypeTable.Entries.Count}, start={worldPrototypeTable.StartOffset}, hint={worldPrototypeTable.CountHint}, lead={worldPrototypeTable.LeadingWord}");
            builder.AppendLine($"world 原型补尾：{BuildWorldPrototypeTailSummary(worldPrototypeTable)}");
            builder.AppendLine(
                $"world 原型样本：{string.Join(" / ", worldPrototypeTable.Entries.Take(12).Select(entry => $"{entry.Name}<{entry.FlagA},{entry.FlagB}>"))}");

            if (worldInstanceProbe is not null)
            {
                builder.AppendLine(
                    $"world instance probe: declared={worldInstanceProbe.DeclaredCount}, parsed={worldInstanceProbe.ParsedCount}, unresolved={worldInstanceProbe.UnresolvedPrototypeCount}, start={worldInstanceProbe.StartOffset}, end={worldInstanceProbe.EndOffset}, countModel={BuildWorldCountModelLabel(worldInstanceProbe)}");
                builder.AppendLine($"world instance status: {BuildWorldParseStatusLabel(worldInstanceProbe)}");
                if (!worldInstanceProbe.ParseAccepted)
                {
                    builder.AppendLine($"world instance rejection: {worldInstanceProbe.RejectionReason}");
                }

                builder.AppendLine($"world instance headers: {BuildWorldHeaderDistributionSummary(worldInstanceProbe)}");
                builder.AppendLine($"world instance bounds: {BuildWorldCoordinateRangeSummary(worldInstanceProbe)}");
                builder.AppendLine($"world coordinate anomalies: {BuildWorldCoordinateAnomalySummary(worldInstanceProbe)}");
                builder.AppendLine($"world header->prototype sample: {BuildWorldHeaderPrototypeCorrelationSummary(worldInstanceProbe, 4, 3)}");
                if (worldInstanceTable is not null)
                {
                    builder.AppendLine(
                        $"world instance sample: {string.Join(" / ", worldInstanceTable.Entries.Take(8).Select(entry => $"{entry.PrototypeName}@({entry.X},{entry.Y})"))}");
                    builder.AppendLine($"world instance hotspots: {BuildWorldPrototypeHotspotSummary(worldInstanceTable, 8)}");
                    var unresolvedSample = BuildWorldUnresolvedPrototypeSample(worldInstanceTable, worldPrototypeTable, 6);
                    if (!string.Equals(unresolvedSample, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        builder.AppendLine($"world unresolved sample: {unresolvedSample}");
                    }
                }
            }

            var localAssetSummary = BuildLocalWorldAssetSummary(mapDirectory, worldPrototypeTable, worldTailTokens);
            if (!string.IsNullOrWhiteSpace(localAssetSummary))
            {
                builder.AppendLine($"world 素材匹配：{localAssetSummary}");
            }
            if (worldTailTokens.Length > worldPrototypeTable.Entries.Count)
            {
                builder.AppendLine($"world 尾部词元补充：{string.Join(" / ", worldTailTokens.Take(12))}");
            }
            var localAssetSamples = BuildLocalWorldAssetSamples(mapDirectory, worldPrototypeTable);
            if (!string.IsNullOrWhiteSpace(localAssetSamples))
            {
                builder.AppendLine($"world 原型命中样本：{localAssetSamples}");
            }
        }
        else if (worldTokens.Length > 0)
        {
            builder.AppendLine($"world 词元样本：{string.Join(" / ", worldTokens.Take(12))}");
        }

        if (scenarioHeader is not null)
        {
            builder.AppendLine($"地图名：{scenarioHeader.MapName}");
            builder.AppendLine($"地图描述：{scenarioHeader.Description}");
        }

        if (scenarioParse is not null)
        {
            builder.AppendLine($"任务数量：{scenarioParse.Scenarios.Count}");
        }

        var childDirectories = Directory.GetDirectories(mapDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        if (childDirectories.Length > 0)
        {
            builder.AppendLine($"附加素材目录：{string.Join(" / ", childDirectories)}");
        }

        if (scenarioParse is not null && scenarioParse.Scenarios.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("任务与部署：");
            foreach (var scenario in scenarioParse.Scenarios)
            {
                builder.AppendLine($"- {scenario.Title}");
                builder.AppendLine($"  描述：{scenario.Description}");
                builder.AppendLine(
                    $"  元数据：阵营A={FormatNationalityGuess(scenario.SideACode)} / 阵营B={FormatNationalityGuess(scenario.SideBCode)} / 月份={scenario.Month} / 年份={scenario.Year} / 炮击HE={scenario.ArtilleryHeSideA}/{scenario.ArtilleryHeSideB} / 炮击Smoke={scenario.ArtillerySmokeSideA}/{scenario.ArtillerySmokeSideB} / 相机中心=({scenario.CameraCenterX:N1}, {scenario.CameraCenterY:N1})");
                builder.AppendLine(
                    $"  元数据原始整数：{string.Join(", ", scenario.MetadataInts.Take(8))}");
                builder.AppendLine($"  元数据尾部：{scenario.MetadataTailHex}");
                builder.AppendLine($"  部署阵营摘要：{BuildNationalityBreakdown(scenario.Deployments)}");
                builder.AppendLine($"  部署前缀摘要：{BuildDeploymentPrefixBreakdown(scenario.Deployments)}");
                builder.AppendLine($"  部署数量：{scenario.Deployments.Count}");

                builder.AppendLine($"  元数据尾部猜测：{BuildScenarioMetadataTailGuess(scenario)}");

                builder.AppendLine(
                    $"  metadata inferred: defender={FormatNationalityGuess(scenario.SideACode)} / attacker={FormatNationalityGuess(scenario.SideBCode)} / date={scenario.Year:D4}-{scenario.Month:D2} / deploymentCountPlusOne={scenario.DeploymentCountPlusOne}");
                builder.AppendLine("  prefix note: prefix32/flag4/flag9 are raw deployment bytes, not confirmed gameplay labels.");
                builder.AppendLine($"  definition coverage: {BuildDefinitionCoverageSummary(scenario.Deployments)}");
                builder.AppendLine($"  definition sample: {BuildDefinitionResolutionSample(scenario.Deployments, 6)}");

                var aliasResolutionNotes = scenario.Deployments
                    .Where(item => !string.IsNullOrWhiteSpace(item.DefinitionPath))
                    .Select(item => new
                    {
                        item.UnitName,
                        ResolvedName = Path.GetFileNameWithoutExtension(item.DefinitionPath),
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.ResolvedName)
                        && !NormalizeLookupToken(item.ResolvedName).Equals(NormalizeLookupToken(item.UnitName), StringComparison.OrdinalIgnoreCase))
                    .Select(item => $"{item.UnitName} => {item.ResolvedName}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .ToArray();
                if (aliasResolutionNotes.Length > 0)
                {
                    builder.AppendLine($"  alias resolution: {string.Join(" / ", aliasResolutionNotes)}");
                }

                var specialDeployments = scenario.Deployments
                    .Where(item => item.DelayGuess > 0 || item.FlagByte != 0 || item.ScaleGuess is < 0.99f or > 1.01f)
                    .Take(4)
                    .ToArray();
                if (specialDeployments.Length > 0)
                {
                    builder.AppendLine(
                        $"  部署字节线索：{string.Join(" / ", specialDeployments.Select(item => $"{item.UnitName} => team={item.GroupByte}, delay={item.DelayGuess}s, flag={item.FlagByte}"))}");
                }

                foreach (var deployment in scenario.Deployments.Take(12))
                {
                    builder.AppendLine(
                        $"  - {deployment.UnitName} -> {GetResolvedDefinitionLabel(deployment)} [{deployment.Category}] x={deployment.X:N1}, y={deployment.Y:N1}, angle={deployment.AngleRadians:N3}, extra={deployment.ExtraValue:N3}, flag9={deployment.GroupByte}{BuildDeploymentInlineHint(deployment)}");
                }

                if (scenario.Deployments.Count > 12)
                {
                    builder.AppendLine($"  - ... 其余 {scenario.Deployments.Count - 12} 个单位已省略");
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private void WriteEngineSummary(StringBuilder builder)
    {
        builder.AppendLine("一、引擎结论");
        builder.AppendLine("1. 当前 APK 为 SDL2 + 自研 C++ 引擎，安卓 Java 层不是核心玩法实现。");
        builder.AppendLine("2. 数据层已经高度外部化，可优先围绕 assets 做桌面端重建。");
        builder.AppendLine("3. 本工作台当前聚焦 assets 解析，不直接解析 `libmain.so` 行为公式。");
        builder.AppendLine();
    }

    private void WriteModSummary(StringBuilder builder)
    {
        builder.AppendLine("二、模组与国家摘要");
        var modPath = Path.Combine(assetRoot, "Mod", "mod.txt");
        if (!File.Exists(modPath))
        {
            builder.AppendLine("1. 未找到 `Mod/mod.txt`。");
            builder.AppendLine();
            return;
        }

        var text = FileEncoding.ReadAllTextWithFallback(modPath);
        var startYear = ExtractTagValue(text, "start");
        var endYear = ExtractTagValue(text, "end");
        var nationalityNames = Regex.Matches(text, "<nationality>[\\s\\S]*?<name>(.*?)</name>", RegexOptions.Singleline)
            .Select(match => Sanitize(match.Groups[1].Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        builder.AppendLine($"1. 模组年份跨度：{startYear} - {endYear}");
        builder.AppendLine($"2. 国家数量：{nationalityNames.Count}");
        builder.AppendLine($"3. 国家样本：{string.Join(" / ", nationalityNames.Take(12))}");
        builder.AppendLine();
    }

    private void WriteDataSummary(StringBuilder builder)
    {
        builder.AppendLine("三、数据驱动内容摘要");
        var dataRoot = Path.Combine(assetRoot, "Data");
        var infantryDirectory = Path.Combine(dataRoot, "Infantry");
        var vehiclesDirectory = Path.Combine(dataRoot, "Vehicles");
        var atGunsDirectory = Path.Combine(dataRoot, "AT Guns");
        var weaponsDirectory = Path.Combine(dataRoot, "Weapons");
        var surnamesDirectory = Path.Combine(dataRoot, "Surnames");

        builder.AppendLine($"1. Infantry 文件数：{SafeFileCount(infantryDirectory)}");
        builder.AppendLine($"2. Vehicles 文件数：{SafeFileCount(vehiclesDirectory)}");
        builder.AppendLine($"3. AT Guns 文件数：{SafeFileCount(atGunsDirectory)}");
        builder.AppendLine($"4. Weapons 文件数：{SafeFileCount(weaponsDirectory)}");
        builder.AppendLine($"5. Surnames 文件数：{SafeFileCount(surnamesDirectory)}");

        var equipmentFiles = Directory.Exists(dataRoot)
            ? Directory.GetFiles(dataRoot, "equipment_*_WW2.txt", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName).ToList()
            : new List<string>();
        builder.AppendLine($"6. 国家装备表数量：{equipmentFiles.Count}");

        foreach (var equipmentFile in equipmentFiles.Take(6))
        {
            var summary = ParseEquipmentFile(dataRoot, equipmentFile);
            builder.AppendLine(
                $"   - {Path.GetFileName(equipmentFile)}：步兵 {summary.InfantryCount} / AT 炮 {summary.AtGunCount} / 车辆 {summary.VehicleCount}");
        }

        var sampleFiles = new[]
        {
            Path.Combine(infantryDirectory, "German-Infantry Section 0.txt"),
            Path.Combine(vehiclesDirectory, "American-M10 Wolverine.txt"),
            Path.Combine(atGunsDirectory, "American-37mm M3.txt"),
            Path.Combine(weaponsDirectory, "WEAPON_LMG_MG34.txt"),
        };

        builder.AppendLine("7. 样本对象解析：");
        foreach (var sampleFile in sampleFiles.Where(File.Exists))
        {
            builder.AppendLine($"   - {BuildSampleSummary(sampleFile)}");
        }

        builder.AppendLine();
    }

    private void WriteMapSummary(StringBuilder builder)
    {
        builder.AppendLine("四、地图与任务摘要");
        foreach (var relativeRoot in new[] { "Maps", "Custom Maps" })
        {
            var root = Path.Combine(assetRoot, relativeRoot);
            if (!Directory.Exists(root))
            {
                continue;
            }

            var mapDirectories = Directory.GetDirectories(root).OrderBy(Path.GetFileName).ToList();
            builder.AppendLine($"{relativeRoot} 目录数量：{mapDirectories.Count}");

            foreach (var mapDirectory in mapDirectories.Take(6))
            {
                var mapCode = Path.GetFileName(mapDirectory);
                var tileFiles = Directory.GetFiles(mapDirectory, "*-tile*.jpg", SearchOption.TopDirectoryOnly);
                var tileGrid = InferTileGrid(tileFiles);
                var worldFile = Directory.GetFiles(mapDirectory, "*-world.dat", SearchOption.TopDirectoryOnly).FirstOrDefault();
                var scenarioFile = Directory.GetFiles(mapDirectory, "*-scenarios.dat", SearchOption.TopDirectoryOnly).FirstOrDefault();

                var worldHeader = worldFile is null ? null : BinaryParsers.TryReadWorldHeader(worldFile);
                var worldTokens = worldFile is null ? Array.Empty<string>() : BinaryParsers.TryExtractInterestingWorldTokens(worldFile);
                var worldTailTokens = worldFile is null ? Array.Empty<string>() : BinaryParsers.TryExtractTailInterestingWorldTokens(worldFile);
                var (worldPrototypeTable, _, _) = ResolveWorldData(worldFile, worldHeader);
                var scenarioHeader = scenarioFile is null ? null : BinaryParsers.TryReadScenarioHeader(scenarioFile);
                var scenarioParse = scenarioFile is null ? null : TryParseScenarioFile(scenarioFile);

                builder.AppendLine(
                    $"   - {mapCode}：tile {tileFiles.Length} 张，推定网格 {tileGrid.Columns}x{tileGrid.Rows}，" +
                    $"world {(worldHeader is null ? "缺失" : $"{worldHeader.PixelWidth}x{worldHeader.PixelHeight}")}，" +
                    $"地图名 {(scenarioHeader?.MapName ?? "未知")}");

                if (scenarioParse is not null && scenarioParse.Scenarios.Count > 0)
                {
                    builder.AppendLine(
                        $"     任务样本：{string.Join(" | ", scenarioParse.Scenarios.Take(2).Select(item => $"{item.Title} => {item.Description}"))}");
                    builder.AppendLine($"     已解析部署总数：{scenarioParse.Scenarios.Sum(item => item.Deployments.Count)}");
                }

                if (scenarioParse is not null && scenarioParse.Scenarios.Count > 0)
                {
                    var mapDeployments = scenarioParse.Scenarios.SelectMany(item => item.Deployments).ToArray();
                    builder.AppendLine($"     definition coverage: {BuildDefinitionCoverageSummary(mapDeployments)}");
                    builder.AppendLine($"     definition sample: {BuildDefinitionResolutionSample(mapDeployments, 4)}");
                }

                if (worldTokens.Length > 0)
                {
                    builder.AppendLine($"     world 词元样本：{string.Join(" / ", worldTokens.Take(6))}");
                }
            }
        }

        builder.AppendLine();
    }

    private void WriteDeploymentPayloadStatistics(StringBuilder builder)
    {
        builder.AppendLine("五、Deployment Payload 统计");

        var scenarioFiles = CollectParsedScenarioFiles();
        var scenarios = scenarioFiles
            .SelectMany(item => item.ParseResult.Scenarios)
            .ToArray();
        var observations = CollectDeploymentObservations(scenarioFiles);
        var deployments = observations
            .Select(item => item.Deployment)
            .ToArray();

        builder.AppendLine(
            $"1. sample coverage: maps={scenarioFiles.Select(item => item.MapCode).Distinct(StringComparer.OrdinalIgnoreCase).Count()} / scenarioFiles={scenarioFiles.Length} / scenarios={scenarios.Length} / deployments={deployments.Length}");

        if (deployments.Length == 0)
        {
            builder.AppendLine("2. no deployment payload samples parsed.");
            builder.AppendLine();
            return;
        }

        var flag9Counts = deployments
            .GroupBy(item => (int)item.GroupByte)
            .OrderBy(group => group.Key)
            .ToArray();
        var mixedFlag9Scenarios = scenarios
            .Where(item => item.Deployments.Any(deployment => deployment.GroupByte == 0)
                && item.Deployments.Any(deployment => deployment.GroupByte == 1))
            .ToArray();
        var binaryOnlyFlag9ScenarioCount = scenarios.Count(item =>
            item.Deployments.Count > 0 && item.Deployments.All(deployment => deployment.GroupByte is 0 or 1));
        var firstDeploymentFlag9ZeroCount = mixedFlag9Scenarios.Count(item => item.Deployments[0].GroupByte == 0);
        var orderedZeroBeforeOneCount = mixedFlag9Scenarios.Count(item => IsOrderedZeroBeforeOne(item.Deployments));

        var flag9ZeroDeployments = deployments.Where(item => item.GroupByte == 0).ToArray();
        var flag9OneDeployments = deployments.Where(item => item.GroupByte == 1).ToArray();
        var flag9ZeroDelayedCount = flag9ZeroDeployments.Count(item => item.DelayGuess > 0);
        var flag9OneDelayedCount = flag9OneDeployments.Count(item => item.DelayGuess > 0);

        var nonZeroDelays = deployments
            .Select(item => item.DelayGuess)
            .Where(value => value > 0)
            .OrderBy(value => value)
            .ToArray();
        var zeroDelayCount = deployments.Length - nonZeroDelays.Length;
        var topNonZeroDelays = nonZeroDelays
            .GroupBy(value => value)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(6)
            .Select(group => $"{group.Key}x{group.Count()}")
            .ToArray();
        var secondStepDelayCount = nonZeroDelays.Count(value => value % 300 == 0);

        var flag4Counts = deployments
            .GroupBy(item => (int)item.FlagByte)
            .OrderBy(group => group.Key)
            .ToArray();
        var nonZeroFlag4Deployments = deployments.Where(item => item.FlagByte != 0).ToArray();
        var nonZeroFlag4ByCategory = nonZeroFlag4Deployments
            .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key} {group.Count()}")
            .ToArray();
        var flag4WithFlag9OneCount = nonZeroFlag4Deployments.Count(item => item.GroupByte == 1);
        var flag4WithNonZeroDelayCount = nonZeroFlag4Deployments.Count(item => item.DelayGuess > 0);
        var flag9Outliers = GetFlag9OutlierObservations(observations);
        var topDelayBuckets = observations
            .Where(item => item.Deployment.DelayGuess > 0)
            .GroupBy(item => item.Deployment.DelayGuess)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(4)
            .ToArray();
        var flag4Samples = GetFlag4ActiveObservations(observations);

        builder.AppendLine($"2. flag9 global: {string.Join(", ", flag9Counts.Select(group => $"{group.Key}x{group.Count()}"))}");
        builder.AppendLine(
            $"3. flag9 scenario shape: binaryOnly={binaryOnlyFlag9ScenarioCount}/{scenarios.Length}, mixed(0+1)={mixedFlag9Scenarios.Length}, firstDeployment=0 => {firstDeploymentFlag9ZeroCount}/{mixedFlag9Scenarios.Length}, ordered 0->1 => {orderedZeroBeforeOneCount}/{mixedFlag9Scenarios.Length}");
        builder.AppendLine(
            $"4. flag9 vs delay>0: flag9=0 => {flag9ZeroDelayedCount}/{flag9ZeroDeployments.Length} ({FormatRate(flag9ZeroDelayedCount, flag9ZeroDeployments.Length)}), flag9=1 => {flag9OneDelayedCount}/{flag9OneDeployments.Length} ({FormatRate(flag9OneDelayedCount, flag9OneDeployments.Length)})");
        builder.AppendLine(
            $"5. prefix32 global: zero={zeroDelayCount}, nonzero={nonZeroDelays.Length}, nonzero min/median/max={BuildMinMedianMaxSummary(nonZeroDelays)}");
        builder.AppendLine(
            $"6. prefix32 top nonzero: {(topNonZeroDelays.Length == 0 ? "none" : string.Join(", ", topNonZeroDelays))}");
        builder.AppendLine(
            $"7. prefix32 300-step buckets: {secondStepDelayCount}/{nonZeroDelays.Length} ({FormatRate(secondStepDelayCount, nonZeroDelays.Length)})");
        builder.AppendLine($"8. flag4 global: {string.Join(", ", flag4Counts.Select(group => $"{group.Key}x{group.Count()}"))}");
        builder.AppendLine(
            $"9. flag4 nonzero by category: {(nonZeroFlag4ByCategory.Length == 0 ? "none" : string.Join(" / ", nonZeroFlag4ByCategory))}");
        builder.AppendLine(
            $"10. flag4 co-occurrence: flag4>0 & flag9=1 => {flag4WithFlag9OneCount}/{nonZeroFlag4Deployments.Length} ({FormatRate(flag4WithFlag9OneCount, nonZeroFlag4Deployments.Length)}), flag4>0 & prefix32>0 => {flag4WithNonZeroDelayCount}/{nonZeroFlag4Deployments.Length} ({FormatRate(flag4WithNonZeroDelayCount, nonZeroFlag4Deployments.Length)})");
        builder.AppendLine(
            $"11. flag9 outliers: {(flag9Outliers.Length == 0 ? "none" : BuildObservationSample(flag9Outliers, 6))}");
        builder.AppendLine(
            $"12. prefix32 hot samples: {(topDelayBuckets.Length == 0 ? "none" : BuildDelayBucketSample(topDelayBuckets, 2))}");
        builder.AppendLine(
            $"13. flag4 active samples: {(flag4Samples.Length == 0 ? "none" : BuildObservationSample(flag4Samples, 6))}");
        builder.AppendLine(
            "14. inference: flag9 currently fits a side-slot 0/1 model best; prefix32 still looks like deployment delay; flag4 remains a sparse control bit pending world/runtime validation.");
        builder.AppendLine();
    }

    private void WriteWorldInstanceStatistics(StringBuilder builder)
    {
        builder.AppendLine("涓冦€乄orld Instance Statistics");

        var worldObservations = CollectWorldMapObservations();
        builder.AppendLine($"1. maps scanned: {worldObservations.Length}");
        builder.AppendLine($"2. maps with world.dat: {worldObservations.Count(item => item.WorldFilePath is not null)}");
        builder.AppendLine($"3. maps with prototype table: {worldObservations.Count(item => item.WorldPrototypeTable is not null)}");
        builder.AppendLine($"4. maps with accepted instance table: {worldObservations.Count(item => item.WorldInstanceProbe is { ParseAccepted: true })}");
        builder.AppendLine($"5. maps with rejected instance probe: {worldObservations.Count(item => item.WorldInstanceProbe is { ParseAccepted: false })}");

        var parsedTables = worldObservations
            .Where(item => item.WorldInstanceProbe is { ParseAccepted: true })
            .Select(item => item.WorldInstanceProbe!)
            .ToArray();
        builder.AppendLine($"6. total world instances: {parsedTables.Sum(item => item.ParsedCount)}");
        builder.AppendLine($"7. total unresolved prototype refs: {parsedTables.Sum(item => item.UnresolvedPrototypeCount)}");
        builder.AppendLine(
            $"8. header distribution: {(parsedTables.Length == 0 ? "none" : BuildGlobalWorldHeaderDistributionSummary(parsedTables))}");
        builder.AppendLine(
            $"9. prototype hotspots: {(parsedTables.Length == 0 ? "none" : BuildGlobalWorldPrototypeHotspotSummary(parsedTables, 10))}");
        builder.AppendLine($"10. canonical 0xCD00 maps: {parsedTables.Count(IsCanonicalWorldInstanceModel)}");
        builder.AppendLine($"11. variant-header maps: {parsedTables.Count(IsVariantHeaderWorldInstanceModel)}");
        builder.AppendLine($"12. sentinel-coordinate maps: {parsedTables.Count(HasSentinelCoordinateRisk)}");

        var worldAnomalies = GetWorldInstanceAnomalyObservations(worldObservations);
        builder.AppendLine(
            $"13. anomaly maps: {(worldAnomalies.Length == 0 ? "none" : string.Join(" / ", worldAnomalies.Take(8).Select(FormatWorldObservationLabel)))}");
        builder.AppendLine();
    }

    private static void WriteUpdatedNextSteps(StringBuilder builder)
    {
        builder.AppendLine("六、下一步建议");
        builder.AppendLine("1. Continue naming raw `scenarios.dat` deployment bytes, especially `flag4`, `flag9`, and the metadata tail.");
        builder.AppendLine("2. Continue breaking down the `world.dat` instance region after the prototype table into stable field boundaries, especially header words and unresolved prototype refs.");
        builder.AppendLine("3. Promote the workbench from static reporting into a visual map/unit browser once binary field names stabilize.");
        builder.AppendLine("4. Keep runtime reconstruction grounded in original XML/data assets instead of speculative gameplay labels.");
    }

    private void WriteNextSteps(StringBuilder builder)
    {
        builder.AppendLine("五、下一步建议");
        builder.AppendLine("1. 继续给 `scenarios.dat` 的部署前缀字段与元数据尾部字段命名。");
        builder.AppendLine("2. 继续拆 `world.dat` 的实例记录、碰撞边界与地形字段。");
        builder.AppendLine("3. 把当前工作台升级为可视化地图/单位浏览器。");
        builder.AppendLine("4. 以原始 XML 资产为准建立统一的桌面运行时对象模型。");
    }

    private static string BuildDeploymentPrefixBreakdown(IReadOnlyList<ScenarioDeployment> deployments)
    {
        if (deployments.Count == 0)
        {
            return "none";
        }

        return $"prefix32={BuildNumericCountSummary(deployments.Select(item => item.DelayGuess))}; flag4={BuildNumericCountSummary(deployments.Select(item => (int)item.FlagByte))}; flag9={BuildNumericCountSummary(deployments.Select(item => (int)item.GroupByte))}";
    }

    private static string BuildNumericCountSummary(IEnumerable<int> values)
    {
        return string.Join(
            ", ",
            values
                .GroupBy(item => item)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}x{group.Count()}"));
    }

    private WorldMapObservation[] CollectWorldMapObservations(string? mapCodeFilter = null)
    {
        var results = new List<WorldMapObservation>();
        foreach (var location in EnumerateMapDirectoryLocations())
        {
            var mapCode = Path.GetFileName(location.MapDirectory) ?? location.MapDirectory;
            if (!string.IsNullOrWhiteSpace(mapCodeFilter)
                && !mapCode.Equals(mapCodeFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var worldFilePath = TryFindFirstFile(location.MapDirectory, "*-world.dat");
            var worldHeader = worldFilePath is null ? null : BinaryParsers.TryReadWorldHeader(worldFilePath);
            var (worldPrototypeTable, worldInstanceProbe, worldInstanceTable) = ResolveWorldData(worldFilePath, worldHeader);
            results.Add(new WorldMapObservation(
                location.RootName,
                mapCode,
                location.MapDirectory,
                worldFilePath,
                worldHeader,
                worldPrototypeTable,
                worldInstanceProbe,
                worldInstanceTable));
        }

        return results
            .OrderBy(item => item.RootName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.MapCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static WorldMapObservation[] GetWorldInstanceAnomalyObservations(IEnumerable<WorldMapObservation> observations)
    {
        return observations
            .Where(item =>
                item.WorldFilePath is null
                || item.WorldPrototypeTable is null
                || item.WorldInstanceProbe is null
                || !item.WorldInstanceProbe.ParseAccepted
                || item.WorldInstanceProbe.UnresolvedPrototypeCount > 0
                || item.WorldInstanceProbe.DistinctHeaderCount > 1
                || item.WorldInstanceProbe.CanonicalHeaderCount != item.WorldInstanceProbe.ParsedCount
                || item.WorldInstanceProbe.Sentinel65532CoordinateCount > 0
                || item.WorldInstanceProbe.OverflowCoordinateCount > 0)
            .OrderByDescending(GetWorldObservationSeverity)
            .ThenByDescending(item => item.WorldInstanceProbe?.InvalidCoordinateCount ?? int.MaxValue)
            .ThenByDescending(item => item.WorldInstanceProbe?.UnresolvedPrototypeCount ?? int.MaxValue)
            .ThenBy(item => item.MapCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatWorldObservationLabel(WorldMapObservation observation)
    {
        if (observation.WorldFilePath is null)
        {
            return $"{observation.MapCode}[missing-world]";
        }

        var signaturePrefix = observation.WorldHeader is null
            ? string.Empty
            : $"sig={BuildWorldFormatSignature(observation.WorldHeader)}, ";
        var prototypeTailSummary = observation.WorldPrototypeTable is { SupplementalSegments.Count: > 0 } table
            ? $", prototypeTail={BuildWorldPrototypeTailSummary(table)}"
            : string.Empty;
        var familySummary = $", family={BuildWorldObservationFamilyCode(observation)}";

        if (observation.WorldPrototypeTable is null)
        {
            return $"{observation.MapCode}[{signaturePrefix}missing-prototypes]";
        }

        if (observation.WorldInstanceProbe is null)
        {
            return $"{observation.MapCode}[{signaturePrefix}missing-instances]";
        }

        var probe = observation.WorldInstanceProbe;
        var headerSummary = BuildWorldHeaderDistributionSummary(probe);
        if (!probe.ParseAccepted)
        {
            return $"{observation.MapCode}[{signaturePrefix}rejected:{probe.RejectionReason}, instances={probe.ParsedCount}, headers={headerSummary}{familySummary}{prototypeTailSummary}]";
        }

        return $"{observation.MapCode}[{signaturePrefix}status={BuildWorldParseStatusLabel(probe)}, instances={probe.ParsedCount}, unresolved={probe.UnresolvedPrototypeCount}, headers={headerSummary}{familySummary}{prototypeTailSummary}]";
    }

    private static string FormatWorldObservationDetail(WorldMapObservation observation)
    {
        var builder = new StringBuilder();
        builder.AppendLine(FormatWorldObservationLabel(observation));
        builder.AppendLine($"map root: {observation.RootName}");
        builder.AppendLine($"map directory: {observation.MapDirectory}");
        builder.AppendLine($"world file: {observation.WorldFilePath ?? "missing"}");
        builder.AppendLine($"world format signature: {BuildWorldFormatSignature(observation.WorldHeader)}");
        if (observation.WorldHeader is not null)
        {
            builder.AppendLine($"world size: {observation.WorldHeader.PixelWidth}x{observation.WorldHeader.PixelHeight}");
        }

        builder.AppendLine($"prototype table: {(observation.WorldPrototypeTable is null ? "missing" : observation.WorldPrototypeTable.Entries.Count.ToString())}");
        if (observation.WorldPrototypeTable is not null)
        {
            builder.AppendLine($"prototype supplemental tail: {BuildWorldPrototypeTailSummary(observation.WorldPrototypeTable)}");
        }
        if (observation.WorldInstanceProbe is null)
        {
            builder.AppendLine("instance table: missing");
            return builder.ToString().TrimEnd();
        }

        var probe = observation.WorldInstanceProbe;
        var nearbyCandidates = GetNearbyWorldInstanceCandidates(observation, 5);
        builder.AppendLine($"instance table: {probe.ParsedCount}");
        builder.AppendLine($"parse status: {BuildWorldParseStatusLabel(probe)}");
        builder.AppendLine($"count model: {BuildWorldCountModelLabel(probe)}");
        builder.AppendLine($"family diagnosis: {BuildWorldObservationFamilyDiagnosis(observation, nearbyCandidates)}");
        builder.AppendLine($"family evidence: {BuildWorldNearbyCandidateFamilySummary(observation, nearbyCandidates)}");
        if (!probe.ParseAccepted)
        {
            builder.AppendLine($"parse rejection: {probe.RejectionReason}");
        }

        builder.AppendLine($"header distribution: {BuildWorldHeaderDistributionSummary(probe)}");
        builder.AppendLine($"coordinate bounds: {BuildWorldCoordinateRangeSummary(probe)}");
        builder.AppendLine($"coordinate anomalies: {BuildWorldCoordinateAnomalySummary(probe)}");
        builder.AppendLine($"header->prototype sample: {BuildWorldHeaderPrototypeCorrelationSummary(probe, 4, 3)}");
        builder.AppendLine($"prototype hotspots: {BuildWorldPrototypeHotspotSummary(probe, 8)}");
        if (observation.WorldPrototypeTable is not null)
        {
            builder.AppendLine($"unresolved sample: {BuildWorldUnresolvedPrototypeSample(probe, observation.WorldPrototypeTable, 8)}");
            var nearbyCandidateLines = BuildNearbyWorldInstanceCandidateLines(observation, nearbyCandidates);
            if (nearbyCandidateLines.Length > 0)
            {
                builder.AppendLine("nearby start candidates:");
                foreach (var candidate in nearbyCandidateLines)
                {
                    builder.AppendLine($"- {candidate}");
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildMapBrowseWorldDiagnosticsSummary(WorldMapObservation observation, int maxNearbyCandidates)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"World format signature: {BuildWorldFormatSignature(observation.WorldHeader)}");
        if (observation.WorldPrototypeTable is not null)
        {
            builder.AppendLine($"World prototype supplemental tail: {BuildWorldPrototypeTailSummary(observation.WorldPrototypeTable)}");
        }

        if (observation.WorldInstanceProbe is null)
        {
            var missingStatus = observation.WorldFilePath is null
                ? "missing-world"
                : observation.WorldPrototypeTable is null
                    ? "missing-prototypes"
                    : "missing-probe";
            builder.AppendLine($"World parse status: {missingStatus}");
            return builder.ToString().TrimEnd();
        }

        var probe = observation.WorldInstanceProbe;
        var nearbyCandidates = GetNearbyWorldInstanceCandidates(observation, maxNearbyCandidates);
        builder.AppendLine($"World parse status: {BuildWorldParseStatusLabel(probe)}");
        builder.AppendLine($"World family diagnosis: {BuildWorldObservationFamilyDiagnosis(observation, nearbyCandidates)}");
        builder.AppendLine($"World nearby family evidence: {BuildWorldNearbyCandidateFamilySummary(observation, nearbyCandidates)}");
        builder.AppendLine($"World instance probe: declared={probe.DeclaredCount}, parsed={probe.ParsedCount}, start={probe.StartOffset}, end={probe.EndOffset}, countModel={BuildWorldCountModelLabel(probe)}");
        if (!probe.ParseAccepted)
        {
            builder.AppendLine($"World parse rejection: {probe.RejectionReason}");
        }

        builder.AppendLine($"World header distribution: {BuildWorldHeaderDistributionSummary(probe)}");
        builder.AppendLine($"World coordinate bounds: {BuildWorldCoordinateRangeSummary(probe)}");
        builder.AppendLine($"World coordinate anomalies: {BuildWorldCoordinateAnomalySummary(probe)}");
        builder.AppendLine($"World header->prototype sample: {BuildWorldHeaderPrototypeCorrelationSummary(probe, 4, 3)}");
        builder.AppendLine($"World prototype hotspots: {BuildWorldPrototypeHotspotSummary(probe, 8)}");
        if (observation.WorldPrototypeTable is not null)
        {
            builder.AppendLine($"World unresolved sample: {BuildWorldUnresolvedPrototypeSample(probe, observation.WorldPrototypeTable, 8)}");
        }

        var nearbyCandidateLines = BuildNearbyWorldInstanceCandidateLines(observation, nearbyCandidates);
        if (nearbyCandidateLines.Length > 0)
        {
            builder.AppendLine("World nearby start candidates:");
            foreach (var candidate in nearbyCandidateLines)
            {
                builder.AppendLine($"- {candidate}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static int GetWorldObservationSeverity(WorldMapObservation observation)
    {
        if (observation.WorldFilePath is null)
        {
            return 6;
        }

        if (observation.WorldPrototypeTable is null)
        {
            return 5;
        }

        if (observation.WorldInstanceProbe is null)
        {
            return 4;
        }

        if (!observation.WorldInstanceProbe.ParseAccepted)
        {
            return 4;
        }

        if (HasSentinelCoordinateRisk(observation.WorldInstanceProbe))
        {
            return 3;
        }

        if (IsVariantHeaderWorldInstanceModel(observation.WorldInstanceProbe))
        {
            return 2;
        }

        return observation.WorldInstanceProbe.UnresolvedPrototypeCount > 0 ? 1 : 0;
    }

    private static bool IsCanonicalWorldInstanceModel(WorldInstanceTable table)
    {
        return table.ParseAccepted
            && table.CanonicalHeaderCount == table.ParsedCount
            && table.DistinctHeaderCount == 1
            && table.OverflowCoordinateCount == 0
            && table.Sentinel65532CoordinateCount == 0;
    }

    private static bool IsVariantHeaderWorldInstanceModel(WorldInstanceTable table)
    {
        return table.ParseAccepted
            && !HasSentinelCoordinateRisk(table)
            && (table.DistinctHeaderCount > 1 || table.CanonicalHeaderCount != table.ParsedCount);
    }

    private static bool HasSentinelCoordinateRisk(WorldInstanceTable table)
    {
        return table.ParseAccepted
            && (table.OverflowCoordinateCount > 0 || table.Sentinel65532CoordinateCount > 0);
    }

    internal static string BuildWorldFormatSignature(WorldHeader? header)
    {
        return header is null
            ? "n/a"
            : $"{header.UnknownHeader}/{header.PayloadMarker}";
    }

    internal static string BuildWorldParseStatusLabel(WorldInstanceTable table)
    {
        if (!table.ParseAccepted)
        {
            return "rejected";
        }

        if (HasSentinelCoordinateRisk(table))
        {
            return "sentinel-coordinates";
        }

        if (IsVariantHeaderWorldInstanceModel(table))
        {
            return "variant-headers";
        }

        return table.UnresolvedPrototypeCount > 0 ? "canonical+unresolved-tail" : "canonical-0xCD00";
    }

    internal static string BuildWorldCountModelLabel(WorldInstanceTable table)
    {
        return table.CountModel == "uint16+prelude"
            ? $"{table.CountModel}(0x{table.CountPreludeWord:X4})"
            : table.CountModel;
    }

    internal static string BuildWorldPrototypeTailSummary(WorldPrototypeTable table)
    {
        if (table.SupplementalSegments.Count == 0)
        {
            return "none";
        }

        return string.Join(
            " / ",
            table.SupplementalSegments.Select(item => $"0x{item.StartOffset:X}-0x{item.EndOffset:X}({item.EntryCount})"));
    }

    internal static string BuildWorldHeaderDistributionSummary(WorldInstanceTable table)
    {
        return BuildGlobalWorldHeaderDistributionSummary(new[] { table });
    }

    private static string BuildGlobalWorldHeaderDistributionSummary(IEnumerable<WorldInstanceTable> tables)
    {
        var labels = tables
            .SelectMany(item => item.Entries)
            .GroupBy(item => item.HeaderWord)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(6)
            .Select(group => $"0x{group.Key:X4}x{group.Count()}")
            .ToArray();
        return labels.Length == 0 ? "none" : string.Join(" / ", labels);
    }

    internal static string BuildWorldCoordinateRangeSummary(WorldInstanceTable table)
    {
        if (table.Entries.Count == 0)
        {
            return "n/a";
        }

        return $"x={table.Entries.Min(item => item.X)}..{table.Entries.Max(item => item.X)}, y={table.Entries.Min(item => item.Y)}..{table.Entries.Max(item => item.Y)}";
    }

    internal static string BuildWorldCoordinateAnomalySummary(WorldInstanceTable table)
    {
        return $"invalid={table.InvalidCoordinateCount}/{table.InvalidCoordinateThreshold}, overflow={table.OverflowCoordinateCount}, zero={table.ZeroCoordinateCount}, sentinel65532={table.Sentinel65532CoordinateCount}";
    }

    private static WorldInstanceTable[] GetNearbyWorldInstanceCandidates(WorldMapObservation observation, int maxCandidates)
    {
        if (observation.WorldFilePath is null || observation.WorldPrototypeTable is null)
        {
            return Array.Empty<WorldInstanceTable>();
        }

        return BinaryParsers.ScanNearbyWorldInstanceTableCandidates(
                observation.WorldFilePath,
                observation.WorldPrototypeTable,
                observation.WorldHeader?.PixelWidth,
                observation.WorldHeader?.PixelHeight,
                maxBackwardBytes: 32,
                maxForwardBytes: 128,
                maxResults: maxCandidates)
            .ToArray();
    }

    private static string[] BuildNearbyWorldInstanceCandidateLines(WorldMapObservation observation, int maxCandidates)
    {
        return BuildNearbyWorldInstanceCandidateLines(observation, GetNearbyWorldInstanceCandidates(observation, maxCandidates));
    }

    private static string[] BuildNearbyWorldInstanceCandidateLines(WorldMapObservation observation, IReadOnlyList<WorldInstanceTable> candidates)
    {
        if (observation.WorldPrototypeTable is null || candidates.Count == 0)
        {
            return Array.Empty<string>();
        }

        var baseStartOffset = observation.WorldPrototypeTable.EndOffset;
        return candidates
            .Select(candidate =>
            {
                var delta = candidate.StartOffset - baseStartOffset;
                var deltaLabel = delta >= 0 ? $"+{delta}" : delta.ToString();
                var status = candidate.ParseAccepted
                    ? BuildWorldParseStatusLabel(candidate)
                    : $"rejected:{candidate.RejectionReason}";
                return $"delta {deltaLabel}, declared={candidate.DeclaredCount}, parsed={candidate.ParsedCount}, countModel={BuildWorldCountModelLabel(candidate)}, status={status}, invalid={candidate.InvalidCoordinateCount}, unresolved={candidate.UnresolvedPrototypeCount}, family={BuildWorldNearbyCandidateFamilyLabel(observation, candidate)}, headers={BuildWorldHeaderDistributionSummary(candidate)}";
            })
            .ToArray();
    }

    private static string BuildWorldObservationFamilyCode(WorldMapObservation observation)
    {
        return BuildWorldObservationFamilyCode(observation, GetNearbyWorldInstanceCandidates(observation, 5));
    }

    private static string BuildWorldObservationFamilyCode(WorldMapObservation observation, IReadOnlyList<WorldInstanceTable> nearbyCandidates)
    {
        if (observation.WorldFilePath is null)
        {
            return "missing-world";
        }

        if (observation.WorldPrototypeTable is null)
        {
            return "missing-prototypes";
        }

        var probe = observation.WorldInstanceProbe;
        if (probe is null)
        {
            return "missing-probe";
        }

        if (probe.ParseAccepted)
        {
            var status = BuildWorldParseStatusLabel(probe);
            if (observation.WorldPrototypeTable.SupplementalSegments.Count > 0 && probe.UnresolvedPrototypeCount == 0)
            {
                return status switch
                {
                    "canonical-0xCD00" => "supplemental-tail-resolved-canonical",
                    "variant-headers" => "supplemental-tail-resolved-variant",
                    "sentinel-coordinates" => "supplemental-tail-resolved-sentinel",
                    _ => "supplemental-tail-resolved"
                };
            }

            return status switch
            {
                "canonical-0xCD00" => "stable-canonical-family",
                "variant-headers" => "stable-variant-family",
                "sentinel-coordinates" => "stable-sentinel-family",
                _ when probe.UnresolvedPrototypeCount > 0 => "unresolved-tail-family",
                _ => "accepted-world-family"
            };
        }

        var baseStartOffset = observation.WorldPrototypeTable.EndOffset;
        var cleanVariantCount = nearbyCandidates.Count(candidate =>
            candidate.ParseAccepted
            && string.Equals(BuildWorldParseStatusLabel(candidate), "variant-headers", StringComparison.Ordinal)
            && candidate.InvalidCoordinateCount == 0
            && candidate.UnresolvedPrototypeCount == 0);
        var sentinelAdjacentCount = nearbyCandidates.Count(candidate =>
            candidate.ParseAccepted
            && string.Equals(BuildWorldParseStatusLabel(candidate), "sentinel-coordinates", StringComparison.Ordinal)
            && candidate.InvalidCoordinateCount <= 10
            && candidate.UnresolvedPrototypeCount <= 16);
        var prototypeGapCount = nearbyCandidates.Count(candidate =>
            !candidate.ParseAccepted
            && candidate.InvalidCoordinateCount == 0
            && candidate.RejectionReason?.StartsWith("unresolved-prototypes", StringComparison.Ordinal) == true);
        var garbageOffsetCount = nearbyCandidates.Count(candidate =>
        {
            var delta = candidate.StartOffset - baseStartOffset;
            return delta is >= 8 and <= 64
                && !candidate.ParseAccepted
                && candidate.RejectionReason?.StartsWith("invalid-coordinates", StringComparison.Ordinal) == true
                && candidate.InvalidCoordinateCount >= Math.Max(16, (int)Math.Ceiling(candidate.ParsedCount * 0.6));
        });

        if (prototypeGapCount > 0 && cleanVariantCount == 0)
        {
            return "prototype-gap-family";
        }

        if (sentinelAdjacentCount > 0 && cleanVariantCount == 0)
        {
            return "sentinel-adjacent-family";
        }

        if (cleanVariantCount > 0)
        {
            return "clean-variant-family";
        }

        if (garbageOffsetCount > 0)
        {
            return "garbage-offset-family";
        }

        return nearbyCandidates.Count == 0 ? "no-nearby-signal" : "mixed-nearby-family";
    }

    private static string BuildWorldObservationFamilyDiagnosis(WorldMapObservation observation, IReadOnlyList<WorldInstanceTable> nearbyCandidates)
    {
        var familyCode = BuildWorldObservationFamilyCode(observation, nearbyCandidates);
        return familyCode switch
        {
            "supplemental-tail-resolved-canonical" => "supplemental-tail-resolved-canonical | post-instance prototype names close the former unresolved tail and restore a clean canonical table",
            "supplemental-tail-resolved-variant" => "supplemental-tail-resolved-variant | post-instance prototype names close unresolved indices while preserving a mixed-header accepted family",
            "supplemental-tail-resolved-sentinel" => "supplemental-tail-resolved-sentinel | post-instance prototype names close unresolved indices while the map still shows sentinel-coordinate behavior",
            "stable-canonical-family" => "stable-canonical-family | accepted canonical 0xCD00 instance model without supplemental-tail dependency",
            "stable-variant-family" => "stable-variant-family | accepted mixed-header 8-byte instance family with coherent coordinates",
            "stable-sentinel-family" => "stable-sentinel-family | accepted instance family with persistent sentinel/reserved coordinate behavior",
            "unresolved-tail-family" => "unresolved-tail-family | accepted table still references prototype indices outside the currently resolved prototype set",
            "prototype-gap-family" => "prototype-gap-family | nearby int32 candidates stay spatially clean but still fail on unresolved-prototypes, suggesting the structure is close while prototype resolution remains incomplete",
            "sentinel-adjacent-family" => "sentinel-adjacent-family | nearby accepted candidates are already spatially coherent but remain sentinel-heavy, so this does not currently look like a clean variant rescue",
            "clean-variant-family" => "clean-variant-family | nearby accepted variant-header candidates exist and should be treated as the nearest stable family reference",
            "garbage-offset-family" => "garbage-offset-family | small nearby offsets rapidly collapse into invalid-coordinate-heavy garbage probes",
            "no-nearby-signal" => "no-nearby-signal | no nearby candidate family was available to refine this map yet",
            "mixed-nearby-family" => "mixed-nearby-family | nearby candidates expose more than one weak pattern and need further byte-level reduction",
            _ => familyCode
        };
    }

    private static string BuildWorldNearbyCandidateFamilySummary(WorldMapObservation observation, IReadOnlyList<WorldInstanceTable> nearbyCandidates)
    {
        if (nearbyCandidates.Count == 0)
        {
            return "none";
        }

        var labels = nearbyCandidates
            .Select(candidate => BuildWorldNearbyCandidateFamilyLabel(observation, candidate))
            .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}x{group.Count()}")
            .ToArray();
        return string.Join(" / ", labels);
    }

    private static string BuildWorldNearbyCandidateFamilyLabel(WorldMapObservation observation, WorldInstanceTable candidate)
    {
        var baseStartOffset = observation.WorldPrototypeTable?.EndOffset ?? candidate.StartOffset;
        var delta = candidate.StartOffset - baseStartOffset;
        if (candidate.ParseAccepted)
        {
            var status = BuildWorldParseStatusLabel(candidate);
            if (string.Equals(status, "variant-headers", StringComparison.Ordinal)
                && candidate.InvalidCoordinateCount == 0
                && candidate.UnresolvedPrototypeCount == 0)
            {
                return "clean-variant";
            }

            if (string.Equals(status, "sentinel-coordinates", StringComparison.Ordinal)
                && candidate.InvalidCoordinateCount <= 10
                && candidate.UnresolvedPrototypeCount <= 16)
            {
                return "sentinel-adjacent";
            }

            if (string.Equals(status, "canonical-0xCD00", StringComparison.Ordinal)
                && candidate.InvalidCoordinateCount == 0
                && candidate.UnresolvedPrototypeCount == 0)
            {
                return "clean-canonical";
            }

            return "accepted-nearby";
        }

        if (candidate.InvalidCoordinateCount == 0
            && candidate.RejectionReason?.StartsWith("unresolved-prototypes", StringComparison.Ordinal) == true)
        {
            return "prototype-gap-candidate";
        }

        if (delta is >= 8 and <= 64
            && candidate.RejectionReason?.StartsWith("invalid-coordinates", StringComparison.Ordinal) == true
            && candidate.InvalidCoordinateCount >= Math.Max(16, (int)Math.Ceiling(candidate.ParsedCount * 0.6)))
        {
            return "garbage-offset";
        }

        return "mixed-nearby";
    }

    private static string BuildWorldPrototypeHotspotSummary(WorldInstanceTable table, int maxEntries)
    {
        return BuildGlobalWorldPrototypeHotspotSummary(new[] { table }, maxEntries);
    }

    private static string BuildWorldHeaderPrototypeCorrelationSummary(WorldInstanceTable table, int maxHeaders, int maxPrototypeEntries)
    {
        var labels = table.Entries
            .GroupBy(item => item.HeaderWord)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(maxHeaders)
            .Select(group =>
            {
                var prototypes = group
                    .GroupBy(item => item.PrototypeName, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(inner => inner.Count())
                    .ThenBy(inner => inner.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(maxPrototypeEntries)
                    .Select(inner => $"{inner.Key}x{inner.Count()}")
                    .ToArray();
                return $"0x{group.Key:X4}=>{string.Join(", ", prototypes)}";
            })
            .ToArray();
        return labels.Length == 0 ? "none" : string.Join(" / ", labels);
    }

    private static string BuildGlobalWorldPrototypeHotspotSummary(IEnumerable<WorldInstanceTable> tables, int maxEntries)
    {
        var labels = tables
            .SelectMany(item => item.Entries)
            .GroupBy(item => item.PrototypeName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(maxEntries)
            .Select(group => $"{group.Key}x{group.Count()}")
            .ToArray();
        return labels.Length == 0 ? "none" : string.Join(" / ", labels);
    }

    private static string BuildWorldUnresolvedPrototypeSample(WorldInstanceTable table, WorldPrototypeTable prototypeTable, int maxEntries)
    {
        var labels = table.Entries
            .Where(item => item.PrototypeIndex < 0 || item.PrototypeIndex >= prototypeTable.Entries.Count)
            .Take(maxEntries)
            .Select(item => $"prototype#{item.PrototypeIndex}@({item.X},{item.Y})/0x{item.Offset:X4}")
            .ToArray();
        return labels.Length == 0 ? "none" : string.Join(" / ", labels);
    }

    private ScenarioFileContext[] CollectParsedScenarioFiles(string? mapCodeFilter = null)
    {
        var results = new List<ScenarioFileContext>();
        foreach (var relativeRoot in new[] { "Maps", "Custom Maps" })
        {
            var root = Path.Combine(assetRoot, relativeRoot);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var mapDirectory in Directory.GetDirectories(root).OrderBy(Path.GetFileName))
            {
                var mapCode = Path.GetFileName(mapDirectory) ?? mapDirectory;
                if (!string.IsNullOrWhiteSpace(mapCodeFilter)
                    && !mapCode.Equals(mapCodeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var scenarioFile = Directory.GetFiles(mapDirectory, "*-scenarios.dat", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (scenarioFile is null)
                {
                    continue;
                }

                var parseResult = TryParseScenarioFile(scenarioFile);
                if (parseResult is null || parseResult.Scenarios.Count == 0)
                {
                    continue;
                }

                results.Add(new ScenarioFileContext(
                    relativeRoot,
                    mapCode,
                    scenarioFile,
                    parseResult));
            }
        }

        return results.ToArray();
    }

    private static DeploymentObservation[] CollectDeploymentObservations(IEnumerable<ScenarioFileContext> scenarioFiles)
    {
        var results = new List<DeploymentObservation>();
        foreach (var scenarioFile in scenarioFiles)
        {
            foreach (var scenario in scenarioFile.ParseResult.Scenarios)
            {
                foreach (var deployment in scenario.Deployments)
                {
                    results.Add(new DeploymentObservation(
                        scenarioFile.RootName,
                        scenarioFile.MapCode,
                        scenarioFile.ScenarioFilePath,
                        scenario,
                        deployment));
                }
            }
        }

        return results.ToArray();
    }

    private static DeploymentObservation[] GetFlag9OutlierObservations(IEnumerable<DeploymentObservation> observations)
    {
        return observations
            .Where(item => item.Deployment.GroupByte is not 0 and not 1)
            .OrderBy(item => item.MapCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Scenario.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Deployment.RecordOffset)
            .ToArray();
    }

    private static DeploymentObservation[] GetFlag4ActiveObservations(IEnumerable<DeploymentObservation> observations)
    {
        return observations
            .Where(item => item.Deployment.FlagByte != 0)
            .OrderBy(item => item.MapCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Scenario.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Deployment.RecordOffset)
            .ToArray();
    }

    private static bool IsOrderedZeroBeforeOne(IReadOnlyList<ScenarioDeployment> deployments)
    {
        var seenOne = false;
        var sawZero = false;
        var sawOne = false;

        foreach (var deployment in deployments)
        {
            if (deployment.GroupByte == 1)
            {
                seenOne = true;
                sawOne = true;
                continue;
            }

            if (deployment.GroupByte == 0)
            {
                sawZero = true;
                if (seenOne)
                {
                    return false;
                }
            }
        }

        return sawZero && sawOne;
    }

    private static string BuildMinMedianMaxSummary(IReadOnlyList<int> orderedValues)
    {
        if (orderedValues.Count == 0)
        {
            return "n/a";
        }

        var medianIndex = orderedValues.Count / 2;
        var medianValue = orderedValues.Count % 2 == 1
            ? orderedValues[medianIndex]
            : (orderedValues[medianIndex - 1] + orderedValues[medianIndex]) / 2.0;
        return $"{orderedValues[0]}/{medianValue:0.##}/{orderedValues[^1]}";
    }

    private static string FormatRate(int numerator, int denominator)
    {
        return denominator <= 0
            ? "n/a"
            : $"{(double)numerator / denominator:P1}";
    }

    private static string BuildObservationSample(IEnumerable<DeploymentObservation> observations, int maxEntries)
    {
        var labels = observations
            .Take(maxEntries)
            .Select(item => FormatObservationLabel(item, includePayloadHint: true))
            .ToArray();
        return labels.Length == 0 ? "none" : string.Join(" / ", labels);
    }

    private static string BuildDelayBucketSample(IEnumerable<IGrouping<int, DeploymentObservation>> groups, int maxSamplesPerGroup)
    {
        var labels = groups
            .Select(group =>
                $"{group.Key} => {string.Join(" ; ", group.Take(maxSamplesPerGroup).Select(item => FormatObservationLabel(item, includePayloadHint: false)))}")
            .ToArray();
        return labels.Length == 0 ? "none" : string.Join(" / ", labels);
    }

    private static string FormatObservationLabel(DeploymentObservation observation, bool includePayloadHint)
    {
        var label = $"{observation.MapCode}/{observation.Scenario.Title}/{observation.Deployment.UnitName}@0x{observation.Deployment.RecordOffset:X}";
        if (!includePayloadHint)
        {
            return label;
        }

        var hints = new List<string> { $"flag9={observation.Deployment.GroupByte}" };
        if (observation.Deployment.DelayGuess > 0)
        {
            hints.Add($"prefix32={observation.Deployment.DelayGuess}");
        }

        if (observation.Deployment.FlagByte != 0)
        {
            hints.Add($"flag4={observation.Deployment.FlagByte}");
        }

        var specialCandidate = TryFindSpecialRecordCandidate(observation.Scenario, observation.Deployment);
        if (specialCandidate is not null)
        {
            hints.Add($"special0x{specialCandidate.MarkerWord:X4}={specialCandidate.Points.Count}pts");
        }

        return $"{label} [{string.Join(", ", hints)}]";
    }

    private static string FormatObservationDetail(DeploymentObservation observation)
    {
        var deployment = observation.Deployment;
        var specialCandidate = TryFindSpecialRecordCandidate(observation.Scenario, deployment);
        var builder = new StringBuilder();
        builder.AppendLine(FormatObservationLabel(observation, includePayloadHint: true));
        builder.AppendLine($"map root: {observation.RootName}");
        builder.AppendLine($"scenario file: {observation.ScenarioFilePath}");
        builder.AppendLine($"category: {deployment.Category}");
        builder.AppendLine($"definition: {(string.IsNullOrWhiteSpace(deployment.DefinitionPath) ? "unresolved" : deployment.DefinitionPath)}");
        builder.AppendLine($"position: ({deployment.X:N1}, {deployment.Y:N1})");
        builder.AppendLine($"angle radians: {deployment.AngleRadians:N3}");
        builder.AppendLine($"extra value: {deployment.ExtraValue:N3}");
        builder.AppendLine($"prefix hex: {deployment.PrefixHex}");
        builder.AppendLine($"payload hex: {deployment.PayloadHex}");
        builder.AppendLine($"flag4: {deployment.FlagByte}");
        builder.AppendLine($"flag9: {deployment.GroupByte}");
        builder.AppendLine($"delay/prefix32: {deployment.DelayGuess}");
        if (specialCandidate is not null)
        {
            builder.AppendLine($"special marker: 0x{specialCandidate.MarkerWord:X4}");
            builder.AppendLine($"special points: {specialCandidate.Points.Count}");
            builder.AppendLine($"special point sample: {string.Join(" / ", specialCandidate.Points.Take(8).Select(point => $"({point.X:N0}, {point.Y:N0})"))}");
            builder.AppendLine($"special raw: {specialCandidate.RawHex}");
        }

        return builder.ToString().TrimEnd();
    }

    private static ScenarioSpecialRecordCandidate? TryFindSpecialRecordCandidate(
        ParsedScenario scenario,
        ScenarioDeployment deployment)
    {
        return scenario.SpecialRecordCandidates
            .FirstOrDefault(item => item.DeploymentRecordOffset == deployment.RecordOffset);
    }

    private static string? BuildLocalWorldAssetSummary(string mapDirectory, WorldPrototypeTable worldPrototypeTable, IReadOnlyList<string> tailTokens)
    {
        var prototypeNames = worldPrototypeTable.Entries
            .Select(entry => entry.Name)
            .Concat(tailTokens)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var parts = new List<string>();
        foreach (var directory in Directory.GetDirectories(mapDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var assetNames = Directory.GetFiles(directory, "*.png", SearchOption.TopDirectoryOnly)
                .Select(GetLocalWorldAssetName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (assetNames.Length == 0)
            {
                continue;
            }

            var matches = assetNames.Count(name => prototypeNames.Contains(name));
            parts.Add($"{Path.GetFileName(directory)} {matches}/{assetNames.Length}");
        }

        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }

    private static string? BuildLocalWorldAssetSamples(string mapDirectory, WorldPrototypeTable worldPrototypeTable)
    {
        var prototypeLookup = worldPrototypeTable.Entries
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var matches = new List<WorldPrototypeEntry>();
        foreach (var directory in Directory.GetDirectories(mapDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            foreach (var assetName in Directory.GetFiles(directory, "*.png", SearchOption.TopDirectoryOnly)
                         .Select(GetLocalWorldAssetName)
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (prototypeLookup.TryGetValue(assetName, out var prototypeEntry))
                {
                    matches.Add(prototypeEntry);
                }
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        var samples = string.Join(" / ", matches.Take(8).Select(entry => $"{entry.Name}@0x{entry.Offset:X}"));
        var range = $"range=0x{matches.Min(entry => entry.Offset):X}-0x{matches.Max(entry => entry.Offset):X}";
        return $"{samples} / {range}";
    }

    private static string BuildScenarioMetadataTailGuess(ParsedScenario scenario)
    {
        var deploySlots = ReadMetadataInt(scenario.MetadataBytes, 40);
        var flag44 = scenario.MetadataBytes.Length > 44 ? scenario.MetadataBytes[44] : (byte)0;
        var remainingTail = scenario.MetadataBytes.Length <= 45
            ? string.Empty
            : BitConverter.ToString(scenario.MetadataBytes, 45, scenario.MetadataBytes.Length - 45);
        return $"deploymentCountPlusOne={deploySlots}, byte44={flag44}, bytes45_60={remainingTail}";
    }

    private static string BuildDeploymentInlineHint(ScenarioDeployment deployment)
    {
        var hints = new List<string>();

        if (deployment.DelayGuess > 0)
        {
            hints.Add($"prefix32={deployment.DelayGuess}");
        }

        if (deployment.FlagByte != 0)
        {
            hints.Add($"flag4={deployment.FlagByte}");
        }

        if (deployment.ScaleGuess is < 0.99f or > 1.01f)
        {
            hints.Add($"scale={deployment.ScaleGuess:N2}");
        }

        return hints.Count == 0 ? string.Empty : $", {string.Join(", ", hints)}";
    }

    private static string BuildDefinitionCoverageSummary(IReadOnlyList<ScenarioDeployment> deployments)
    {
        if (deployments.Count == 0)
        {
            return "0/0 resolved";
        }

        var resolved = deployments.Count(item => !string.IsNullOrWhiteSpace(item.DefinitionPath));
        return $"{resolved}/{deployments.Count} resolved";
    }

    private static string BuildDefinitionResolutionSample(IReadOnlyList<ScenarioDeployment> deployments, int maxEntries)
    {
        if (deployments.Count == 0)
        {
            return "n/a";
        }

        var groupedMappings = deployments
            .Select(item =>
            {
                var resolvedName = GetResolvedDefinitionLabel(item);
                return new
                {
                    item.UnitName,
                    ResolvedName = resolvedName,
                    IsAliasOrUnresolved = string.IsNullOrWhiteSpace(item.DefinitionPath)
                        || !NormalizeLookupToken(resolvedName).Equals(NormalizeLookupToken(item.UnitName), StringComparison.OrdinalIgnoreCase),
                };
            })
            .GroupBy(
                item => $"{item.UnitName} -> {item.ResolvedName}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Label = group.Key,
                group.First().IsAliasOrUnresolved,
                Count = group.Count(),
            })
            .OrderByDescending(item => item.IsAliasOrUnresolved)
            .ThenByDescending(item => item.Count)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Take(maxEntries)
            .Select(item => item.Count > 1 ? $"{item.Label} x{item.Count}" : item.Label)
            .ToArray();

        return groupedMappings.Length == 0 ? "n/a" : string.Join(" / ", groupedMappings);
    }

    private static string GetResolvedDefinitionLabel(ScenarioDeployment deployment)
    {
        if (string.IsNullOrWhiteSpace(deployment.DefinitionPath))
        {
            return "unresolved";
        }

        return Path.GetFileNameWithoutExtension(deployment.DefinitionPath) ?? "unresolved";
    }

    private static string GetLocalWorldAssetName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        return name.EndsWith("-shadow", StringComparison.OrdinalIgnoreCase)
            ? name[..^"-shadow".Length]
            : name;
    }

    private static int SafeFileCount(string directory)
    {
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.txt", SearchOption.TopDirectoryOnly).Length
            : 0;
    }

    private static string BuildSampleSummary(string filePath)
    {
        if (Path.GetDirectoryName(filePath)?.EndsWith("Weapons", StringComparison.OrdinalIgnoreCase) == true)
        {
            var weapon = XmlSummaries.TryReadWeapon(filePath);
            return weapon is null
                ? $"{Path.GetFileName(filePath)}：解析失败"
                : $"{Path.GetFileName(filePath)} => 武器 `{weapon.Name}` / 类型 `{weapon.Type}`";
        }

        var squad = XmlSummaries.TryReadSquad(filePath);
        return squad is null
            ? $"{Path.GetFileName(filePath)}：解析失败"
            : $"{Path.GetFileName(filePath)} => `{squad.LongName}` / 国家 `{squad.Nationality}` / 类型 `{squad.Type}`";
    }

    private static EquipmentSummary ParseEquipmentFile(string dataRoot, string filePath)
    {
        var infantryDirectory = Path.Combine(dataRoot, "Infantry");
        var atGunsDirectory = Path.Combine(dataRoot, "AT Guns");
        var vehiclesDirectory = Path.Combine(dataRoot, "Vehicles");
        var lines = FileEncoding.ReadAllTextWithFallback(filePath)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        var section = string.Empty;
        var infantry = 0;
        var atGuns = 0;
        var vehicles = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Split("//", 2, StringSplitOptions.None)[0].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                section = line;
                continue;
            }

            var resolvedKind = TryResolveEquipmentKind(infantryDirectory, atGunsDirectory, vehiclesDirectory, line);
            if (resolvedKind is not null)
            {
                switch (resolvedKind)
                {
                    case "infantry":
                        infantry++;
                        break;
                    case "atgun":
                        atGuns++;
                        break;
                    case "vehicle":
                        vehicles++;
                        break;
                }

                continue;
            }

            if (section.Contains("infantry", StringComparison.OrdinalIgnoreCase))
            {
                infantry++;
                continue;
            }

            if (section.Contains("AT guns", StringComparison.OrdinalIgnoreCase))
            {
                atGuns++;
                continue;
            }

            if (section.Contains("vehicles", StringComparison.OrdinalIgnoreCase))
            {
                vehicles++;
            }
        }

        return new EquipmentSummary(infantry, atGuns, vehicles);
    }

    private static string? TryResolveEquipmentKind(string infantryDirectory, string atGunsDirectory, string vehiclesDirectory, string entryName)
    {
        if (File.Exists(Path.Combine(infantryDirectory, $"{entryName}.txt")))
        {
            return "infantry";
        }

        if (File.Exists(Path.Combine(atGunsDirectory, $"{entryName}.txt")))
        {
            return "atgun";
        }

        if (File.Exists(Path.Combine(vehiclesDirectory, $"{entryName}.txt")))
        {
            return "vehicle";
        }

        return null;
    }

    private ScenarioParseResult? TryParseScenarioFile(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var offset = 0;

            var unknownHeader = BinaryParsers.ReadInt32(bytes, ref offset);
            var mapName = BinaryParsers.ReadLengthPrefixedAscii(bytes, ref offset, 256);
            var mapDescription = BinaryParsers.ReadLengthPrefixedAscii(bytes, ref offset, 4096);
            if (mapName is null || mapDescription is null)
            {
                return null;
            }

            var scenarioOffsets = FindScenarioStartOffsets(bytes, offset);
            var scenarios = new List<ParsedScenario>();
            for (var index = 0; index < scenarioOffsets.Count; index++)
            {
                var titleOffset = scenarioOffsets[index];
                var cursor = titleOffset;
                var title = BinaryParsers.ReadLengthPrefixedAscii(bytes, ref cursor, 128);
                if (title is null)
                {
                    continue;
                }

                var description = BinaryParsers.ReadLengthPrefixedAscii(bytes, ref cursor, 4096);
                if (description is null)
                {
                    continue;
                }

                if (cursor + 61 > bytes.Length)
                {
                    continue;
                }

                var metadata = bytes.AsSpan(cursor, 61).ToArray();
                cursor += 61;

                var scenarioMonth = ReadMetadataInt(metadata, 8);
                var scenarioYear = ReadMetadataInt(metadata, 12);

                var boundary = index + 1 < scenarioOffsets.Count ? scenarioOffsets[index + 1] : bytes.Length;
                var deploymentParse = ParseDeployments(bytes, cursor, boundary, scenarioMonth, scenarioYear);
                var deployments = deploymentParse.Deployments;
                var trailingBytes = boundary > deploymentParse.EndOffset
                    ? bytes.AsSpan(deploymentParse.EndOffset, boundary - deploymentParse.EndOffset).ToArray()
                    : Array.Empty<byte>();
                var specialRecordCandidates = AnalyzeSpecialRecordCandidates(deployments, trailingBytes);

                scenarios.Add(new ParsedScenario(
                    Title: title,
                    Description: description,
                    TitleOffset: titleOffset,
                    MetadataBytes: metadata,
                    MetadataInts: ReadMetadataInts(metadata),
                    SideACode: ReadMetadataInt(metadata, 0),
                    SideBCode: ReadMetadataInt(metadata, 4),
                    Month: scenarioMonth,
                    Year: scenarioYear,
                    ArtilleryHeSideA: ReadMetadataInt(metadata, 16),
                    ArtilleryHeSideB: ReadMetadataInt(metadata, 20),
                    ArtillerySmokeSideA: ReadMetadataInt(metadata, 24),
                    ArtillerySmokeSideB: ReadMetadataInt(metadata, 28),
                    CameraCenterX: ReadMetadataFloat(metadata, 32),
                    CameraCenterY: ReadMetadataFloat(metadata, 36),
                    DeploymentCountPlusOne: ReadMetadataInt(metadata, 40),
                    MetadataTailHex: ReadMetadataTailHex(metadata),
                    PostDeploymentTailBytes: trailingBytes,
                    PostDeploymentTailHex: trailingBytes.Length == 0 ? string.Empty : BitConverter.ToString(trailingBytes),
                    SpecialRecordCandidates: specialRecordCandidates,
                    Deployments: deployments));
            }

            return new ScenarioParseResult(unknownHeader, mapName, mapDescription, scenarios);
        }
        catch
        {
            return null;
        }
    }

    private List<int> FindScenarioStartOffsets(byte[] bytes, int searchStart)
    {
        var offsets = new List<int>();

        for (var offset = searchStart; offset < bytes.Length - 100; offset++)
        {
            var cursor = offset;
            var title = BinaryParsers.ReadLengthPrefixedAscii(bytes, ref cursor, 128);
            if (title is null || title.Contains('-', StringComparison.Ordinal) || !LooksLikeScenarioDescriptionTitle(title))
            {
                continue;
            }

            var description = BinaryParsers.ReadLengthPrefixedAscii(bytes, ref cursor, 4096);
            if (description is null || !LooksLikeScenarioDescriptionBody(description))
            {
                continue;
            }

            if (cursor + 61 + 30 > bytes.Length)
            {
                continue;
            }

            var probeOffset = cursor + 61;
            var probeCursor = probeOffset;
            var unitProbe = BinaryParsers.ReadLengthPrefixedAscii(bytes, ref probeCursor, 128);
            if (unitProbe is null || !LooksLikeUnitToken(unitProbe) || probeCursor + 26 > bytes.Length)
            {
                continue;
            }

            var payload = bytes.AsSpan(probeCursor, 26).ToArray();
            if (!LooksLikeDeploymentPayload(payload))
            {
                continue;
            }

            offsets.Add(offset);
            offset = cursor - 1;
        }

        return offsets;
    }

    private ScenarioDeploymentParseResult ParseDeployments(byte[] bytes, int startOffset, int boundary, int scenarioMonth, int scenarioYear)
    {
        var deployments = new List<ScenarioDeployment>();
        var offset = startOffset;

        while (offset < boundary - 30)
        {
            var recordOffset = offset;
            var unitName = BinaryParsers.ReadLengthPrefixedAscii(bytes, ref offset, 128);
            if (unitName is null || !LooksLikeUnitToken(unitName) || offset + 26 > boundary)
            {
                break;
            }

            var payload = bytes.AsSpan(offset, 26).ToArray();
            if (!LooksLikeDeploymentPayload(payload))
            {
                break;
            }

            var definitionPath = TryFindDefinitionPath(unitName, scenarioMonth, scenarioYear);
            deployments.Add(ParseDeployment(recordOffset, unitName, definitionPath, payload));
            offset += 26;
        }

        return new ScenarioDeploymentParseResult(deployments, offset);
    }

    private ScenarioDeployment ParseDeployment(int recordOffset, string unitName, string? definitionPath, byte[] payload)
    {
        var category = definitionPath is null ? "Unknown" : Path.GetFileName(Path.GetDirectoryName(definitionPath)) ?? "Unknown";
        return new ScenarioDeployment(
            RecordOffset: recordOffset,
            UnitName: unitName,
            DefinitionPath: definitionPath ?? string.Empty,
            Category: category,
            PayloadHex: BitConverter.ToString(payload),
            PayloadBytes: payload,
            PrefixHex: BitConverter.ToString(payload, 0, 10),
            DelayGuess: BitConverter.ToInt32(payload, 0),
            FlagByte: payload[4],
            ScaleGuess: BitConverter.ToSingle(payload, 5),
            GroupByte: payload[9],
            X: BitConverter.ToSingle(payload, 10),
            Y: BitConverter.ToSingle(payload, 14),
            AngleRadians: BitConverter.ToSingle(payload, 18),
            ExtraValue: BitConverter.ToSingle(payload, 22));
    }

    private static IReadOnlyList<ScenarioSpecialRecordCandidate> AnalyzeSpecialRecordCandidates(
        IReadOnlyList<ScenarioDeployment> deployments,
        byte[] trailingBytes)
    {
        if (deployments.Count == 0)
        {
            return Array.Empty<ScenarioSpecialRecordCandidate>();
        }

        var results = new List<ScenarioSpecialRecordCandidate>();
        for (var index = 0; index < deployments.Count; index++)
        {
            var continuationBytes = index == deployments.Count - 1 ? trailingBytes : Array.Empty<byte>();
            var candidate = TryAnalyzeSpecialRecordCandidate(deployments[index], continuationBytes);
            if (candidate is not null)
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    private static ScenarioSpecialRecordCandidate? TryAnalyzeSpecialRecordCandidate(
        ScenarioDeployment deployment,
        byte[] continuationBytes)
    {
        if (deployment.GroupByte is 0 or 1
            || deployment.DelayGuess != 0
            || deployment.FlagByte != 0
            || deployment.ScaleGuess is < 0.99f or > 1.01f
            || Math.Abs(deployment.X) > 0.01f
            || Math.Abs(deployment.Y) > 0.01f
            || Math.Abs(deployment.AngleRadians) > 0.01f
            || Math.Abs(deployment.ExtraValue) > 0.01f
            || deployment.PayloadBytes.Length < 26
            || deployment.PayloadBytes[10] != 0
            || deployment.PayloadBytes[11] != 0
            || deployment.PayloadBytes[12] != 0)
        {
            return null;
        }

        var combined = deployment.PayloadBytes
            .Skip(13)
            .Concat(continuationBytes)
            .ToArray();
        if (combined.Length < 10)
        {
            return null;
        }

        var points = new List<ScenarioSpecialPoint>();
        var rawBytes = new List<byte>();
        ushort? markerWord = null;
        var cursor = 0;
        while (cursor + 10 <= combined.Length)
        {
            var marker = BitConverter.ToUInt16(combined, cursor);
            if (marker != 0x1401)
            {
                break;
            }

            var x = BitConverter.ToSingle(combined, cursor + 2);
            var y = BitConverter.ToSingle(combined, cursor + 6);
            if (x is < 0f or > 20000f || y is < 0f or > 20000f)
            {
                break;
            }

            markerWord ??= marker;
            points.Add(new ScenarioSpecialPoint(x, y));
            rawBytes.AddRange(combined.AsSpan(cursor, 10).ToArray());
            cursor += 10;
        }

        if (points.Count == 0 || !markerWord.HasValue)
        {
            return null;
        }

        return new ScenarioSpecialRecordCandidate(
            DeploymentRecordOffset: deployment.RecordOffset,
            MarkerWord: markerWord.Value,
            Points: points,
            RawHex: BitConverter.ToString(rawBytes.ToArray()),
            WorldPointLinks: Array.Empty<ScenarioSpecialPointWorldLink>());
    }

    private static ScenarioParseResult AttachWorldRouteLinks(
        ScenarioParseResult scenarioParse,
        WorldInstanceTable worldInstanceTable)
    {
        if (scenarioParse.Scenarios.Count == 0 || worldInstanceTable.Entries.Count == 0)
        {
            return scenarioParse;
        }

        var enrichedScenarios = scenarioParse.Scenarios
            .Select(scenario => scenario with
            {
                SpecialRecordCandidates = scenario.SpecialRecordCandidates
                    .Select(candidate => AttachWorldRouteLinks(candidate, worldInstanceTable))
                    .ToArray(),
            })
            .ToArray();

        return scenarioParse with
        {
            Scenarios = enrichedScenarios,
        };
    }

    private static ScenarioSpecialRecordCandidate AttachWorldRouteLinks(
        ScenarioSpecialRecordCandidate candidate,
        WorldInstanceTable worldInstanceTable)
    {
        if (candidate.Points.Count == 0 || worldInstanceTable.Entries.Count == 0)
        {
            return candidate;
        }

        var links = candidate.Points
            .Select((point, index) => BuildWorldRouteLink(index, point, worldInstanceTable))
            .ToArray();

        return candidate with
        {
            WorldPointLinks = links,
        };
    }

    private static ScenarioSpecialPointWorldLink BuildWorldRouteLink(
        int pointIndex,
        ScenarioSpecialPoint point,
        WorldInstanceTable worldInstanceTable)
    {
        WorldInstanceEntry? nearest = null;
        var nearestDistanceSquared = float.MaxValue;
        foreach (var entry in worldInstanceTable.Entries)
        {
            var dx = entry.X - point.X;
            var dy = entry.Y - point.Y;
            var distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestDistanceSquared = distanceSquared;
                nearest = entry;
            }
        }

        if (nearest is null)
        {
            return new ScenarioSpecialPointWorldLink(
                PointIndex: pointIndex,
                Point: point,
                WorldInstanceOffset: -1,
                WorldHeaderWord: 0,
                WorldX: 0,
                WorldY: 0,
                PrototypeIndex: -1,
                PrototypeName: string.Empty,
                Distance: float.PositiveInfinity);
        }

        return new ScenarioSpecialPointWorldLink(
            PointIndex: pointIndex,
            Point: point,
            WorldInstanceOffset: nearest.Offset,
            WorldHeaderWord: nearest.HeaderWord,
            WorldX: nearest.X,
            WorldY: nearest.Y,
            PrototypeIndex: nearest.PrototypeIndex,
            PrototypeName: nearest.PrototypeName,
            Distance: MathF.Sqrt(nearestDistanceSquared));
    }

    private static bool LooksLikeScenarioDescriptionTitle(string value)
    {
        var compact = value.Trim();
        return compact.Length is >= 4 and <= 64 && compact.Any(char.IsLetter);
    }

    private static bool LooksLikeScenarioDescriptionBody(string value)
    {
        var compact = value.Trim();
        return compact.Length is >= 30 and <= 400 && compact.Contains(' ') && compact.Any(char.IsLetter);
    }

    private static bool LooksLikeUnitToken(string value)
    {
        return value.Contains('-', StringComparison.Ordinal) && value.Any(char.IsLetter);
    }

    private static bool LooksLikeDeploymentPayload(byte[] payload)
    {
        if (payload.Length < 26)
        {
            return false;
        }

        var x = BitConverter.ToSingle(payload, 10);
        var y = BitConverter.ToSingle(payload, 14);
        var angle = BitConverter.ToSingle(payload, 18);
        var scale = BitConverter.ToSingle(payload, 5);

        return x is >= -1f and <= 20000f
            && y is >= -1f and <= 20000f
            && angle is >= -0.5f and <= 6.6f
            && scale is >= 0f and <= 10f;
    }

    private static IReadOnlyList<int> ReadMetadataInts(byte[] metadata)
    {
        var values = new List<int>();
        for (var offset = 0; offset <= 28 && offset + 4 <= metadata.Length; offset += 4)
        {
            values.Add(BitConverter.ToInt32(metadata, offset));
        }

        return values;
    }

    private static int ReadMetadataInt(byte[] metadata, int offset)
    {
        if (offset + 4 > metadata.Length)
        {
            return 0;
        }

        return BitConverter.ToInt32(metadata, offset);
    }

    private static float ReadMetadataFloat(byte[] metadata, int offset)
    {
        if (offset + 4 > metadata.Length)
        {
            return 0f;
        }

        return BitConverter.ToSingle(metadata, offset);
    }

    private static string ReadMetadataTailHex(byte[] metadata)
    {
        return metadata.Length <= 40
            ? string.Empty
            : BitConverter.ToString(metadata, 40, metadata.Length - 40);
    }

    private static string ExtractTagValue(string text, string tagName)
    {
        var regex = new Regex(string.Format(TagRegexTemplate.ToString(), tagName), RegexOptions.Singleline);
        var match = regex.Match(text);
        return match.Success ? Sanitize(match.Groups[1].Value) : "未知";
    }

    private static string Sanitize(string value)
    {
        return Regex.Replace(value, "\\s+", " ").Trim();
    }

    private static TileGrid InferTileGrid(IEnumerable<string> tileFiles)
    {
        var coordinates = tileFiles
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => Regex.Match(name ?? string.Empty, "-tile(?<column>\\d)(?<row>\\d)$"))
            .Where(match => match.Success)
            .Select(match => new
            {
                Row = int.Parse(match.Groups["row"].Value),
                Column = int.Parse(match.Groups["column"].Value),
            })
            .ToList();

        if (coordinates.Count == 0)
        {
            return new TileGrid(0, 0);
        }

        return new TileGrid(
            Rows: coordinates.Max(item => item.Row) + 1,
            Columns: coordinates.Max(item => item.Column) + 1);
    }

    private MapBrowseData BuildMapBrowseDataCore(MapDirectoryLocation location)
    {
        var mapCode = Path.GetFileName(location.MapDirectory);
        var tileFiles = Directory.GetFiles(location.MapDirectory, "*-tile*.jpg", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var tileGrid = InferTileGrid(tileFiles);
        var mapImagePath = TryFindFirstFile(location.MapDirectory, "*-map.jpg");
        var contoursPath = TryFindFirstFile(location.MapDirectory, "*-contours.png");
        var worldFilePath = TryFindFirstFile(location.MapDirectory, "*-world.dat");
        var scenarioFilePath = TryFindFirstFile(location.MapDirectory, "*-scenarios.dat");
        var worldHeader = worldFilePath is null ? null : BinaryParsers.TryReadWorldHeader(worldFilePath);
        var worldTokenSample = worldFilePath is null ? Array.Empty<string>() : BinaryParsers.TryExtractInterestingWorldTokens(worldFilePath).Take(12).ToArray();
        var worldTailTokenSample = worldFilePath is null ? Array.Empty<string>() : BinaryParsers.TryExtractTailInterestingWorldTokens(worldFilePath).Take(12).ToArray();
        var (worldPrototypeTable, worldInstanceProbe, worldInstanceTable) = ResolveWorldData(worldFilePath, worldHeader);
        var worldDiagnosticsSummary = BuildMapBrowseWorldDiagnosticsSummary(
            new WorldMapObservation(
                location.RootName,
                mapCode,
                location.MapDirectory,
                worldFilePath,
                worldHeader,
                worldPrototypeTable,
                worldInstanceProbe,
                worldInstanceTable),
            maxNearbyCandidates: 4);
        var scenarioData = scenarioFilePath is null ? null : TryParseScenarioFile(scenarioFilePath);
        if (scenarioData is not null && worldInstanceTable is not null)
        {
            scenarioData = AttachWorldRouteLinks(scenarioData, worldInstanceTable);
        }
        var scenarioHeader = scenarioFilePath is null || scenarioData is not null
            ? null
            : BinaryParsers.TryReadScenarioHeader(scenarioFilePath);
        var localDirectories = Directory.GetDirectories(location.MapDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MapBrowseData(
            RootName: location.RootName,
            MapCode: mapCode,
            MapDirectory: location.MapDirectory,
            MapDisplayName: scenarioData?.MapName ?? scenarioHeader?.MapName ?? mapCode,
            MapDescription: scenarioData?.Description ?? scenarioHeader?.Description ?? string.Empty,
            MapImagePath: mapImagePath,
            ContoursPath: contoursPath,
            WorldFilePath: worldFilePath,
            ScenarioFilePath: scenarioFilePath,
            TileRows: tileGrid.Rows,
            TileColumns: tileGrid.Columns,
            WorldHeader: worldHeader,
            WorldWidth: worldHeader?.PixelWidth,
            WorldHeight: worldHeader?.PixelHeight,
            WorldDiagnosticsSummary: worldDiagnosticsSummary,
            WorldPrototypeTable: worldPrototypeTable,
            WorldInstanceProbe: worldInstanceProbe,
            WorldInstanceTable: worldInstanceTable,
            WorldTokenSample: worldTokenSample,
            WorldTailTokenSample: worldTailTokenSample,
            TileFiles: tileFiles,
            LocalResourceDirectories: localDirectories,
            ScenarioData: scenarioData);
    }

    private IEnumerable<MapDirectoryLocation> EnumerateMapDirectoryLocations()
    {
        foreach (var rootName in new[] { "Maps", "Custom Maps" })
        {
            var root = Path.Combine(assetRoot, rootName);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var mapDirectory in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                yield return new MapDirectoryLocation(rootName, mapDirectory);
            }
        }
    }

    private static (WorldPrototypeTable? PrototypeTable, WorldInstanceTable? Probe, WorldInstanceTable? AcceptedTable) ResolveWorldData(
        string? worldFilePath,
        WorldHeader? worldHeader)
    {
        if (string.IsNullOrWhiteSpace(worldFilePath))
        {
            return (null, null, null);
        }

        var (worldPrototypeTable, worldInstanceProbe) = BinaryParsers.ResolveWorldPrototypeAndProbe(
            worldFilePath,
            worldHeader?.PixelWidth,
            worldHeader?.PixelHeight);

        return (
            worldPrototypeTable,
            worldInstanceProbe,
            worldInstanceProbe is { ParseAccepted: true } ? worldInstanceProbe : null);
    }
    private MapDirectoryLocation? TryFindMapLocation(string mapCode)
    {
        return EnumerateMapDirectoryLocations()
            .FirstOrDefault(item => Path.GetFileName(item.MapDirectory).Equals(mapCode, StringComparison.OrdinalIgnoreCase));
    }

    private string? TryFindMapDirectory(string mapCode)
    {
        return TryFindMapLocation(mapCode)?.MapDirectory;
    }

    private static string? TryFindFirstFile(string directory, string searchPattern)
    {
        return Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private string? TryFindDefinitionPath(string unitName, int? month = null, int? year = null)
    {
        return ResolveDefinition(unitName, month, year).SelectedDefinition?.FilePath;
    }

    private DefinitionResolution ResolveDefinition(string unitName, int? month = null, int? year = null, bool allowTargetedFamilyFallback = true)
    {
        foreach (var directory in GetDefinitionDirectories(assetRoot).Where(Directory.Exists))
        {
            var directPath = Path.Combine(directory, $"{unitName}.txt");
            if (definitionPathIndex.TryGetValue(directPath, out var directDefinition))
            {
                return BuildDefinitionResolution("exact file path", new[] { directDefinition }, month, year);
            }
        }

        foreach (var directory in GetDefinitionDirectories(assetRoot).Where(Directory.Exists))
        {
            var match = Directory.GetFiles(directory, "*.txt", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(unitName, StringComparison.OrdinalIgnoreCase));
            if (match is not null && definitionPathIndex.TryGetValue(match, out var exactDefinition))
            {
                return BuildDefinitionResolution("case-insensitive file name", new[] { exactDefinition }, month, year);
            }
        }

        var normalizedToken = NormalizeLookupToken(unitName);
        if (normalizedDefinitionIndex.TryGetValue(normalizedToken, out var normalizedDefinition))
        {
            return BuildDefinitionResolution("exact normalized file name", new[] { normalizedDefinition }, month, year);
        }

        if (normalizedDefinitionAliasIndex.TryGetValue(normalizedToken, out var aliasDefinitions))
        {
            return BuildDefinitionResolution("exact alias", aliasDefinitions, month, year);
        }

        var fuzzyExactKey = FindBestMatchKey(normalizedDefinitionIndex.Keys, normalizedToken);
        if (fuzzyExactKey is not null && normalizedDefinitionIndex.TryGetValue(fuzzyExactKey, out var fuzzyDefinition))
        {
            return BuildDefinitionResolution("fuzzy file name", new[] { fuzzyDefinition }, month, year);
        }

        var fuzzyAliasKey = FindBestMatchKey(normalizedDefinitionAliasIndex.Keys, normalizedToken);
        if (fuzzyAliasKey is not null && normalizedDefinitionAliasIndex.TryGetValue(fuzzyAliasKey, out var fuzzyAliasDefinitions))
        {
            return BuildDefinitionResolution("fuzzy alias", fuzzyAliasDefinitions, month, year);
        }

        if (allowTargetedFamilyFallback)
        {
            foreach (var fallbackAlias in EnumerateTargetedFamilyFallbackAliases(unitName))
            {
                var fallbackResolution = ResolveDefinition(fallbackAlias, month, year, allowTargetedFamilyFallback: false);
                if (fallbackResolution.SelectedDefinition is not null)
                {
                    return new DefinitionResolution(
                        fallbackResolution.SelectedDefinition,
                        $"targeted family fallback ({unitName} -> {fallbackAlias}) / {fallbackResolution.MatchStrategy}",
                        fallbackResolution.Candidates);
                }
            }
        }

        return new DefinitionResolution(null, "not found", Array.Empty<DefinitionCandidate>());
    }

    private static DefinitionResolution BuildDefinitionResolution(string matchStrategy, IEnumerable<DefinitionEntry> definitions, int? month, int? year)
    {
        var candidates = ScoreDefinitionCandidates(definitions, month, year);
        return new DefinitionResolution(candidates.FirstOrDefault()?.Definition, matchStrategy, candidates);
    }

    private static IReadOnlyList<DefinitionCandidate> ScoreDefinitionCandidates(IEnumerable<DefinitionEntry> definitions, int? month, int? year)
    {
        var candidates = definitions
            .DistinctBy(item => item.FilePath)
            .Select(item =>
            {
                var activeAvailability = TryGetActiveAvailability(item.Availability, month, year);
                var peakAvailability = item.Availability.Count == 0 ? 0 : item.Availability.Max(point => point.Number);
                var latestAvailabilityDate = item.Availability.Count == 0 ? 0 : item.Availability.Max(point => point.Year * 100 + point.Month);
                return new DefinitionCandidate(
                    item,
                    activeAvailability is not null && activeAvailability.Number > 0,
                    activeAvailability?.Number ?? 0,
                    peakAvailability,
                    latestAvailabilityDate,
                    activeAvailability);
            });

        return IsUsableScenarioDate(month, year)
            ? candidates
                .OrderByDescending(item => item.IsAvailableOnDate)
                .ThenByDescending(item => item.ActiveAvailabilityNumber)
                .ThenByDescending(item => item.AvailabilityPeakNumber)
                .ThenByDescending(item => item.AvailabilityLatestDateKey)
                .ThenBy(item => item.Definition.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : candidates
                .OrderByDescending(item => item.AvailabilityPeakNumber)
                .ThenByDescending(item => item.AvailabilityLatestDateKey)
                .ThenBy(item => item.Definition.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private static AvailabilityPoint? TryGetActiveAvailability(IReadOnlyList<AvailabilityPoint> availability, int? month, int? year)
    {
        if (!IsUsableScenarioDate(month, year))
        {
            return null;
        }

        var dateKey = year!.Value * 100 + month!.Value;
        return availability
            .Where(point => point.Year * 100 + point.Month <= dateKey)
            .OrderByDescending(point => point.Year)
            .ThenByDescending(point => point.Month)
            .FirstOrDefault();
    }

    private static Dictionary<string, DefinitionEntry> BuildDefinitionCatalog(string assetRoot)
    {
        var result = new Dictionary<string, DefinitionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in GetDefinitionDirectories(assetRoot).Where(Directory.Exists))
        {
            var category = Path.GetFileName(directory) ?? "Unknown";
            var isWeaponsDirectory = category.Equals("Weapons", StringComparison.OrdinalIgnoreCase);
            foreach (var filePath in Directory.GetFiles(directory, "*.txt", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var definition = isWeaponsDirectory
                    ? BuildWeaponDefinition(filePath, category)
                    : BuildSquadDefinition(filePath, category);
                if (definition is not null)
                {
                    result[filePath] = definition;
                }
            }
        }

        return result;
    }

    private static DefinitionEntry? BuildWeaponDefinition(string filePath, string category)
    {
        var weapon = XmlSummaries.TryReadWeapon(filePath);
        return weapon is null
            ? null
            : new DefinitionEntry(filePath, category, weapon.Name, weapon.Name, string.Empty, weapon.Type, Array.Empty<AvailabilityPoint>());
    }

    private static DefinitionEntry? BuildSquadDefinition(string filePath, string category)
    {
        var squad = XmlSummaries.TryReadSquad(filePath);
        return squad is null
            ? null
            : new DefinitionEntry(filePath, category, squad.LongName, squad.ShortName, squad.Nationality, squad.Type, squad.Availability);
    }

    private static Dictionary<string, DefinitionEntry> BuildDefinitionIndex(IEnumerable<DefinitionEntry> definitions)
    {
        var result = new Dictionary<string, DefinitionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            var key = NormalizeLookupToken(Path.GetFileNameWithoutExtension(definition.FilePath));
            if (!result.ContainsKey(key))
            {
                result.Add(key, definition);
            }
        }

        return result;
    }

    private static Dictionary<string, List<DefinitionEntry>> BuildDefinitionAliasIndex(IEnumerable<DefinitionEntry> definitions)
    {
        var result = new Dictionary<string, List<DefinitionEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions.OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var alias in EnumerateDefinitionAliases(definition))
            {
                TryRegisterDefinitionAlias(result, alias, definition);
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateDefinitionAliases(DefinitionEntry definition)
    {
        var fileName = Path.GetFileNameWithoutExtension(definition.FilePath);
        foreach (var alias in ExpandAliasVariants(fileName))
        {
            yield return alias;
        }

        foreach (var alias in ExpandAliasVariants(definition.DisplayName))
        {
            yield return alias;
        }

        foreach (var alias in ExpandAliasVariants(definition.ShortName))
        {
            yield return alias;
        }

        var simplifiedNationality = SimplifyNationalityToken(definition.Nationality);
        foreach (var alias in ExpandAliasVariants(definition.DisplayName))
        {
            if (!string.IsNullOrWhiteSpace(simplifiedNationality))
            {
                yield return $"{simplifiedNationality}-{alias}";
            }

            if (!string.IsNullOrWhiteSpace(definition.Nationality))
            {
                yield return $"{definition.Nationality}-{alias}";
            }
        }

        foreach (var alias in ExpandAliasVariants(definition.ShortName))
        {
            if (!string.IsNullOrWhiteSpace(simplifiedNationality))
            {
                yield return $"{simplifiedNationality}-{alias}";
            }

            if (!string.IsNullOrWhiteSpace(definition.Nationality))
            {
                yield return $"{definition.Nationality}-{alias}";
            }
        }
    }

    private static IEnumerable<string> ExpandAliasVariants(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            yield break;
        }

        yield return alias;

        var stripped = Regex.Replace(alias, "\\s*\\([^)]*\\)", string.Empty).Trim();
        if (!stripped.Equals(alias, StringComparison.Ordinal))
        {
            yield return stripped;
        }
    }

    private static IEnumerable<string> EnumerateTargetedFamilyFallbackAliases(string unitName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var interposedVariantMatch = Regex.Match(
            unitName,
            @"^American-M4A2(?<variant>\([^)]*\))\s+Sherman(?<suffix>\s*\([^)]*\))?$",
            RegexOptions.IgnoreCase);
        if (interposedVariantMatch.Success)
        {
            var variant = interposedVariantMatch.Groups["variant"].Value;
            var variantSuffix = interposedVariantMatch.Groups["suffix"].Value;

            foreach (var alias in new[]
            {
                $"American-M4A3{variant} Sherman{variantSuffix}",
                $"American-M4A1{variant} Sherman{variantSuffix}",
                $"American-M4{variant} Sherman{variantSuffix}",
            })
            {
                if (!string.IsNullOrWhiteSpace(alias) && seen.Add(alias))
                {
                    yield return alias;
                }
            }

            yield break;
        }

        var match = Regex.Match(
            unitName,
            @"^American-M4A2 Sherman(?<suffix>\s*\([^)]*\))?$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            yield break;
        }

        var suffix = match.Groups["suffix"].Value;

        foreach (var alias in new[]
        {
            $"American-M4 Sherman{suffix}",
            "American-M4 Sherman",
        })
        {
            if (!string.IsNullOrWhiteSpace(alias) && seen.Add(alias))
            {
                yield return alias;
            }
        }
    }

    private static string SimplifyNationalityToken(string? nationality)
    {
        if (string.IsNullOrWhiteSpace(nationality))
        {
            return string.Empty;
        }

        var value = nationality.Trim();
        if (value.StartsWith("NATIONALITY_", StringComparison.OrdinalIgnoreCase))
        {
            value = value["NATIONALITY_".Length..];
        }

        return value.Replace('_', ' ').Trim();
    }

    private static void TryRegisterDefinitionAlias(IDictionary<string, List<DefinitionEntry>> aliases, string? alias, DefinitionEntry definition)
    {
        var normalizedAlias = NormalizeLookupToken(alias);
        if (string.IsNullOrWhiteSpace(normalizedAlias))
        {
            return;
        }

        if (!aliases.TryGetValue(normalizedAlias, out var matches))
        {
            matches = new List<DefinitionEntry>();
            aliases.Add(normalizedAlias, matches);
        }

        if (!matches.Any(item => item.FilePath.Equals(definition.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            matches.Add(definition);
        }
    }

    private static string? FindBestMatchKey(IEnumerable<string> keys, string normalizedToken)
    {
        return keys
            .Select(key => new
            {
                Key = key,
                Score = ComputeFuzzyScore(normalizedToken, key),
            })
            .Where(item => item.Score >= 0)
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Key.Length)
            .Select(item => item.Key)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> GetDefinitionDirectories(string assetRoot)
    {
        return new[]
        {
            Path.Combine(assetRoot, "Data", "Infantry"),
            Path.Combine(assetRoot, "Data", "AT Guns"),
            Path.Combine(assetRoot, "Data", "Vehicles"),
            Path.Combine(assetRoot, "Data", "Weapons"),
        };
    }

    private static bool IsUsableScenarioDate(int? month, int? year)
    {
        return month is >= 1 and <= 12 && year is >= 1900 and <= 2100;
    }

    private static string BuildResolutionLabel(DefinitionResolution resolution)
    {
        if (resolution.Candidates.Count <= 1)
        {
            return resolution.MatchStrategy;
        }

        return $"{resolution.MatchStrategy} / {resolution.Candidates.Count} candidates";
    }

    private static string FormatAvailabilitySummary(IReadOnlyList<AvailabilityPoint> availability)
    {
        if (availability.Count == 0)
        {
            return "n/a";
        }

        return string.Join(
            " / ",
            availability
                .OrderBy(point => point.Year)
                .ThenBy(point => point.Month)
                .Select(point => $"{point.Year:D4}-{point.Month:D2}:{point.Number}"));
    }

    private static string NormalizeLookupToken(string? value)
    {
        var text = value ?? string.Empty;
        text = Regex.Replace(text, "_(?=\\d{4})", "M", RegexOptions.CultureInvariant);
        return Regex.Replace(text, "[^A-Za-z0-9]+", string.Empty).ToLowerInvariant();
    }

    private static int ComputeFuzzyScore(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return -1;
        }

        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return candidate.Length - query.Length;
        }

        if (query.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return query.Length - candidate.Length + 100;
        }

        var compactCandidate = candidate.Replace("late", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("early", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("mid", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("elite", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (compactCandidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return compactCandidate.Length - query.Length + 25;
        }

        return -1;
    }

    private static string FormatNationalityGuess(int code)
    {
        return KnownNationalityCodes.TryGetValue(code, out var name)
            ? $"{code} ({name})"
            : $"{code} (unknown)";
    }

    private static string BuildNationalityBreakdown(IReadOnlyList<ScenarioDeployment> deployments)
    {
        if (deployments.Count == 0)
        {
            return "none";
        }

        return string.Join(
            " / ",
            deployments
                .GroupBy(item => ExtractUnitNationality(item.UnitName))
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key} {group.Count()}"));
    }

    private static string ExtractUnitNationality(string unitName)
    {
        var dash = unitName.IndexOf('-');
        return dash > 0 ? unitName[..dash] : unitName;
    }

    private sealed record MapDirectoryLocation(string RootName, string MapDirectory);

    private sealed record ScenarioFileContext(
        string RootName,
        string MapCode,
        string ScenarioFilePath,
        ScenarioParseResult ParseResult);

    private sealed record WorldMapObservation(
        string RootName,
        string MapCode,
        string MapDirectory,
        string? WorldFilePath,
        WorldHeader? WorldHeader,
        WorldPrototypeTable? WorldPrototypeTable,
        WorldInstanceTable? WorldInstanceProbe,
        WorldInstanceTable? WorldInstanceTable);

    private sealed record DeploymentObservation(
        string RootName,
        string MapCode,
        string ScenarioFilePath,
        ParsedScenario Scenario,
        ScenarioDeployment Deployment);

    private sealed record EquipmentSummary(int InfantryCount, int AtGunCount, int VehicleCount);
    private sealed record TileGrid(int Rows, int Columns);
}

internal static class XmlSummaries
{
    public static SquadSummary? TryReadSquad(string filePath)
    {
        var document = TryParseDocumentWithFallback(filePath);
        if (document is null)
        {
            return null;
        }

        var description = document.Root?.Element("description");
        if (description is null)
        {
            return null;
        }

        return new SquadSummary(
            LongName: description.Element("long_name")?.Value.Trim() ?? Path.GetFileNameWithoutExtension(filePath),
            ShortName: description.Element("short_name")?.Value.Trim() ?? string.Empty,
            Nationality: description.Element("nationality")?.Value.Trim() ?? "UNKNOWN",
            Type: description.Element("type")?.Value.Trim() ?? "UNKNOWN",
            Availability: TryReadAvailability(document.Root?.Element("availability")));
    }

    public static WeaponSummary? TryReadWeapon(string filePath)
    {
        var document = TryParseDocumentWithFallback(filePath);
        if (document is null)
        {
            return null;
        }

        var root = document.Root;
        if (root is null)
        {
            return null;
        }

        return new WeaponSummary(
            Name: root.Element("name")?.Value.Trim() ?? Path.GetFileNameWithoutExtension(filePath),
            Type: root.Element("type")?.Value.Trim() ?? "UNKNOWN");
    }

    private static XDocument? TryParseDocumentWithFallback(string filePath)
    {
        var rawText = FileEncoding.ReadAllTextWithFallback(filePath);
        if (TryParseDocument(rawText, out var directDocument))
        {
            return directDocument;
        }

        // Some stock data files contain bare '&' inside text nodes (for example "Ausf D&E").
        var sanitizedText = Regex.Replace(
            rawText,
            @"&(?!#\d+;|#x[0-9A-Fa-f]+;|[A-Za-z][A-Za-z0-9]+;)",
            "&amp;");

        return sanitizedText.Equals(rawText, StringComparison.Ordinal)
            ? null
            : TryParseDocument(sanitizedText, out var sanitizedDocument)
                ? sanitizedDocument
                : null;
    }

    private static bool TryParseDocument(string text, out XDocument? document)
    {
        try
        {
            document = XDocument.Parse(text);
            return true;
        }
        catch
        {
            document = null;
            return false;
        }
    }

    private static IReadOnlyList<AvailabilityPoint> TryReadAvailability(XElement? availabilityElement)
    {
        if (availabilityElement is null)
        {
            return Array.Empty<AvailabilityPoint>();
        }

        return availabilityElement.Elements("data")
            .Select(data => new AvailabilityPoint(
                Month: ParseInt(data.Element("month")?.Value),
                Year: ParseInt(data.Element("year")?.Value),
                Number: ParseInt(data.Element("number")?.Value)))
            .Where(point => point.Month is >= 1 and <= 12 && point.Year > 0)
            .OrderBy(point => point.Year)
            .ThenBy(point => point.Month)
            .ToArray();
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(value?.Trim(), out var parsed) ? parsed : 0;
    }
}

internal static class BinaryParsers
{
    public static WorldHeader? TryReadWorldHeader(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            return new WorldHeader(
                UnknownHeader: reader.ReadInt32(),
                PixelWidth: reader.ReadInt32(),
                PixelHeight: reader.ReadInt32(),
                PayloadMarker: reader.ReadInt32());
        }
        catch
        {
            return null;
        }
    }

    public static ScenarioHeader? TryReadScenarioHeader(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            var unknownHeader = reader.ReadInt32();
            var mapName = ReadLengthPrefixedString(reader);
            var description = ReadLengthPrefixedString(reader);

            return new ScenarioHeader(unknownHeader, mapName, description);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<ScenarioBrief> TryExtractScenarioBriefs(string filePath)
    {
        try
        {
            var tokens = ExtractPrintableTokens(File.ReadAllBytes(filePath));
            var results = new List<ScenarioBrief>();

            for (var index = 0; index < tokens.Count - 1; index++)
            {
                var title = CleanToken(tokens[index]);
                var description = CleanToken(tokens[index + 1]);

                if (!LooksLikeScenarioTitle(title) || !LooksLikeScenarioDescription(description))
                {
                    continue;
                }

                results.Add(new ScenarioBrief(title, description));
                index++;
            }

            return results
                .DistinctBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
        }
        catch
        {
            return Array.Empty<ScenarioBrief>();
        }
    }

    public static string[] TryExtractInterestingWorldTokens(string filePath)
    {
        try
        {
            var tokens = ExtractPrintableTokens(File.ReadAllBytes(filePath))
                .Select(CleanToken)
                .Where(LooksLikeInterestingWorldToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToArray();

            return tokens;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static WorldPrototypeTable? TryExtractWorldPrototypeTable(string filePath)
    {
        try
        {
            return TryExtractWorldPrototypeTable(File.ReadAllBytes(filePath));
        }
        catch
        {
            return null;
        }
    }

    public static (WorldPrototypeTable? PrototypeTable, WorldInstanceTable? Probe) ResolveWorldPrototypeAndProbe(
        string filePath,
        int? worldWidth,
        int? worldHeight)
    {
        try
        {
            return ResolveWorldPrototypeAndProbe(File.ReadAllBytes(filePath), worldWidth, worldHeight);
        }
        catch
        {
            return (null, null);
        }
    }

    public static WorldInstanceTable? TryExtractWorldInstanceTable(
        string filePath,
        WorldPrototypeTable worldPrototypeTable,
        int? worldWidth,
        int? worldHeight)
    {
        try
        {
            return ProbeWorldInstanceTable(File.ReadAllBytes(filePath), worldPrototypeTable, worldWidth, worldHeight) is { ParseAccepted: true } table
                ? table
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static WorldInstanceTable? ProbeWorldInstanceTable(
        string filePath,
        WorldPrototypeTable worldPrototypeTable,
        int? worldWidth,
        int? worldHeight)
    {
        try
        {
            return ProbeWorldInstanceTable(File.ReadAllBytes(filePath), worldPrototypeTable, worldWidth, worldHeight);
        }
        catch
        {
            return null;
        }
    }

    public static WorldPrototypeTable? TryExtendWorldPrototypeTableFromInstanceTail(
        string filePath,
        WorldPrototypeTable worldPrototypeTable,
        WorldInstanceTable worldInstanceTable)
    {
        try
        {
            return TryExtendWorldPrototypeTableFromInstanceTail(
                File.ReadAllBytes(filePath),
                worldPrototypeTable,
                worldInstanceTable);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<WorldInstanceTable> ScanNearbyWorldInstanceTableCandidates(
        string filePath,
        WorldPrototypeTable worldPrototypeTable,
        int? worldWidth,
        int? worldHeight,
        int maxBackwardBytes = 32,
        int maxForwardBytes = 128,
        int maxResults = 6)
    {
        try
        {
            return ScanNearbyWorldInstanceTableCandidates(
                File.ReadAllBytes(filePath),
                worldPrototypeTable,
                worldWidth,
                worldHeight,
                maxBackwardBytes,
                maxForwardBytes,
                maxResults);
        }
        catch
        {
            return Array.Empty<WorldInstanceTable>();
        }
    }

    public static string[] TryExtractTailInterestingWorldTokens(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var tailLength = Math.Min(bytes.Length, 256 * 1024);
            var tailStart = bytes.Length - tailLength;
            var tailBytes = new byte[tailLength];
            Array.Copy(bytes, tailStart, tailBytes, 0, tailLength);

            return ExtractPrintableTokens(tailBytes)
                .Select(CleanToken)
                .Where(LooksLikeInterestingWorldToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static int ReadInt32(byte[] bytes, ref int offset)
    {
        if (offset + 4 > bytes.Length)
        {
            throw new EndOfStreamException();
        }

        var value = BitConverter.ToInt32(bytes, offset);
        offset += 4;
        return value;
    }

    public static string? ReadLengthPrefixedAscii(byte[] bytes, ref int offset, int maxLength)
    {
        if (offset + 4 > bytes.Length)
        {
            return null;
        }

        var checkpoint = offset;
        var length = BitConverter.ToInt32(bytes, offset);
        offset += 4;

        if (length < 1 || length > maxLength || offset + length > bytes.Length)
        {
            offset = checkpoint;
            return null;
        }

        var text = Encoding.ASCII.GetString(bytes, offset, length);
        offset += length;
        return text;
    }

    private static string ReadLengthPrefixedString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > 8192)
        {
            throw new InvalidDataException($"字符串长度异常：{length}");
        }

        var bytes = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static List<string> ExtractPrintableTokens(byte[] bytes)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();

        foreach (var value in bytes)
        {
            if (value is >= 32 and <= 126)
            {
                builder.Append((char)value);
                continue;
            }

            FlushToken(builder, tokens);
        }

        FlushToken(builder, tokens);
        return tokens;
    }

    private static void FlushToken(StringBuilder builder, List<string> tokens)
    {
        if (builder.Length < 4)
        {
            builder.Clear();
            return;
        }

        var token = builder.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(token))
        {
            tokens.Add(token);
        }

        builder.Clear();
    }

    private static string CleanToken(string value)
    {
        var compact = Regex.Replace(value, "\\s+", " ").Trim();
        return Regex.Replace(compact, "^[^A-Za-z0-9]+|[^A-Za-z0-9\\.!\\?'\\)]+$", string.Empty).Trim();
    }

    private static bool LooksLikeScenarioTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 4 or > 64)
        {
            return false;
        }

        if (value.Contains('-', StringComparison.Ordinal))
        {
            return false;
        }

        return value.Any(char.IsLetter) && value.Any(char.IsUpper);
    }

    private static bool LooksLikeScenarioDescription(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 30 or > 220)
        {
            return false;
        }

        if (!value.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        return value.Any(char.IsLetter);
    }

    private static bool LooksLikeInterestingWorldToken(string token)
    {
        if (token.Length is < 4 or > 48)
        {
            return false;
        }

        var letters = token.Count(char.IsLetter);
        if (letters < 3)
        {
            return false;
        }

        var spaces = token.Count(ch => ch == ' ');
        var digits = token.Count(char.IsDigit);
        var punctuation = token.Count(ch => !char.IsLetterOrDigit(ch) && ch != ' ');

        if (punctuation > 2)
        {
            return false;
        }

        if (Regex.IsMatch(token, "^[xX2:]+$"))
        {
            return false;
        }

        var alphaNumeric = new string(token.Where(char.IsLetterOrDigit).ToArray());
        if (alphaNumeric.Length >= 4
            && alphaNumeric.All(ch => char.ToLowerInvariant(ch) == char.ToLowerInvariant(alphaNumeric[0])))
        {
            return false;
        }

        return spaces > 0 || digits > 0 || letters >= 5;
    }

    private static WorldPrototypeTable? TryExtractWorldPrototypeTable(byte[] bytes)
    {
        if (bytes.Length < 32)
        {
            return null;
        }

        WorldPrototypeTable? best = null;
        var searchStart = Math.Max(16, bytes.Length / 2);

        for (var offset = searchStart; offset < bytes.Length - 8; offset++)
        {
            var entries = TryParseWorldPrototypeEntries(bytes, offset, out var endOffset);
            if (entries is null)
            {
                continue;
            }

            if (best is null
                || entries.Count > best.Entries.Count
                || (entries.Count == best.Entries.Count && endOffset - offset > best.EndOffset - best.StartOffset))
            {
                var leadingWord = offset >= 4 ? BitConverter.ToUInt16(bytes, offset - 4) : (ushort)0;
                var countHint = offset >= 2 ? BitConverter.ToUInt16(bytes, offset - 2) : (ushort)0;
                best = new WorldPrototypeTable(offset, endOffset, leadingWord, countHint, entries, Array.Empty<WorldPrototypeTailSegment>());
            }

            offset = endOffset - 1;
        }

        return best is { Entries.Count: >= 4 } ? best : null;
    }

    private static WorldPrototypeTable? TryExtendWorldPrototypeTableFromInstanceTail(
        byte[] bytes,
        WorldPrototypeTable worldPrototypeTable,
        WorldInstanceTable worldInstanceTable)
    {
        if (!worldInstanceTable.ParseAccepted || worldInstanceTable.UnresolvedPrototypeCount <= 0)
        {
            return null;
        }

        if (worldInstanceTable.EndOffset < 0 || worldInstanceTable.EndOffset >= bytes.Length)
        {
            return null;
        }

        var tailEntries = TryParseWorldPrototypeTailEntries(bytes, worldInstanceTable.EndOffset, out var endOffset);
        if (tailEntries is null)
        {
            return null;
        }

        var requiredPrototypeIndex = worldInstanceTable.Entries
            .Where(item => item.PrototypeIndex >= worldPrototypeTable.Entries.Count)
            .Select(item => item.PrototypeIndex)
            .DefaultIfEmpty(worldPrototypeTable.Entries.Count - 1)
            .Max();
        var requiredAdditionalEntries = (requiredPrototypeIndex - worldPrototypeTable.Entries.Count) + 1;
        if (tailEntries.Count < requiredAdditionalEntries)
        {
            return null;
        }

        var supplementalSegments = worldPrototypeTable.SupplementalSegments
            .Concat(new[] { new WorldPrototypeTailSegment(worldInstanceTable.EndOffset, endOffset, tailEntries.Count) })
            .OrderBy(item => item.StartOffset)
            .ToArray();
        return new WorldPrototypeTable(
            StartOffset: worldPrototypeTable.StartOffset,
            EndOffset: worldPrototypeTable.EndOffset,
            LeadingWord: worldPrototypeTable.LeadingWord,
            CountHint: worldPrototypeTable.CountHint,
            Entries: worldPrototypeTable.Entries.Concat(tailEntries).ToArray(),
            SupplementalSegments: supplementalSegments);
    }

    private static (WorldPrototypeTable? PrototypeTable, WorldInstanceTable? Probe) ResolveWorldPrototypeAndProbe(
        byte[] bytes,
        int? worldWidth,
        int? worldHeight)
    {
        var prototypeTable = TryExtractWorldPrototypeTable(bytes);
        if (prototypeTable is null)
        {
            return (null, null);
        }

        var probe = ProbeWorldInstanceTable(bytes, prototypeTable, worldWidth, worldHeight);
        if (probe is not { ParseAccepted: true, UnresolvedPrototypeCount: > 0 })
        {
            return (prototypeTable, probe);
        }

        var bestTable = prototypeTable;
        var bestProbe = probe;
        var candidateStarts = probe.Entries
            .Where(item => item.PrototypeIndex >= prototypeTable.Entries.Count)
            .Select(item => item.Offset + 8)
            .Append(probe.EndOffset)
            .Distinct()
            .OrderByDescending(item => item)
            .ToArray();
        foreach (var candidateStart in candidateStarts)
        {
            var appendedTable = TryAppendSupplementalWorldPrototypeSegment(bytes, prototypeTable, candidateStart);
            if (appendedTable is null)
            {
                continue;
            }

            var reparsed = ProbeWorldInstanceTable(bytes, appendedTable, worldWidth, worldHeight);
            if (reparsed is not { ParseAccepted: true })
            {
                continue;
            }

            if (reparsed.StartOffset != probe.StartOffset
                || reparsed.EndOffset != probe.EndOffset
                || reparsed.ParsedCount != probe.ParsedCount
                || reparsed.CountModel != probe.CountModel
                || reparsed.CountPreludeWord != probe.CountPreludeWord
                || reparsed.InvalidCoordinateCount > bestProbe.InvalidCoordinateCount
                || reparsed.OverflowCoordinateCount > bestProbe.OverflowCoordinateCount
                || reparsed.Sentinel65532CoordinateCount > bestProbe.Sentinel65532CoordinateCount)
            {
                continue;
            }

            if (reparsed.UnresolvedPrototypeCount < bestProbe.UnresolvedPrototypeCount
                || (reparsed.UnresolvedPrototypeCount == bestProbe.UnresolvedPrototypeCount
                    && appendedTable.Entries.Count > bestTable.Entries.Count))
            {
                bestTable = appendedTable;
                bestProbe = reparsed;
            }
        }

        return (bestTable, bestProbe);
    }

    private static WorldPrototypeTable? TryAppendSupplementalWorldPrototypeSegment(
        byte[] bytes,
        WorldPrototypeTable baseTable,
        int startOffset)
    {
        if (startOffset < 0 || startOffset + 4 > bytes.Length)
        {
            return null;
        }

        if (baseTable.SupplementalSegments.Any(item => item.StartOffset == startOffset))
        {
            return null;
        }

        var standardEntries = TryParseWorldPrototypeEntries(bytes, startOffset, out var standardEndOffset);
        var tailEntries = TryParseWorldPrototypeTailEntries(bytes, startOffset, out var tailEndOffset);
        var appendedEntries = (tailEntries?.Count ?? 0) >= (standardEntries?.Count ?? 0)
            ? tailEntries
            : standardEntries;
        if (appendedEntries is null)
        {
            return null;
        }

        var endOffset = ReferenceEquals(appendedEntries, tailEntries)
            ? tailEndOffset
            : standardEndOffset;

        var supplementalSegments = baseTable.SupplementalSegments
            .Concat(new[] { new WorldPrototypeTailSegment(startOffset, endOffset, appendedEntries.Count) })
            .OrderBy(item => item.StartOffset)
            .ToArray();
        return new WorldPrototypeTable(
            StartOffset: baseTable.StartOffset,
            EndOffset: baseTable.EndOffset,
            LeadingWord: baseTable.LeadingWord,
            CountHint: baseTable.CountHint,
            Entries: baseTable.Entries.Concat(appendedEntries).ToArray(),
            SupplementalSegments: supplementalSegments);
    }

    private static IReadOnlyList<WorldInstanceTable> ScanNearbyWorldInstanceTableCandidates(
        byte[] bytes,
        WorldPrototypeTable worldPrototypeTable,
        int? worldWidth,
        int? worldHeight,
        int maxBackwardBytes,
        int maxForwardBytes,
        int maxResults)
    {
        var baseStartOffset = worldPrototypeTable.EndOffset;
        var candidates = new List<WorldInstanceTable>();
        for (var delta = -maxBackwardBytes; delta <= maxForwardBytes; delta++)
        {
            if (delta == 0)
            {
                continue;
            }

            var startOffset = baseStartOffset + delta;
            if (startOffset < 0 || startOffset + 4 > bytes.Length)
            {
                continue;
            }

            var candidate = ProbeWorldInstanceTable(bytes, worldPrototypeTable, worldWidth, worldHeight, startOffset);
            if (candidate is null || candidate.ParsedCount < 32)
            {
                continue;
            }

            if (candidate.RejectionReason is string rejectionReason
                && (rejectionReason.StartsWith("invalid-count", StringComparison.Ordinal)
                    || rejectionReason.StartsWith("payload-overflow", StringComparison.Ordinal)
                    || rejectionReason.StartsWith("truncated-count-word", StringComparison.Ordinal)))
            {
                continue;
            }

            candidates.Add(candidate);
        }

        return candidates
            .OrderByDescending(item => item.ParseAccepted ? 1 : 0)
            .ThenBy(item => item.InvalidCoordinateCount)
            .ThenBy(item => item.UnresolvedPrototypeCount)
            .ThenBy(item => item.OverflowCoordinateCount)
            .ThenBy(item => item.Sentinel65532CoordinateCount)
            .ThenByDescending(item => item.ParsedCount)
            .ThenBy(item => Math.Abs(item.StartOffset - baseStartOffset))
            .Take(maxResults)
            .ToArray();
    }

    private static WorldInstanceTable? ProbeWorldInstanceTable(
        byte[] bytes,
        WorldPrototypeTable worldPrototypeTable,
        int? worldWidth,
        int? worldHeight)
    {
        return ProbeWorldInstanceTable(bytes, worldPrototypeTable, worldWidth, worldHeight, worldPrototypeTable.EndOffset);
    }

    private static WorldInstanceTable? ProbeWorldInstanceTable(
        byte[] bytes,
        WorldPrototypeTable worldPrototypeTable,
        int? worldWidth,
        int? worldHeight,
        int startOffset)
    {
        var candidates = new[]
            {
                ProbeWorldInstanceTable(bytes, worldPrototypeTable, worldWidth, worldHeight, startOffset, useSplitCountWord: false),
                ProbeWorldInstanceTable(bytes, worldPrototypeTable, worldWidth, worldHeight, startOffset, useSplitCountWord: true)
            }
            .OrderByDescending(item => item.ParseAccepted ? 1 : 0)
            .ThenBy(item => item.InvalidCoordinateCount)
            .ThenBy(item => item.UnresolvedPrototypeCount)
            .ThenBy(item => item.OverflowCoordinateCount)
            .ThenBy(item => item.Sentinel65532CoordinateCount)
            .ThenByDescending(item => item.CanonicalHeaderCount)
            .ThenByDescending(item => item.ParsedCount)
            .ThenBy(item => item.CountModel == "int32" ? 0 : 1)
            .ToArray();
        return candidates.FirstOrDefault();
    }

    private static WorldInstanceTable ProbeWorldInstanceTable(
        byte[] bytes,
        WorldPrototypeTable worldPrototypeTable,
        int? worldWidth,
        int? worldHeight,
        int startOffset,
        bool useSplitCountWord)
    {
        var countModel = useSplitCountWord ? "uint16+prelude" : "int32";
        if (startOffset + 4 > bytes.Length)
        {
            return BuildRejectedWorldInstanceTable(startOffset, bytes.Length, 0, "truncated-count-word", countModel);
        }

        var countPreludeWord = useSplitCountWord ? BitConverter.ToUInt16(bytes, startOffset + 2) : (ushort)0;
        var declaredCount = useSplitCountWord
            ? BitConverter.ToUInt16(bytes, startOffset)
            : BitConverter.ToInt32(bytes, startOffset);
        if (declaredCount is <= 0 or > 500000)
        {
            return BuildRejectedWorldInstanceTable(
                startOffset,
                startOffset + 4,
                declaredCount,
                $"invalid-count {declaredCount}",
                countModel,
                countPreludeWord);
        }

        if (useSplitCountWord && countPreludeWord != 0 && declaredCount < 32)
        {
            return BuildRejectedWorldInstanceTable(
                startOffset,
                startOffset + 4,
                declaredCount,
                $"split-count-too-small {declaredCount} prelude=0x{countPreludeWord:X4}",
                countModel,
                countPreludeWord);
        }

        var dataStartOffset = startOffset + 4;
        var payloadLength = declaredCount * 8L;
        if (dataStartOffset + payloadLength > bytes.Length)
        {
            return BuildRejectedWorldInstanceTable(
                startOffset,
                bytes.Length,
                declaredCount,
                $"payload-overflow end={dataStartOffset + payloadLength} bytes={bytes.Length}",
                countModel,
                countPreludeWord);
        }

        var coordinateLimitX = worldWidth is > 0 ? worldWidth.Value + 64 : 20000;
        var coordinateLimitY = worldHeight is > 0 ? worldHeight.Value + 64 : 20000;
        var worldBoundX = worldWidth is > 0 ? worldWidth.Value : int.MaxValue;
        var worldBoundY = worldHeight is > 0 ? worldHeight.Value : int.MaxValue;
        var entries = new List<WorldInstanceEntry>(declaredCount);
        var unresolvedPrototypeCount = 0;
        var invalidCoordinateCount = 0;
        var overflowCoordinateCount = 0;
        var zeroCoordinateCount = 0;
        var sentinel65532CoordinateCount = 0;
        var canonicalHeaderCount = 0;
        for (var index = 0; index < declaredCount; index++)
        {
            var recordOffset = dataStartOffset + (index * 8);
            var headerWord = BitConverter.ToUInt16(bytes, recordOffset);
            var x = BitConverter.ToUInt16(bytes, recordOffset + 2);
            var y = BitConverter.ToUInt16(bytes, recordOffset + 4);
            var prototypeIndex = BitConverter.ToUInt16(bytes, recordOffset + 6);

            if (x > coordinateLimitX || y > coordinateLimitY)
            {
                invalidCoordinateCount++;
            }

            if (x > worldBoundX || y > worldBoundY)
            {
                overflowCoordinateCount++;
            }

            if (x == 0 || y == 0)
            {
                zeroCoordinateCount++;
            }

            if (x == 65532 || y == 65532)
            {
                sentinel65532CoordinateCount++;
            }

            if (headerWord == 0xCD00)
            {
                canonicalHeaderCount++;
            }

            var prototypeName = $"prototype#{prototypeIndex}";
            byte prototypeFlagA = 0;
            byte prototypeFlagB = 0;
            if (prototypeIndex < worldPrototypeTable.Entries.Count)
            {
                var prototype = worldPrototypeTable.Entries[prototypeIndex];
                prototypeName = prototype.Name;
                prototypeFlagA = prototype.FlagA;
                prototypeFlagB = prototype.FlagB;
            }
            else
            {
                unresolvedPrototypeCount++;
            }

            entries.Add(new WorldInstanceEntry(
                Offset: recordOffset,
                HeaderWord: headerWord,
                X: x,
                Y: y,
                PrototypeIndex: prototypeIndex,
                PrototypeName: prototypeName,
                PrototypeFlagA: prototypeFlagA,
                PrototypeFlagB: prototypeFlagB));
        }

        var invalidCoordinateThreshold = Math.Max(8, declaredCount / 20);
        var unresolvedPrototypeThreshold = Math.Max(32, declaredCount / 5);
        string? rejectionReason = null;
        if (invalidCoordinateCount > invalidCoordinateThreshold)
        {
            rejectionReason = $"invalid-coordinates {invalidCoordinateCount}>{invalidCoordinateThreshold}";
        }
        else if (unresolvedPrototypeCount > unresolvedPrototypeThreshold)
        {
            rejectionReason = $"unresolved-prototypes {unresolvedPrototypeCount}>{unresolvedPrototypeThreshold}";
        }

        return new WorldInstanceTable(
            StartOffset: startOffset,
            EndOffset: dataStartOffset + (declaredCount * 8),
            DeclaredCount: declaredCount,
            CountModel: countModel,
            CountPreludeWord: countPreludeWord,
            ParsedCount: entries.Count,
            UnresolvedPrototypeCount: unresolvedPrototypeCount,
            InvalidCoordinateCount: invalidCoordinateCount,
            InvalidCoordinateThreshold: invalidCoordinateThreshold,
            OverflowCoordinateCount: overflowCoordinateCount,
            ZeroCoordinateCount: zeroCoordinateCount,
            Sentinel65532CoordinateCount: sentinel65532CoordinateCount,
            DistinctHeaderCount: entries.Select(item => item.HeaderWord).Distinct().Count(),
            CanonicalHeaderCount: canonicalHeaderCount,
            ParseAccepted: rejectionReason is null,
            RejectionReason: rejectionReason,
            Entries: entries);
    }

    private static WorldInstanceTable BuildRejectedWorldInstanceTable(
        int startOffset,
        int endOffset,
        int declaredCount,
        string rejectionReason,
        string countModel = "int32",
        ushort countPreludeWord = 0)
    {
        return new WorldInstanceTable(
            StartOffset: startOffset,
            EndOffset: endOffset,
            DeclaredCount: declaredCount,
            CountModel: countModel,
            CountPreludeWord: countPreludeWord,
            ParsedCount: 0,
            UnresolvedPrototypeCount: 0,
            InvalidCoordinateCount: 0,
            InvalidCoordinateThreshold: 0,
            OverflowCoordinateCount: 0,
            ZeroCoordinateCount: 0,
            Sentinel65532CoordinateCount: 0,
            DistinctHeaderCount: 0,
            CanonicalHeaderCount: 0,
            ParseAccepted: false,
            RejectionReason: rejectionReason,
            Entries: Array.Empty<WorldInstanceEntry>());
    }

    private static List<WorldPrototypeEntry>? TryParseWorldPrototypeEntries(byte[] bytes, int startOffset, out int endOffset)
    {
        var entries = new List<WorldPrototypeEntry>();
        var cursor = startOffset;

        while (cursor + 4 <= bytes.Length)
        {
            var length = BitConverter.ToUInt16(bytes, cursor);
            if (length is < 4 or > 48)
            {
                break;
            }

            var textOffset = cursor + 2;
            var flagOffset = textOffset + length;
            if (flagOffset + 2 > bytes.Length)
            {
                break;
            }

            var text = Encoding.ASCII.GetString(bytes, textOffset, length).Trim();
            if (!LooksLikeWorldPrototypeName(text))
            {
                break;
            }

            entries.Add(new WorldPrototypeEntry(
                Offset: cursor,
                Name: text,
                FlagA: bytes[flagOffset],
                FlagB: bytes[flagOffset + 1]));
            cursor = flagOffset + 2;
        }

        endOffset = cursor;
        return entries.Count >= 4 ? entries : null;
    }

    private static List<WorldPrototypeEntry>? TryParseWorldPrototypeTailEntries(byte[] bytes, int startOffset, out int endOffset)
    {
        var entries = new List<WorldPrototypeEntry>();
        var cursor = startOffset;

        while (cursor + 5 <= bytes.Length)
        {
            var length = BitConverter.ToUInt16(bytes, cursor);
            if (length is < 4 or > 48)
            {
                break;
            }

            var textOffset = cursor + 2;
            var flagOffset = textOffset + length;
            if (flagOffset + 3 > bytes.Length)
            {
                break;
            }

            var text = Encoding.ASCII.GetString(bytes, textOffset, length).Trim();
            if (!LooksLikeWorldPrototypeName(text))
            {
                break;
            }

            entries.Add(new WorldPrototypeEntry(
                Offset: cursor,
                Name: text,
                FlagA: bytes[flagOffset],
                FlagB: bytes[flagOffset + 1]));
            cursor = flagOffset + 3;
        }

        endOffset = cursor;
        return entries.Count >= 4 ? entries : null;
    }

    private static bool LooksLikeWorldPrototypeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 4 or > 48)
        {
            return false;
        }

        if (value.Any(ch => ch < 32 || ch > 126))
        {
            return false;
        }

        var letters = value.Count(char.IsLetter);
        var digits = value.Count(char.IsDigit);
        var punctuation = value.Count(ch => !char.IsLetterOrDigit(ch) && ch is not ' ' and not '_' and not '-' and not '(' and not ')');

        if (letters < 3 || punctuation > 1)
        {
            return false;
        }

        if (Regex.IsMatch(value, "^[xX2 ]+$"))
        {
            return false;
        }

        return value.Contains(' ') || value.Contains('_') || digits > 0;
    }
}

internal static class FileEncoding
{
    private static readonly Encoding Utf8;
    private static readonly Encoding Gb18030;

    static FileEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        Gb18030 = Encoding.GetEncoding(54936);
    }

    public static string ReadAllTextWithFallback(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var utf8 = Utf8.GetString(bytes);
        var gb = Gb18030.GetString(bytes);

        return CountReplacementCharacters(utf8) <= CountReplacementCharacters(gb) ? utf8 : gb;
    }

    private static int CountReplacementCharacters(string value)
    {
        return value.Count(ch => ch == '\uFFFD');
    }
}

internal sealed record AvailabilityPoint(int Month, int Year, int Number);

internal sealed record DefinitionEntry(
    string FilePath,
    string Category,
    string DisplayName,
    string ShortName,
    string Nationality,
    string Type,
    IReadOnlyList<AvailabilityPoint> Availability);

internal sealed record DefinitionCandidate(
    DefinitionEntry Definition,
    bool IsAvailableOnDate,
    int ActiveAvailabilityNumber,
    int AvailabilityPeakNumber,
    int AvailabilityLatestDateKey,
    AvailabilityPoint? ActiveAvailability);

internal sealed record DefinitionResolution(
    DefinitionEntry? SelectedDefinition,
    string MatchStrategy,
    IReadOnlyList<DefinitionCandidate> Candidates)
{
    public string MatchSource => MatchStrategy;

    public string SelectionNote
    {
        get
        {
            if (Candidates.Count <= 1)
            {
                return string.Empty;
            }

            var selectedName = SelectedDefinition is null
                ? "none"
                : Path.GetFileNameWithoutExtension(SelectedDefinition.FilePath);
            var activeAvailability = Candidates[0].ActiveAvailability;
            return activeAvailability is null
                ? $"picked {selectedName} from {Candidates.Count} candidates by peak availability; use --year/--month to disambiguate."
                : $"picked {selectedName} from {Candidates.Count} candidates using {activeAvailability.Year:D4}-{activeAvailability.Month:D2}:{activeAvailability.Number}.";
        }
    }

    public IReadOnlyList<string> CandidateSummaries =>
        Candidates.Select(BuildCandidateSummary).ToArray();

    private static string BuildCandidateSummary(DefinitionCandidate candidate)
    {
        var activeSummary = candidate.ActiveAvailability is null
            ? "inactive for requested date"
            : $"active={candidate.ActiveAvailability.Year:D4}-{candidate.ActiveAvailability.Month:D2}:{candidate.ActiveAvailability.Number}";
        var availabilitySummary = candidate.Definition.Availability.Count == 0
            ? "n/a"
            : string.Join(
                " / ",
                candidate.Definition.Availability
                    .OrderBy(point => point.Year)
                    .ThenBy(point => point.Month)
                    .Select(point => $"{point.Year:D4}-{point.Month:D2}:{point.Number}"));
        return $"{Path.GetFileNameWithoutExtension(candidate.Definition.FilePath)} [{candidate.Definition.Category}] {activeSummary}; peak={candidate.AvailabilityPeakNumber}; availability={availabilitySummary}";
    }
}

internal sealed record SquadSummary(string LongName, string ShortName, string Nationality, string Type, IReadOnlyList<AvailabilityPoint> Availability);

internal sealed record WeaponSummary(string Name, string Type);

internal sealed record WorldHeader(int UnknownHeader, int PixelWidth, int PixelHeight, int PayloadMarker);

internal sealed record ScenarioHeader(int UnknownHeader, string MapName, string Description);

internal sealed record ScenarioBrief(string Title, string Description);

internal sealed record MapBrowseSummary(
    string RootName,
    string MapCode,
    string MapDirectory,
    string DisplayName,
    string Description,
    int ScenarioCount,
    int DeploymentCount,
    int TileRows,
    int TileColumns,
    int? WorldWidth,
    int? WorldHeight,
    bool HasMapImage,
    bool HasScenarioData,
    bool HasWorldData,
    IReadOnlyList<string> LocalResourceDirectories);

internal sealed record MapBrowseData(
    string RootName,
    string MapCode,
    string MapDirectory,
    string MapDisplayName,
    string MapDescription,
    string? MapImagePath,
    string? ContoursPath,
    string? WorldFilePath,
    string? ScenarioFilePath,
    int TileRows,
    int TileColumns,
    WorldHeader? WorldHeader,
    int? WorldWidth,
    int? WorldHeight,
    string? WorldDiagnosticsSummary,
    WorldPrototypeTable? WorldPrototypeTable,
    WorldInstanceTable? WorldInstanceProbe,
    WorldInstanceTable? WorldInstanceTable,
    IReadOnlyList<string> WorldTokenSample,
    IReadOnlyList<string> WorldTailTokenSample,
    IReadOnlyList<string> TileFiles,
    IReadOnlyList<string> LocalResourceDirectories,
    ScenarioParseResult? ScenarioData);

internal sealed record DefinitionBrowseData(
    string DefinitionPath,
    string Category,
    string DisplayName,
    string SecondaryName,
    string Nationality,
    string Type,
    string AvailabilitySummary,
    string RawDefinitionText);

internal sealed record ScenarioParseResult(int UnknownHeader, string MapName, string Description, IReadOnlyList<ParsedScenario> Scenarios);

internal sealed record ParsedScenario(
    string Title,
    string Description,
    int TitleOffset,
    byte[] MetadataBytes,
    IReadOnlyList<int> MetadataInts,
    int SideACode,
    int SideBCode,
    int Month,
    int Year,
    int ArtilleryHeSideA,
    int ArtilleryHeSideB,
    int ArtillerySmokeSideA,
    int ArtillerySmokeSideB,
    float CameraCenterX,
    float CameraCenterY,
    int DeploymentCountPlusOne,
    string MetadataTailHex,
    byte[] PostDeploymentTailBytes,
    string PostDeploymentTailHex,
    IReadOnlyList<ScenarioSpecialRecordCandidate> SpecialRecordCandidates,
    IReadOnlyList<ScenarioDeployment> Deployments);

internal sealed record ScenarioDeployment(
    int RecordOffset,
    string UnitName,
    string DefinitionPath,
    string Category,
    string PayloadHex,
    byte[] PayloadBytes,
    string PrefixHex,
    int DelayGuess,
    byte FlagByte,
    float ScaleGuess,
    byte GroupByte,
    float X,
    float Y,
    float AngleRadians,
    float ExtraValue);

internal sealed record ScenarioDeploymentParseResult(
    IReadOnlyList<ScenarioDeployment> Deployments,
    int EndOffset);

internal sealed record ScenarioSpecialRecordCandidate(
    int DeploymentRecordOffset,
    ushort MarkerWord,
    IReadOnlyList<ScenarioSpecialPoint> Points,
    string RawHex,
    IReadOnlyList<ScenarioSpecialPointWorldLink> WorldPointLinks);

internal sealed record ScenarioSpecialPoint(float X, float Y);

internal sealed record ScenarioSpecialPointWorldLink(
    int PointIndex,
    ScenarioSpecialPoint Point,
    int WorldInstanceOffset,
    ushort WorldHeaderWord,
    int WorldX,
    int WorldY,
    int PrototypeIndex,
    string PrototypeName,
    float Distance);

internal sealed record WorldPrototypeTailSegment(
    int StartOffset,
    int EndOffset,
    int EntryCount);

internal sealed record WorldPrototypeTable(
    int StartOffset,
    int EndOffset,
    int LeadingWord,
    int CountHint,
    IReadOnlyList<WorldPrototypeEntry> Entries,
    IReadOnlyList<WorldPrototypeTailSegment> SupplementalSegments);

internal sealed record WorldInstanceTable(
    int StartOffset,
    int EndOffset,
    int DeclaredCount,
    string CountModel,
    ushort CountPreludeWord,
    int ParsedCount,
    int UnresolvedPrototypeCount,
    int InvalidCoordinateCount,
    int InvalidCoordinateThreshold,
    int OverflowCoordinateCount,
    int ZeroCoordinateCount,
    int Sentinel65532CoordinateCount,
    int DistinctHeaderCount,
    int CanonicalHeaderCount,
    bool ParseAccepted,
    string? RejectionReason,
    IReadOnlyList<WorldInstanceEntry> Entries);

internal sealed record WorldPrototypeEntry(
    int Offset,
    string Name,
    byte FlagA,
    byte FlagB);

internal sealed record WorldInstanceEntry(
    int Offset,
    ushort HeaderWord,
    int X,
    int Y,
    int PrototypeIndex,
    string PrototypeName,
    byte PrototypeFlagA,
    byte PrototypeFlagB);
