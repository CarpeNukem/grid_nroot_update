using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace GridNrootUpdate;

/// <summary>
/// Small, theme-agnostic cyberdeck widgets. Colors are supplied by the caller so
/// this file can be used with either the current inline palette or CyberdeckTheme.
/// </summary>
internal static class CyberdeckWidgets
{
    /// <summary>
    /// Draws a compact segmented activity indicator and advances the cursor by its diameter.
    /// Reduced-motion mode renders the same indicator without rotating the highlight.
    /// </summary>
    public static void DrawSegmentedSpinner(
        bool reduceMotion,
        Vector4 accentColor,
        float diameter = 18.0f,
        float thickness = 2.0f,
        int segmentCount = 12)
    {
        diameter = MathF.Max(6.0f, diameter);
        thickness = Math.Clamp(thickness, 1.0f, diameter * 0.2f);
        segmentCount = Math.Clamp(segmentCount, 6, 24);

        var origin = ImGui.GetCursorScreenPos();
        var center = origin + new Vector2(diameter * 0.5f);
        var drawList = ImGui.GetWindowDrawList();
        var outerRadius = MathF.Max(1.0f, (diameter - thickness) * 0.5f);
        var innerRadius = outerRadius * 0.58f;
        var head = reduceMotion
            ? 0.0f
            : (float)(ImGui.GetTime() * segmentCount * 0.85) % segmentCount;

        for (var index = 0; index < segmentCount; index++)
        {
            var angle = ((MathF.PI * 2.0f * index) / segmentCount) - (MathF.PI * 0.5f);
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var alpha = reduceMotion
                ? 0.62f
                : GetTrailAlpha(head, index, segmentCount);

            drawList.AddLine(
                center + (direction * innerRadius),
                center + (direction * outerRadius),
                ImGui.GetColorU32(WithMultipliedAlpha(accentColor, alpha)),
                thickness);
        }

        ImGui.Dummy(new Vector2(diameter));
    }

    /// <summary>
    /// Draws an indeterminate scanner bar. A centered, static signal is used when motion is reduced.
    /// Pass zero for width to fill the currently available content width.
    /// </summary>
    public static void DrawIndeterminateScanner(
        bool reduceMotion,
        Vector4 trackColor,
        Vector4 accentColor,
        float width = 0.0f,
        float height = 7.0f)
    {
        var resolvedWidth = ResolveWidth(width);
        var resolvedHeight = MathF.Max(3.0f, height);
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(resolvedWidth, resolvedHeight);

        DrawScannerAt(
            ImGui.GetWindowDrawList(),
            min,
            max,
            reduceMotion,
            trackColor,
            accentColor);

        ImGui.Dummy(new Vector2(resolvedWidth, resolvedHeight));
    }

    /// <summary>
    /// Draws a label plus either determinate progress or an indeterminate scanner when fraction is null.
    /// The optional value text replaces the generated percentage/right-side activity label.
    /// Pass zero for width to fill the currently available content width.
    /// </summary>
    public static void DrawLabeledProgress(
        string label,
        float? fraction,
        bool reduceMotion,
        Vector4 trackColor,
        Vector4 accentColor,
        Vector4 textColor,
        Vector4 mutedColor,
        string? valueText = null,
        float width = 0.0f,
        float height = 8.0f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var resolvedWidth = ResolveWidth(width);
        var resolvedHeight = MathF.Max(3.0f, height);
        var fractionValue = fraction.HasValue && float.IsFinite(fraction.Value)
            ? Math.Clamp(fraction.Value, 0.0f, 1.0f)
            : (float?)null;
        var rightText = valueText ?? (fractionValue.HasValue ? $"{fractionValue.Value * 100.0f:0}%" : "ACTIVE");
        var labelSize = ImGui.CalcTextSize(label);
        var rightSize = ImGui.CalcTextSize(rightText);
        var headerHeight = MathF.Max(labelSize.Y, rightSize.Y);
        var headerGap = MathF.Max(3.0f, ImGui.GetStyle().ItemInnerSpacing.Y);
        var origin = ImGui.GetCursorScreenPos();
        var barMin = origin + new Vector2(0.0f, headerHeight + headerGap);
        var barMax = barMin + new Vector2(resolvedWidth, resolvedHeight);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddText(origin, ImGui.GetColorU32(textColor), label);
        if (rightSize.X + labelSize.X + 8.0f <= resolvedWidth)
        {
            drawList.AddText(
                new Vector2(origin.X + resolvedWidth - rightSize.X, origin.Y),
                ImGui.GetColorU32(mutedColor),
                rightText);
        }

        if (fractionValue.HasValue)
        {
            DrawTrack(drawList, barMin, barMax, trackColor, accentColor);
            var fillWidth = (barMax.X - barMin.X) * fractionValue.Value;
            if (fillWidth > 0.0f)
            {
                var fillMax = new Vector2(barMin.X + fillWidth, barMax.Y);
                drawList.AddRectFilled(
                    barMin,
                    fillMax,
                    ImGui.GetColorU32(accentColor),
                    resolvedHeight * 0.22f);
            }
        }
        else
        {
            DrawScannerAt(drawList, barMin, barMax, reduceMotion, trackColor, accentColor);
        }

        ImGui.Dummy(new Vector2(resolvedWidth, headerHeight + headerGap + resolvedHeight));
    }

    /// <summary>
    /// Draws a non-interactive status pill with a small status signal and advances the cursor.
    /// </summary>
    public static void DrawStatusChip(
        string label,
        Vector4 statusColor,
        Vector4 textColor,
        float uiScale = 1.0f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        uiScale = MathF.Max(0.5f, uiScale);
        var horizontalPadding = 8.0f * uiScale;
        var verticalPadding = 3.0f * uiScale;
        var signalRadius = 2.5f * uiScale;
        var signalGap = 6.0f * uiScale;
        var chromeWidth = (horizontalPadding * 2.0f) + (signalRadius * 2.0f) + signalGap;
        var maxTextWidth = MathF.Max(12.0f, ImGui.GetContentRegionAvail().X - chromeWidth);
        var displayLabel = Ellipsize(label, maxTextWidth);
        var textSize = ImGui.CalcTextSize(displayLabel);
        var size = new Vector2(
            textSize.X + chromeWidth,
            MathF.Max(textSize.Y + (verticalPadding * 2.0f), 18.0f * uiScale));
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var rounding = size.Y * 0.5f;
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(WithAlpha(statusColor, 0.13f)),
            rounding);
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(WithAlpha(statusColor, 0.72f)),
            rounding,
            ImDrawFlags.None,
            MathF.Max(1.0f, uiScale));

        var signalCenter = new Vector2(
            min.X + horizontalPadding + signalRadius,
            min.Y + (size.Y * 0.5f));
        drawList.AddCircleFilled(signalCenter, signalRadius, ImGui.GetColorU32(statusColor));

        var textPos = new Vector2(
            signalCenter.X + signalRadius + signalGap,
            min.Y + ((size.Y - textSize.Y) * 0.5f));
        drawList.AddText(textPos, ImGui.GetColorU32(textColor), displayLabel);

        ImGui.Dummy(size);
        if (!string.Equals(displayLabel, label, StringComparison.Ordinal) && ImGui.IsItemHovered())
            ImGui.SetTooltip(label);
    }

    /// <summary>
    /// Draws a button inside Dear ImGui's disabled scope and returns true only when enabled and clicked.
    /// </summary>
    public static bool DrawActionButton(string label, bool disabled, Vector2 size = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        ImGui.BeginDisabled(disabled);
        try
        {
            return size == default
                ? ImGui.Button(label)
                : ImGui.Button(label, size);
        }
        finally
        {
            ImGui.EndDisabled();
        }
    }

    private static void DrawScannerAt(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        bool reduceMotion,
        Vector4 trackColor,
        Vector4 accentColor)
    {
        DrawTrack(drawList, min, max, trackColor, accentColor);

        var width = max.X - min.X;
        var height = max.Y - min.Y;
        var signalWidth = MathF.Min(width, MathF.Max(height * 3.0f, width * 0.28f));
        var travel = MathF.Max(0.0f, width - signalWidth);
        var phase = reduceMotion
            ? 0.5f
            : 0.5f + (0.5f * MathF.Sin((float)ImGui.GetTime() * MathF.PI * 1.6f));
        var signalMin = new Vector2(min.X + (travel * phase), min.Y);
        var signalMax = new Vector2(signalMin.X + signalWidth, max.Y);

        drawList.AddRectFilled(
            signalMin,
            signalMax,
            ImGui.GetColorU32(reduceMotion ? WithMultipliedAlpha(accentColor, 0.72f) : accentColor),
            height * 0.22f);
    }

    private static void DrawTrack(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        Vector4 trackColor,
        Vector4 accentColor)
    {
        var height = max.Y - min.Y;
        var rounding = height * 0.22f;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(trackColor), rounding);
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(WithMultipliedAlpha(accentColor, 0.42f)),
            rounding,
            ImDrawFlags.None,
            1.0f);
    }

    private static float ResolveWidth(float requestedWidth)
    {
        if (requestedWidth > 0.0f && float.IsFinite(requestedWidth))
            return requestedWidth;

        return MathF.Max(1.0f, ImGui.GetContentRegionAvail().X);
    }

    private static string Ellipsize(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        const string ellipsis = "...";
        if (ImGui.CalcTextSize(ellipsis).X >= maxWidth)
            return ellipsis;

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid] + ellipsis).X <= maxWidth)
                low = mid;
            else
                high = mid - 1;
        }

        return text[..low].TrimEnd() + ellipsis;
    }

    private static float GetTrailAlpha(float head, int index, int segmentCount)
    {
        var distance = (head - index + segmentCount) % segmentCount;
        var trailLength = segmentCount * 0.72f;
        var intensity = 1.0f - Math.Clamp(distance / trailLength, 0.0f, 1.0f);
        return 0.16f + (intensity * 0.84f);
    }

    private static Vector4 WithAlpha(Vector4 color, float alpha)
        => new(color.X, color.Y, color.Z, Math.Clamp(alpha, 0.0f, 1.0f));

    private static Vector4 WithMultipliedAlpha(Vector4 color, float multiplier)
        => new(color.X, color.Y, color.Z, Math.Clamp(color.W * multiplier, 0.0f, 1.0f));
}
