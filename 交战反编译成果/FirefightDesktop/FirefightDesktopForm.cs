using System.Text;
using System.Text.RegularExpressions;
using FirefightWorkbench;

namespace FirefightDesktop;

internal sealed class FirefightDesktopForm : Form
{
    private readonly string assetRoot;
    private readonly FirefightAssetInspector inspector;
    private readonly TreeView mapTreeView;
    private readonly TextBox mapSummaryTextBox;
    private readonly Label headerLabel;
    private readonly ComboBox scenarioComboBox;
    private readonly ComboBox deploymentFilterComboBox;
    private readonly Button exportScenarioButton;
    private readonly CheckBox showContoursCheckBox;
    private readonly CheckBox showSpecialRoutesCheckBox;
    private readonly CheckBox showWorldObjectsCheckBox;
    private readonly MapPreviewControl mapPreviewControl;
    private readonly ListView deploymentListView;
    private readonly TextBox definitionTextBox;
    private readonly TextBox worldDiagnosticsTextBox;
    private readonly ToolStripStatusLabel statusLabel;
    private readonly Font monoFont = new("Cascadia Mono", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private Image? currentMapImage;
    private Image? currentContoursImage;
    private MapBrowseData? currentMap;
    private ParsedScenario? currentScenario;
    private IReadOnlyList<ScenarioDeployment> currentVisibleDeployments = Array.Empty<ScenarioDeployment>();
    private int? currentSelectedDeploymentRecordOffset;
    private int? currentSelectedWorldInstanceOffset;
    private bool suppressSelectionSync;

    public FirefightDesktopForm(string assetRoot)
    {
        this.assetRoot = assetRoot;
        inspector = new FirefightAssetInspector(assetRoot);

        Text = "Firefight Desktop Verification";
        MinimumSize = new Size(1360, 860);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(240, 242, 245);

        mapTreeView = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
        };

        mapSummaryTextBox = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 220,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Font = monoFont,
            BackColor = Color.White,
        };

        headerLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold, GraphicsUnit.Point),
            Padding = new Padding(0, 0, 0, 4),
            Text = "Loading maps...",
        };

        scenarioComboBox = new ComboBox
        {
            Dock = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Height = 30,
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
        };

        deploymentFilterComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 120,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
        };
        deploymentFilterComboBox.Items.Add(new DeploymentFilterChoice("All", null));
        deploymentFilterComboBox.Items.Add(new DeploymentFilterChoice("g0", 0));
        deploymentFilterComboBox.Items.Add(new DeploymentFilterChoice("g1", 1));
        deploymentFilterComboBox.Items.Add(new DeploymentFilterChoice("Other", null, IsOtherBucket: true));
        deploymentFilterComboBox.Items.Add(new DeploymentFilterChoice("Flag4>0", null, Flag4Only: true));
        deploymentFilterComboBox.Items.Add(new DeploymentFilterChoice("Delay>0", null, DelayedOnly: true));
        deploymentFilterComboBox.SelectedIndex = 0;

        exportScenarioButton = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Enabled = false,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            Margin = new Padding(12, 0, 0, 0),
            Padding = new Padding(8, 4, 8, 4),
            Text = "Export Scenario Report",
        };

        showContoursCheckBox = new CheckBox
        {
            AutoSize = true,
            Text = "Show Contours",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
        };

        showSpecialRoutesCheckBox = new CheckBox
        {
            AutoSize = true,
            Checked = true,
            Text = "Show Tutorial Routes",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
        };

        showWorldObjectsCheckBox = new CheckBox
        {
            AutoSize = true,
            Checked = true,
            Text = "Show World Objects",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
        };

        mapPreviewControl = new MapPreviewControl
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };

        deploymentListView = new ListView
        {
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = false,
            View = View.Details,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
        };
        deploymentListView.Columns.Add("#", 42);
        deploymentListView.Columns.Add("Side", 52);
        deploymentListView.Columns.Add("Unit", 220);
        deploymentListView.Columns.Add("Category", 90);
        deploymentListView.Columns.Add("Delay", 64);
        deploymentListView.Columns.Add("Position", 120);
        deploymentListView.Columns.Add("Special", 110);
        deploymentListView.Columns.Add("Definition", 220);

        definitionTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = monoFont,
            BackColor = Color.White,
        };

        worldDiagnosticsTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = monoFont,
            BackColor = Color.White,
        };

        statusLabel = new ToolStripStatusLabel();

        BuildLayout();
        WireEvents();
        LoadMapCatalog();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            currentMapImage?.Dispose();
            currentContoursImage?.Dispose();
            monoFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(statusLabel);

        var rootSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 310,
            FixedPanel = FixedPanel.Panel1,
        };

        var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        leftPanel.Controls.Add(mapTreeView);
        leftPanel.Controls.Add(mapSummaryTextBox);
        leftPanel.Controls.Add(CreateSectionLabel("Map Catalog", DockStyle.Top));
        rootSplit.Panel1.Controls.Add(leftPanel);

        var metadataPanel = new Panel { Dock = DockStyle.Top, Height = 126, Padding = new Padding(12, 12, 12, 8) };
        var optionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 4, 0, 0),
        };
        optionsPanel.Controls.Add(showContoursCheckBox);
        optionsPanel.Controls.Add(showSpecialRoutesCheckBox);
        optionsPanel.Controls.Add(showWorldObjectsCheckBox);
        optionsPanel.Controls.Add(CreateOptionsLabel("Deployment Filter"));
        optionsPanel.Controls.Add(deploymentFilterComboBox);
        optionsPanel.Controls.Add(exportScenarioButton);
        metadataPanel.Controls.Add(optionsPanel);
        metadataPanel.Controls.Add(scenarioComboBox);
        metadataPanel.Controls.Add(CreateInlineLabel("Scenario", DockStyle.Top));
        metadataPanel.Controls.Add(headerLabel);

        var rightBodySplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 860,
            FixedPanel = FixedPanel.Panel2,
        };

        var previewPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 6, 12) };
        previewPanel.Controls.Add(mapPreviewControl);
        previewPanel.Controls.Add(CreateSectionLabel("Map Preview", DockStyle.Top));
        rightBodySplit.Panel1.Controls.Add(previewPanel);

        var rightDetailSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 320,
            FixedPanel = FixedPanel.Panel1,
        };

        var deploymentPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 12, 6) };
        deploymentPanel.Controls.Add(deploymentListView);
        deploymentPanel.Controls.Add(CreateSectionLabel("Deployments", DockStyle.Top));
        rightDetailSplit.Panel1.Controls.Add(deploymentPanel);

        var detailTabs = new TabControl
        {
            Dock = DockStyle.Fill,
        };
        var inspectorTabPage = new TabPage("Inspector Details");
        inspectorTabPage.Controls.Add(definitionTextBox);
        var worldDiagnosticsTabPage = new TabPage("World Diagnostics");
        worldDiagnosticsTabPage.Controls.Add(worldDiagnosticsTextBox);
        detailTabs.TabPages.Add(inspectorTabPage);
        detailTabs.TabPages.Add(worldDiagnosticsTabPage);

        var definitionPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 6, 12, 12) };
        definitionPanel.Controls.Add(detailTabs);
        rightDetailSplit.Panel2.Controls.Add(definitionPanel);

        rightBodySplit.Panel2.Controls.Add(rightDetailSplit);

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(rightBodySplit);
        rightPanel.Controls.Add(metadataPanel);
        rootSplit.Panel2.Controls.Add(rightPanel);

        Controls.Add(rootSplit);
        Controls.Add(statusStrip);
    }

    private void WireEvents()
    {
        mapTreeView.AfterSelect += MapTreeViewOnAfterSelect;
        scenarioComboBox.SelectedIndexChanged += ScenarioComboBoxOnSelectedIndexChanged;
        deploymentFilterComboBox.SelectedIndexChanged += DeploymentFilterComboBoxOnSelectedIndexChanged;
        showContoursCheckBox.CheckedChanged += ShowContoursCheckBoxOnCheckedChanged;
        showSpecialRoutesCheckBox.CheckedChanged += ShowSpecialRoutesCheckBoxOnCheckedChanged;
        showWorldObjectsCheckBox.CheckedChanged += ShowWorldObjectsCheckBoxOnCheckedChanged;
        exportScenarioButton.Click += ExportScenarioButtonOnClick;
        deploymentListView.SelectedIndexChanged += DeploymentListViewOnSelectedIndexChanged;
        mapPreviewControl.DeploymentSelected += MapPreviewControlOnDeploymentSelected;
        mapPreviewControl.WorldInstanceSelected += MapPreviewControlOnWorldInstanceSelected;
        mapPreviewControl.SelectionCleared += MapPreviewControlOnSelectionCleared;
    }

    private void LoadMapCatalog()
    {
        var summaries = inspector.GetMapBrowseSummaries();
        mapTreeView.BeginUpdate();
        try
        {
            mapTreeView.Nodes.Clear();

            foreach (var group in summaries.GroupBy(item => item.RootName))
            {
                var rootNode = new TreeNode(group.Key);
                foreach (var map in group)
                {
                    var node = new TreeNode($"{map.MapCode}  {map.DisplayName}")
                    {
                        Tag = map,
                    };
                    rootNode.Nodes.Add(node);
                }

                rootNode.Expand();
                mapTreeView.Nodes.Add(rootNode);
            }
        }
        finally
        {
            mapTreeView.EndUpdate();
        }

        statusLabel.Text = $"Loaded {summaries.Count} maps from {assetRoot}";

        var firstMapNode = mapTreeView.Nodes.Cast<TreeNode>()
            .SelectMany(node => node.Nodes.Cast<TreeNode>())
            .FirstOrDefault();
        if (firstMapNode is not null)
        {
            mapTreeView.SelectedNode = firstMapNode;
        }
    }

    private void MapTreeViewOnAfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node?.Tag is not MapBrowseSummary summary)
        {
            return;
        }

        LoadMap(summary);
    }

    private void ScenarioComboBoxOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        var scenario = (scenarioComboBox.SelectedItem as ScenarioChoice)?.Scenario;
        ApplyScenarioSelection(scenario);
    }

    private void DeploymentFilterComboBoxOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        RefreshScenarioView();
    }

    private void ShowContoursCheckBoxOnCheckedChanged(object? sender, EventArgs e)
    {
        RefreshScenarioView();
    }

    private void ShowSpecialRoutesCheckBoxOnCheckedChanged(object? sender, EventArgs e)
    {
        RefreshScenarioView();
    }

    private void ShowWorldObjectsCheckBoxOnCheckedChanged(object? sender, EventArgs e)
    {
        RefreshScenarioView();
    }

    private void ExportScenarioButtonOnClick(object? sender, EventArgs e)
    {
        if (currentMap is null || currentScenario is null)
        {
            return;
        }

        try
        {
            var filterLabel = ScenarioSnapshotFormatter.NormalizeFilterLabel(GetCurrentFilterLabel());
            var exportDirectory = ScenarioSnapshotFormatter.GetScenarioExportDirectory(AppContext.BaseDirectory);
            var fileName = ScenarioSnapshotFormatter.BuildScenarioSnapshotFileName(
                currentMap.MapCode,
                GetCurrentScenarioIndex(),
                currentScenario,
                filterLabel);
            var exportPath = Path.Combine(exportDirectory, fileName);
            var text = ScenarioSnapshotFormatter.BuildScenarioExportText(
                assetRoot,
                currentMap,
                currentScenario,
                currentVisibleDeployments,
                filterLabel);
            File.WriteAllText(exportPath, text + Environment.NewLine, new UTF8Encoding(false));
            statusLabel.Text = $"Exported scenario snapshot: {exportPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"{ex.Message}{Environment.NewLine}{Environment.NewLine}{ex}",
                "Scenario snapshot export failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void DeploymentListViewOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (suppressSelectionSync)
        {
            return;
        }

        var deployment = deploymentListView.SelectedItems.Count == 0
            ? null
            : deploymentListView.SelectedItems[0].Tag as ScenarioDeployment;
        SetSelectedDeployment(deployment, syncList: false, syncMap: true);
    }

    private void MapPreviewControlOnDeploymentSelected(object? sender, ScenarioDeploymentEventArgs e)
    {
        SetSelectedDeployment(e.Deployment, syncList: true, syncMap: false);
    }

    private void MapPreviewControlOnWorldInstanceSelected(object? sender, WorldInstanceEventArgs e)
    {
        SetSelectedWorldInstance(e.WorldInstance, syncList: true, syncMap: false);
    }

    private void MapPreviewControlOnSelectionCleared(object? sender, EventArgs e)
    {
        ClearCurrentSelection(syncList: true, syncMap: false);
    }

    private void SetSelectedDeployment(ScenarioDeployment? deployment, bool syncList, bool syncMap)
    {
        currentSelectedDeploymentRecordOffset = deployment?.RecordOffset;
        currentSelectedWorldInstanceOffset = null;

        if (syncList)
        {
            suppressSelectionSync = true;
            try
            {
                foreach (ListViewItem item in deploymentListView.Items)
                {
                    var isMatch = deployment is not null && Equals(item.Tag, deployment);
                    item.Selected = isMatch;
                    if (isMatch)
                    {
                        item.Focused = true;
                        item.EnsureVisible();
                    }
                }
            }
            finally
            {
                suppressSelectionSync = false;
            }
        }

        if (syncMap)
        {
            mapPreviewControl.SelectDeployment(deployment);
        }

        ShowDefinitionDetails(deployment, null);
    }

    private void SetSelectedWorldInstance(WorldInstanceEntry? worldInstance, bool syncList, bool syncMap)
    {
        currentSelectedDeploymentRecordOffset = null;
        currentSelectedWorldInstanceOffset = worldInstance?.Offset;

        if (syncList)
        {
            ClearDeploymentListSelection();
        }

        if (syncMap)
        {
            mapPreviewControl.SelectWorldInstance(worldInstance);
        }

        ShowDefinitionDetails(null, worldInstance);
    }

    private void ClearCurrentSelection(bool syncList, bool syncMap)
    {
        currentSelectedDeploymentRecordOffset = null;
        currentSelectedWorldInstanceOffset = null;

        if (syncList)
        {
            ClearDeploymentListSelection();
        }

        if (syncMap)
        {
            mapPreviewControl.ClearSelection();
        }

        ShowDefinitionDetails(null, null);
    }

    private void ClearDeploymentListSelection()
    {
        suppressSelectionSync = true;
        try
        {
            foreach (ListViewItem item in deploymentListView.Items)
            {
                item.Selected = false;
            }
        }
        finally
        {
            suppressSelectionSync = false;
        }
    }

    private void LoadMap(MapBrowseSummary summary)
    {
        UseWaitCursor = true;
        try
        {
            currentMap = inspector.TryLoadMapBrowseData(summary.MapCode);
            ReplaceCurrentMapImage(currentMap is null ? null : TryLoadMapImage(currentMap));

            if (currentMap is null)
            {
                currentScenario = null;
                currentVisibleDeployments = Array.Empty<ScenarioDeployment>();
                headerLabel.Text = $"{summary.MapCode} | Failed to load";
                mapSummaryTextBox.Text = $"Could not load map {summary.MapCode}.";
                scenarioComboBox.Items.Clear();
                deploymentListView.Items.Clear();
                exportScenarioButton.Enabled = false;
                ReplaceCurrentContoursImage(null);
                currentSelectedDeploymentRecordOffset = null;
                currentSelectedWorldInstanceOffset = null;
                mapPreviewControl.SetScene(
                    null,
                    null,
                    false,
                    null,
                    null,
                    Array.Empty<WorldInstanceEntry>(),
                    Array.Empty<ScenarioDeployment>(),
                    Array.Empty<ScenarioSpecialRecordCandidate>());
                definitionTextBox.Clear();
                worldDiagnosticsTextBox.Clear();
                statusLabel.Text = $"Failed to load {summary.MapCode}";
                return;
            }

            ReplaceCurrentContoursImage(TryLoadContoursImage(currentMap));
            headerLabel.Text = $"{currentMap.MapCode} | {currentMap.MapDisplayName}";
            mapSummaryTextBox.Text = ScenarioSnapshotFormatter.BuildMapSummary(currentMap, includeWorldDiagnostics: false);
            worldDiagnosticsTextBox.Text = ScenarioSnapshotFormatter.BuildWorldDiagnosticsReport(currentMap);
            PopulateScenarioChoices(currentMap);
            statusLabel.Text = $"{currentMap.MapCode}: {currentMap.ScenarioData?.Scenarios.Count ?? 0} scenarios, {(currentMap.ScenarioData?.Scenarios.Sum(item => item.Deployments.Count) ?? 0)} deployments";
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void PopulateScenarioChoices(MapBrowseData map)
    {
        scenarioComboBox.BeginUpdate();
        try
        {
            scenarioComboBox.Items.Clear();
            scenarioComboBox.Enabled = map.ScenarioData is not null && map.ScenarioData.Scenarios.Count > 0;

            if (map.ScenarioData is not null)
            {
                foreach (var scenario in map.ScenarioData.Scenarios)
                {
                    scenarioComboBox.Items.Add(new ScenarioChoice(scenario));
                }
            }
        }
        finally
        {
            scenarioComboBox.EndUpdate();
        }

        if (scenarioComboBox.Items.Count > 0)
        {
            scenarioComboBox.SelectedIndex = 0;
        }
        else
        {
            ApplyScenarioSelection(null);
        }
    }

    private void ApplyScenarioSelection(ParsedScenario? scenario)
    {
        currentScenario = scenario;
        currentSelectedDeploymentRecordOffset = null;
        currentSelectedWorldInstanceOffset = null;
        exportScenarioButton.Enabled = scenario is not null;
        RefreshScenarioView();
    }

    private void PopulateDeploymentList(IReadOnlyList<ScenarioDeployment> deployments)
    {
        deploymentListView.BeginUpdate();
        try
        {
            deploymentListView.Items.Clear();
            for (var index = 0; index < deployments.Count; index++)
            {
                var deployment = deployments[index];
                var item = new ListViewItem((index + 1).ToString())
                {
                    Tag = deployment,
                };
                item.SubItems.Add($"g{deployment.GroupByte}");
                item.SubItems.Add(deployment.UnitName);
                item.SubItems.Add(deployment.Category);
                item.SubItems.Add(deployment.DelayGuess.ToString());
                var specialRoute = TryFindSpecialRouteCandidate(deployment);
                item.SubItems.Add(BuildPositionLabel(deployment, specialRoute));
                item.SubItems.Add(specialRoute is null ? "-" : $"0x{specialRoute.MarkerWord:X4}/{specialRoute.Points.Count}pts");
                item.SubItems.Add(string.IsNullOrWhiteSpace(deployment.DefinitionPath) ? "-" : Path.GetFileNameWithoutExtension(deployment.DefinitionPath));
                deploymentListView.Items.Add(item);
            }
        }
        finally
        {
            deploymentListView.EndUpdate();
        }
    }

    private void RefreshScenarioView()
    {
        var deployments = FilterDeployments(currentScenario?.Deployments ?? Array.Empty<ScenarioDeployment>());
        var availableSpecialRoutes = GetVisibleSpecialRoutes(currentScenario, deployments);
        var availableWorldObjects = currentMap?.WorldInstanceTable?.Entries ?? Array.Empty<WorldInstanceEntry>();
        var visibleSpecialRoutes = showSpecialRoutesCheckBox.Checked
            ? availableSpecialRoutes
            : Array.Empty<ScenarioSpecialRecordCandidate>();
        var visibleWorldObjects = showWorldObjectsCheckBox.Checked
            ? availableWorldObjects
            : Array.Empty<WorldInstanceEntry>();
        currentVisibleDeployments = deployments;
        mapPreviewControl.SetScene(
            currentMapImage,
            currentContoursImage,
            showContoursCheckBox.Checked,
            currentMap?.WorldWidth,
            currentMap?.WorldHeight,
            visibleWorldObjects,
            deployments,
            visibleSpecialRoutes);
        PopulateDeploymentList(deployments);

        var selectedDeployment = currentSelectedDeploymentRecordOffset.HasValue
            ? deployments.FirstOrDefault(item => item.RecordOffset == currentSelectedDeploymentRecordOffset.Value)
            : null;
        var selectedWorldInstance = currentSelectedWorldInstanceOffset.HasValue
            ? visibleWorldObjects.FirstOrDefault(item => item.Offset == currentSelectedWorldInstanceOffset.Value)
            : null;

        if (selectedDeployment is not null)
        {
            SetSelectedDeployment(selectedDeployment, syncList: true, syncMap: true);
        }
        else if (selectedWorldInstance is not null)
        {
            SetSelectedWorldInstance(selectedWorldInstance, syncList: true, syncMap: true);
        }
        else
        {
            ClearCurrentSelection(syncList: true, syncMap: true);
        }

        if (currentMap is not null)
        {
            var totalCount = currentScenario?.Deployments.Count ?? 0;
            var filterLabel = deploymentFilterComboBox.SelectedItem?.ToString() ?? "All";
            var worldObjectStatus = $"{(showWorldObjectsCheckBox.Checked ? visibleWorldObjects.Count : availableWorldObjects.Count)} {(showWorldObjectsCheckBox.Checked ? "shown" : "hidden")}";
            var suppressedRawMarkers = deployments.Count(item => ScenarioSnapshotFormatter.ShouldSuppressRawDeploymentMarker(item, TryFindSpecialRouteCandidate(item)));
            var rawMarkerStatus = suppressedRawMarkers > 0
                ? $", raw-route markers={suppressedRawMarkers} suppressed"
                : string.Empty;
            statusLabel.Text = $"{currentMap.MapCode}: showing {deployments.Count}/{totalCount} deployments [{filterLabel}], special routes={(showSpecialRoutesCheckBox.Checked ? $"{visibleSpecialRoutes.Count} shown" : $"{availableSpecialRoutes.Count} hidden")}, world objects={worldObjectStatus}{rawMarkerStatus}{BuildSelectionStatusSuffix()}";
        }
    }

    private static IReadOnlyList<ScenarioSpecialRecordCandidate> GetVisibleSpecialRoutes(
        ParsedScenario? scenario,
        IReadOnlyList<ScenarioDeployment> visibleDeployments)
    {
        if (scenario is null || scenario.SpecialRecordCandidates.Count == 0 || visibleDeployments.Count == 0)
        {
            return Array.Empty<ScenarioSpecialRecordCandidate>();
        }

        var visibleOffsets = visibleDeployments
            .Select(item => item.RecordOffset)
            .ToHashSet();
        return scenario.SpecialRecordCandidates
            .Where(item => visibleOffsets.Contains(item.DeploymentRecordOffset))
            .ToArray();
    }

    private IReadOnlyList<ScenarioDeployment> FilterDeployments(IReadOnlyList<ScenarioDeployment> deployments)
    {
        return ScenarioSnapshotFormatter.FilterDeployments(deployments, GetCurrentFilterLabel());
    }

    private void ShowDefinitionDetails(ScenarioDeployment? deployment, WorldInstanceEntry? worldInstance)
    {
        if (currentMap is null)
        {
            definitionTextBox.Clear();
            return;
        }

        if (deployment is null && worldInstance is null)
        {
            definitionTextBox.Text = currentScenario is null
                ? ScenarioSnapshotFormatter.BuildMapSummary(currentMap)
                : ScenarioSnapshotFormatter.BuildScenarioSummary(currentMap, currentScenario, currentVisibleDeployments, GetCurrentFilterLabel());
            return;
        }

        if (worldInstance is not null)
        {
            definitionTextBox.Text = BuildWorldInstanceDetails(worldInstance);
            return;
        }

        var selectedDeployment = deployment!;
        var builder = new StringBuilder();
        var specialRoute = TryFindSpecialRouteCandidate(selectedDeployment);
        builder.AppendLine($"{selectedDeployment.UnitName}");
        builder.AppendLine(new string('=', Math.Max(12, selectedDeployment.UnitName.Length)));
        builder.AppendLine($"Category: {selectedDeployment.Category}");
        builder.AppendLine($"Group byte: {selectedDeployment.GroupByte}");
        builder.AppendLine($"Delay guess: {selectedDeployment.DelayGuess}");
        builder.AppendLine($"Flag byte: {selectedDeployment.FlagByte}");
        builder.AppendLine($"Scale guess: {selectedDeployment.ScaleGuess:N3}");
        builder.AppendLine($"Position: {(ScenarioSnapshotFormatter.ShouldSuppressRawDeploymentMarker(selectedDeployment, specialRoute) ? "raw " : string.Empty)}({selectedDeployment.X:N1}, {selectedDeployment.Y:N1})");
        builder.AppendLine($"Angle radians: {selectedDeployment.AngleRadians:N3}");
        builder.AppendLine($"Extra value: {selectedDeployment.ExtraValue:N3}");
        builder.AppendLine($"Definition path: {selectedDeployment.DefinitionPath}");
        if (specialRoute is not null)
        {
            builder.AppendLine($"Special marker: 0x{specialRoute.MarkerWord:X4}");
            builder.AppendLine($"Special route points: {specialRoute.Points.Count}");
            builder.AppendLine($"Special route sample: {string.Join(" -> ", specialRoute.Points.Take(6).Select(point => $"({point.X:N0}, {point.Y:N0})"))}");
            builder.AppendLine("Special route detail:");
            for (var index = 0; index < specialRoute.Points.Count && index < 12; index++)
            {
                var point = specialRoute.Points[index];
                builder.AppendLine($"  #{index + 1:D2} ({point.X:N0}, {point.Y:N0})");
            }
            if (specialRoute.Points.Count > 12)
            {
                builder.AppendLine($"  ... {specialRoute.Points.Count - 12} more points");
            }

            if (specialRoute.WorldPointLinks.Count > 0)
            {
                builder.AppendLine($"Nearest world objects: {specialRoute.WorldPointLinks.Count}");
                builder.AppendLine("World-link detail:");
                foreach (var link in specialRoute.WorldPointLinks.Take(12))
                {
                    builder.AppendLine(
                        $"  #{link.PointIndex + 1:D2} ({link.Point.X:N0}, {link.Point.Y:N0}) -> {link.PrototypeName} @ ({link.WorldX:N0}, {link.WorldY:N0}), d={link.Distance:N1}, off=0x{link.WorldInstanceOffset:X4}");
                }

                if (specialRoute.WorldPointLinks.Count > 12)
                {
                    builder.AppendLine($"  ... {specialRoute.WorldPointLinks.Count - 12} more world links");
                }
            }
        }

        if (currentScenario is not null)
        {
            builder.AppendLine();
            builder.AppendLine(ScenarioSnapshotFormatter.BuildDeploymentReplicaComparisonNotes(currentScenario, selectedDeployment));
        }

        builder.AppendLine();

        var definition = inspector.TryLoadDefinitionBrowseData(selectedDeployment.DefinitionPath);
        if (definition is null)
        {
            builder.AppendLine("Definition file could not be resolved.");
        }
        else
        {
            builder.AppendLine($"Display name: {definition.DisplayName}");
            if (!string.IsNullOrWhiteSpace(definition.SecondaryName))
            {
                builder.AppendLine($"Short name: {definition.SecondaryName}");
            }
            if (!string.IsNullOrWhiteSpace(definition.Nationality))
            {
                builder.AppendLine($"Nationality: {definition.Nationality}");
            }
            builder.AppendLine($"Type: {definition.Type}");
            if (!string.IsNullOrWhiteSpace(definition.AvailabilitySummary))
            {
                builder.AppendLine($"Availability: {definition.AvailabilitySummary}");
            }
            builder.AppendLine();
            builder.AppendLine("Raw definition:");
            builder.AppendLine(definition.RawDefinitionText);
        }

        definitionTextBox.Text = builder.ToString().TrimEnd();
    }

    private string BuildWorldInstanceDetails(WorldInstanceEntry worldInstance)
    {
        var builder = new StringBuilder();
        builder.AppendLine(worldInstance.PrototypeName);
        builder.AppendLine(new string('=', Math.Max(12, worldInstance.PrototypeName.Length)));
        builder.AppendLine("Item type: World object instance");
        builder.AppendLine($"Map offset: 0x{worldInstance.Offset:X4}");
        builder.AppendLine($"Header word: 0x{worldInstance.HeaderWord:X4}");
        builder.AppendLine($"Position: ({worldInstance.X:N1}, {worldInstance.Y:N1})");
        builder.AppendLine($"Prototype index: {worldInstance.PrototypeIndex}");
        builder.AppendLine($"Prototype flags: {worldInstance.PrototypeFlagA} / {worldInstance.PrototypeFlagB}");

        if (currentMap?.WorldInstanceTable is not null)
        {
            builder.AppendLine($"Map world objects: {currentMap.WorldInstanceTable.Entries.Count}");
        }

        if (currentScenario is not null)
        {
            var matchingLinks = currentScenario.SpecialRecordCandidates
                .SelectMany(candidate => candidate.WorldPointLinks.Select(link => (candidate, link)))
                .Where(item => item.link.WorldInstanceOffset == worldInstance.Offset)
                .ToArray();

            builder.AppendLine($"Linked route points in current scenario: {matchingLinks.Length}");
            if (matchingLinks.Length > 0)
            {
                builder.AppendLine("Route-link detail:");
                foreach (var item in matchingLinks.Take(12))
                {
                    var linkedDeployment = currentScenario.Deployments
                        .FirstOrDefault(deployment => deployment.RecordOffset == item.candidate.DeploymentRecordOffset);
                    var routeLabel = linkedDeployment?.UnitName ?? $"0x{item.candidate.DeploymentRecordOffset:X4}";
                    builder.AppendLine(
                        $"  {routeLabel} -> point #{item.link.PointIndex + 1:D2} ({item.link.Point.X:N0}, {item.link.Point.Y:N0}), d={item.link.Distance:N1}");
                }

                if (matchingLinks.Length > 12)
                {
                    builder.AppendLine($"  ... {matchingLinks.Length - 12} more links");
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private string BuildSelectionStatusSuffix()
    {
        if (currentSelectedDeploymentRecordOffset.HasValue && currentVisibleDeployments.Count > 0)
        {
            var selectedDeployment = currentVisibleDeployments
                .FirstOrDefault(item => item.RecordOffset == currentSelectedDeploymentRecordOffset.Value);
            if (selectedDeployment is not null)
            {
                return $", selected={selectedDeployment.UnitName}";
            }
        }

        if (currentSelectedWorldInstanceOffset.HasValue)
        {
            var selectedWorldInstance = currentMap?.WorldInstanceTable?.Entries
                .FirstOrDefault(item => item.Offset == currentSelectedWorldInstanceOffset.Value);
            if (selectedWorldInstance is not null)
            {
                return $", selected world object={selectedWorldInstance.PrototypeName}@0x{selectedWorldInstance.Offset:X4}";
            }
        }

        return string.Empty;
    }

    private ScenarioSpecialRecordCandidate? TryFindSpecialRouteCandidate(ScenarioDeployment deployment)
    {
        return currentScenario?.SpecialRecordCandidates
            .FirstOrDefault(item => item.DeploymentRecordOffset == deployment.RecordOffset);
    }

    private static string BuildPositionLabel(
        ScenarioDeployment deployment,
        ScenarioSpecialRecordCandidate? specialRoute)
    {
        var prefix = ScenarioSnapshotFormatter.ShouldSuppressRawDeploymentMarker(deployment, specialRoute)
            ? "raw "
            : string.Empty;
        return $"{prefix}{deployment.X:N0}, {deployment.Y:N0}";
    }

    private static Label CreateSectionLabel(string text, DockStyle dock)
    {
        return new Label
        {
            Dock = dock,
            Height = 28,
            Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold, GraphicsUnit.Point),
            Padding = new Padding(0, 0, 0, 6),
            Text = text,
        };
    }

    private static Label CreateInlineLabel(string text, DockStyle dock)
    {
        return new Label
        {
            Dock = dock,
            Height = 22,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            Padding = new Padding(0, 4, 0, 2),
            Text = text,
        };
    }

    private static Label CreateOptionsLabel(string text)
    {
        return new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            Margin = new Padding(12, 6, 6, 0),
            Text = text,
        };
    }

    private int GetCurrentScenarioIndex()
    {
        if (currentMap?.ScenarioData is null || currentScenario is null)
        {
            return -1;
        }

        for (var index = 0; index < currentMap.ScenarioData.Scenarios.Count; index++)
        {
            if (ReferenceEquals(currentMap.ScenarioData.Scenarios[index], currentScenario))
            {
                return index;
            }
        }

        return -1;
    }

    private string GetCurrentFilterLabel()
    {
        return deploymentFilterComboBox.SelectedItem?.ToString() ?? "All";
    }

    private void ReplaceCurrentMapImage(Image? replacement)
    {
        currentMapImage?.Dispose();
        currentMapImage = replacement;
    }

    private void ReplaceCurrentContoursImage(Image? replacement)
    {
        currentContoursImage?.Dispose();
        currentContoursImage = replacement;
    }

    private static Image? TryLoadMapImage(MapBrowseData map)
    {
        if (!string.IsNullOrWhiteSpace(map.MapImagePath) && File.Exists(map.MapImagePath))
        {
            return TryLoadImageCopy(map.MapImagePath);
        }

        return TryBuildTileComposite(map);
    }

    private static Image? TryLoadContoursImage(MapBrowseData map)
    {
        return !string.IsNullOrWhiteSpace(map.ContoursPath) && File.Exists(map.ContoursPath)
            ? TryLoadImageCopy(map.ContoursPath)
            : null;
    }

    private static Image? TryBuildTileComposite(MapBrowseData map)
    {
        if (map.TileFiles.Count == 0 || map.TileColumns <= 0 || map.TileRows <= 0)
        {
            return null;
        }

        using var firstTile = TryLoadImageCopy(map.TileFiles[0]);
        if (firstTile is null)
        {
            return null;
        }

        var composite = new Bitmap(firstTile.Width * map.TileColumns, firstTile.Height * map.TileRows);
        using var graphics = Graphics.FromImage(composite);
        graphics.Clear(Color.Black);

        foreach (var tileFile in map.TileFiles)
        {
            if (!TryParseTileCoordinate(tileFile, out var column, out var row))
            {
                continue;
            }

            using var tile = TryLoadImageCopy(tileFile);
            if (tile is null)
            {
                continue;
            }

            graphics.DrawImage(tile, column * firstTile.Width, row * firstTile.Height, firstTile.Width, firstTile.Height);
        }

        return composite;
    }

    private static bool TryParseTileCoordinate(string tileFile, out int column, out int row)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(tileFile) ?? string.Empty, "-tile(?<column>\\d)(?<row>\\d)$");
        if (match.Success
            && int.TryParse(match.Groups["column"].Value, out column)
            && int.TryParse(match.Groups["row"].Value, out row))
        {
            return true;
        }

        column = 0;
        row = 0;
        return false;
    }

    private static Image LoadImageCopy(string path)
    {
        using var source = Image.FromFile(path);
        return new Bitmap(source);
    }

    private static Image? TryLoadImageCopy(string path)
    {
        try
        {
            return LoadImageCopy(path);
        }
        catch
        {
            return null;
        }
    }

    private sealed record ScenarioChoice(ParsedScenario Scenario)
    {
        public override string ToString()
        {
            return $"{Scenario.Title}  [{Scenario.Year:D4}-{Scenario.Month:D2}]  ({Scenario.Deployments.Count} deployments, {Scenario.SpecialRecordCandidates.Count} routes)";
        }
    }

    private sealed record DeploymentFilterChoice(
        string Label,
        int? GroupByte,
        bool IsOtherBucket = false,
        bool Flag4Only = false,
        bool DelayedOnly = false)
    {
        public override string ToString()
        {
            return Label;
        }
    }
}
