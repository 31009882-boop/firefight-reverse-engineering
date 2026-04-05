using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using FirefightWorkbench;

namespace FirefightDesktop;

internal sealed class MapPreviewControl : Control
{
    private const float MarkerRadius = 6f;
    private const float RouteNodeRadius = 4.5f;
    private const float WorldObjectRadius = 2.5f;
    private readonly ToolTip toolTip = new();
    private Image? previewImage;
    private Image? contoursImage;
    private bool showContours;
    private IReadOnlyList<WorldInstanceEntry> worldInstances = Array.Empty<WorldInstanceEntry>();
    private int? worldWidth;
    private int? worldHeight;
    private IReadOnlyList<ScenarioDeployment> deployments = Array.Empty<ScenarioDeployment>();
    private IReadOnlyList<ScenarioSpecialRecordCandidate> specialRoutes = Array.Empty<ScenarioSpecialRecordCandidate>();
    private IReadOnlyDictionary<int, ScenarioSpecialRecordCandidate> specialRoutesByOffset = new Dictionary<int, ScenarioSpecialRecordCandidate>();
    private int? selectedDeploymentRecordOffset;
    private int? selectedWorldInstanceOffset;
    private string? lastTooltipText;

    public MapPreviewControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(20, 24, 28);
        ForeColor = Color.WhiteSmoke;
        Cursor = Cursors.Cross;
        toolTip.AutoPopDelay = 4000;
        toolTip.InitialDelay = 100;
        toolTip.ReshowDelay = 50;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    public event EventHandler<ScenarioDeploymentEventArgs>? DeploymentSelected;
    public event EventHandler<WorldInstanceEventArgs>? WorldInstanceSelected;
    public event EventHandler? SelectionCleared;

    public void SetScene(
        Image? image,
        Image? contours,
        bool contoursVisible,
        int? mapWorldWidth,
        int? mapWorldHeight,
        IReadOnlyList<WorldInstanceEntry> sceneWorldInstances,
        IReadOnlyList<ScenarioDeployment> sceneDeployments,
        IReadOnlyList<ScenarioSpecialRecordCandidate> scenarioSpecialRoutes)
    {
        var previousDeploymentSelection = selectedDeploymentRecordOffset;
        var previousWorldSelection = selectedWorldInstanceOffset;
        previewImage = image;
        contoursImage = contours;
        showContours = contoursVisible;
        worldWidth = mapWorldWidth;
        worldHeight = mapWorldHeight;
        worldInstances = sceneWorldInstances ?? Array.Empty<WorldInstanceEntry>();
        deployments = sceneDeployments ?? Array.Empty<ScenarioDeployment>();
        specialRoutes = scenarioSpecialRoutes ?? Array.Empty<ScenarioSpecialRecordCandidate>();
        specialRoutesByOffset = specialRoutes
            .GroupBy(item => item.DeploymentRecordOffset)
            .ToDictionary(group => group.Key, group => group.Last());
        selectedDeploymentRecordOffset = previousDeploymentSelection.HasValue && deployments.Any(item => item.RecordOffset == previousDeploymentSelection.Value)
            ? previousDeploymentSelection
            : null;
        selectedWorldInstanceOffset = previousWorldSelection.HasValue && worldInstances.Any(item => item.Offset == previousWorldSelection.Value)
            ? previousWorldSelection
            : null;
        lastTooltipText = null;
        toolTip.Hide(this);
        Invalidate();
    }

    public void SelectDeployment(ScenarioDeployment? deployment)
    {
        selectedDeploymentRecordOffset = deployment?.RecordOffset;
        selectedWorldInstanceOffset = null;
        toolTip.Hide(this);
        lastTooltipText = null;
        Invalidate();
    }

    public void SelectWorldInstance(WorldInstanceEntry? worldInstance)
    {
        selectedWorldInstanceOffset = worldInstance?.Offset;
        selectedDeploymentRecordOffset = null;
        toolTip.Hide(this);
        lastTooltipText = null;
        Invalidate();
    }

    public void ClearSelection()
    {
        selectedDeploymentRecordOffset = null;
        selectedWorldInstanceOffset = null;
        toolTip.Hide(this);
        lastTooltipText = null;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

        if (!TryGetMapLayout(out var mapRectangle, out var scaleX, out var scaleY))
        {
            DrawEmptyState(e.Graphics);
            return;
        }

        if (previewImage is not null)
        {
            e.Graphics.DrawImage(previewImage, mapRectangle);
        }
        else
        {
            using var placeholderBrush = new SolidBrush(Color.FromArgb(38, 45, 55));
            using var placeholderPen = new Pen(Color.FromArgb(90, 120, 150));
            e.Graphics.FillRectangle(placeholderBrush, mapRectangle);
            e.Graphics.DrawRectangle(placeholderPen, Rectangle.Round(mapRectangle));
        }

        DrawContours(e.Graphics, mapRectangle);
        DrawWorldObjects(e.Graphics, mapRectangle, scaleX, scaleY);
        DrawSpecialRoutes(e.Graphics, mapRectangle, scaleX, scaleY);
        DrawWorldBorder(e.Graphics, mapRectangle);
        DrawDeployments(e.Graphics, mapRectangle, scaleX, scaleY);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (TryFindNearestRouteHit(e.Location, out var route, out var routeHit))
        {
            var routeDeployment = deployments.FirstOrDefault(item => item.RecordOffset == route.DeploymentRecordOffset);
            var routeLabel = routeDeployment is null ? $"0x{route.DeploymentRecordOffset:X4}" : routeDeployment.UnitName;
            string routeTooltip;
            if (routeHit.IsNode)
            {
                var routePoint = route.Points[routeHit.Index];
                routeTooltip = $"{routeLabel} route\nPoint {routeHit.Index + 1}/{route.Points.Count}\n({routePoint.X:N0}, {routePoint.Y:N0})";
            }
            else
            {
                var startPoint = route.Points[routeHit.Index];
                var endPoint = route.Points[Math.Min(routeHit.Index + 1, route.Points.Count - 1)];
                routeTooltip =
                    $"{routeLabel} route\nSegment {routeHit.Index + 1}->{Math.Min(routeHit.Index + 2, route.Points.Count)}\n({startPoint.X:N0}, {startPoint.Y:N0}) -> ({endPoint.X:N0}, {endPoint.Y:N0})";
            }

            var worldLink = TryGetNearestWorldLink(route, routeHit);
            if (worldLink is not null && worldLink.WorldInstanceOffset >= 0)
            {
                routeTooltip = $"{routeTooltip}\nNearest object: {worldLink.PrototypeName}\nObject pos: ({worldLink.WorldX:N0}, {worldLink.WorldY:N0}), d={worldLink.Distance:N0}";
            }

            if (!string.Equals(routeTooltip, lastTooltipText, StringComparison.Ordinal))
            {
                toolTip.Show(routeTooltip, this, e.Location.X + 14, e.Location.Y + 14, 1500);
                lastTooltipText = routeTooltip;
            }

            return;
        }

        if (TryFindNearestWorldInstance(e.Location, out var worldInstance))
        {
            var worldTooltip = $"{worldInstance.PrototypeName}\nWorld object\n({worldInstance.X:N0}, {worldInstance.Y:N0})\nPrototype #{worldInstance.PrototypeIndex}";
            if (!string.Equals(worldTooltip, lastTooltipText, StringComparison.Ordinal))
            {
                toolTip.Show(worldTooltip, this, e.Location.X + 14, e.Location.Y + 14, 1500);
                lastTooltipText = worldTooltip;
            }

            return;
        }

        if (!TryFindNearestDeployment(e.Location, out var deployment))
        {
            if (lastTooltipText is not null)
            {
                toolTip.Hide(this);
                lastTooltipText = null;
            }

            return;
        }

        if (deployment is null)
        {
            return;
        }

        var specialRoute = TryFindSpecialRoute(deployment);
        var positionLabel = ShouldSuppressDeploymentMarker(deployment)
            ? $"raw ({deployment.X:N0}, {deployment.Y:N0})"
            : $"({deployment.X:N0}, {deployment.Y:N0})";
        var tooltip = $"{deployment.UnitName}\n{deployment.Category}\n{positionLabel}";
        if (specialRoute is not null)
        {
            tooltip = $"{tooltip}\nSpecial route: {specialRoute.Points.Count} pts";
            if (ShouldSuppressDeploymentMarker(deployment))
            {
                tooltip = $"{tooltip}\nDesktop marker suppressed; use route nodes";
            }
        }

        if (!string.Equals(tooltip, lastTooltipText, StringComparison.Ordinal))
        {
            toolTip.Show(tooltip, this, e.Location.X + 14, e.Location.Y + 14, 1500);
            lastTooltipText = tooltip;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        toolTip.Hide(this);
        lastTooltipText = null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (TryFindNearestRouteHit(e.Location, out var route, out _))
        {
            var routeDeployment = deployments.FirstOrDefault(item => item.RecordOffset == route.DeploymentRecordOffset);
            if (routeDeployment is not null)
            {
                SelectDeployment(routeDeployment);
                DeploymentSelected?.Invoke(this, new ScenarioDeploymentEventArgs(routeDeployment));
            }

            return;
        }

        if (TryFindNearestWorldInstance(e.Location, out var worldInstance))
        {
            SelectWorldInstance(worldInstance);
            WorldInstanceSelected?.Invoke(this, new WorldInstanceEventArgs(worldInstance));
            return;
        }

        if (!TryFindNearestDeployment(e.Location, out var deployment))
        {
            if (selectedDeploymentRecordOffset.HasValue || selectedWorldInstanceOffset.HasValue)
            {
                ClearSelection();
                SelectionCleared?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (deployment is null)
        {
            if (selectedDeploymentRecordOffset.HasValue || selectedWorldInstanceOffset.HasValue)
            {
                ClearSelection();
                SelectionCleared?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        SelectDeployment(deployment);
        DeploymentSelected?.Invoke(this, new ScenarioDeploymentEventArgs(deployment));
    }

    private void DrawEmptyState(Graphics graphics)
    {
        using var brush = new SolidBrush(Color.FromArgb(180, ForeColor));
        using var font = new Font(Font.FontFamily, 11f, FontStyle.Regular);
        var text = "No map image available";
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(
            text,
            font,
            brush,
            (ClientSize.Width - size.Width) / 2f,
            (ClientSize.Height - size.Height) / 2f);
    }

    private static void DrawWorldBorder(Graphics graphics, RectangleF mapRectangle)
    {
        using var borderPen = new Pen(Color.FromArgb(180, 235, 235, 235), 1.5f);
        graphics.DrawRectangle(borderPen, Rectangle.Round(mapRectangle));
    }

    private void DrawContours(Graphics graphics, RectangleF mapRectangle)
    {
        if (!showContours || contoursImage is null)
        {
            return;
        }

        using var attributes = new ImageAttributes();
        var matrix = new ColorMatrix
        {
            Matrix33 = 0.34f,
        };
        attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        graphics.DrawImage(
            contoursImage,
            Rectangle.Round(mapRectangle),
            0,
            0,
            contoursImage.Width,
            contoursImage.Height,
            GraphicsUnit.Pixel,
            attributes);
    }

    private void DrawSpecialRoutes(Graphics graphics, RectangleF mapRectangle, float scaleX, float scaleY)
    {
        if (specialRoutes.Count == 0)
        {
            return;
        }

        using var labelFont = new Font(Font.FontFamily, 7f, FontStyle.Bold, GraphicsUnit.Point);
        using var labelBrush = new SolidBrush(Color.FromArgb(240, 16, 20, 24));
        using var haloBrush = new SolidBrush(Color.FromArgb(215, 255, 248, 210));
        using var startBrush = new SolidBrush(Color.FromArgb(245, 96, 238, 206));
        using var endBrush = new SolidBrush(Color.FromArgb(245, 255, 196, 110));

        var selectedRoute = selectedDeploymentRecordOffset.HasValue && specialRoutesByOffset.TryGetValue(selectedDeploymentRecordOffset.Value, out var selectedSpecialRoute)
            ? selectedSpecialRoute
            : null;
        foreach (var route in specialRoutes)
        {
            if (route.Points.Count == 0)
            {
                continue;
            }

            var isSelected = selectedRoute is not null
                && selectedRoute.DeploymentRecordOffset == route.DeploymentRecordOffset;
            var points = route.Points
                .Select(point => ToMapPoint(mapRectangle, scaleX, scaleY, point.X, point.Y))
                .ToArray();

            using var routePen = new Pen(
                isSelected ? Color.FromArgb(255, 52, 216, 170) : Color.FromArgb(220, 245, 210, 84),
                isSelected ? 3f : 2f)
            {
                DashStyle = isSelected ? DashStyle.Solid : DashStyle.Dash,
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            using var fillBrush = new SolidBrush(
                isSelected ? Color.FromArgb(250, 52, 216, 170) : Color.FromArgb(235, 255, 236, 168));
            using var outlinePen = new Pen(
                isSelected ? Color.FromArgb(255, 230, 255, 250) : Color.FromArgb(235, 88, 76, 18),
                isSelected ? 1.8f : 1.2f);

            if (points.Length > 1)
            {
                graphics.DrawLines(routePen, points);
            }

            for (var index = 0; index < points.Length; index++)
            {
                var point = points[index];
                var isStart = index == 0;
                var isEnd = index == points.Length - 1;
                var radius = isSelected && isStart ? RouteNodeRadius + 1f : RouteNodeRadius;
                var nodeBrush = isStart
                    ? startBrush
                    : isEnd
                        ? endBrush
                        : fillBrush;
                graphics.FillEllipse(nodeBrush, point.X - radius, point.Y - radius, radius * 2f, radius * 2f);
                graphics.DrawEllipse(outlinePen, point.X - radius, point.Y - radius, radius * 2f, radius * 2f);

                var shouldDrawLabel = isSelected || points.Length <= 8 || isStart || isEnd;
                if (shouldDrawLabel)
                {
                    var label = isStart
                        ? "S"
                        : isEnd
                            ? "E"
                            : (index + 1).ToString();
                    var labelSize = graphics.MeasureString(label, labelFont);
                    var labelX = point.X + radius + 3f;
                    var labelY = point.Y - (labelSize.Height / 2f);
                    graphics.FillRectangle(
                        haloBrush,
                        labelX - 1f,
                        labelY,
                        labelSize.Width + 2f,
                        labelSize.Height);
                    graphics.DrawString(label, labelFont, labelBrush, labelX, labelY);
                }
            }
        }
    }

    private void DrawWorldObjects(Graphics graphics, RectangleF mapRectangle, float scaleX, float scaleY)
    {
        if (worldInstances.Count == 0)
        {
            return;
        }

        HashSet<int>? highlightedOffsets = null;
        if (selectedDeploymentRecordOffset.HasValue
            && specialRoutesByOffset.TryGetValue(selectedDeploymentRecordOffset.Value, out var selectedRoute)
            && selectedRoute.WorldPointLinks.Count > 0)
        {
            highlightedOffsets = selectedRoute.WorldPointLinks
                .Where(item => item.WorldInstanceOffset >= 0)
                .Select(item => item.WorldInstanceOffset)
                .ToHashSet();
        }

        using var regularBrush = new SolidBrush(Color.FromArgb(104, 210, 226, 240));
        using var regularPen = new Pen(Color.FromArgb(120, 66, 94, 116), 0.8f);
        using var highlightedBrush = new SolidBrush(Color.FromArgb(232, 76, 221, 183));
        using var highlightedPen = new Pen(Color.FromArgb(245, 236, 255, 248), 1.3f);
        using var selectedBrush = new SolidBrush(Color.FromArgb(250, 255, 214, 102));
        using var selectedPen = new Pen(Color.FromArgb(255, 255, 249, 230), 1.6f);
        foreach (var item in worldInstances)
        {
            var center = ToMapPoint(mapRectangle, scaleX, scaleY, item.X, item.Y);
            var isSelected = selectedWorldInstanceOffset.HasValue && item.Offset == selectedWorldInstanceOffset.Value;
            var isHighlighted = isSelected || highlightedOffsets?.Contains(item.Offset) == true;
            var radius = isSelected
                ? WorldObjectRadius + 2.2f
                : isHighlighted
                    ? WorldObjectRadius + 1.6f
                    : WorldObjectRadius;
            var brush = isSelected
                ? selectedBrush
                : isHighlighted
                    ? highlightedBrush
                    : regularBrush;
            var pen = isSelected
                ? selectedPen
                : isHighlighted
                    ? highlightedPen
                    : regularPen;
            graphics.FillEllipse(brush, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
            graphics.DrawEllipse(pen, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        }
    }

    private void DrawDeployments(Graphics graphics, RectangleF mapRectangle, float scaleX, float scaleY)
    {
        foreach (var deployment in deployments)
        {
            if (ShouldSuppressDeploymentMarker(deployment))
            {
                continue;
            }

            var center = ToMapPoint(mapRectangle, scaleX, scaleY, deployment.X, deployment.Y);
            var isSelected = selectedDeploymentRecordOffset.HasValue && deployment.RecordOffset == selectedDeploymentRecordOffset.Value;
            var radius = isSelected ? MarkerRadius + 2f : MarkerRadius;
            var color = GetMarkerColor(deployment);
            using var fillBrush = new SolidBrush(Color.FromArgb(225, color));
            using var outlinePen = new Pen(Color.FromArgb(230, Color.WhiteSmoke), isSelected ? 2f : 1f);
            using var directionPen = new Pen(Color.FromArgb(220, color), 2f);

            graphics.FillEllipse(fillBrush, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
            graphics.DrawEllipse(outlinePen, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);

            var directionLength = radius * 2.2f;
            var dx = MathF.Cos(deployment.AngleRadians) * directionLength;
            var dy = MathF.Sin(deployment.AngleRadians) * directionLength;
            graphics.DrawLine(directionPen, center, new PointF(center.X + dx, center.Y + dy));
        }
    }

    private bool TryGetMapLayout(out RectangleF mapRectangle, out float scaleX, out float scaleY)
    {
        mapRectangle = RectangleF.Empty;
        scaleX = 1f;
        scaleY = 1f;

        var sourceWidth = previewImage?.Width ?? contoursImage?.Width ?? worldWidth ?? 0;
        var sourceHeight = previewImage?.Height ?? contoursImage?.Height ?? worldHeight ?? 0;
        if (sourceWidth <= 0 || sourceHeight <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return false;
        }

        const float padding = 16f;
        var availableWidth = Math.Max(1f, ClientSize.Width - padding * 2f);
        var availableHeight = Math.Max(1f, ClientSize.Height - padding * 2f);
        var fitScale = Math.Min(availableWidth / sourceWidth, availableHeight / sourceHeight);
        var drawWidth = sourceWidth * fitScale;
        var drawHeight = sourceHeight * fitScale;
        var left = (ClientSize.Width - drawWidth) / 2f;
        var top = (ClientSize.Height - drawHeight) / 2f;
        mapRectangle = new RectangleF(left, top, drawWidth, drawHeight);

        var worldBasisWidth = worldWidth ?? sourceWidth;
        var worldBasisHeight = worldHeight ?? sourceHeight;
        scaleX = drawWidth / worldBasisWidth;
        scaleY = drawHeight / worldBasisHeight;
        return true;
    }

    private static PointF ToMapPoint(RectangleF mapRectangle, float scaleX, float scaleY, float worldX, float worldY)
    {
        return new PointF(
            mapRectangle.Left + worldX * scaleX,
            mapRectangle.Top + worldY * scaleY);
    }

    private bool TryFindNearestDeployment(Point point, out ScenarioDeployment? deployment)
    {
        deployment = null;
        if (!TryGetMapLayout(out var mapRectangle, out var scaleX, out var scaleY) || deployments.Count == 0)
        {
            return false;
        }

        ScenarioDeployment? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var item in deployments)
        {
            if (ShouldSuppressDeploymentMarker(item))
            {
                continue;
            }

            var markerX = mapRectangle.Left + item.X * scaleX;
            var markerY = mapRectangle.Top + item.Y * scaleY;
            var dx = markerX - point.X;
            var dy = markerY - point.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= 14f && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = item;
            }
        }

        deployment = nearest;
        return nearest is not null;
    }

    private bool TryFindNearestWorldInstance(Point point, out WorldInstanceEntry worldInstance)
    {
        worldInstance = null!;
        if (!TryGetMapLayout(out var mapRectangle, out var scaleX, out var scaleY) || worldInstances.Count == 0)
        {
            return false;
        }

        WorldInstanceEntry? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var item in worldInstances)
        {
            var marker = ToMapPoint(mapRectangle, scaleX, scaleY, item.X, item.Y);
            var dx = marker.X - point.X;
            var dy = marker.Y - point.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= 10f && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = item;
            }
        }

        if (nearest is null)
        {
            return false;
        }

        worldInstance = nearest;
        return true;
    }

    private bool TryFindNearestRouteNode(Point point, out ScenarioSpecialRecordCandidate route, out int pointIndex)
    {
        route = null!;
        pointIndex = -1;
        if (!TryGetMapLayout(out var mapRectangle, out var scaleX, out var scaleY) || specialRoutes.Count == 0)
        {
            return false;
        }

        ScenarioSpecialRecordCandidate? nearestRoute = null;
        var nearestIndex = -1;
        var nearestDistance = float.MaxValue;
        foreach (var item in specialRoutes)
        {
            for (var index = 0; index < item.Points.Count; index++)
            {
                var marker = ToMapPoint(mapRectangle, scaleX, scaleY, item.Points[index].X, item.Points[index].Y);
                var dx = marker.X - point.X;
                var dy = marker.Y - point.Y;
                var distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance <= 12f && distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestRoute = item;
                    nearestIndex = index;
                }
            }
        }

        if (nearestRoute is null)
        {
            return false;
        }

        route = nearestRoute;
        pointIndex = nearestIndex;
        return true;
    }

    private bool TryFindNearestRouteHit(Point point, out ScenarioSpecialRecordCandidate route, out RouteHitInfo hitInfo)
    {
        if (TryFindNearestRouteNode(point, out route, out var pointIndex))
        {
            hitInfo = new RouteHitInfo(true, pointIndex);
            return true;
        }

        return TryFindNearestRouteSegment(point, out route, out hitInfo);
    }

    private bool TryFindNearestRouteSegment(Point point, out ScenarioSpecialRecordCandidate route, out RouteHitInfo hitInfo)
    {
        route = null!;
        hitInfo = default;
        if (!TryGetMapLayout(out var mapRectangle, out var scaleX, out var scaleY) || specialRoutes.Count == 0)
        {
            return false;
        }

        ScenarioSpecialRecordCandidate? nearestRoute = null;
        var nearestSegmentIndex = -1;
        var nearestDistance = float.MaxValue;
        foreach (var item in specialRoutes)
        {
            if (item.Points.Count < 2)
            {
                continue;
            }

            for (var index = 0; index < item.Points.Count - 1; index++)
            {
                var start = ToMapPoint(mapRectangle, scaleX, scaleY, item.Points[index].X, item.Points[index].Y);
                var end = ToMapPoint(mapRectangle, scaleX, scaleY, item.Points[index + 1].X, item.Points[index + 1].Y);
                var distance = DistanceToSegment(point, start, end);
                if (distance <= 9f && distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestRoute = item;
                    nearestSegmentIndex = index;
                }
            }
        }

        if (nearestRoute is null)
        {
            return false;
        }

        route = nearestRoute;
        hitInfo = new RouteHitInfo(false, nearestSegmentIndex);
        return true;
    }

    private static float DistanceToSegment(Point point, PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (MathF.Abs(dx) < float.Epsilon && MathF.Abs(dy) < float.Epsilon)
        {
            var pointDx = point.X - start.X;
            var pointDy = point.Y - start.Y;
            return MathF.Sqrt(pointDx * pointDx + pointDy * pointDy);
        }

        var projection = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / ((dx * dx) + (dy * dy));
        projection = Math.Clamp(projection, 0f, 1f);
        var projectedX = start.X + projection * dx;
        var projectedY = start.Y + projection * dy;
        var distanceX = point.X - projectedX;
        var distanceY = point.Y - projectedY;
        return MathF.Sqrt(distanceX * distanceX + distanceY * distanceY);
    }

    private static ScenarioSpecialPointWorldLink? TryGetNearestWorldLink(
        ScenarioSpecialRecordCandidate route,
        RouteHitInfo hitInfo)
    {
        if (route.WorldPointLinks.Count == 0)
        {
            return null;
        }

        if (hitInfo.IsNode)
        {
            return route.WorldPointLinks.FirstOrDefault(item => item.PointIndex == hitInfo.Index);
        }

        return route.WorldPointLinks
            .Where(item => item.PointIndex == hitInfo.Index || item.PointIndex == hitInfo.Index + 1)
            .OrderBy(item => item.Distance)
            .FirstOrDefault();
    }

    private static Color GetMarkerColor(ScenarioDeployment deployment)
    {
        return deployment.GroupByte switch
        {
            0 => Color.FromArgb(76, 139, 245),
            1 => Color.FromArgb(230, 82, 82),
            _ => Color.FromArgb(235, 170, 54),
        };
    }

    private ScenarioSpecialRecordCandidate? TryFindSpecialRoute(ScenarioDeployment deployment)
    {
        return specialRoutesByOffset.TryGetValue(deployment.RecordOffset, out var route)
            ? route
            : null;
    }

    private bool ShouldSuppressDeploymentMarker(ScenarioDeployment deployment)
    {
        var specialRoute = TryFindSpecialRoute(deployment);
        return ScenarioSnapshotFormatter.ShouldSuppressRawDeploymentMarker(deployment, specialRoute);
    }

    private readonly record struct RouteHitInfo(bool IsNode, int Index);
}

internal sealed class ScenarioDeploymentEventArgs : EventArgs
{
    public ScenarioDeploymentEventArgs(ScenarioDeployment? deployment)
    {
        Deployment = deployment;
    }

    public ScenarioDeployment? Deployment { get; }
}

internal sealed class WorldInstanceEventArgs : EventArgs
{
    public WorldInstanceEventArgs(WorldInstanceEntry worldInstance)
    {
        WorldInstance = worldInstance;
    }

    public WorldInstanceEntry WorldInstance { get; }
}
