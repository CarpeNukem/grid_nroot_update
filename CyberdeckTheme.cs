using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace GridNrootUpdate;

/// <summary>
/// Shared cyberdeck palette and scoped ImGui styling.
/// Push the theme before <see cref="ImGui.Begin(string)"/> so window and title colors are applied.
/// </summary>
internal static class CyberdeckTheme
{
    private const int ThemeColorCount = 38;
    private const int ThemeStyleVarCount = 16;

    internal static class Palette
    {
        public static readonly Vector4 Background = Rgba(0x07, 0x10, 0x17);
        public static readonly Vector4 Panel = Rgba(0x0B, 0x18, 0x20);
        public static readonly Vector4 PanelRaised = Rgba(0x10, 0x26, 0x30);
        public static readonly Vector4 Cyan = Rgba(0x31, 0xE6, 0xDF);
        public static readonly Vector4 Magenta = Rgba(0xFF, 0x3E, 0xB5);
        public static readonly Vector4 Amber = Rgba(0xFF, 0xC8, 0x57);
        public static readonly Vector4 Success = Rgba(0x4F, 0xE3, 0x8A);
        public static readonly Vector4 Error = Rgba(0xFF, 0x5C, 0x6C);
        public static readonly Vector4 Text = Rgba(0xD5, 0xFB, 0xF7);
        public static readonly Vector4 TextMuted = Rgba(0x79, 0xAA, 0xA7);
        public static readonly Vector4 Border = Rgba(0x23, 0x66, 0x69);

        private static Vector4 Rgba(byte red, byte green, byte blue, byte alpha = 0xFF)
            => new(red / 255f, green / 255f, blue / 255f, alpha / 255f);
    }

    /// <summary>
    /// Applies the complete theme and returns a scope which restores the previous ImGui style.
    /// </summary>
    public static ThemeScope Push(float uiScale = 1f)
    {
        uiScale = Math.Clamp(uiScale, 0.75f, 2.5f);

        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Palette.TextMuted);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Palette.Background);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, WithAlpha(Palette.Panel, 0.94f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, WithAlpha(Palette.PanelRaised, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Border, WithAlpha(Palette.Border, 0.90f));
        ImGui.PushStyleColor(ImGuiCol.BorderShadow, new Vector4(0f, 0f, 0f, 0.35f));

        ImGui.PushStyleColor(ImGuiCol.FrameBg, WithAlpha(Palette.PanelRaised, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Rgba(0x14, 0x3A, 0x43));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Rgba(0x19, 0x50, 0x58));

        ImGui.PushStyleColor(ImGuiCol.TitleBg, Rgba(0x08, 0x1A, 0x22));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Rgba(0x0C, 0x2C, 0x35));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, WithAlpha(Palette.Background, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.MenuBarBg, Palette.Panel);

        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Rgba(0x06, 0x12, 0x18));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Rgba(0x1A, 0x54, 0x59));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Rgba(0x25, 0x82, 0x83));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Palette.Cyan);

        ImGui.PushStyleColor(ImGuiCol.CheckMark, Palette.Cyan);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Rgba(0x25, 0xA7, 0xA4));
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Palette.Cyan);

        ImGui.PushStyleColor(ImGuiCol.Button, Rgba(0x10, 0x35, 0x3C));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Rgba(0x16, 0x59, 0x61));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Rgba(0x1D, 0x78, 0x7D));

        ImGui.PushStyleColor(ImGuiCol.Header, Rgba(0x10, 0x38, 0x40));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Rgba(0x17, 0x55, 0x5D));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Rgba(0x1B, 0x6B, 0x70));

        ImGui.PushStyleColor(ImGuiCol.Separator, WithAlpha(Palette.Border, 0.82f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, WithAlpha(Palette.Cyan, 0.78f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, Palette.Cyan);

        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, WithAlpha(Palette.Cyan, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, WithAlpha(Palette.Cyan, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, WithAlpha(Palette.Cyan, 0.85f));

        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, Rgba(0x0E, 0x2D, 0x35));
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, WithAlpha(Palette.Border, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, WithAlpha(Palette.Border, 0.48f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, WithAlpha(Palette.Panel, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, Rgba(0x0E, 0x24, 0x2B, 0.58f));

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18f, 16f) * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 5f * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 6f) * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(9f, 8f) * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(7f, 5f) * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(8f, 6f) * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 12f * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, 10f * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 3f * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowTitleAlign, new Vector2(0.03f, 0.5f));

        return new ThemeScope(ThemeColorCount, ThemeStyleVarCount);
    }

    public static AccentScope PushAccentButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Rgba(0x12, 0x59, 0x60));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Rgba(0x1D, 0x83, 0x86));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Rgba(0x20, 0x92, 0x91));
        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Text);
        return new AccentScope(4);
    }

    public static float ResolveUiScale(float configuredScale)
    {
        if (configuredScale > 0)
            return Math.Clamp(configuredScale, 1f, 2f);

        var display = ImGui.GetIO().DisplaySize;
        var maxDimension = MathF.Max(display.X, display.Y);
        var minDimension = MathF.Min(display.X, display.Y);
        if (maxDimension >= 3600 || minDimension >= 1800)
            return 1.5f;
        if (maxDimension >= 2400 || minDimension >= 1300)
            return 1.25f;
        return 1f;
    }

    public static (Vector2 Min, Vector2 Max) ResolveWindowConstraints(
        float uiScale,
        Vector2 logicalMin,
        Vector2 logicalMax)
    {
        uiScale = Math.Clamp(uiScale, 0.75f, 2.5f);
        var minimum = logicalMin * uiScale;
        var maximum = logicalMax * uiScale;
        var display = ImGui.GetIO().DisplaySize;
        if (display.X <= 0 || display.Y <= 0)
            return (minimum, maximum);

        var margin = 32f * uiScale;
        var viewportMaximum = new Vector2(
            MathF.Max(160f, display.X - margin),
            MathF.Max(240f, display.Y - margin));
        maximum = Vector2.Min(maximum, viewportMaximum);
        minimum = Vector2.Min(minimum, maximum);
        return (minimum, maximum);
    }

    public static void DrawSectionHeading(string label)
    {
        ImGui.TextColored(Palette.Cyan, label);
        ImGui.Separator();
    }

    private static Vector4 Rgba(byte red, byte green, byte blue, byte alpha = 0xFF)
        => new(red / 255f, green / 255f, blue / 255f, alpha / 255f);

    private static Vector4 Rgba(byte red, byte green, byte blue, float alpha)
        => new(red / 255f, green / 255f, blue / 255f, alpha);

    public static Vector4 WithAlpha(Vector4 color, float alpha)
        => new(color.X, color.Y, color.Z, alpha);

    public readonly ref struct ThemeScope
    {
        private readonly int colorCount;
        private readonly int styleVarCount;

        internal ThemeScope(int colorCount, int styleVarCount)
        {
            this.colorCount = colorCount;
            this.styleVarCount = styleVarCount;
        }

        public void Dispose()
        {
            ImGui.PopStyleVar(styleVarCount);
            ImGui.PopStyleColor(colorCount);
        }
    }

    public readonly ref struct AccentScope
    {
        private readonly int colorCount;

        internal AccentScope(int colorCount)
            => this.colorCount = colorCount;

        public void Dispose()
            => ImGui.PopStyleColor(colorCount);
    }
}
