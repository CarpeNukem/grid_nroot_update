using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace GridNrootUpdate;

public enum CyberdeckThemeId
{
    Grid,
    BlackIce,
    Ghost,
    Terminal,
    Redline,
    Custom,
}

/// <summary>
/// Shared cyberdeck palette and scoped ImGui styling.
/// Push the theme before <see cref="ImGui.Begin(string)"/> so window and title colors are applied.
/// </summary>
internal static class CyberdeckTheme
{
    private const int ThemeColorCount = 38;
    private const int ThemeStyleVarCount = 16;

    private readonly record struct ThemeDefinition(
        Vector4 Background,
        Vector4 Panel,
        Vector4 PanelRaised,
        Vector4 Primary,
        Vector4 Secondary,
        Vector4 Text,
        Vector4 TextMuted,
        Vector4 Border);

    private static readonly ThemeDefinition GridTheme = new(
        Rgba(0x07, 0x10, 0x17), Rgba(0x0B, 0x18, 0x20), Rgba(0x10, 0x26, 0x30),
        Rgba(0x31, 0xE6, 0xDF), Rgba(0xFF, 0x3E, 0xB5), Rgba(0xD5, 0xFB, 0xF7),
        Rgba(0x79, 0xAA, 0xA7), Rgba(0x23, 0x66, 0x69));
    private static readonly ThemeDefinition BlackIceTheme = new(
        Rgba(0x04, 0x09, 0x10), Rgba(0x08, 0x13, 0x1D), Rgba(0x10, 0x22, 0x30),
        Rgba(0x91, 0xE5, 0xFF), Rgba(0xE1, 0xF7, 0xFF), Rgba(0xE4, 0xF8, 0xFF),
        Rgba(0x7C, 0xA6, 0xB5), Rgba(0x2A, 0x6F, 0x86));
    private static readonly ThemeDefinition GhostTheme = new(
        Rgba(0x08, 0x07, 0x15), Rgba(0x10, 0x0E, 0x24), Rgba(0x1C, 0x18, 0x38),
        Rgba(0x9B, 0xE8, 0xFF), Rgba(0xB0, 0x61, 0xFF), Rgba(0xEC, 0xE7, 0xFF),
        Rgba(0x9A, 0x8F, 0xBA), Rgba(0x4E, 0x3E, 0x78));
    private static readonly ThemeDefinition TerminalTheme = new(
        Rgba(0x00, 0x00, 0x00), Rgba(0x00, 0x00, 0x00), Rgba(0x03, 0x0A, 0x05),
        Rgba(0x56, 0xF5, 0x8A), Rgba(0xA0, 0xFF, 0xB9), Rgba(0xCC, 0xFF, 0xDA),
        Rgba(0x68, 0xA6, 0x79), Rgba(0x1D, 0x6D, 0x3B));
    private static readonly ThemeDefinition RedlineTheme = new(
        Rgba(0x07, 0x04, 0x06), Rgba(0x12, 0x08, 0x0B), Rgba(0x25, 0x0E, 0x15),
        Rgba(0xFF, 0x35, 0x59), Rgba(0xFF, 0x75, 0x3D), Rgba(0xFF, 0xE9, 0xEC),
        Rgba(0xB7, 0x7E, 0x88), Rgba(0x78, 0x1B, 0x30));

    private static ThemeDefinition current = GridTheme;

    internal static class Palette
    {
        public static Vector4 Background => current.Background;
        public static Vector4 Panel => current.Panel;
        public static Vector4 PanelRaised => current.PanelRaised;
        public static Vector4 Cyan => current.Primary;
        public static Vector4 Magenta => current.Secondary;
        public static Vector4 Amber => Rgba(0xFF, 0xC8, 0x57);
        public static Vector4 Success => Rgba(0x4F, 0xE3, 0x8A);
        public static Vector4 Error => Rgba(0xFF, 0x5C, 0x6C);
        public static Vector4 Text => current.Text;
        public static Vector4 TextMuted => current.TextMuted;
        public static Vector4 Border => current.Border;
    }

    public static void SetTheme(CyberdeckThemeId theme)
        => current = GetDefinition(theme);

    public static void SetCustomTheme(Vector4 background, Vector4 primary, Vector4 secondary, Vector4 text)
    {
        background.W = 1f;
        primary.W = 1f;
        secondary.W = 1f;
        text.W = 1f;
        current = new ThemeDefinition(
            background,
            Mix(background, Vector4.One, 0.03f),
            Mix(background, Vector4.One, 0.07f),
            primary,
            secondary,
            text,
            Mix(text, background, 0.55f),
            Mix(background, primary, 0.40f));
    }

    public static string GetThemeName(CyberdeckThemeId theme)
        => theme switch
        {
            CyberdeckThemeId.Grid => "Grid",
            CyberdeckThemeId.BlackIce => "Black Ice",
            CyberdeckThemeId.Ghost => "Ghost",
            CyberdeckThemeId.Terminal => "Terminal",
            CyberdeckThemeId.Redline => "Redline",
            CyberdeckThemeId.Custom => "Custom",
            _ => "Grid",
        };

    public static string GetThemeDescription(CyberdeckThemeId theme)
        => theme switch
        {
            CyberdeckThemeId.Grid => "Original cyan and magenta Cyberdeck palette.",
            CyberdeckThemeId.BlackIce => "Cold blue, ice white and deep navy.",
            CyberdeckThemeId.Ghost => "Violet shadows with pale spectral cyan.",
            CyberdeckThemeId.Terminal => "Monochrome green on terminal black.",
            CyberdeckThemeId.Redline => "Black, aggressive red and hot-orange accents.",
            CyberdeckThemeId.Custom => "Your four-color palette with automatically derived surfaces and states.",
            _ => "Original cyan and magenta Cyberdeck palette.",
        };

    public static (Vector4 Background, Vector4 Primary, Vector4 Secondary, Vector4 Text) GetThemePreview(
        CyberdeckThemeId theme)
    {
        var definition = GetDefinition(theme);
        return (definition.Background, definition.Primary, definition.Secondary, definition.Text);
    }

    private static ThemeDefinition GetDefinition(CyberdeckThemeId theme)
        => theme switch
        {
            CyberdeckThemeId.BlackIce => BlackIceTheme,
            CyberdeckThemeId.Ghost => GhostTheme,
            CyberdeckThemeId.Terminal => TerminalTheme,
            CyberdeckThemeId.Redline => RedlineTheme,
            _ => GridTheme,
        };

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
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Mix(Palette.PanelRaised, Palette.Cyan, 0.20f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Mix(Palette.PanelRaised, Palette.Cyan, 0.34f));

        ImGui.PushStyleColor(ImGuiCol.TitleBg, Mix(Palette.Background, Palette.Cyan, 0.05f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Mix(Palette.Background, Palette.Cyan, 0.14f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, WithAlpha(Palette.Background, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.MenuBarBg, Palette.Panel);

        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Palette.Background);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Mix(Palette.PanelRaised, Palette.Cyan, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Mix(Palette.PanelRaised, Palette.Cyan, 0.50f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Palette.Cyan);

        ImGui.PushStyleColor(ImGuiCol.CheckMark, Palette.Cyan);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Mix(Palette.PanelRaised, Palette.Cyan, 0.68f));
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Palette.Cyan);

        ImGui.PushStyleColor(ImGuiCol.Button, Mix(Palette.PanelRaised, Palette.Cyan, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Mix(Palette.PanelRaised, Palette.Cyan, 0.30f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Mix(Palette.PanelRaised, Palette.Cyan, 0.48f));

        ImGui.PushStyleColor(ImGuiCol.Header, Mix(Palette.PanelRaised, Palette.Cyan, 0.14f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Mix(Palette.PanelRaised, Palette.Cyan, 0.30f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Mix(Palette.PanelRaised, Palette.Cyan, 0.44f));

        ImGui.PushStyleColor(ImGuiCol.Separator, WithAlpha(Palette.Border, 0.82f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, WithAlpha(Palette.Cyan, 0.78f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, Palette.Cyan);

        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, WithAlpha(Palette.Cyan, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, WithAlpha(Palette.Cyan, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, WithAlpha(Palette.Cyan, 0.85f));

        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, Mix(Palette.PanelRaised, Palette.Cyan, 0.10f));
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, WithAlpha(Palette.Border, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, WithAlpha(Palette.Border, 0.48f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, WithAlpha(Palette.Panel, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, WithAlpha(Mix(Palette.Panel, Palette.Cyan, 0.07f), 0.58f));

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
        ImGui.PushStyleColor(ImGuiCol.Button, Mix(Palette.PanelRaised, Palette.Cyan, 0.32f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Mix(Palette.PanelRaised, Palette.Cyan, 0.54f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Mix(Palette.PanelRaised, Palette.Cyan, 0.70f));
        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Text);
        return new AccentScope(4);
    }

    /// <summary>
    /// Applies the font scale before a top-level window begins so native title
    /// controls and their hit boxes are laid out in the same coordinate space.
    /// </summary>
    public static FontScaleScope PushFontScale(float uiScale)
    {
        var io = ImGui.GetIO();
        var previousScale = io.FontGlobalScale;
        io.FontGlobalScale = previousScale * Math.Clamp(uiScale, 0.75f, 2.5f);
        ImGui.PushFont(ImGui.GetFont());
        return new FontScaleScope(previousScale);
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

    private static Vector4 Mix(Vector4 from, Vector4 to, float amount)
        => Vector4.Lerp(from, to, Math.Clamp(amount, 0f, 1f));

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

    public readonly ref struct FontScaleScope
    {
        private readonly float previousScale;

        internal FontScaleScope(float previousScale)
            => this.previousScale = previousScale;

        public void Dispose()
        {
            ImGui.GetIO().FontGlobalScale = previousScale;
            ImGui.PopFont();
        }
    }
}
