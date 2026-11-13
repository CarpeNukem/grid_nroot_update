using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace GridNrootUpdate;

/// <summary>
/// The venue's vending machine, opened from its activation zone.
///
/// Everything here is local: nothing is bought, sent or shown to anyone else.
/// </summary>
internal sealed partial class CyberdeckWindow
{
    private const long VendingDispenseMs = 1400;
    private const long VendingThumpShakeMs = 320;

    private static readonly VendingProduct[] VendingProducts =
    [
        new("A1", "Static Pop", "Citrus soda with a crackle that is mostly marketing.", new Vector4(0.31f, 0.90f, 0.87f, 1f)),
        new("A2", "Chrome Cola", "The classic, now with nine percent more shine.", new Vector4(0.86f, 0.24f, 0.30f, 1f)),
        new("B1", "Null Water", "Water, probably. The label is blank on purpose.", new Vector4(0.72f, 0.80f, 0.90f, 1f)),
        new("B2", "Overclock Bar", "Protein, caffeine and a warranty disclaimer.", new Vector4(1.00f, 0.78f, 0.34f, 1f)),
        new("C1", "Glitch Gum", "The flavour changes every chew. Not always for the better.", new Vector4(1.00f, 0.24f, 0.71f, 1f)),
        new("C2", "Synth Ramen", "Add hot water. Lower expectations.", new Vector4(0.95f, 0.55f, 0.25f, 1f)),
    ];

    private static readonly string[] VendingDispenseLines =
    [
        "*KA-CHUNK*",
        "*THUNK*",
        "*CLATTER-CLONK*",
    ];

    private static readonly string[] VendingBonusLines =
    [
        "Two fell out. The machine does not need to know.",
        "It came out cold. Suspiciously cold.",
        "The receipt says THANK YOU, CITIZEN.",
    ];

    private readonly Random vendingRandom = new();
    private bool vendingWindowOpen;
    private bool focusVendingWindow;
    private VendingState vendingState = VendingState.Idle;
    private VendingProduct? vendingProduct;
    private long vendingStateStartedAt;
    private int vendingThumpsNeeded;
    private long vendingShakeUntil;
    private string vendingResultLine = string.Empty;
    private string vendingBonusLine = string.Empty;
    private int vendingDispensedCount;

    public bool IsVendingMachineOpen => vendingWindowOpen;

    public void OpenVendingMachine()
    {
        if (!vendingWindowOpen)
            ResetVending();
        vendingWindowOpen = true;
        focusVendingWindow = true;
    }

    public void CloseVendingMachine()
    {
        vendingWindowOpen = false;
        ResetVending();
    }

    private void ResetVending()
    {
        vendingState = VendingState.Idle;
        vendingProduct = null;
        vendingResultLine = string.Empty;
        vendingBonusLine = string.Empty;
    }

    private void DrawVendingWindow()
    {
        var uiScale = GetUiScale();
        using var fontScale = CyberdeckTheme.PushFontScale(uiScale);
        using var theme = CyberdeckTheme.Push(uiScale);
        ImGui.SetNextWindowSize(new Vector2(380, 560) * uiScale, ImGuiCond.FirstUseEver);
        var (minimumSize, maximumSize) = CyberdeckTheme.ResolveWindowConstraints(
            uiScale,
            new Vector2(330, 480),
            new Vector2(560, 760));
        ImGui.SetNextWindowSizeConstraints(minimumSize, maximumSize);
        if (focusVendingWindow)
        {
            ImGui.SetNextWindowFocus();
            focusVendingWindow = false;
        }

        if (!ImGui.Begin("NEON-VEND 9 // VENDING###grid_vending", ref vendingWindowOpen, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.End();
            return;
        }

        var now = Environment.TickCount64;
        AdvanceVending(now);

        var shake = !config.ReduceMotion && now < vendingShakeUntil
            ? MathF.Sin(now * 0.09f) * 5f * uiScale * ((vendingShakeUntil - now) / (float)VendingThumpShakeMs)
            : 0f;
        if (shake != 0f)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + shake);

        ImGui.BeginGroup();
        ImGui.TextColored(CyberdeckTheme.Palette.Magenta, "NEON-VEND 9");
        ImGui.SameLine();
        ImGui.TextDisabled("// ON THE HOUSE");
        DrawNeonSeparator();
        ImGui.Spacing();

        DrawVendingDisplay(now, uiScale);
        ImGui.Spacing();
        DrawVendingGrid(uiScale);
        ImGui.Spacing();
        DrawNeonSeparator();
        DrawVendingTray(uiScale);
        ImGui.EndGroup();

        ImGui.End();
        if (!vendingWindowOpen)
            ResetVending();
    }

    private void AdvanceVending(long now)
    {
        if (vendingState != VendingState.Dispensing || now - vendingStateStartedAt < VendingDispenseMs)
            return;

        // One in seven sticks on the coil, because it is a vending machine.
        if (vendingRandom.Next(7) == 0)
        {
            vendingState = VendingState.Jammed;
            vendingThumpsNeeded = vendingRandom.Next(1, 4);
            vendingStateStartedAt = now;
            return;
        }

        CompleteVend(now, bonus: vendingRandom.Next(10) == 0);
    }

    private void CompleteVend(long now, bool bonus)
    {
        vendingState = VendingState.Dispensed;
        vendingStateStartedAt = now;
        vendingResultLine = VendingDispenseLines[vendingRandom.Next(VendingDispenseLines.Length)];
        vendingBonusLine = bonus ? VendingBonusLines[vendingRandom.Next(VendingBonusLines.Length)] : string.Empty;
        vendingDispensedCount++;
    }

    /// <summary>The machine's little screen: what it is doing, in its own voice.</summary>
    private void DrawVendingDisplay(long now, float uiScale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        var height = (ImGui.GetTextLineHeight() * 2f) + (18f * uiScale);
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.02f, 0.05f, 0.06f, 0.95f)), 4f * uiScale);
        drawList.AddRect(min, max, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.55f)), 4f * uiScale);

        var (top, bottom, color) = vendingState switch
        {
            VendingState.Dispensing => (
                $"VENDING {vendingProduct?.Slot} ...",
                new string('#', 1 + (int)(Math.Clamp((now - vendingStateStartedAt) / (float)VendingDispenseMs, 0f, 1f) * 18f)),
                CyberdeckTheme.Palette.Cyan),
            VendingState.Jammed => (
                "ERROR 0x9 // ITEM STUCK ON COIL",
                "PERCUSSIVE MAINTENANCE ADVISED",
                CyberdeckTheme.Palette.Amber),
            VendingState.Dispensed => (
                $"{vendingProduct?.Name.ToUpperInvariant()} DISPENSED",
                "COLLECT FROM TRAY BELOW",
                CyberdeckTheme.Palette.Success),
            _ => (
                "SELECT AN ITEM",
                vendingDispensedCount == 0 ? "ALL ITEMS COMPLIMENTARY TONIGHT" : $"SERVED THIS VISIT: {vendingDispensedCount:00}",
                CyberdeckTheme.Palette.Cyan),
        };

        var blink = config.ReduceMotion || vendingState != VendingState.Jammed || (now / 400) % 2 == 0;
        var padding = new Vector2(10f, 8f) * uiScale;
        if (blink)
            drawList.AddText(min + padding, ImGui.GetColorU32(color), top);
        drawList.AddText(
            min + padding + new Vector2(0f, ImGui.GetTextLineHeight() + (2f * uiScale)),
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, 0.65f)),
            bottom);
        ImGui.Dummy(new Vector2(width, height));
    }

    private void DrawVendingGrid(float uiScale)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var cellWidth = (ImGui.GetContentRegionAvail().X - spacing) / 2f;
        var cellHeight = 74f * uiScale;
        var busy = vendingState is VendingState.Dispensing or VendingState.Jammed;
        for (var index = 0; index < VendingProducts.Length; index++)
        {
            if (index % 2 == 1)
                ImGui.SameLine();

            if (DrawVendingCell(VendingProducts[index], new Vector2(cellWidth, cellHeight), busy, uiScale))
            {
                vendingProduct = VendingProducts[index];
                vendingState = VendingState.Dispensing;
                vendingStateStartedAt = Environment.TickCount64;
                vendingResultLine = string.Empty;
                vendingBonusLine = string.Empty;
            }
        }
    }

    private bool DrawVendingCell(VendingProduct product, Vector2 size, bool busy, float uiScale)
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var clicked = ImGui.InvisibleButton($"##vend_{product.Slot}", size) && !busy;
        var hovered = ImGui.IsItemHovered() && !busy;
        if (hovered)
            ImGui.SetTooltip(product.Description);

        var selected = vendingProduct == product && vendingState != VendingState.Idle;
        var drawList = ImGui.GetWindowDrawList();
        var border = selected
            ? CyberdeckTheme.Palette.Magenta
            : hovered
                ? CyberdeckTheme.Palette.Cyan
                : CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.9f);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.PanelRaised, hovered ? 1f : 0.85f)), 4f * uiScale);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), 4f * uiScale, ImDrawFlags.None, hovered || selected ? 2f : 1f);

        // A can in the product's colour.
        var canMin = min + new Vector2(10f, 12f) * uiScale;
        var canMax = canMin + new Vector2(18f, size.Y - (24f * uiScale));
        drawList.AddRectFilled(canMin, canMax, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(product.Color, busy && !selected ? 0.35f : 0.9f)), 3f * uiScale);
        drawList.AddLine(
            new Vector2(canMin.X + (3f * uiScale), canMin.Y + (4f * uiScale)),
            new Vector2(canMin.X + (3f * uiScale), canMax.Y - (4f * uiScale)),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.45f)),
            1.5f * uiScale);

        var textX = canMax.X + (10f * uiScale);
        var textColor = busy && !selected ? CyberdeckTheme.Palette.TextMuted : CyberdeckTheme.Palette.Text;
        drawList.AddText(new Vector2(textX, min.Y + (10f * uiScale)), ImGui.GetColorU32(CyberdeckTheme.Palette.Cyan), product.Slot);
        drawList.AddText(
            new Vector2(textX, min.Y + (10f * uiScale) + ImGui.GetTextLineHeight()),
            ImGui.GetColorU32(textColor),
            EllipsizeToWidth(product.Name, max.X - textX - (6f * uiScale)));
        drawList.AddText(
            new Vector2(textX, min.Y + (10f * uiScale) + (ImGui.GetTextLineHeight() * 2f)),
            ImGui.GetColorU32(CyberdeckTheme.Palette.TextMuted),
            "0 CR");
        return clicked;
    }

    /// <summary>Where the item lands, or where the machine is thumped when it does not.</summary>
    private void DrawVendingTray(float uiScale)
    {
        var width = ImGui.GetContentRegionAvail().X;
        switch (vendingState)
        {
            case VendingState.Jammed:
                ImGui.TextColored(CyberdeckTheme.Palette.Amber, $"{vendingProduct?.Name} is hanging off the coil.");
                ImGui.Spacing();
                using (CyberdeckTheme.PushAccentButton())
                {
                    if (ImGui.Button("THUMP THE MACHINE", new Vector2(width, 38 * uiScale)))
                    {
                        vendingShakeUntil = Environment.TickCount64 + VendingThumpShakeMs;
                        if (--vendingThumpsNeeded <= 0)
                            CompleteVend(Environment.TickCount64, bonus: vendingRandom.Next(3) == 0);
                    }
                }

                break;

            case VendingState.Dispensed:
                ImGui.TextColored(CyberdeckTheme.Palette.Success, $"{vendingResultLine}  {vendingProduct?.Name} drops into the tray.");
                DrawMutedWrapped(string.IsNullOrEmpty(vendingBonusLine) ? vendingProduct?.Description ?? string.Empty : vendingBonusLine);
                ImGui.Spacing();
                using (CyberdeckTheme.PushAccentButton())
                {
                    if (ImGui.Button("TAKE IT", new Vector2(width, 38 * uiScale)))
                        ResetVending();
                }

                break;

            case VendingState.Dispensing:
                DrawMutedWrapped("The coil turns...");
                break;

            default:
                DrawMutedWrapped("Pick a slot. Snacks are on the house while you are at The Grid.");
                break;
        }
    }

    private enum VendingState
    {
        Idle,
        Dispensing,
        Jammed,
        Dispensed,
    }

    private sealed record VendingProduct(string Slot, string Name, string Description, Vector4 Color);
}
