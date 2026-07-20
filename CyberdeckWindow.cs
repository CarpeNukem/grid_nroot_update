using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Bindings.ImGui;
using Dalamud.Utility;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace GridNrootUpdate;

internal sealed class CyberdeckWindow
{
    private const float DefaultMapZoom = 0.44f;
    private const string LifestreamNavigationTooltip = "Click to navigate. Requires Lifestream plugin to work";
    private static readonly float[] ManualUiScales = [1.0f, 1.25f, 1.5f, 1.75f, 2.0f];
    private const string LightlessSyncshellId = "LLS-6AAKEJBAPRB0";
    private const string PlayerSyncSyncshellId = "n_root";
    private static readonly string[] AmbientTerminalMessages =
    [
        "PROBING RELAY_06...",
        "N_ROOT HEARTBEAT // OK",
        "ICE SIGNATURE // DORMANT",
        "ROUTE CIPHER // STABLE",
        "PACKET TRACE // CLEAN",
        "GHOST PORT 09 // OPEN",
        "MIRROR NODE // SYNCED",
        "DECRYPTING VENUE LINK...",
        "ROOFTOP UPLINK // ONLINE",
        "NULL SECTOR // NO THREATS",
    ];
    private static readonly DrinkMenuItem[] DrinkMenu =
    [
        new("Above The Grid", "10 000", "above_the_grid.png", "gin, elderflower cordial, lemon, ChroManticore Ultraviolet",
            "A cold, luminous rooftop cocktail for those who have risen above the city's noise. Smooth, silver, and electric-blue, Above The Grid tastes like neon reflected through glass and rain from the top floor.",
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
        new("Frostbite", "15 000", "frostbite.png", "ceruleum-infused vodka, blue curacao, synth-mint, synth-lime; reboot chaser: orange, ginger, cinnamon",
            "A neon-blue, ceruleum-infused cryo shot with a sharp mint-citrus bite. The unstable infusion activates on impact, triggering a brief system freeze before the reboot chaser brings you back online.",
            "Sharp, cold, and electric. Sweet blue citrus hits first, then peppermint bite and a metallic ceruleum tingle. The reboot chaser follows warm with orange, ginger, and cinnamon heat. Note: By ordering Frostbite, you consent to a brief, non-lethal physical interaction as part of the serving ritual. The management assures you it is theatrical, consensual, and only emotionally questionable."),
    ];

    private readonly PluginConfig config;
    private readonly PenumbraIpc penumbra;
    private readonly Dictionary<string, ISharedImmediateTexture> textures;
    private readonly string textureLoadSource;
    private readonly Action queueReconcile;
    private readonly Action queueReconcileForce;
    private readonly Action assignAll;
    private readonly Action checkForUpdates;
    private readonly Action<bool> autoOpenChanged;
    private readonly Func<bool> isPenumbraAvailable;
    private readonly Func<UpdateUiSnapshot> getUpdateStatus;

    private DeckView selectedView = DeckView.Home;
    private float mapZoom = DefaultMapZoom;
    private string? transientFeedback;
    private long transientFeedbackUntil;
    private readonly Dictionary<DeckView, int> badgeCounts = new();
    private readonly Dictionary<DeckView, Vector4> badgeColors = new();
    private long lastBadgeUpdateTick;
    private string? ambientTerminalMessage;
    private int lastAmbientMessageIndex = -1;
    private long ambientMessageStartedAt;
    private long ambientMessageUntil;
    private long nextAmbientMessageAt;
    private string? hoverGlitchTile;
    private long hoverGlitchLastSeenAt;
    private long hoverGlitchUntil;
    private long nextHoverGlitchAt;

    public bool IsOpen;
    public long InstallStatusTimestamp;
    public List<(bool? Ok, string Label)> InstallStatusItems { get; } = [];

    public CyberdeckWindow(
        PluginConfig config,
        PenumbraIpc penumbra,
        Dictionary<string, ISharedImmediateTexture> textures,
        string textureLoadSource,
        Action queueReconcile,
        Action queueReconcileForce,
        Action assignAll,
        Action checkForUpdates,
        Action<bool> autoOpenChanged,
        Func<bool> isPenumbraAvailable,
        Func<UpdateUiSnapshot> getUpdateStatus)
    {
        this.config = config;
        this.penumbra = penumbra;
        this.textures = textures;
        this.textureLoadSource = textureLoadSource;
        this.queueReconcile = queueReconcile;
        this.queueReconcileForce = queueReconcileForce;
        this.assignAll = assignAll;
        this.checkForUpdates = checkForUpdates;
        this.autoOpenChanged = autoOpenChanged;
        this.isPenumbraAvailable = isPenumbraAvailable;
        this.getUpdateStatus = getUpdateStatus;
    }

    public void OpenSettings()
    {
        selectedView = DeckView.Settings;
        IsOpen = true;
    }

    public void Draw()
    {
        if (!IsOpen)
            return;

        var uiScale = GetUiScale();
        using var theme = CyberdeckTheme.Push(uiScale);
        ImGui.SetNextWindowSize(GetInitialWindowSize(uiScale), ImGuiCond.FirstUseEver);
        var (minimumSize, maximumSize) = CyberdeckTheme.ResolveWindowConstraints(
            uiScale,
            new Vector2(280, 420),
            new Vector2(720, 1000));
        ImGui.SetNextWindowSizeConstraints(minimumSize, maximumSize);
        if (!ImGui.Begin("The Grid Cyberdeck", ref IsOpen, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.End();
            return;
        }

        ImGui.SetWindowFontScale(uiScale);
        UpdateBadges();
        var updateStatus = getUpdateStatus();
        if (config.FirstRunCompleted && selectedView != DeckView.Home && ShouldShowUpdateStatusRail(updateStatus))
        {
            DrawUpdateStatusRail(updateStatus);
            ImGui.Spacing();
        }

        if (ImGui.BeginChild("deck_body", new Vector2(0, 0), true))
        {
            var deckMin = ImGui.GetWindowPos();
            var deckMax = deckMin + ImGui.GetWindowSize();
            if (!config.FirstRunCompleted)
                DrawFirstRunPrompt();
            else
            {
                if (selectedView == DeckView.Home)
                    DrawHomeView();
                else
                    DrawAppScreen();
            }

            if (!config.ReduceMotion && updateStatus.IsBusy)
                DrawDeckScanline(deckMin, deckMax, uiScale);
        }

        ImGui.EndChild();
        DrawTransientFeedbackOverlay();
        ImGui.SetWindowFontScale(1.0f);
        ImGui.End();
    }

    private static bool ShouldShowUpdateStatusRail(UpdateUiSnapshot status)
    {
        if (status.IsBusy ||
            status.Phase is UpdateOperationPhase.Error or UpdateOperationPhase.NeedsAttention ||
            status.ReleaseAvailability == UpdateReleaseAvailability.UpdateAvailable)
            return true;

        return status.Phase == UpdateOperationPhase.Success &&
               status.CompletedAt is { } completedAt &&
               DateTimeOffset.UtcNow - completedAt < TimeSpan.FromSeconds(8);
    }

    private void DrawFirstRunPrompt()
    {
        ImGui.Spacing();
        ImGui.TextColored(CyberdeckTheme.Palette.Cyan, "Welcome to The Grid");
        DrawNeonSeparator();
        ImGui.Spacing();
        ImGui.TextWrapped("How should the plugin manage venue mod updates?");
        ImGui.Spacing();

        ImGui.TextColored(CyberdeckTheme.Palette.Amber, "RECOMMENDED");
        if (ImGui.Button("Automatic", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
        {
            config.FullAuto = true;
            config.FirstRunCompleted = true;
            config.Save();
            queueReconcile();
        }
        DrawMutedWrapped("Checks and installs venue updates automatically, then assigns the collection when you enter the venue.");

        ImGui.Spacing();
        if (ImGui.Button("Manual", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
        {
            config.FullAuto = false;
            config.FirstRunCompleted = true;
            config.Save();
        }
        DrawMutedWrapped("Only checks for availability. You decide when to download, install, and assign updates.");

        ImGui.Spacing();
        ImGui.TextDisabled("You can change this later in Settings.");
    }

    private void DrawUpdateStatusRail(UpdateUiSnapshot status)
    {
        var uiScale = GetUiScale();
        var statusColor = GetUpdateStatusColor(status);
        var height = (status.IsBusy ? 82f : 42f) * uiScale;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 7) * uiScale);
        if (ImGui.BeginChild("update_status_rail", new Vector2(0, height), true, ImGuiWindowFlags.NoScrollbar))
        {
            if (status.IsBusy)
            {
                var compactStatus = ImGui.GetContentRegionAvail().X < (300 * uiScale);
                CyberdeckWidgets.DrawSegmentedSpinner(
                    config.ReduceMotion,
                    statusColor,
                    18 * uiScale,
                    MathF.Max(1.5f, 2 * uiScale));
                ImGui.SameLine();
                ImGui.TextColored(statusColor, compactStatus ? GetOperationLabel(status) : status.Status);

                var progress = status.ProgressFraction is { } fraction ? (float)fraction : (float?)null;
                CyberdeckWidgets.DrawLabeledProgress(
                    GetOperationLabel(status),
                    progress,
                    config.ReduceMotion,
                    CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.42f),
                    statusColor,
                    CyberdeckTheme.Palette.Text,
                    CyberdeckTheme.Palette.TextMuted,
                    GetProgressValue(status),
                    height: 7 * uiScale);
            }
            else
            {
                CyberdeckWidgets.DrawStatusChip(
                    status.Status,
                    statusColor,
                    CyberdeckTheme.Palette.Text,
                    uiScale);
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private static Vector4 GetUpdateStatusColor(UpdateUiSnapshot status)
    {
        if (status.Phase == UpdateOperationPhase.Error)
            return CyberdeckTheme.Palette.Error;
        if (status.IsBusy ||
            status.Phase == UpdateOperationPhase.NeedsAttention ||
            status.ReleaseAvailability == UpdateReleaseAvailability.UpdateAvailable)
            return CyberdeckTheme.Palette.Amber;
        if (status.Phase == UpdateOperationPhase.Success || status.ReleaseAvailability == UpdateReleaseAvailability.UpToDate)
            return CyberdeckTheme.Palette.Success;
        return CyberdeckTheme.Palette.Cyan;
    }

    private static string GetOperationLabel(UpdateUiSnapshot status)
        => status.Phase switch
        {
            UpdateOperationPhase.Downloading => "PACKAGE TRANSFER",
            UpdateOperationPhase.Importing => "PENUMBRA IMPORT",
            UpdateOperationPhase.WaitingForPenumbra => "IPC HANDSHAKE",
            UpdateOperationPhase.Configuring => "COLLECTION CONFIG",
            UpdateOperationPhase.Assigning => "OBJECT ASSIGNMENT",
            _ => "RELEASE CHANNEL",
        };

    private static string GetProgressValue(UpdateUiSnapshot status)
    {
        if (status.BytesDownloaded is { } downloaded)
        {
            return status.TotalBytes is { } total
                ? $"{FormatBytes(downloaded)} / {FormatBytes(total)}"
                : FormatBytes(downloaded);
        }

        if (status.StartedAt is { } startedAt)
        {
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            return $"{Math.Max(0, (int)elapsed.TotalMinutes):00}:{Math.Max(0, elapsed.Seconds):00}";
        }

        return "ACTIVE";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
            return $"{bytes / (1024d * 1024d):0.0} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }

    private void DrawHomeView()
    {
        DrawDeckHeader();
        DrawNeonSeparator();
        ImGui.Spacing();
        DrawDeckButtons(ImGui.GetContentRegionAvail().X);
    }

    private void DrawAppScreen()
    {
        if (ImGui.Button("< HOME"))
            selectedView = DeckView.Home;

        ImGui.SameLine();
        ImGui.TextUnformatted($"// {GetDeckViewTitle(selectedView).ToUpperInvariant()}");
        DrawNeonSeparator();
        ImGui.Spacing();
        DrawDeckView();
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
        var uiScale = GetUiScale();
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 130 * uiScale);
        var max = start + size;
        var drawList = ImGui.GetWindowDrawList();
        ImGui.Dummy(size);

        drawList.PushClipRect(start, max, true);
        var rooftop = GetTextureWrap("rooftop.png");
        if (rooftop is not null)
        {
            var (uvMin, uvMax) = GetCoverUv(rooftop, size);
            drawList.AddImage(
                rooftop.Handle,
                start,
                max,
                uvMin,
                uvMax,
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(Vector4.One, 0.66f)));
        }

        drawList.AddRectFilled(start, max, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Background, 0.66f)), 6 * uiScale);
        drawList.AddRect(start, max, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.78f)), 6 * uiScale, ImDrawFlags.None, MathF.Max(1, uiScale));

        var logo = GetTextureWrap("grid.png");
        var textX = start.X + (14 * uiScale);
        if (logo is not null && size.X >= (270 * uiScale))
        {
            var logoSize = new Vector2(78, 78) * uiScale;
            var logoPos = start + new Vector2(12, 15) * uiScale;
            drawList.AddImage(logo.Handle, logoPos, logoPos + logoSize);
            textX = logoPos.X + logoSize.X + (10 * uiScale);
        }

        drawList.AddText(
            new Vector2(textX, start.Y + (24 * uiScale)),
            ImGui.GetColorU32(CyberdeckTheme.Palette.Text),
            "THE GRID // n_root");
        drawList.AddText(
            new Vector2(textX, start.Y + (50 * uiScale)),
            ImGui.GetColorU32(CyberdeckTheme.Palette.TextMuted),
            "CYBERDECK LINK // ONLINE");
        drawList.AddText(
            new Vector2(textX, start.Y + (76 * uiScale)),
            ImGui.GetColorU32(CyberdeckTheme.Palette.Cyan),
            config.VenueAddress);
        DrawAmbientTerminalBurst(drawList, start, size, uiScale);
        drawList.PopClipRect();

        if (ImGui.Button($"NAVIGATE // {config.VenueAddress}", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
            OpenAddress();
        DrawHoverTooltip(LifestreamNavigationTooltip);
        ImGui.Spacing();
    }

    private void DrawAmbientTerminalBurst(ImDrawListPtr drawList, Vector2 start, Vector2 size, float uiScale)
    {
        var now = Environment.TickCount64;
        var updateStatus = getUpdateStatus();
        if (ShouldShowUpdateStatusRail(updateStatus))
        {
            ambientTerminalMessage = null;
            DrawSystemTerminalStatus(drawList, start, size, uiScale, updateStatus);
            return;
        }

        if (config.ReduceMotion)
        {
            ambientTerminalMessage = null;
            if (nextAmbientMessageAt <= now)
                nextAmbientMessageAt = now + Random.Shared.Next(3000, 7001);
            return;
        }

        if (ambientTerminalMessage is null)
        {
            if (nextAmbientMessageAt == 0)
                nextAmbientMessageAt = now + Random.Shared.Next(1800, 4501);
            if (now < nextAmbientMessageAt)
                return;

            var index = Random.Shared.Next(AmbientTerminalMessages.Length);
            if (AmbientTerminalMessages.Length > 1 && index == lastAmbientMessageIndex)
                index = (index + 1) % AmbientTerminalMessages.Length;

            lastAmbientMessageIndex = index;
            ambientTerminalMessage = AmbientTerminalMessages[index];
            ambientMessageStartedAt = now;
            ambientMessageUntil = now + Random.Shared.Next(2100, 3001);
        }

        if (now >= ambientMessageUntil)
        {
            ambientTerminalMessage = null;
            nextAmbientMessageAt = now + Random.Shared.Next(8000, 18001);
            return;
        }

        if (ambientTerminalMessage is not { } message)
            return;
        var elapsed = Math.Max(0, now - ambientMessageStartedAt);
        var visibleCharacters = Math.Clamp((int)(elapsed / 24), 1, message.Length);
        var renderedMessage = $"> {message[..visibleCharacters]}{(visibleCharacters < message.Length ? "_" : string.Empty)}";
        renderedMessage = EllipsizeToWidth(renderedMessage, MathF.Max(1, size.X - (28 * uiScale)));

        var fadeRemaining = ambientMessageUntil - now;
        var alpha = fadeRemaining < 360 ? Math.Clamp(fadeRemaining / 360f, 0f, 1f) : 1f;
        var bandMin = new Vector2(start.X + (8 * uiScale), start.Y + (101 * uiScale));
        var bandMax = new Vector2(start.X + size.X - (8 * uiScale), start.Y + (123 * uiScale));
        drawList.AddRectFilled(
            bandMin,
            bandMax,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Panel, 0.70f * alpha)),
            2 * uiScale);
        drawList.AddLine(
            bandMin,
            new Vector2(bandMax.X, bandMin.Y),
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, 0.52f * alpha)),
            MathF.Max(1, uiScale));
        drawList.AddText(
            bandMin + new Vector2(7, 3) * uiScale,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, alpha)),
            renderedMessage);
    }

    private void DrawSystemTerminalStatus(
        ImDrawListPtr drawList,
        Vector2 start,
        Vector2 size,
        float uiScale,
        UpdateUiSnapshot status)
    {
        var statusColor = GetUpdateStatusColor(status);
        var bandMin = new Vector2(start.X + (8 * uiScale), start.Y + (101 * uiScale));
        var bandMax = new Vector2(start.X + size.X - (8 * uiScale), start.Y + (123 * uiScale));
        var statusText = $"> {status.Status}";
        if (status.IsBusy)
            statusText += $" // {GetProgressValue(status)}";
        statusText = EllipsizeToWidth(statusText, MathF.Max(1, size.X - (28 * uiScale)));

        drawList.AddRectFilled(
            bandMin,
            bandMax,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Panel, 0.88f)),
            2 * uiScale);
        drawList.AddLine(
            bandMin,
            new Vector2(bandMax.X, bandMin.Y),
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(statusColor, 0.82f)),
            MathF.Max(1, uiScale));
        drawList.AddText(
            bandMin + new Vector2(7, 3) * uiScale,
            ImGui.GetColorU32(statusColor),
            statusText);

        if (!status.IsBusy)
            return;

        var trackStart = new Vector2(bandMin.X, bandMax.Y - MathF.Max(1, uiScale));
        var trackEnd = new Vector2(bandMax.X, trackStart.Y);
        drawList.AddLine(
            trackStart,
            trackEnd,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.55f)),
            MathF.Max(1, uiScale));

        var trackWidth = trackEnd.X - trackStart.X;
        if (status.ProgressFraction is { } fraction)
        {
            drawList.AddLine(
                trackStart,
                new Vector2(trackStart.X + (trackWidth * (float)Math.Clamp(fraction, 0d, 1d)), trackStart.Y),
                ImGui.GetColorU32(statusColor),
                MathF.Max(1.5f, 2 * uiScale));
            return;
        }

        var signalWidth = MathF.Min(46 * uiScale, trackWidth * 0.28f);
        var travel = MathF.Max(0, trackWidth - signalWidth);
        var phase = config.ReduceMotion
            ? 0.5f
            : 0.5f + (0.5f * MathF.Sin((float)ImGui.GetTime() * 4.2f));
        var signalStart = new Vector2(trackStart.X + (travel * phase), trackStart.Y);
        drawList.AddLine(
            signalStart,
            signalStart + new Vector2(signalWidth, 0),
            ImGui.GetColorU32(statusColor),
            MathF.Max(1.5f, 2 * uiScale));
    }

    private static (Vector2 UvMin, Vector2 UvMax) GetCoverUv(IDalamudTextureWrap texture, Vector2 targetSize)
    {
        var sourceAspect = texture.Width / (float)texture.Height;
        var targetAspect = targetSize.X / MathF.Max(1, targetSize.Y);
        if (sourceAspect > targetAspect)
        {
            var visibleWidth = targetAspect / sourceAspect;
            var inset = (1 - visibleWidth) / 2;
            return (new Vector2(inset, 0), new Vector2(1 - inset, 1));
        }

        var visibleHeight = sourceAspect / targetAspect;
        var verticalInset = (1 - visibleHeight) / 2;
        return (new Vector2(0, verticalInset), new Vector2(1, 1 - verticalInset));
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
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.16f)));
        drawList.AddLine(
            new Vector2(min.X + (4 * uiScale), y + uiScale),
            new Vector2(max.X - (4 * uiScale), y + uiScale),
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Text, 0.32f)),
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
        drawList.AddLine(new Vector2(pos.X, y - uiScale), new Vector2(pos.X + width, y - uiScale), ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.20f)), 3.0f * uiScale);
        drawList.AddLine(new Vector2(pos.X, y), new Vector2(pos.X + width, y), ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.88f)), MathF.Max(1.0f, uiScale));
        drawList.AddLine(new Vector2(pos.X, y + (2 * uiScale)), new Vector2(pos.X + width, y + (2 * uiScale)), ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, 0.42f)), MathF.Max(1.0f, uiScale));
        ImGui.Dummy(new Vector2(width, 7 * uiScale));
    }

    private void DrawDeckButtons(float width)
    {
        var uiScale = GetUiScale();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var useTwoColumns = width >= (260 * uiScale);
        var buttonWidth = useTwoColumns ? (width - spacing) / 2 : width;
        var buttonHeight = (useTwoColumns ? 132f : 108f) * uiScale;
        var buttonSize = new Vector2(buttonWidth, buttonHeight);

        if (DrawImageNavButton("Menu", "menu.png", buttonSize))
            selectedView = DeckView.Menu;
        if (useTwoColumns)
            ImGui.SameLine();
        if (DrawImageNavButton("Wi-Fi", "wifi.png", buttonSize))
            selectedView = DeckView.Wifi;

        if (DrawImageNavButton("Address", "address.png", buttonSize))
            selectedView = DeckView.Map;
        if (useTwoColumns)
            ImGui.SameLine();
        var networkPos = ImGui.GetCursorScreenPos();
        if (DrawImageNavButton("Network", "network.png", buttonSize))
            selectedView = DeckView.Network;
        if (badgeCounts.TryGetValue(DeckView.Network, out var networkBadge) && networkBadge > 0)
            DrawTileBadge(networkPos, buttonSize, networkBadge, badgeColors.GetValueOrDefault(DeckView.Network, CyberdeckTheme.Palette.Error));

        DrawDisabledImageNavButton("Services", "services.png", buttonSize);
        if (useTwoColumns)
            ImGui.SameLine();
        var settingsPos = ImGui.GetCursorScreenPos();
        if (DrawImageNavButton("Settings", "settings.png", buttonSize))
            selectedView = DeckView.Settings;
        var updateStatus = getUpdateStatus();
        if (updateStatus.IsBusy)
            DrawTileActivityBadge(settingsPos, buttonSize, GetUpdateStatusColor(updateStatus));
        else if (badgeCounts.TryGetValue(DeckView.Settings, out var settingsBadge) && settingsBadge > 0)
            DrawTileBadge(settingsPos, buttonSize, settingsBadge, badgeColors.GetValueOrDefault(DeckView.Settings, CyberdeckTheme.Palette.Amber));
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
            var glitching = IsHoverGlitchActive(label, hovered);
            DrawTileGlow(start, size, hovered, glitching, uiScale);
            var iconSize = FitTileIcon(wrap, size, uiScale);
            var iconPos = new Vector2(start.X + (size.X - iconSize.X) / 2, start.Y + (12 * uiScale));
            if (glitching)
                DrawGlitchedImage(wrap.Handle, iconPos, iconSize, ImGui.GetColorU32(Vector4.One), uiScale, reduceMotion: false);
            else
                ImGui.GetWindowDrawList().AddImage(wrap.Handle, iconPos, iconPos + iconSize);
            if (glitching)
                DrawHoverGlitchOverlay(start, size, uiScale);

            var displayLabel = glitching ? ScrambleTileLabel(label) : label;
            var textWidth = ImGui.CalcTextSize(displayLabel).X;
            ImGui.GetWindowDrawList().AddText(
                new Vector2(start.X + MathF.Max(0, (size.X - textWidth) / 2), start.Y + size.Y - (25 * uiScale)),
                ImGui.GetColorU32(ImGuiCol.Text),
                displayLabel);
        }
        else
        {
            clicked = ImGui.Button(label, size);
            hovered = ImGui.IsItemHovered();
            var glitching = IsHoverGlitchActive(label, hovered);
            DrawTileGlow(start, size, hovered, glitching, uiScale);
            var displayLabel = glitching ? ScrambleTileLabel(label) : label;
            var textWidth = ImGui.CalcTextSize(displayLabel).X;
            ImGui.GetWindowDrawList().AddText(
                new Vector2(start.X + MathF.Max(0, (size.X - textWidth) / 2), start.Y + size.Y - (25 * uiScale)),
                ImGui.GetColorU32(ImGuiCol.Text),
                displayLabel);
        }
        ImGui.EndGroup();
        return clicked;
    }

    private static Vector2 FitTileIcon(IDalamudTextureWrap texture, Vector2 tileSize, float uiScale)
    {
        var naturalSize = GetTextureSize(texture, uiScale);
        var available = new Vector2(
            MathF.Max(1, tileSize.X - (20 * uiScale)),
            MathF.Max(1, tileSize.Y - (42 * uiScale)));
        var fit = MathF.Min(1.0f, MathF.Min(available.X / naturalSize.X, available.Y / naturalSize.Y));
        return naturalSize * fit;
    }

    private void DrawTileGlow(Vector2 start, Vector2 size, bool hovered, bool glitching, float uiScale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var max = start + size;
        var pulse = !config.ReduceMotion && hovered
            ? 0.82f + MathF.Sin((float)ImGui.GetTime() * 4.0f) * 0.18f
            : 1.0f;
        var cyanAlpha = (glitching ? 1.0f : hovered ? 0.90f : 0.38f) * pulse;
        var magentaAlpha = (glitching ? 1.0f : hovered ? 0.82f : 0.24f) * pulse;
        var cyan = ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, cyanAlpha));
        var magenta = ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, magentaAlpha));
        drawList.AddRect(start, max, cyan, 5 * uiScale, ImDrawFlags.None, (hovered ? 2.2f : 1.2f) * uiScale);

        var corner = MathF.Min(18 * uiScale, MathF.Min(size.X, size.Y) * 0.18f);
        var inset = 4 * uiScale;
        drawList.AddLine(start + new Vector2(inset, inset), start + new Vector2(inset + corner, inset), magenta, MathF.Max(1, 1.5f * uiScale));
        drawList.AddLine(start + new Vector2(inset, inset), start + new Vector2(inset, inset + corner), magenta, MathF.Max(1, 1.5f * uiScale));
        drawList.AddLine(max - new Vector2(inset + corner, inset), max - new Vector2(inset, inset), magenta, MathF.Max(1, 1.5f * uiScale));
        drawList.AddLine(max - new Vector2(inset, inset + corner), max - new Vector2(inset, inset), magenta, MathF.Max(1, 1.5f * uiScale));

        if (!config.ReduceMotion && hovered && !glitching)
        {
            var signalLength = MathF.Min(18 * uiScale, size.X * 0.14f);
            var travel = MathF.Max(0, size.X - (inset * 2) - signalLength);
            var phase = (float)(ImGui.GetTime() * 0.24) % 1f;
            var signalStart = new Vector2(start.X + inset + (travel * phase), max.Y - inset);
            drawList.AddLine(
                signalStart,
                signalStart + new Vector2(signalLength, 0),
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.86f)),
                MathF.Max(1.5f, 2 * uiScale));
        }
    }

    private bool IsHoverGlitchActive(string label, bool hovered)
    {
        if (config.ReduceMotion || !hovered)
            return false;

        var now = Environment.TickCount64;
        if (!string.Equals(hoverGlitchTile, label, StringComparison.Ordinal) || now - hoverGlitchLastSeenAt > 160)
        {
            hoverGlitchTile = label;
            hoverGlitchUntil = 0;
            nextHoverGlitchAt = now + Random.Shared.Next(420, 1101);
        }

        hoverGlitchLastSeenAt = now;
        if (hoverGlitchUntil <= now && now >= nextHoverGlitchAt)
        {
            hoverGlitchUntil = now + Random.Shared.Next(100, 181);
            nextHoverGlitchAt = hoverGlitchUntil + Random.Shared.Next(2600, 5201);
        }

        return now < hoverGlitchUntil;
    }

    private static void DrawHoverGlitchOverlay(Vector2 start, Vector2 size, float uiScale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var tick = Environment.TickCount64 / 24;
        var firstY = start.Y + size.Y * (0.24f + ((tick % 5) * 0.09f));
        var secondY = start.Y + size.Y * (0.62f + ((tick % 3) * 0.07f));
        drawList.AddRectFilled(
            new Vector2(start.X - (2 * uiScale), firstY),
            new Vector2(start.X + size.X + (3 * uiScale), firstY + (2 * uiScale)),
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.58f)));
        drawList.AddRectFilled(
            new Vector2(start.X + (5 * uiScale), secondY),
            new Vector2(start.X + size.X - (4 * uiScale), secondY + uiScale),
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, 0.64f)));
    }

    private static string ScrambleTileLabel(string label)
    {
        if (label.Length == 0)
            return label;

        const string substitutions = "#/_73X";
        var characters = label.ToCharArray();
        var seed = unchecked((uint)(Environment.TickCount64 / 32));
        var position = (int)(seed % (uint)characters.Length);
        characters[position] = substitutions[(int)((seed / (uint)Math.Max(1, characters.Length)) % substitutions.Length)];
        return new string(characters);
    }

    private void DrawDisabledImageNavButton(string label, string imageName, Vector2 size)
    {
        ImGui.BeginGroup();
        var wrap = GetTextureWrap(imageName);
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        ImGui.BeginDisabled();
        ImGui.Button($"##tile_{label}", size);
        ImGui.EndDisabled();

        var textWidth = ImGui.CalcTextSize(label).X;
        var uiScale = GetUiScale();
        var textPos = new Vector2(start.X + MathF.Max(0, (size.X - textWidth) / 2), start.Y + size.Y - (25 * uiScale));
        var disabledColor = ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.TextMuted, 0.60f));

        if (wrap is not null)
        {
            var iconSize = FitTileIcon(wrap, size, uiScale);
            var iconPos = new Vector2(start.X + (size.X - iconSize.X) / 2, start.Y + (12 * uiScale));
            DrawGlitchedImage(wrap.Handle, iconPos, iconSize, disabledColor, uiScale, config.ReduceMotion);
        }

        drawList.AddText(textPos, disabledColor, label);
        DrawGlitchOverlay(start, size, textPos, label, uiScale, config.ReduceMotion);

        const string offlineLabel = "OFFLINE";
        var offlineSize = ImGui.CalcTextSize(offlineLabel);
        var offlinePadding = new Vector2(6, 3) * uiScale;
        var offlineMax = start + new Vector2(size.X - (8 * uiScale), (8 * uiScale) + offlineSize.Y + (offlinePadding.Y * 2));
        var offlineMin = offlineMax - offlineSize - (offlinePadding * 2);
        drawList.AddRectFilled(offlineMin, offlineMax, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Error, 0.14f)), 3 * uiScale);
        drawList.AddRect(offlineMin, offlineMax, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Error, 0.66f)), 3 * uiScale);
        drawList.AddText(offlineMin + offlinePadding, ImGui.GetColorU32(CyberdeckTheme.Palette.Error), offlineLabel);
        ImGui.EndGroup();
    }

    private static void DrawGlitchedImage(ImTextureID textureHandle, Vector2 iconPos, Vector2 iconSize, uint baseColor, float uiScale, bool reduceMotion)
    {
        var drawList = ImGui.GetWindowDrawList();
        if (reduceMotion)
        {
            drawList.AddImage(textureHandle, iconPos, iconPos + iconSize, Vector2.Zero, Vector2.One, baseColor);
            return;
        }

        var t = (float)ImGui.GetTime();
        var shift = MathF.Sin(t * 1.05f) * 2.8f * uiScale;
        var jitter = MathF.Sin(t * 1.65f) > 0.78f ? 2.0f * uiScale : 0.0f;

        drawList.AddImage(textureHandle, iconPos + new Vector2(-shift - jitter, 0), iconPos + iconSize + new Vector2(-shift - jitter, 0), Vector2.Zero, Vector2.One, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Error, 0.20f)));
        drawList.AddImage(textureHandle, iconPos + new Vector2(shift + jitter, 0), iconPos + iconSize + new Vector2(shift + jitter, 0), Vector2.Zero, Vector2.One, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.22f)));
        drawList.AddImage(textureHandle, iconPos, iconPos + iconSize, Vector2.Zero, Vector2.One, baseColor);
    }

    private static void DrawGlitchOverlay(Vector2 start, Vector2 size, Vector2 textPos, string label, float uiScale, bool reduceMotion)
    {
        if (reduceMotion)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var t = (float)ImGui.GetTime();
        var pulse = 0.62f + (MathF.Sin(t * 7.0f) * 0.22f);
        var cyan = ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.42f * pulse));
        var red = ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Error, 0.38f * pulse));

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
        ImGui.TextWrapped(config.VenueAddress);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var stackActions = availableWidth < (260 * GetUiScale());
        var actionWidth = stackActions
            ? availableWidth
            : MathF.Max(1, (availableWidth - spacing) / 2);
        if (ImGui.Button("Copy Address", new Vector2(actionWidth, 0)))
            CopyToClipboard(config.VenueAddress, "ADDRESS COPIED");
        DrawHoverTooltip("Copy to clipboard");
        if (stackActions)
            ImGui.Spacing();
        else
            ImGui.SameLine();
        if (ImGui.Button("Navigate", new Vector2(actionWidth, 0)))
            OpenAddress();
        DrawHoverTooltip(LifestreamNavigationTooltip);

        ImGui.Spacing();

        var compactToolbar = ImGui.GetContentRegionAvail().X < (280 * GetUiScale());
        var zoomOutLabel = compactToolbar ? "-##map_zoom_out" : "Zoom -##map_zoom_out";
        var zoomInLabel = compactToolbar ? "+##map_zoom_in" : "Zoom +##map_zoom_in";
        var fitLabel = compactToolbar ? "FIT##map_zoom_fit" : "Fit##map_zoom_fit";
        var compactButtonSize = compactToolbar ? new Vector2(42 * GetUiScale(), 0) : Vector2.Zero;
        if (ImGui.Button(zoomOutLabel, compactButtonSize))
            mapZoom = MathF.Max(0.2f, mapZoom - 0.1f);
        ImGui.SameLine();
        if (ImGui.Button(zoomInLabel, compactButtonSize))
            mapZoom = MathF.Min(3.0f, mapZoom + 0.1f);
        ImGui.SameLine();
        if (ImGui.Button(fitLabel, compactButtonSize))
            mapZoom = DefaultMapZoom;
        if (!compactToolbar)
            ImGui.SameLine();
        else
            ImGui.Spacing();
        ImGui.TextDisabled($"{mapZoom:0.00}x");
        DrawMutedWrapped("Mouse wheel to zoom // drag to pan");
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

            var narrowCard = ImGui.GetContentRegionAvail().X < (340 * GetUiScale());
            var wrap = GetTextureWrap(item.ImageName);
            if (wrap is not null)
            {
                ImGui.Image(wrap.Handle, GetTextureSize(wrap, GetUiScale()));
                if (!narrowCard)
                    ImGui.SameLine();
                else
                    ImGui.Spacing();
            }

            ImGui.BeginGroup();
            ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.Palette.Cyan);
            ImGui.TextWrapped(item.Name);
            ImGui.PopStyleColor();
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, $"{item.Price} gil");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Copy##drink_{item.Name}"))
                CopyToClipboard(item.Name, "DRINK NAME COPIED");
            DrawHoverTooltip("Copy to clipboard");
            ImGui.TextWrapped(item.Description);
            if (ImGui.CollapsingHeader($"FLAVOR PROFILE##drink_profile_{i}"))
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                ImGui.TextDisabled($"Ingredients: {item.Ingredients}");
                ImGui.PopTextWrapPos();
                ImGui.TextWrapped($"Taste: {item.Taste}");
            }
            ImGui.EndGroup();
        }
    }

    private void DrawNetworkView()
    {
        var players = PluginService.Objects
            .OfType<IPlayerCharacter>()
            .Where(IsNetworkPlayer)
            .GroupBy(GetPlayerTellName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(GetPlayerTellName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ImGui.TextUnformatted($"Local players detected: {players.Count}");
        ImGui.TextDisabled("Client-visible players in this instance.");
        DrawMutedWrapped("<!> alert // ★ friend // weapon and minion icons show visible status");
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
            DrawNetworkPlayerRow(player, IsFriend(player));

        ImGui.EndTable();
    }

    private void DrawNetworkPlayerRow(IPlayerCharacter player, bool isFriend)
    {
        var tellName = GetPlayerTellName(player);
        var status = GetNetworkPlayerStatus(player);

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (status.HasAlert)
        {
            var pulse = config.ReduceMotion
                ? 1.0f
                : 0.72f + MathF.Sin((float)ImGui.GetTime() * 6.0f) * 0.28f;
            ImGui.TextColored(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Error, pulse), "<!>");
            DrawHoverTooltip("Player has weapon/offhand drawn and/or visible minion present");
        }

        ImGui.TableSetColumnIndex(1);
        if (isFriend)
        {
            var glow = config.ReduceMotion
                ? 1.0f
                : 0.80f + MathF.Sin((float)ImGui.GetTime() * 3.0f) * 0.20f;
            ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Amber, glow));
        }
        if (ImGui.Selectable(tellName, false, ImGuiSelectableFlags.SpanAllColumns))
            PluginService.Targets.Target = player;
        if (isFriend)
        {
            ImGui.PopStyleColor();
            DrawHoverTooltip("★ Friend");
        }

        ImGui.TableSetColumnIndex(2);
        if (status.HasWeapon)
            DrawNetworkStatusIcon("weapon.png", "Weapon", GetWeaponTooltip(status));

        ImGui.TableSetColumnIndex(3);
        if (!string.IsNullOrWhiteSpace(status.MinionName))
            DrawNetworkStatusIcon("minion.png", "Minion", $"Visible minion present: {status.MinionName}");
    }

    private static bool IsFriend(IPlayerCharacter player)
        => player.StatusFlags.HasFlag(StatusFlags.Friend);

    private static bool IsNetworkPlayer(IPlayerCharacter player)
        => player.ObjectKind == ObjectKind.Pc && !string.IsNullOrWhiteSpace(player.Name.TextValue);

    private static bool IsVenueMannequinInRange(ModMapping mapping)
    {
        var mannequinFallbackCandidates = 0;
        for (var i = 0; i < PluginService.Objects.Length; i++)
        {
            var obj = PluginService.Objects[i];
            if (obj is null || PluginService.Objects.LocalPlayer?.GameObjectId == obj.GameObjectId)
                continue;

            if (NamesMatch(obj.Name.TextValue, mapping.NpcName))
                return true;

            if (NamesMatch(obj.Name.TextValue, "Mannequin"))
                mannequinFallbackCandidates++;
        }

        return mannequinFallbackCandidates == 1;
    }

    private static bool NamesMatch(string objectName, string targetName)
    {
        var normalizedObject = objectName.Trim();
        var normalizedTarget = targetName.Trim();
        return normalizedObject.Length > 0 &&
               normalizedTarget.Length > 0 &&
               (string.Equals(normalizedObject, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                normalizedObject.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                normalizedTarget.Contains(normalizedObject, StringComparison.OrdinalIgnoreCase));
    }

    private static NetworkPlayerStatus GetNetworkPlayerStatus(IPlayerCharacter player)
        => new(
            IsWeaponDisplayed(player),
            player.StatusFlags.HasFlag(StatusFlags.WeaponOut),
            player.StatusFlags.HasFlag(StatusFlags.OffhandOut),
            GetVisibleMinionName(player));

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

    private static string GetWeaponTooltip(NetworkPlayerStatus status)
    {
        if (status.WeaponDisplayed == true)
        {
            if (status.WeaponOut && status.OffhandOut)
                return "Weapon displayed; weapon and offhand drawn";
            if (status.WeaponOut)
                return "Weapon displayed and drawn";
            if (status.OffhandOut)
                return "Weapon displayed; offhand drawn";

            return "Weapon displayed";
        }

        return (status.WeaponOut, status.OffhandOut) switch
        {
            (true, true) => "Weapon and offhand drawn",
            (false, true) => "Offhand drawn",
            _ => "Weapon drawn",
        };
    }

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
        var mapping = config.GetPrimaryMapping();
        var penumbraAvailable = isPenumbraAvailable();
        var collection = penumbraAvailable ? FindCollectionSafely(mapping.CollectionName) : null;
        var modDirectory = GetImportedModDirectory(mapping, penumbraAvailable);
        var updateStatus = getUpdateStatus();

        DrawSettingsGroupHeader("UPDATE CHANNEL");
        DrawUpdateOperationDetails(updateStatus, mapping.LastStatus);
        ImGui.Spacing();
        DrawUpdaterActions(updateStatus, modDirectory);
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        DrawSettingsGroupHeader("SYSTEM HEALTH");
        DrawStatusCheck(penumbraAvailable, "Penumbra");
        var modLabel = modDirectory is not null && !string.IsNullOrWhiteSpace(mapping.LastAppliedVersion)
            ? $"Venue mod (v{mapping.LastAppliedVersion})"
            : "Venue mod";
        DrawStatusCheck(modDirectory is not null, modLabel);

        var collectionLabel = collection is not null
            ? $"Collection '{collection.Value.Name}'"
            : $"Collection matching '{mapping.CollectionName}'";
        DrawStatusCheck(collection is not null, collectionLabel);
        if (collection is null)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (22 * GetUiScale()));
            if (ImGui.SmallButton("Open Penumbra"))
                PluginService.Commands.ProcessCommand("/penumbra");
            DrawHoverTooltip("Open Penumbra to create the collection");
        }

        if (penumbraAvailable && modDirectory is not null && collection is not null)
        {
            bool modEnabled;
            try { modEnabled = penumbra.IsModEnabled(collection.Value.Id, modDirectory, mapping.ModName); }
            catch { modEnabled = false; }

            DrawStatusCheck(modEnabled, $"Mod enabled in '{collection.Value.Name}'");
            if (!modEnabled)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (22 * GetUiScale()));
                if (CyberdeckWidgets.DrawActionButton("Enable", updateStatus.IsBusy))
                    assignAll();
                DrawHoverTooltip("Enable the mod in the collection");
            }

            var npcFound = IsVenueMannequinInRange(mapping);

            if (npcFound)
            {
                if (config.FullAuto)
                {
                    var cachedNpc = InstallStatusItems.FirstOrDefault(s => s.Label.Contains(mapping.NpcName));
                    DrawStatusCheck(cachedNpc != default ? cachedNpc.Ok : null,
                        cachedNpc != default ? "Mannequin assigned" : "Mannequin in range");
                }
                else
                {
                    DrawStatusCheck(null, "Mannequin is in range");
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (22 * GetUiScale()));
                    if (CyberdeckWidgets.DrawActionButton("Assign", updateStatus.IsBusy))
                        assignAll();
                    DrawHoverTooltip("Assign collection to NPC and redraw");
                }
            }
            else
            {
                DrawStatusCheck(null, "Mannequin not in range");
            }
        }
        else if (modDirectory is null || collection is null)
        {
            DrawStatusCheck(null, "Install not yet confirmed");
        }

        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        DrawSettingsGroupHeader("INTERFACE");
        DrawInterfaceSettings();
    }

    private void DrawUpdateOperationDetails(UpdateUiSnapshot status, string fallbackDetail)
    {
        var statusColor = GetUpdateStatusColor(status);
        CyberdeckWidgets.DrawStatusChip(status.Status, statusColor, CyberdeckTheme.Palette.Text, GetUiScale());

        var installed = status.InstalledVersion is { Length: > 0 } installedVersion ? $"v{installedVersion}" : "NONE";
        var latest = status.LatestVersion is { Length: > 0 } latestVersion ? $"v{latestVersion}" : "UNKNOWN";
        if (ImGui.GetContentRegionAvail().X < (300 * GetUiScale()))
        {
            ImGui.TextDisabled($"INSTALLED // {installed}");
            ImGui.TextDisabled($"LATEST // {latest}");
        }
        else
        {
            ImGui.TextDisabled($"INSTALLED // {installed}    LATEST // {latest}");
        }

        var detail = !string.IsNullOrWhiteSpace(status.Detail) && status.Detail != "No update operation is active."
            ? status.Detail
            : fallbackDetail;
        if (!string.IsNullOrWhiteSpace(detail))
            ImGui.TextWrapped(detail);

        if (status.CompletedAt is { } completedAt)
            ImGui.TextDisabled($"Last result: {completedAt.ToLocalTime():HH:mm:ss}");

        if (!status.IsBusy)
            return;

        var progress = status.ProgressFraction is { } fraction ? (float)fraction : (float?)null;
        CyberdeckWidgets.DrawLabeledProgress(
            GetOperationLabel(status),
            progress,
            config.ReduceMotion,
            CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.42f),
            statusColor,
            CyberdeckTheme.Palette.Text,
            CyberdeckTheme.Palette.TextMuted,
            GetProgressValue(status),
            height: 8 * GetUiScale());
    }

    private void DrawUpdaterActions(UpdateUiSnapshot status, string? modDirectory)
    {
        var availableVersion = status.AvailableVersion;
        var needsInstall = modDirectory is null;
        var primaryLabel = needsInstall
            ? "Install Venue Mod"
            : availableVersion is not null
                ? $"Install v{availableVersion}"
                : "Sync / Update Now";
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var stackActions = availableWidth < (320 * GetUiScale());
        var actionWidth = stackActions
            ? availableWidth
            : MathF.Max(1, (availableWidth - spacing) / 2);

        using (CyberdeckTheme.PushAccentButton())
        {
            if (CyberdeckWidgets.DrawActionButton(primaryLabel, status.IsBusy, new Vector2(actionWidth, 0)))
            {
                if (needsInstall)
                    queueReconcileForce();
                else
                    queueReconcile();
            }
        }

        if (stackActions)
            ImGui.Spacing();
        else
            ImGui.SameLine();
        if (CyberdeckWidgets.DrawActionButton("Check Now", status.IsBusy, new Vector2(actionWidth, 0)))
            checkForUpdates();
        DrawHoverTooltip("Check availability without installing anything");

        if (status.Phase is UpdateOperationPhase.Error or UpdateOperationPhase.NeedsAttention)
        {
            ImGui.Spacing();
            var retryLabel = status.Phase == UpdateOperationPhase.NeedsAttention
                ? status.Operation switch
                {
                    UpdateOperationKind.None => "Validate New Mapping",
                    UpdateOperationKind.Assignment => "Retry Penumbra Setup",
                    UpdateOperationKind.Repair => "Retry Repair",
                    _ => "Retry Sync",
                }
                : "Retry Last Operation";
            if (CyberdeckWidgets.DrawActionButton(retryLabel, status.IsBusy))
            {
                if (status.Phase == UpdateOperationPhase.NeedsAttention)
                {
                    if (status.Operation == UpdateOperationKind.None)
                        queueReconcile();
                    else if (status.Operation == UpdateOperationKind.Assignment)
                        assignAll();
                    else if (status.Operation == UpdateOperationKind.Repair)
                        queueReconcileForce();
                    else
                        queueReconcile();
                }
                else if (status.Operation == UpdateOperationKind.UpdateCheck)
                    checkForUpdates();
                else if (status.Operation == UpdateOperationKind.Assignment)
                    assignAll();
                else if (status.Operation == UpdateOperationKind.Repair)
                    queueReconcileForce();
                else
                    queueReconcile();
            }
        }

        ImGui.Spacing();
        if (CyberdeckWidgets.DrawActionButton("Repair / Reinstall...", status.IsBusy))
            ImGui.OpenPopup("confirm_reinstall");

        if (ImGui.BeginPopup("confirm_reinstall"))
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "REPAIR VENUE MOD");
            ImGui.TextWrapped("This downloads the latest package again and replaces the managed Penumbra mod. Use it when files are missing or corrupted.");
            ImGui.Spacing();
            using (CyberdeckTheme.PushAccentButton())
            {
                if (CyberdeckWidgets.DrawActionButton("Reinstall", status.IsBusy))
                {
                    queueReconcileForce();
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void DrawStatusCheck(bool? ok, string label)
    {
        switch (ok)
        {
            case true:
                ImGui.TextColored(CyberdeckTheme.Palette.Success, "<\u2713>");
                break;
            case false:
                var pulse = config.ReduceMotion
                    ? 1.0f
                    : 0.72f + MathF.Sin((float)ImGui.GetTime() * 6.0f) * 0.28f;
                ImGui.TextColored(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Error, pulse), "<X>");
                break;
            default:
                ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, "<->");
                break;
        }

        ImGui.SameLine();
        ImGui.TextWrapped(label);
    }

    private void DrawStatusCheck(bool ok, string label)
        => DrawStatusCheck((bool?)ok, label);

    private static void DrawSettingsGroupHeader(string label)
        => ImGui.TextColored(CyberdeckTheme.Palette.Cyan, label);

    private string? GetImportedModDirectory(ModMapping mapping, bool penumbraAvailable)
    {
        if (!penumbraAvailable)
            return null;

        try
        {
            return Plugin.FindInstalledModDirectory(mapping, penumbra.GetModList());
        }
        catch (Exception ex)
        {
            PluginService.Log.Debug(ex, "Could not check imported Penumbra mod status.");
            return null;
        }
    }

    private void DrawInterfaceSettings()
    {
        var effectiveScale = GetUiScale();
        var scaleLabel = config.UiScale <= 0
            ? $"UI Scale: Auto ({effectiveScale:0.##}x)"
            : $"UI Scale: {effectiveScale:0.##}x";

        ImGui.TextUnformatted(scaleLabel);
        if (ImGui.SmallButton("Auto##ui_scale_auto"))
        {
            config.UiScale = 0;
            config.Save();
            ImGui.SetWindowSize("The Grid Cyberdeck", GetInitialWindowSize(GetUiScale()));
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("-##ui_scale_down"))
        {
            SetManualUiScale(GetPreviousUiScale(effectiveScale));
            ImGui.SetWindowSize("The Grid Cyberdeck", GetInitialWindowSize(GetUiScale()));
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("+##ui_scale_up"))
        {
            SetManualUiScale(GetNextUiScale(effectiveScale));
            ImGui.SetWindowSize("The Grid Cyberdeck", GetInitialWindowSize(GetUiScale()));
        }

        ImGui.Spacing();
        if (ImGui.Button("Restore Window Size"))
            ImGui.SetWindowSize("The Grid Cyberdeck", GetInitialWindowSize(GetUiScale()));

        ImGui.Spacing();
        var networkAlert = config.NetworkAlertBadge;
        if (ImGui.Checkbox("Network alert badge", ref networkAlert))
        {
            config.NetworkAlertBadge = networkAlert;
            config.Save();
        }
        DrawHoverTooltip("Show player count with weapons/minions on the Network tile");

        var animationsEnabled = !config.ReduceMotion;
        if (ImGui.Checkbox("Animations & ambient FX", ref animationsEnabled))
        {
            config.ReduceMotion = !animationsEnabled;
            config.Save();
        }
        DrawMutedWrapped("Controls terminal bursts, hover glitches, pulses, scanlines, and loading animation together.");

        var autoOpenOnEntrance = config.AutoOpenOnVenueAddress;
        if (ImGui.Checkbox("Auto-open Cyberdeck", ref autoOpenOnEntrance))
        {
            config.AutoOpenOnVenueAddress = autoOpenOnEntrance;
            config.Save();
            autoOpenChanged(autoOpenOnEntrance);
        }
        DrawMutedWrapped("Opens automatically when you enter the venue address.");

        var fullAuto = config.FullAuto;
        ImGui.BeginDisabled(getUpdateStatus().IsBusy);
        if (ImGui.Checkbox("Automatic updates", ref fullAuto))
        {
            config.FullAuto = fullAuto;
            config.Save();
            if (fullAuto)
                queueReconcile();
        }
        ImGui.EndDisabled();
        DrawHoverTooltip("Automatically download mod updates on login and assign collections on zone changes. When disabled, you'll be notified of new versions but must press Update/Install manually.");
    }

    private void SetManualUiScale(float uiScale)
    {
        config.UiScale = ClampUiScale(uiScale);
        config.Save();
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

    private static void DrawMutedWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.Palette.TextMuted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static string EllipsizeToWidth(string text, float maximumWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maximumWidth)
            return text;

        const string ellipsis = "...";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..middle] + ellipsis).X <= maximumWidth)
                low = middle;
            else
                high = middle - 1;
        }

        return text[..low].TrimEnd() + ellipsis;
    }

    private void DrawCopyableTerminalLine(string label, string value, string id)
    {
        var compact = ImGui.GetContentRegionAvail().X < (300 * GetUiScale());
        ImGui.TextDisabled(">");
        ImGui.SameLine();
        if (compact)
            ImGui.TextWrapped($"{label}: {value}");
        else
            ImGui.TextUnformatted($"{label}: {value}");
        if (!compact)
            ImGui.SameLine();
        else
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (22 * GetUiScale()));
        if (ImGui.SmallButton($"Copy##{id}"))
            CopyToClipboard(value, $"{label.ToUpperInvariant()} COPIED");
        DrawHoverTooltip("Copy to clipboard");
    }

    private void CopyToClipboard(string value, string feedback = "DATA COPIED")
    {
        ImGui.SetClipboardText(value);
        SetTransientFeedback(feedback);
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

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Panel, 0.94f)), 5 * uiScale);
        drawList.AddRect(min, max, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.62f)), 5 * uiScale);
        drawList.AddText(min + padding, ImGui.GetColorU32(CyberdeckTheme.Palette.Text), transientFeedback);
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

    private static string? GetVisibleMinionName(IPlayerCharacter player)
    {
        if (player.CurrentMount is not null)
            return null;

        return player.CurrentMinion?.ValueNullable?.Singular.ExtractText();
    }

    private static string GetWorldName(IPlayerCharacter player)
    {
        var homeWorld = player.HomeWorld.ValueNullable?.Name.ExtractText();
        if (!string.IsNullOrWhiteSpace(homeWorld))
            return homeWorld;

        var currentWorld = player.CurrentWorld.ValueNullable?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(currentWorld) ? "Unknown World" : currentWorld;
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
        => CyberdeckTheme.ResolveUiScale(config.UiScale);

    private static float ClampUiScale(float uiScale)
        => Math.Clamp(uiScale, 1.0f, 2.0f);

    private void OpenAddress()
    {
        if (string.IsNullOrWhiteSpace(config.VenueAddress))
            return;

        SetTransientFeedback("Opening Lifestream...");
        PluginService.Commands.ProcessCommand($"/li {config.VenueAddress}");
    }

    private void OpenDiscord()
    {
        if (!string.IsNullOrWhiteSpace(config.DiscordUrl))
            Util.OpenLink(config.DiscordUrl);
    }

    private (Guid Id, string Name)? FindCollectionSafely(string collectionName)
    {
        try
        {
            return penumbra.FindCollectionByName(collectionName);
        }
        catch (Exception ex)
        {
            PluginService.Log.Debug(ex, "Could not check Penumbra collection {Collection}.", collectionName);
            return null;
        }
    }

    private static string DisplayValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "(unset)" : value;

    private void UpdateBadges()
    {
        var now = Environment.TickCount64;
        if (now - lastBadgeUpdateTick < 5000)
            return;
        lastBadgeUpdateTick = now;

        badgeCounts.Clear();
        badgeColors.Clear();

        try
        {
            var faults = 0;
            var actions = 0;
            var mapping = config.GetPrimaryMapping();
            var penumbraAvailable = isPenumbraAvailable();
            var updateStatus = getUpdateStatus();

            if (!penumbraAvailable)
                faults++;

            if (penumbraAvailable)
            {
                var modDir = GetImportedModDirectory(mapping, penumbraAvailable);
                if (modDir is null) actions++;

                if (FindCollectionSafely(mapping.CollectionName) is null) actions++;
            }

            faults += InstallStatusItems.Count(s => s.Ok == false);
            if (updateStatus.Phase == UpdateOperationPhase.Error)
                faults++;
            else if (updateStatus.Phase == UpdateOperationPhase.NeedsAttention)
                actions++;
            if (updateStatus.ReleaseAvailability == UpdateReleaseAvailability.UpdateAvailable)
                actions++;

            var count = faults + actions;
            if (count > 0)
            {
                badgeCounts[DeckView.Settings] = count;
                badgeColors[DeckView.Settings] = faults > 0
                    ? CyberdeckTheme.Palette.Error
                    : CyberdeckTheme.Palette.Amber;
            }
        }
        catch
        {
            // Silently ignore badge computation failures
        }

        if (config.NetworkAlertBadge)
        {
            try
            {
                var flagged = PluginService.Objects
                    .OfType<IPlayerCharacter>()
                    .Count(player => IsNetworkPlayer(player) && GetNetworkPlayerStatus(player).HasAlert);

                if (flagged > 0)
                {
                    badgeCounts[DeckView.Network] = flagged;
                    badgeColors[DeckView.Network] = CyberdeckTheme.Palette.Error;
                }
            }
            catch
            {
                // Silently ignore network badge computation failures
            }
        }
    }

    private void DrawTileBadge(Vector2 tileStart, Vector2 tileSize, int count, Vector4 color)
    {
        if (count <= 0) return;

        var uiScale = GetUiScale();
        var radius = 11 * uiScale;
        var center = new Vector2(
            tileStart.X + tileSize.X - radius - (2 * uiScale),
            tileStart.Y + radius + (2 * uiScale));

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(color));
        drawList.AddCircle(center, radius, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, 0.60f)), 0, 1.5f * uiScale);

        var text = count.ToString();
        var textSize = ImGui.CalcTextSize(text);
        drawList.AddText(
            center - textSize / 2,
            ImGui.GetColorU32(Vector4.One),
            text);
    }

    private void DrawTileActivityBadge(Vector2 tileStart, Vector2 tileSize, Vector4 color)
    {
        var uiScale = GetUiScale();
        var radius = 12 * uiScale;
        var center = new Vector2(
            tileStart.X + tileSize.X - radius - (2 * uiScale),
            tileStart.Y + radius + (2 * uiScale));
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(CyberdeckTheme.Palette.Panel));
        drawList.AddCircle(center, radius, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, 0.72f)), 0, 1.5f * uiScale);

        const int segments = 8;
        var head = config.ReduceMotion ? 0 : (int)((float)ImGui.GetTime() * 7) % segments;
        for (var index = 0; index < segments; index++)
        {
            var angle = ((MathF.PI * 2 * index) / segments) - (MathF.PI / 2);
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var alpha = config.ReduceMotion || index == head ? 1.0f : 0.24f;
            drawList.AddLine(
                center + direction * (5 * uiScale),
                center + direction * (9 * uiScale),
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, alpha)),
                MathF.Max(1.5f, 2 * uiScale));
        }
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

    private readonly record struct NetworkPlayerStatus(bool? WeaponDisplayed, bool WeaponOut, bool OffhandOut, string? MinionName)
    {
        public bool HasWeapon => WeaponDisplayed == true || WeaponOut || OffhandOut;
        public bool HasAlert => HasWeapon || !string.IsNullOrWhiteSpace(MinionName);
    }

    private sealed record DrinkMenuItem(string Name, string Price, string ImageName, string Ingredients, string Description, string Taste);
}
