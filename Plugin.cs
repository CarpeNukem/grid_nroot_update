using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace GridNrootUpdate;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/thegrid";
    private const byte MaleNonPlayerCharacterCollectionType = 3;
    private const float DefaultMapZoom = 0.44f;
    private const string LifestreamNavigationTooltip = "Click to navigate. Requires Lifestream plugin to work";
    private static readonly float[] ManualUiScales = [1.0f, 1.25f, 1.5f, 1.75f, 2.0f];
    private const string LightlessSyncshellId = "LLS-6AAKEJBAPRB0";
    private const string PlayerSyncSyncshellId = "n_root";
    private static readonly DrinkMenuItem[] DrinkMenu =
    [
        new("Above The Grid", "10 000", "above_the_grid.png", "gin, elderflower cordial, lemon, ChroManticore Ultraviolet",
            "A cold, luminous rooftop cocktail for those who have risen above the city’s noise. Smooth, silver, and electric-blue, Above The Grid tastes like neon reflected through glass and rain from the top floor.",
            "Cool, bright, and elegantly electric. Lemon cuts through soft elderflower first, followed by crisp gin botanicals and a smooth ultraviolet finish with a faint synthetic berry glow."),
        new("Toxic Brat (Rhas' Special)", "15 000", "toxic_brat.png", "vodka, blackberry liqueur, lime, ChroManticore Lime, Ab-Synth rinse",
            "A dangerously sweet neon cocktail glowing in toxic pink and violet. Built with vodka, blackberry, lime, and an Ab-Synth™ rinse, Toxic Brat is playful, sharp, and made for nights that end in chaos.",
            "Sweet, sharp, and dangerously smooth. Neon blackberry and toxic lime hit first with a candy-bright burst, followed by a cold absinthe edge and a clean vodka finish. Dark berry lingers underneath, sweet, electric, and almost addictive."),
        new("Trust Issues", "10 000", "trust_issues.png", "??? / zero alcohol",
            "Presented with far too much confidence for something this clear. Chilled, elegant, and treated like a house secret.",
            "Clean, crisp, alarmingly honest. Notes of nothing, followed by a refreshing finish of betrayal."),
        new("Chornobyl Vice", "5 000", "chornobyl_vice.png", "Cactus Juice, lychee syrup, pear nectar, lemon, Vatnajokull Sparkling / zero alcohol",
            "A neon-green rooftop temptation served with a tiny activation vial and radioactive glow, Chornobyl Vice looks like a containment breach but drinks smooth, floral, and dangerously gentle.",
            "Soft, floral, fruity, and lightly sweet. Lychee and pear bloom first, followed by cool cactus freshness, a bright lemon cut, and a crisp sparkling finish that leaves a clean, almost mineral glow."),
        new("9", "20 000", "nine.png", "vodka, gin, white rum, tequila, blue curacao, lime, lemon, syrup, Vatnajokull Sparkling",
            "The Grid's overloaded house anomaly: a glowing, chaotic cocktail built from exactly nine ingredients.",
            "Bright, sharp, and dangerously drinkable. Citrus cuts through the layered spirits, blue curacao adds sweet orange, and soda gives a clean finish."),
        new("Hurricane Havo", "15 000", "hurricane_havo.png", "tequila, lime, salt",
            "A deceptively simple tequila shot with lime, salt, and a customer-service incident built in, Hurricane Havo is less a cocktail and more proof that the smallest storms can still pack a punch.",
            "Sharp, clean, and immediate. Note: By ordering Hurricane Havo, you consent to a brief, non-lethal physical interaction as part of the serving ritual. The management assures you it is theatrical, consensual, and only emotionally questionable."),
    ];

    private readonly CancellationTokenSource lifetime = new();
    private readonly GitHubReleaseClient github = new();
    private readonly PenumbraIpc penumbra;
    private readonly object modAddedLock = new();
    private readonly Dictionary<string, ISharedImmediateTexture> textures;
    private readonly string textureLoadSource;
    private bool reconcileQueued;
    private bool reconcileRunning;
    private bool mainUiOpen;
    private bool configUiOpen;
    private DeckView selectedView = DeckView.Home;
    private float mapZoom = DefaultMapZoom;
    private uint lastAutoOpenedTerritory;
    private string? transientFeedback;
    private long transientFeedbackUntil;
    private bool modAddedSubscribed;
    private TaskCompletionSource<string>? pendingModAdded;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<PluginService>();
        Config = pluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        Config.GetPrimaryMapping();
        Config.Save();

        penumbra = new PenumbraIpc(pluginInterface);
        (textures, textureLoadSource) = LoadTextures();

        PluginService.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open The Grid cyberdeck app and manage venue sync/update tools.",
            ShowInHelp = true,
        });

        PluginService.ClientState.Login += OnLogin;
        PluginService.ClientState.TerritoryChanged += OnTerritoryChanged;
        PluginService.Framework.Update += OnFrameworkUpdate;
        TrySubscribePenumbraEvents();
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

        QueueReconcile();
    }

    public PluginConfig Config { get; }

    public void Dispose()
    {
        lifetime.Cancel();
        PluginService.Framework.Update -= OnFrameworkUpdate;
        PluginService.ClientState.TerritoryChanged -= OnTerritoryChanged;
        PluginService.ClientState.Login -= OnLogin;
        if (modAddedSubscribed)
            penumbra.UnsubscribeModAdded(OnPenumbraModAdded);
        PluginService.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginService.PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginService.PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginService.Commands.RemoveHandler(CommandName);
        github.Dispose();
        lifetime.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        var split = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var subCommand = split.Length == 0 ? string.Empty : split[0].ToLowerInvariant();

        switch (subCommand)
        {
            case "":
                OpenMainUi();
                break;
            case "update":
                QueueReconcile();
                PluginService.Chat.Print("Update queued. Checking Penumbra and latest The Grid release...", "TheGrid");
                break;
            case "config":
                OpenConfigUi();
                break;
            default:
                PluginService.Chat.PrintError($"Unknown command '{args}'. Use /thegrid, /thegrid update, or /thegrid config.", "TheGrid");
                break;
        }
    }

    private void OpenMainUi()
        => mainUiOpen = true;

    private void TrySubscribePenumbraEvents()
    {
        if (modAddedSubscribed)
            return;

        try
        {
            penumbra.SubscribeModAdded(OnPenumbraModAdded);
            modAddedSubscribed = true;
        }
        catch (Exception ex)
        {
            PluginService.Log.Debug(ex, "Could not subscribe to Penumbra ModAdded yet.");
        }
    }

    private void OpenConfigUi()
        => configUiOpen = true;

    private void DrawUi()
    {
        DrawMainWindow();
        DrawConfigWindow();
    }

    private void DrawMainWindow()
    {
        if (!mainUiOpen)
            return;

        var uiScale = GetUiScale();
        ImGui.SetNextWindowSize(GetInitialWindowSize(uiScale), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("The Grid Cyberdeck", ref mainUiOpen, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.End();
            return;
        }

        PushCyberdeckStyle(uiScale);
        ImGui.SetWindowFontScale(uiScale);
        DrawRooftopBackground();
        if (ImGui.BeginChild("deck_body", new Vector2(0, 0), true))
        {
            var deckMin = ImGui.GetWindowPos();
            var deckMax = deckMin + ImGui.GetWindowSize();
            if (selectedView == DeckView.Home)
                DrawHomeView();
            else
                DrawAppScreen();

            DrawDeckScanline(deckMin, deckMax, uiScale);
        }

        ImGui.EndChild();
        ImGui.SetWindowFontScale(1.0f);
        PopCyberdeckStyle();
        ImGui.End();
    }

    private void DrawHomeView()
    {
        DrawDeckHeader();
        DrawNeonSeparator();
        ImGui.Spacing();
        DrawDeckButtons(ImGui.GetContentRegionAvail().X);
        DrawTransientFeedbackOverlay();
    }

    private void DrawAppScreen()
    {
        if (ImGui.Button("<-"))
            selectedView = DeckView.Home;

        ImGui.SameLine();
        ImGui.TextUnformatted(GetDeckViewTitle(selectedView));
        DrawNeonSeparator();
        ImGui.Spacing();
        DrawDeckView();
        DrawTransientFeedbackOverlay();
    }

    private void DrawDeckView()
    {
        switch (selectedView)
        {
            case DeckView.Home:
                DrawHomeView();
                break;
            case DeckView.Wifi:
                DrawWifiView();
                break;
            case DeckView.Map:
                DrawMapView();
                break;
            case DeckView.Menu:
                DrawMenuView();
                break;
            case DeckView.Network:
                DrawNetworkView();
                break;
            case DeckView.Settings:
                DrawSettingsView();
                break;
        }
    }

    private static string GetDeckViewTitle(DeckView view)
        => view switch
        {
            DeckView.Map => "Address",
            DeckView.Wifi => "Wi-Fi",
            DeckView.Menu => "Menu",
            DeckView.Network => "Network",
            DeckView.Settings => "Settings",
            _ => "The Grid",
        };

    private void DrawDeckHeader()
    {
        var wrap = GetTextureWrap("grid.png");
        if (wrap is not null)
        {
            ImGui.Image(wrap.Handle, GetTextureSize(wrap, GetUiScale()));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.TextUnformatted("THE GRID // n_root");
        ImGui.TextWrapped("Welcome to The Grid.");
        ImGui.TextColored(new Vector4(0.54f, 0.84f, 0.80f, 1.00f), Config.VenueAddress);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(LifestreamNavigationTooltip);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                OpenAddress();
        }

        ImGui.EndGroup();
        ImGui.Spacing();
    }

    private void DrawRooftopBackground()
    {
        var wrap = GetTextureWrap("rooftop.png");
        if (wrap is null)
            return;

        var contentMin = ImGui.GetCursorScreenPos();
        var contentSize = ImGui.GetContentRegionAvail();
        if (contentSize.X <= 0 || contentSize.Y <= 0)
            return;

        var scale = contentSize.X / wrap.Width;
        var imageSize = new Vector2(contentSize.X, wrap.Height * scale);
        if (imageSize.Y < contentSize.Y)
        {
            scale = contentSize.Y / wrap.Height;
            imageSize = new Vector2(wrap.Width * scale, contentSize.Y);
        }

        var imageMin = new Vector2(contentMin.X + (contentSize.X - imageSize.X) / 2, contentMin.Y);
        var imageMax = imageMin + imageSize;
        var contentMax = contentMin + contentSize;
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(contentMin, contentMax, true);
        drawList.AddImage(wrap.Handle, imageMin, imageMax, Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.34f)));
        drawList.AddRectFilled(contentMin, contentMax, ImGui.GetColorU32(new Vector4(0.01f, 0.03f, 0.04f, 0.60f)));
        drawList.PopClipRect();
    }

    private static void DrawDeckScanline(Vector2 min, Vector2 max, float uiScale)
    {
        var height = max.Y - min.Y;
        if (height <= 0)
            return;

        var t = (float)ImGui.GetTime();
        var y = min.Y + ((t * 34f) % height);
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(min, max, true);
        drawList.AddRectFilled(
            new Vector2(min.X + (4 * uiScale), y - uiScale),
            new Vector2(max.X - (4 * uiScale), y + (3 * uiScale)),
            ImGui.GetColorU32(new Vector4(0.16f, 0.95f, 0.90f, 0.16f)));
        drawList.AddLine(
            new Vector2(min.X + (4 * uiScale), y + uiScale),
            new Vector2(max.X - (4 * uiScale), y + uiScale),
            ImGui.GetColorU32(new Vector4(0.70f, 1.00f, 0.96f, 0.32f)),
            MathF.Max(1.0f, uiScale));
        drawList.PopClipRect();
    }

    private void DrawNeonSeparator()
    {
        var uiScale = GetUiScale();
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        if (width <= 0)
        {
            ImGui.Separator();
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var y = pos.Y + (2 * uiScale);
        drawList.AddLine(new Vector2(pos.X, y - uiScale), new Vector2(pos.X + width, y - uiScale), ImGui.GetColorU32(new Vector4(0.16f, 0.92f, 0.88f, 0.20f)), 3.0f * uiScale);
        drawList.AddLine(new Vector2(pos.X, y), new Vector2(pos.X + width, y), ImGui.GetColorU32(new Vector4(0.42f, 1.00f, 0.94f, 0.88f)), MathF.Max(1.0f, uiScale));
        drawList.AddLine(new Vector2(pos.X, y + (2 * uiScale)), new Vector2(pos.X + width, y + (2 * uiScale)), ImGui.GetColorU32(new Vector4(1.00f, 0.10f, 0.82f, 0.42f)), MathF.Max(1.0f, uiScale));
        ImGui.Dummy(new Vector2(width, 7 * uiScale));
    }

    private void DrawDeckButtons(float width)
    {
        var uiScale = GetUiScale();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonWidth = MathF.Max(92 * uiScale, (width - spacing) / 2);
        var buttonHeight = 158f * uiScale;
        var buttonSize = new Vector2(buttonWidth, buttonHeight);

        if (DrawImageNavButton("Menu", "menu.png", buttonSize))
            selectedView = DeckView.Menu;
        ImGui.SameLine();
        if (DrawImageNavButton("Wi-Fi", "wifi.png", buttonSize))
            selectedView = DeckView.Wifi;

        if (DrawImageNavButton("Address", "address.png", buttonSize))
            selectedView = DeckView.Map;
        ImGui.SameLine();
        if (DrawImageNavButton("Network", "network.png", buttonSize))
            selectedView = DeckView.Network;

        DrawDisabledImageNavButton("Services", "services.png", buttonSize);
        ImGui.SameLine();
        if (DrawImageNavButton("Settings", "settings.png", buttonSize))
            selectedView = DeckView.Settings;
    }

    private bool DrawImageNavButton(string label, string imageName, Vector2 size)
    {
        ImGui.BeginGroup();
        var clicked = false;
        var wrap = GetTextureWrap(imageName);
        var start = ImGui.GetCursorScreenPos();
        var hovered = false;
        var uiScale = GetUiScale();

        if (wrap is not null)
        {
            ImGui.Button($"##tile_{label}", size);
            clicked = ImGui.IsItemClicked();
            hovered = ImGui.IsItemHovered();
            DrawTileGlow(start, size, hovered, uiScale);
            var iconSize = GetTextureSize(wrap, uiScale);
            var iconPos = new Vector2(start.X + (size.X - iconSize.X) / 2, start.Y + (12 * uiScale));
            ImGui.GetWindowDrawList().AddImage(wrap.Handle, iconPos, iconPos + iconSize);
        }
        else
        {
            clicked = ImGui.Button(label, size);
            hovered = ImGui.IsItemHovered();
            DrawTileGlow(start, size, hovered, uiScale);
        }

        var textWidth = ImGui.CalcTextSize(label).X;
        ImGui.GetWindowDrawList().AddText(
            new Vector2(start.X + MathF.Max(0, (size.X - textWidth) / 2), start.Y + size.Y - (25 * uiScale)),
            ImGui.GetColorU32(ImGuiCol.Text),
            label);
        ImGui.EndGroup();
        return clicked;
    }

    private static void DrawTileGlow(Vector2 start, Vector2 size, bool hovered, float uiScale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var max = start + size;
        var pulse = 0.65f + MathF.Sin((float)ImGui.GetTime() * 4.0f) * 0.12f;
        var strength = hovered ? 1.0f : 0.72f;
        var outerCyan = ImGui.GetColorU32(new Vector4(0.08f, 0.95f, 0.90f, 0.22f * pulse * strength));
        var cyan = ImGui.GetColorU32(new Vector4(0.14f, 1.00f, 0.92f, 0.62f * pulse * strength));
        var magenta = ImGui.GetColorU32(new Vector4(1.00f, 0.08f, 0.78f, 0.40f * pulse * strength));

        drawList.AddRect(start - new Vector2(2, 2) * uiScale, max + new Vector2(2, 2) * uiScale, outerCyan, 6 * uiScale, ImDrawFlags.None, (hovered ? 3.0f : 2.0f) * uiScale);
        drawList.AddRect(start + new Vector2(1, 1) * uiScale, max - new Vector2(1, 1) * uiScale, cyan, 5 * uiScale, ImDrawFlags.None, (hovered ? 2.5f : 1.8f) * uiScale);
        drawList.AddRect(start + new Vector2(5, 5) * uiScale, max - new Vector2(5, 5) * uiScale, magenta, 4 * uiScale, ImDrawFlags.None, (hovered ? 1.6f : 1.2f) * uiScale);
    }

    private void DrawDisabledImageNavButton(string label, string imageName, Vector2 size)
    {
        ImGui.BeginGroup();
        var wrap = GetTextureWrap(imageName);
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        ImGui.Button($"##tile_{label}", size);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Services unavailable");
        if (ImGui.IsItemClicked())
            SetTransientFeedback("Services offline");

        var textWidth = ImGui.CalcTextSize(label).X;
        var uiScale = GetUiScale();
        var textPos = new Vector2(start.X + MathF.Max(0, (size.X - textWidth) / 2), start.Y + size.Y - (25 * uiScale));
        var disabledColor = ImGui.GetColorU32(new Vector4(0.56f, 0.78f, 0.80f, 0.60f));

        if (wrap is not null)
        {
            var iconSize = GetTextureSize(wrap, uiScale);
            var iconPos = new Vector2(start.X + (size.X - iconSize.X) / 2, start.Y + (12 * uiScale));
            DrawGlitchedImage(wrap.Handle, iconPos, iconSize, disabledColor, uiScale);
        }
        else
        {
            drawList.AddText(textPos, disabledColor, label);
        }

        drawList.AddText(textPos, disabledColor, label);
        DrawGlitchOverlay(start, size, textPos, label, uiScale);
        ImGui.EndGroup();
    }

    private static void DrawGlitchedImage(ImTextureID textureHandle, Vector2 iconPos, Vector2 iconSize, uint baseColor, float uiScale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var t = (float)ImGui.GetTime();
        var shift = MathF.Sin(t * 1.05f) * 2.8f * uiScale;
        var jitter = MathF.Sin(t * 1.65f) > 0.78f ? 2.0f * uiScale : 0.0f;

        drawList.AddImage(textureHandle, iconPos + new Vector2(-shift - jitter, 0), iconPos + iconSize + new Vector2(-shift - jitter, 0), Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1.00f, 0.08f, 0.24f, 0.20f)));
        drawList.AddImage(textureHandle, iconPos + new Vector2(shift + jitter, 0), iconPos + iconSize + new Vector2(shift + jitter, 0), Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(0.00f, 0.95f, 1.00f, 0.22f)));
        drawList.AddImage(textureHandle, iconPos, iconPos + iconSize, Vector2.Zero, Vector2.One, baseColor);
    }

    private static void DrawGlitchOverlay(Vector2 start, Vector2 size, Vector2 textPos, string label, float uiScale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var t = (float)ImGui.GetTime();
        var pulse = 0.62f + (MathF.Sin(t * 7.0f) * 0.22f);
        var cyan = ImGui.GetColorU32(new Vector4(0.00f, 0.95f, 1.00f, 0.42f * pulse));
        var red = ImGui.GetColorU32(new Vector4(1.00f, 0.08f, 0.24f, 0.38f * pulse));

        drawList.AddText(textPos + new Vector2(-2.5f, -0.5f) * uiScale, red, label);
        drawList.AddText(textPos + new Vector2(2.5f, 0.5f) * uiScale, cyan, label);
    }

    private void DrawWifiView()
    {
        ImGui.TextUnformatted("Wi-Fi / Syncshell");
        DrawNeonSeparator();
        ImGui.Spacing();

        DrawTerminalLine("Lightless");
        DrawCopyableTerminalLine("Id", LightlessSyncshellId, "lightless_id");
        DrawCopyableTerminalLine("Pwd", LightlessSyncshellId, "lightless_pwd");
        ImGui.TextDisabled("same as id");
        ImGui.Spacing();

        DrawTerminalLine("PlayerSync");
        DrawCopyableTerminalLine("Id", PlayerSyncSyncshellId, "playersync_id");
        ImGui.TextDisabled("can join without password");
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();
        ImGui.TextWrapped("Please compress your textures.");
        ImGui.TextWrapped("Please be SFW.");
        ImGui.Spacing();
        if (ImGui.Button("Discord"))
            OpenDiscord();
    }

    private void DrawMapView()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Config.VenueAddress);
        ImGui.SameLine();
        if (ImGui.Button("Copy"))
            CopyToClipboard(Config.VenueAddress);
        DrawHoverTooltip("Copy to clipboard");
        ImGui.SameLine();
        if (ImGui.Button("Navigate"))
            OpenAddress();
        DrawHoverTooltip(LifestreamNavigationTooltip);

        ImGui.Spacing();

        if (ImGui.Button("-"))
            mapZoom = MathF.Max(0.2f, mapZoom - 0.1f);
        ImGui.SameLine();
        if (ImGui.Button("+"))
            mapZoom = MathF.Min(3.0f, mapZoom + 0.1f);
        ImGui.SameLine();
        if (ImGui.Button("Reset"))
            mapZoom = DefaultMapZoom;
        ImGui.SameLine();
        ImGui.TextDisabled($"{mapZoom:0.00}x");
        DrawNeonSeparator();

        if (ImGui.BeginChild("map_scroll", new Vector2(0, 0), true, ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoMove))
        {
            var wrap = GetTextureWrap("map.png");
            if (wrap is not null)
            {
                if (ImGui.IsWindowHovered())
                {
                    var wheel = ImGui.GetIO().MouseWheel;
                    if (Math.Abs(wheel) > 0.001f)
                        mapZoom = Math.Clamp(mapZoom + (wheel * 0.08f), 0.2f, 3.0f);

                    if (ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                    {
                        var drag = ImGui.GetIO().MouseDelta;
                        ImGui.SetScrollX(ImGui.GetScrollX() - drag.X);
                        ImGui.SetScrollY(ImGui.GetScrollY() - drag.Y);
                    }
                }

                var imageSize = new Vector2(wrap.Width * mapZoom, wrap.Height * mapZoom);
                var avail = ImGui.GetContentRegionAvail();
                if (imageSize.X < avail.X)
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail.X - imageSize.X) / 2);

                ImGui.Image(wrap.Handle, imageSize);
            }
            else
            {
                ImGui.TextWrapped("Map image is missing.");
            }
        }

        ImGui.EndChild();
    }

    private void DrawMenuView()
    {
        ImGui.TextUnformatted("Drinks Card");
        DrawNeonSeparator();
        ImGui.Spacing();

        for (var i = 0; i < DrinkMenu.Length; i++)
        {
            var item = DrinkMenu[i];
            if (i > 0)
            {
                ImGui.Spacing();
                DrawNeonSeparator();
                ImGui.Spacing();
            }

            var wrap = GetTextureWrap(item.ImageName);
            if (wrap is not null)
            {
                ImGui.Image(wrap.Handle, GetTextureSize(wrap, GetUiScale()));
                ImGui.SameLine();
            }

            ImGui.BeginGroup();
            ImGui.TextUnformatted(item.Name);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Copy##drink_{item.Name}"))
                CopyToClipboard(item.Name);
            DrawHoverTooltip("Copy to clipboard");
            ImGui.TextDisabled(item.Price);
            ImGui.TextWrapped(item.Description);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled($"Ingredients: {item.Ingredients}");
            ImGui.PopTextWrapPos();
            ImGui.TextWrapped($"Taste: {item.Taste}");
            ImGui.EndGroup();
        }
    }

    private void DrawNetworkView()
    {
        var players = PluginService.Objects
            .OfType<IPlayerCharacter>()
            .Where(player => player.ObjectKind == ObjectKind.Pc && !string.IsNullOrWhiteSpace(player.Name.TextValue))
            .GroupBy(GetPlayerTellName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(GetPlayerTellName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ImGui.TextUnformatted($"Local players detected: {players.Count}");
        ImGui.TextDisabled("Client-visible players in this instance.");
        DrawNeonSeparator();
        ImGui.Spacing();

        if (players.Count == 0)
        {
            ImGui.TextWrapped("No local player signals detected.");
            return;
        }

        if (!ImGui.BeginTable("network_players", 4, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        var uiScale = GetUiScale();
        ImGui.TableSetupColumn("##alert", ImGuiTableColumnFlags.WidthFixed, 24 * uiScale);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##weapon", ImGuiTableColumnFlags.WidthFixed, 24 * uiScale);
        ImGui.TableSetupColumn("##minion", ImGuiTableColumnFlags.WidthFixed, 24 * uiScale);

        foreach (var player in players)
            DrawNetworkPlayerRow(player);

        ImGui.EndTable();
    }

    private void DrawNetworkPlayerRow(IPlayerCharacter player)
    {
        var tellName = GetPlayerTellName(player);
        var weaponDrawn = player.StatusFlags.HasFlag(StatusFlags.WeaponOut);
        var weaponDisplayed = IsWeaponDisplayed(player);
        var showWeapon = weaponDrawn || weaponDisplayed == true;
        var minionName = GetMinionName(player);
        var hasMinion = !string.IsNullOrWhiteSpace(minionName);

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (showWeapon || hasMinion)
        {
            var pulse = 0.72f + MathF.Sin((float)ImGui.GetTime() * 6.0f) * 0.28f;
            ImGui.TextColored(new Vector4(1.00f, 0.16f, 0.14f, pulse), "<!>");
            DrawHoverTooltip("Player has weapon displayed/drawn and/or minion present");
        }

        ImGui.TableSetColumnIndex(1);
        if (ImGui.Selectable(tellName, false, ImGuiSelectableFlags.SpanAllColumns))
            PluginService.Targets.Target = player;

        ImGui.TableSetColumnIndex(2);
        if (showWeapon)
            DrawNetworkStatusIcon("weapon.png", "Weapon", GetWeaponTooltip(weaponDisplayed == true, weaponDrawn));

        ImGui.TableSetColumnIndex(3);
        if (hasMinion)
            DrawNetworkStatusIcon("minion.png", "Minion", $"Minion present: {minionName}");
    }

    private static unsafe bool? IsWeaponDisplayed(IPlayerCharacter player)
    {
        if (player.Address == IntPtr.Zero)
            return null;

        try
        {
            var character = (NativeCharacter*)player.Address;
            return !character->DrawData.IsWeaponHidden;
        }
        catch
        {
            return null;
        }
    }

    private static string GetWeaponTooltip(bool weaponDisplayed, bool weaponDrawn)
        => (weaponDisplayed, weaponDrawn) switch
        {
            (true, true) => "Weapon displayed and drawn",
            (true, false) => "Weapon displayed",
            _ => "Weapon drawn",
        };

    private void DrawNetworkStatusIcon(string imageName, string fallbackText, string tooltip)
    {
        var wrap = GetTextureWrap(imageName);
        if (wrap is not null)
            ImGui.Image(wrap.Handle, new Vector2(18, 18) * GetUiScale());
        else
            ImGui.TextUnformatted(fallbackText);

        DrawHoverTooltip(tooltip);
    }

    private void DrawSettingsView()
    {
        var mapping = Config.GetPrimaryMapping();
        var penumbraAvailable = penumbra.IsAvailable();
        var collection = penumbraAvailable ? FindCollectionSafely(mapping.CollectionName) : null;
        ImGui.TextUnformatted("Settings");
        DrawNeonSeparator();

        DrawSettingsGroupHeader("Interface");
        DrawInterfaceSettings();
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        DrawSettingsGroupHeader("Venue");
        ImGui.TextUnformatted($"Collection: {mapping.CollectionName} {(collection is null ? "missing" : "found")}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy##collection_name"))
            CopyToClipboard(mapping.CollectionName);
        DrawHoverTooltip("Copy to clipboard");
        if (collection is null)
            ImGui.TextWrapped($"Create a new, unassigned persistent Penumbra collection named '{mapping.CollectionName}', then run Update or Assign.");
        ImGui.TextDisabled($"NPC/mannequin marker: {mapping.NpcName}");
        ImGui.TextDisabled($"Auto-open looks for configured NPC/mannequin: {mapping.NpcName}");
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        DrawSettingsGroupHeader("Updater");
        ImGui.TextUnformatted($"Repository: {ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo}");
        ImGui.TextUnformatted($"Penumbra: {(penumbraAvailable ? "online" : "offline")}");
        ImGui.TextUnformatted($"Images: {textures.Count} loaded");
        ImGui.TextDisabled(textureLoadSource);
        ImGui.TextUnformatted($"Last applied: {DisplayValue(mapping.LastAppliedVersion)}");
        ImGui.TextWrapped(mapping.LastStatus);
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        DrawSettingsGroupHeader("Actions");
        if (ImGui.Button("Update"))
            QueueReconcile();

        ImGui.SameLine();
        if (ImGui.Button("Assign"))
            _ = AssignAllAsync(lifetime.Token);

        ImGui.SameLine();
        if (ImGui.Button("Force Reinstall"))
            QueueReconcile(forceDownload: true);

        ImGui.Spacing();
        if (ImGui.Button("Open Config"))
            OpenConfigUi();
    }

    private static void DrawSettingsGroupHeader(string label)
        => ImGui.TextColored(new Vector4(0.54f, 0.84f, 0.80f, 1.00f), label);

    private void DrawInterfaceSettings()
    {
        var effectiveScale = GetUiScale();
        var scaleLabel = Config.UiScale <= 0
            ? $"UI Scale: Auto ({effectiveScale:0.##}x)"
            : $"UI Scale: {effectiveScale:0.##}x";

        ImGui.TextUnformatted(scaleLabel);
        if (ImGui.SmallButton("Auto##ui_scale_auto"))
        {
            Config.UiScale = 0;
            Config.Save();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("-##ui_scale_down"))
            SetManualUiScale(GetPreviousUiScale(effectiveScale));

        ImGui.SameLine();
        if (ImGui.SmallButton("+##ui_scale_up"))
            SetManualUiScale(GetNextUiScale(effectiveScale));

        ImGui.Spacing();
        if (ImGui.Button("Restore Window Size"))
            ImGui.SetWindowSize("The Grid Cyberdeck", GetInitialWindowSize(GetUiScale()));
    }

    private void SetManualUiScale(float uiScale)
    {
        Config.UiScale = ClampUiScale(uiScale);
        Config.Save();
    }

    private static float GetPreviousUiScale(float current)
    {
        for (var i = ManualUiScales.Length - 1; i >= 0; i--)
        {
            if (ManualUiScales[i] < current - 0.01f)
                return ManualUiScales[i];
        }

        return ManualUiScales[0];
    }

    private static float GetNextUiScale(float current)
    {
        foreach (var scale in ManualUiScales)
        {
            if (scale > current + 0.01f)
                return scale;
        }

        return ManualUiScales[^1];
    }

    private static void DrawTerminalLine(string text)
    {
        ImGui.TextDisabled(">");
        ImGui.SameLine();
        ImGui.TextWrapped(text);
    }

    private void DrawCopyableTerminalLine(string label, string value, string id)
    {
        ImGui.TextDisabled(">");
        ImGui.SameLine();
        ImGui.TextUnformatted($"{label}: {value}");
        ImGui.SameLine();
        if (ImGui.SmallButton($"Copy##{id}"))
            CopyToClipboard(value);
        DrawHoverTooltip("Copy to clipboard");
    }

    private void CopyToClipboard(string value)
    {
        ImGui.SetClipboardText(value);
        SetTransientFeedback("Copied");
    }

    private void SetTransientFeedback(string text)
    {
        transientFeedback = text;
        transientFeedbackUntil = Environment.TickCount64 + 1600;
    }

    private void DrawTransientFeedbackOverlay()
    {
        if (transientFeedback is null)
            return;

        if (Environment.TickCount64 > transientFeedbackUntil)
        {
            transientFeedback = null;
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var textSize = ImGui.CalcTextSize(transientFeedback);
        var uiScale = GetUiScale();
        var padding = new Vector2(8, 5) * uiScale;
        var max = ImGui.GetWindowPos() + ImGui.GetWindowSize() - (new Vector2(14, 14) * uiScale);
        var min = max - textSize - (padding * 2);

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.01f, 0.04f, 0.05f, 0.88f)), 5 * uiScale);
        drawList.AddRect(min, max, ImGui.GetColorU32(new Vector4(0.22f, 0.86f, 0.82f, 0.62f)), 5 * uiScale);
        drawList.AddText(min + padding, ImGui.GetColorU32(new Vector4(0.54f, 0.84f, 0.80f, 1.00f)), transientFeedback);
    }

    private static void DrawHoverTooltip(string tooltip)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private static string GetPlayerTellName(IPlayerCharacter player)
    {
        var name = player.Name.TextValue;
        var world = GetWorldName(player);
        return $"{name}@{world}";
    }

    private static string? GetMinionName(IPlayerCharacter player)
        => player.CurrentMinion?.ValueNullable?.Singular.ExtractText();

    private static string GetWorldName(IPlayerCharacter player)
    {
        var homeWorld = player.HomeWorld.ValueNullable?.Name.ExtractText();
        if (!string.IsNullOrWhiteSpace(homeWorld))
            return homeWorld;

        var currentWorld = player.CurrentWorld.ValueNullable?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(currentWorld) ? "Unknown World" : currentWorld;
    }

    private void DrawConfigWindow()
    {
        if (!configUiOpen)
            return;

        if (!ImGui.Begin("TheGrid Updater Config", ref configUiOpen))
        {
            ImGui.End();
            return;
        }

        var mapping = Config.GetPrimaryMapping();
        var changed = false;
        var mappingChanged = false;

        changed |= InputText("Venue address", Config.VenueAddress, value => Config.VenueAddress = value);
        changed |= InputText("Discord URL", Config.DiscordUrl, value => Config.DiscordUrl = value);
        changed |= InputBool("Auto-open when venue mannequin is detected", Config.AutoOpenOnVenueAddress, value => Config.AutoOpenOnVenueAddress = value);
        ImGui.Separator();

        ImGui.TextUnformatted($"Repository: {ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo}");
        mappingChanged |= InputText("Asset pattern", mapping.AssetPattern, value => mapping.AssetPattern = value);
        mappingChanged |= InputText("Collection name", mapping.CollectionName, value => mapping.CollectionName = value);
        mappingChanged |= InputText("NPC name", mapping.NpcName, value => mapping.NpcName = value);
        mappingChanged |= InputText("Penumbra mod directory", mapping.ModDirectory, value => mapping.ModDirectory = value);
        mappingChanged |= InputText("Penumbra mod name", mapping.ModName, value => mapping.ModName = value);

        mappingChanged |= InputInt("Priority", mapping.Priority, value => mapping.Priority = value);
        changed |= mappingChanged;

        if (mappingChanged)
        {
            mapping.LastAppliedVersion = string.Empty;
            mapping.LastStatus = "Config changed. Run Update to apply.";
        }

        if (changed)
            Config.Save();

        if (ImGui.Button("Save and Update"))
        {
            Config.Save();
            QueueReconcile();
        }

        ImGui.SameLine();
        if (ImGui.Button("Assign Now"))
            _ = AssignAllAsync(lifetime.Token);

        ImGui.TextWrapped("TheGrid must already exist in Penumbra. Current public Penumbra IPC can assign existing collections but does not expose named collection creation.");

        ImGui.End();
    }

    private static bool InputBool(string label, bool currentValue, Action<bool> setValue)
    {
        var value = currentValue;
        if (!ImGui.Checkbox(label, ref value))
            return false;

        setValue(value);
        return true;
    }

    private static bool InputText(string label, string currentValue, Action<string> setValue)
    {
        var value = currentValue;
        if (!ImGui.InputText(label, ref value, 512))
            return false;

        setValue(value);
        return true;
    }

    private static bool InputInt(string label, int currentValue, Action<int> setValue)
    {
        var value = currentValue;
        if (!ImGui.InputInt(label, ref value))
            return false;

        setValue(value);
        return true;
    }

    private static string DisplayValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "(unset)" : value;

    private void OnLogin()
    {
        QueueReconcile();
        QueueVenueAutoOpenCheck();
    }

    private void OnTerritoryChanged(uint _)
    {
        QueueAssignment();
        QueueVenueAutoOpenCheck();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!reconcileQueued || reconcileRunning)
            return;

        reconcileQueued = false;
        reconcileRunning = true;
        Task.Run(ReconcileAsync, lifetime.Token);
    }

    private void QueueReconcile(bool forceDownload = false)
    {
        if (forceDownload)
        {
            var mapping = Config.GetPrimaryMapping();
            mapping.LastAppliedVersion = string.Empty;
            Config.Save();
        }

        reconcileQueued = true;
    }

    private void QueueAssignment()
        => PluginService.Framework.RunOnTick(() => _ = AssignAllAsync(lifetime.Token), delay: TimeSpan.FromSeconds(2), cancellationToken: lifetime.Token);

    private void QueueVenueAutoOpenCheck()
    {
        if (!Config.AutoOpenOnVenueAddress)
            return;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            PluginService.Framework.RunOnTick(() =>
            {
                TryAutoOpenForVenueObject();
            }, delay: TimeSpan.FromSeconds(attempt * 2), cancellationToken: lifetime.Token);
        }
    }

    private void TryAutoOpenForVenueObject()
    {
        var territory = PluginService.ClientState.TerritoryType;
        if (lastAutoOpenedTerritory == territory)
            return;

        var mapping = Config.GetPrimaryMapping();
        for (var i = 0; i < PluginService.Objects.Length; i++)
        {
            if (!IsTargetNpc(PluginService.Objects[i], mapping.NpcName))
                continue;

            lastAutoOpenedTerritory = territory;
            OpenMainUi();
            return;
        }
    }

    private void OpenAddress()
    {
        if (string.IsNullOrWhiteSpace(Config.VenueAddress))
            return;

        SetTransientFeedback("Opening Lifestream...");
        PluginService.Commands.ProcessCommand($"/li {Config.VenueAddress}");
    }

    private void OpenDiscord()
    {
        if (!string.IsNullOrWhiteSpace(Config.DiscordUrl))
            Util.OpenLink(Config.DiscordUrl);
    }

    private static (Dictionary<string, ISharedImmediateTexture> Textures, string Source) LoadTextures()
    {
        var loaded = new Dictionary<string, ISharedImmediateTexture>(StringComparer.OrdinalIgnoreCase);
        var source = "No image directory found.";

        foreach (var directory in GetTextureSearchDirectories())
        {
            LoadTextureIfExists(loaded, Path.Combine(directory, "grid.png"));

            var imageDirectory = Path.Combine(directory, "img");
            if (!Directory.Exists(imageDirectory))
                continue;

            foreach (var path in Directory.EnumerateFiles(imageDirectory, "*.png"))
                LoadTextureIfExists(loaded, path);

            source = imageDirectory;
            if (loaded.ContainsKey("map.png") && loaded.ContainsKey("address.png"))
                break;
        }

        PluginService.Log.Information("Loaded {Count} The Grid image asset(s) from {Source}.", loaded.Count, source);
        return (loaded, source);
    }

    private static IEnumerable<string> GetTextureSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in GetTextureSearchDirectoriesCore())
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && seen.Add(directory))
                yield return directory;
        }
    }

    private static IEnumerable<string?> GetTextureSearchDirectoriesCore()
    {
        yield return PluginService.PluginInterface.AssemblyLocation.DirectoryName;
        yield return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        yield return AppContext.BaseDirectory;

        var current = PluginService.PluginInterface.AssemblyLocation.Directory;
        for (var i = 0; i < 4 && current is not null; i++, current = current.Parent)
            yield return current.FullName;
    }

    private static void LoadTextureIfExists(Dictionary<string, ISharedImmediateTexture> loaded, string path)
    {
        if (File.Exists(path))
            loaded[Path.GetFileName(path)] = PluginService.TextureProvider.GetFromFile(path);
    }

    private IDalamudTextureWrap? GetTextureWrap(string imageName)
        => textures.TryGetValue(imageName, out var texture) ? texture.GetWrapOrDefault() : null;

    private static Vector2 GetTextureSize(IDalamudTextureWrap texture, float uiScale = 1.0f)
        => new(texture.Width * uiScale, texture.Height * uiScale);

    private static Vector2 GetInitialWindowSize(float uiScale)
    {
        var desired = new Vector2(360, 720) * uiScale;
        var display = ImGui.GetIO().DisplaySize;
        if (display.X <= 0 || display.Y <= 0)
            return desired;

        var margin = 48 * uiScale;
        return new Vector2(
            MathF.Min(desired.X, MathF.Max(260 * uiScale, display.X - margin)),
            MathF.Min(desired.Y, MathF.Max(360 * uiScale, display.Y - margin)));
    }

    private float GetUiScale()
    {
        if (Config.UiScale > 0)
            return ClampUiScale(Config.UiScale);

        var display = ImGui.GetIO().DisplaySize;
        var maxDimension = MathF.Max(display.X, display.Y);
        var minDimension = MathF.Min(display.X, display.Y);

        if (maxDimension >= 3600 || minDimension >= 1800)
            return 1.5f;

        if (maxDimension >= 2400 || minDimension >= 1300)
            return 1.25f;

        return 1.0f;
    }

    private static float ClampUiScale(float uiScale)
        => Math.Clamp(uiScale, 1.0f, 2.0f);

    private static string EscapeChatCommandArgument(string value)
        => value.Replace("\"", string.Empty, StringComparison.Ordinal);

    private static void PushCyberdeckStyle(float uiScale)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.79f, 0.96f, 0.94f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, new Vector4(0.48f, 0.67f, 0.66f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.03f, 0.05f, 0.06f, 0.88f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.08f, 0.21f, 0.23f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.14f, 0.38f, 0.40f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.23f, 0.57f, 0.58f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.22f, 0.53f, 0.52f, 1.00f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18, 18) * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4 * uiScale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6 * uiScale);
    }

    private static void PopCyberdeckStyle()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(7);
    }


    private async Task ReconcileAsync()
    {
        try
        {
            if (!penumbra.IsAvailable())
            {
                SetAllStatus("Penumbra IPC is not available. Install and enable Penumbra, then run /thegrid update.");
                return;
            }

            TrySubscribePenumbraEvents();

            await ReconcileMappingAsync(Config.GetPrimaryMapping(), lifetime.Token).ConfigureAwait(false);

            Config.Save();
            await AssignAllAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PluginService.Log.Error(ex, "TheGrid reconciliation failed.");
            PluginService.Chat.PrintError($"Update failed: {ex.Message}", "TheGrid");
        }
        finally
        {
            reconcileRunning = false;
        }
    }

    private async Task ReconcileMappingAsync(ModMapping mapping, CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.Combine(PluginService.PluginInterface.ConfigDirectory.FullName, "cache");
        var latestAsset = await github.GetLatestReleaseAssetInfoAsync(mapping, cancellationToken).ConfigureAwait(false);
        if (string.Equals(latestAsset.Version, mapping.LastAppliedVersion, StringComparison.OrdinalIgnoreCase))
        {
            mapping.LastStatus = $"Latest release {latestAsset.Version} already applied.";
            return;
        }

        var download = await github.DownloadReleaseAssetAsync(mapping, latestAsset, cacheDirectory, cancellationToken).ConfigureAwait(false);

        var previousModDirectory = NormalizeManagedModDirectory(mapping.ModDirectory);
        mapping.ModDirectory = previousModDirectory;
        var previousModName = mapping.ModName;
        var modsBeforeInstall = penumbra.GetModList();
        DeleteExistingManagedModBeforeInstall(previousModDirectory, previousModName, modsBeforeInstall);
        modsBeforeInstall = penumbra.GetModList();

        PrepareForModAdded();
        var installCode = penumbra.InstallMod(download.Path);
        if (!IsSuccess(installCode))
            throw new InvalidOperationException($"Penumbra rejected package '{Path.GetFileName(download.Path)}' with code {installCode}.");

        var addedDirectory = await WaitForModAddedAsync(cancellationToken).ConfigureAwait(false);
        var modsAfterInstall = penumbra.GetModList();

        var collection = FindCollection(mapping.CollectionName)
            ?? throw new InvalidOperationException($"Collection '{mapping.CollectionName}' does not exist. Create a new, unassigned persistent Penumbra collection named '{mapping.CollectionName}', then use Settings > Update.");

        var modDirectory = TryResolveModDirectory(mapping, modsAfterInstall, modsBeforeInstall, addedDirectory);
        if (modDirectory is not null)
        {
            var enableCode = penumbra.TrySetMod(collection.Id, modDirectory, mapping.ModName, true);
            if (!IsSuccess(enableCode))
                PluginService.Log.Warning("Could not enable mod {ModDirectory} in {Collection}. Penumbra code {Code}.", modDirectory, mapping.CollectionName, enableCode);

            var priorityCode = penumbra.TrySetModPriority(collection.Id, modDirectory, mapping.ModName, mapping.Priority);
            if (!IsSuccess(priorityCode))
                PluginService.Log.Warning("Could not set mod priority for {ModDirectory}. Penumbra code {Code}.", modDirectory, priorityCode);

            mapping.ModDirectory = modDirectory;
        }
        else
        {
            PluginService.Log.Warning("Penumbra accepted package {Package}, but the imported mod was not visible in the IPC mod list immediately after import.", Path.GetFileName(download.Path));
        }

        mapping.LastAppliedVersion = download.Version;
        mapping.LastStatus = modDirectory is null
            ? $"Imported latest release {download.Version}; Penumbra did not expose the mod in IPC immediately."
            : $"Applied latest release {download.Version}.";
        PluginService.Chat.Print($"{mapping.Name}: {mapping.LastStatus}", "TheGrid");
    }

    private void PrepareForModAdded()
    {
        lock (modAddedLock)
        {
            pendingModAdded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private async Task<string?> WaitForModAddedAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<string>? waiter;
        lock (modAddedLock)
        {
            waiter = pendingModAdded;
        }

        if (waiter is null)
            return null;

        try
        {
            var completed = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)).ConfigureAwait(false);
            return completed == waiter.Task
                ? await waiter.Task.ConfigureAwait(false)
                : null;
        }
        finally
        {
            lock (modAddedLock)
            {
                if (ReferenceEquals(pendingModAdded, waiter))
                    pendingModAdded = null;
            }
        }
    }

    private void OnPenumbraModAdded(string modDirectory)
    {
        lock (modAddedLock)
        {
            pendingModAdded?.TrySetResult(modDirectory);
        }
    }

    private void DeleteExistingManagedModBeforeInstall(string previousModDirectory, string previousModName, System.Collections.Generic.Dictionary<string, string> installedMods)
    {
        if (string.IsNullOrWhiteSpace(previousModDirectory))
            return;

        if (!installedMods.ContainsKey(previousModDirectory))
        {
            var duplicateDirectories = installedMods.Keys
                .Where(IsManagedDuplicateDirectory)
                .ToList();

            foreach (var duplicateDirectory in duplicateDirectories)
                DeleteManagedMod(duplicateDirectory, previousModName);

            return;
        }

        DeleteManagedMod(previousModDirectory, previousModName);
    }

    private void DeleteManagedMod(string modDirectory, string modName)
    {
        var deleteCode = penumbra.DeleteMod(modDirectory, modName);
        if (IsSuccess(deleteCode))
        {
            PluginService.Log.Information("Deleted old managed Penumbra mod {ModDirectory} before update.", modDirectory);
            return;
        }

        PluginService.Log.Warning("Could not delete old managed Penumbra mod {ModDirectory} before update. Penumbra code {Code}.", modDirectory, deleteCode);
    }

    private static string NormalizeManagedModDirectory(string modDirectory)
        => IsManagedDuplicateDirectory(modDirectory) ? "n_root_the_grid" : modDirectory;

    private static bool IsManagedDuplicateDirectory(string modDirectory)
        => modDirectory.StartsWith("n_root_the_grid (", StringComparison.OrdinalIgnoreCase);

    private async Task AssignAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PluginService.Framework.RunOnFrameworkThread(() =>
            {
                AssignMapping(Config.GetPrimaryMapping());

                Config.Save();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PluginService.Log.Error(ex, "TheGrid assignment failed.");
            PluginService.Chat.PrintError($"Assignment failed: {ex.Message}", "TheGrid");
        }
    }

    private void AssignMapping(ModMapping mapping)
    {
        if (!penumbra.IsAvailable())
        {
            mapping.LastStatus = "Penumbra IPC is not available.";
            return;
        }

        var collection = FindCollection(mapping.CollectionName);
        if (collection is null)
        {
            mapping.LastStatus = $"Collection '{mapping.CollectionName}' does not exist.";
            PluginService.Chat.PrintError($"Collection '{mapping.CollectionName}' does not exist. Create a new, unassigned persistent Penumbra collection named '{mapping.CollectionName}', then use Settings > Assign.", "TheGrid");
            return;
        }

        var simpleAssignmentStatus = AssignMaleNonPlayerCharacters(mapping, collection.Value);
        var assigned = 0;
        for (var i = 0; i < PluginService.Objects.Length; i++)
        {
            var gameObject = PluginService.Objects[i];
            if (!IsTargetNpc(gameObject, mapping.NpcName))
                continue;

            var (errorCode, _) = penumbra.SetCollectionForObject(i, collection.Value.Id);
            if (IsSuccess(errorCode))
                assigned++;
            else
                PluginService.Log.Warning("Could not assign collection {Collection} to {Npc} at object index {Index}: {Code}", mapping.CollectionName, mapping.NpcName, i, errorCode);
        }

        var objectAssignmentStatus = assigned == 0
            ? $"No loaded NPC named '{mapping.NpcName}' found. Assignment will retry on territory changes."
            : $"Assigned '{mapping.CollectionName}' to {assigned} loaded '{mapping.NpcName}' object(s).";

        mapping.LastStatus = $"{objectAssignmentStatus} {simpleAssignmentStatus}";
    }

    private string AssignMaleNonPlayerCharacters(ModMapping mapping, (Guid Id, string Name) collection)
    {
        var (errorCode, _) = penumbra.SetCollection(MaleNonPlayerCharacterCollectionType, collection.Id);
        if (IsSuccess(errorCode))
            return $"Assigned '{mapping.CollectionName}' to Male Non-Player Characters.";

        PluginService.Log.Warning("Could not assign collection {Collection} to Male Non-Player Characters: {Code}", mapping.CollectionName, errorCode);
        return $"Could not assign '{mapping.CollectionName}' to Male Non-Player Characters. Penumbra code {errorCode}.";
    }

    private (Guid Id, string Name)? FindCollection(string collectionName)
        => penumbra.GetCollectionsByIdentifier(collectionName)
            .FirstOrDefault(c => string.Equals(c.Name, collectionName, StringComparison.OrdinalIgnoreCase)) is var collection && collection.Id != Guid.Empty
                ? collection
                : null;

    private (Guid Id, string Name)? FindCollectionSafely(string collectionName)
    {
        try
        {
            return FindCollection(collectionName);
        }
        catch (Exception ex)
        {
            PluginService.Log.Debug(ex, "Could not check Penumbra collection {Collection}.", collectionName);
            return null;
        }
    }

    private static string? TryResolveModDirectory(ModMapping mapping, System.Collections.Generic.Dictionary<string, string> mods, System.Collections.Generic.Dictionary<string, string> modsBeforeInstall, string? addedDirectory)
    {
        if (!string.IsNullOrWhiteSpace(addedDirectory) && mods.ContainsKey(addedDirectory))
            return addedDirectory;

        if (mods.ContainsKey(mapping.ModDirectory))
            return mapping.ModDirectory;

        var addedMod = mods.FirstOrDefault(kvp => !modsBeforeInstall.ContainsKey(kvp.Key));
        if (!string.IsNullOrEmpty(addedMod.Key))
        {
            mapping.ModName = addedMod.Value;
            return addedMod.Key;
        }

        var byName = mods.FirstOrDefault(kvp =>
            string.Equals(kvp.Value, mapping.ModName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kvp.Value, mapping.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kvp.Key, mapping.Name, StringComparison.OrdinalIgnoreCase) ||
            kvp.Value.Contains(mapping.Name, StringComparison.OrdinalIgnoreCase) ||
            kvp.Key.Contains(mapping.Name, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(byName.Key))
            return byName.Key;

        return null;
    }

    private static bool IsTargetNpc(IGameObject? gameObject, string npcName)
    {
        if (gameObject is null)
            return false;

        if (gameObject.ObjectKind is ObjectKind.Pc)
            return false;

        return string.Equals(gameObject.Name.TextValue, npcName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuccess(int penumbraCode)
        => penumbraCode is 0 or 1;

    private void SetAllStatus(string status)
    {
        Config.GetPrimaryMapping().LastStatus = status;

        Config.Save();
        PluginService.Chat.PrintError(status, "TheGrid");
    }

    private enum DeckView
    {
        Home,
        Map,
        Wifi,
        Menu,
        Network,
        Settings,
    }

    private sealed record DrinkMenuItem(string Name, string Price, string ImageName, string Ingredients, string Description, string Taste);
}
