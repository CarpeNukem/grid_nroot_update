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

namespace GridNrootUpdate;

internal sealed partial class CyberdeckWindow
{
    private const float DefaultMapZoom = 0.44f;
    private const string LifestreamNavigationTooltip = "Click to navigate. Requires Lifestream plugin to work";
    private static readonly float[] ManualUiScales = [1.0f, 1.25f, 1.5f, 1.75f, 2.0f];
    private static readonly CyberdeckThemeId[] ThemeOptions = Enum.GetValues<CyberdeckThemeId>();
    private static readonly HashSet<string> VenueManagerIdentities = new(StringComparer.OrdinalIgnoreCase)
    {
        "Carpe Nukem@Raiden",
        "Rhas J'ae@Raiden",
        "Sketch Nine@Raiden",
    };
    private const string LightlessSyncshellId = "LLS-6AAKEJBAPRB0";
    private const string PlayerSyncSyncshellId = "n_root";
    private const string TarotReaderRecipient = "Virginia John@Raiden";
    private const string TarotReadingRequestMessage = "Hello, I'd like to request a tarot reading.";
    private static readonly string IntrusionEncryptedPayload = DecodeIntrusionReward(
        [0x59, 0x26, 0x56, 0x29, 0x0A, 0x2F, 0x3C, 0x07, 0x0C, 0x2E, 0x57, 0x31, 0x55, 0x3D, 0x37, 0x17, 0x5D, 0x26, 0x25, 0x59]);
    private static readonly string IntrusionPayloadHint = DecodeIntrusionReward(
        [0x54, 0x1C, 0x52, 0x50, 0x44, 0x09, 0x05, 0x17, 0x0F, 0x17, 0x44, 0x10, 0x0C, 0x01, 0x44, 0x14, 0x05, 0x10, 0x0C, 0x4A, 0x44, 0x36, 0x05, 0x0D, 0x17, 0x01, 0x44, 0x1D, 0x0B, 0x11, 0x16, 0x17, 0x01, 0x08, 0x02, 0x44, 0x05, 0x06, 0x0B, 0x12, 0x01, 0x44, 0x10, 0x0C, 0x01, 0x44, 0x23, 0x36, 0x2D, 0x20, 0x48, 0x44, 0x10, 0x0C, 0x01, 0x0A, 0x44, 0x08, 0x0B, 0x0B, 0x0F, 0x44, 0x06, 0x05, 0x07, 0x0F, 0x13, 0x05, 0x16, 0x00, 0x4A]);
    private static readonly string[] AmbientMissionNames =
    [
        "BREACH CORPO HOST",
        "MIRROR AUTH CACHE",
        "SUBVERT ROUTE DAEMON",
        "EXTRACT SHADER MAP",
        "SEED GHOST BEACON",
    ];

    private static readonly string[] AmbientVectors = ["HTTP-GET", "TLS-RESUME", "DMA-MIRROR", "IPC-HIJACK", "SYN-FLOOD"];
    private static readonly string[] AmbientBackdoors = ["LOFT-SHADOW-D", "GHOST-ECHO", "NULL-HOOK", "MIRROR-WRAITH", "NROOT-SHARD"];

    private static string DecodeIntrusionReward(ReadOnlySpan<byte> masked)
    {
        var decoded = new byte[masked.Length];
        for (var index = 0; index < masked.Length; index++)
            decoded[index] = (byte)(masked[index] ^ 0x64);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }

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
    private readonly Func<NetworkStatsSnapshot> getNetworkStats;
    private readonly Func<CatalogSnapshot> getCatalog;
    private readonly Action refreshNews;
    private readonly RemoteAssetCache remoteAssets;

    private DeckView selectedView = DeckView.Home;
    private float mapZoom = DefaultMapZoom;
    private string? transientFeedback;
    private long transientFeedbackUntil;
    private readonly Dictionary<DeckView, int> badgeCounts = new();
    private readonly Dictionary<DeckView, Vector4> badgeColors = new();
    private long lastBadgeUpdateTick;
    private readonly List<string> ambientTerminalLines = [];
    private string[] ambientTerminalOperation = [];
    private int ambientTerminalOperationIndex;
    private long ambientTerminalTypingStartedAt;
    private long ambientTerminalScrollStartedAt;
    private long nextAmbientTerminalLineAt;
    private long ambientTerminalGlitchUntil;
    private long nextAmbientTerminalGlitchAt;
    private int ambientTerminalGlitchLine = -1;
    private string? hoverGlitchTile;
    private long hoverGlitchLastSeenAt;
    private long hoverGlitchUntil;
    private long nextHoverGlitchAt;
    private long moduleTransitionStartedAt;
    private IntrusionGame? intrusionGame;
    private bool intrusionResultRecorded;
    private bool showIntrusionPayload;
    private bool intrusionWindowOpen;
    private bool focusIntrusionWindow;
    private bool tarotRequestWindowOpen;
    private bool focusTarotRequestWindow;
    private bool tarotRequestSucceeded;
    private long tarotRequestLastSentAt;
    private string tarotRequestFeedback = string.Empty;
    private bool staffDirectoryWindowOpen;
    private bool focusStaffDirectoryWindow;
    private string staffDirectoryTitle = string.Empty;
    private string staffDirectoryCategory = string.Empty;
    private IReadOnlyList<StaffProfile> staffProfiles = [];
    private string staffProfilesSourcePath = string.Empty;
    private string? staffProfilesLoadError;
    private StaffProfile? pendingStaffRequestProfile;
    /// <summary>Set by the request button; consumed where the modal is drawn.</summary>
    private bool staffRequestConfirmationPending;
    private readonly Dictionary<string, long> staffRequestLastSentAt = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Song titles typed per DJ, kept until the request is sent.</summary>
    private readonly Dictionary<string, string> songRequestDrafts = new(StringComparer.OrdinalIgnoreCase);
    private const int SongRequestMaxLength = 128;
    private static readonly TimeSpan StaffRequestCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SongRequestCooldown = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, (bool Success, string Message)> staffRequestFeedback = new(StringComparer.OrdinalIgnoreCase);
    private bool showInstallationDetails;
    private CyberdeckThemeId customThemeSource = CyberdeckThemeId.Grid;
    private bool mainWindowCollapsed;
    private bool restoreMainWindowSize;
    private bool mainWindowDragging;
    private Vector2 mainWindowDragOffset;
    private Vector2 mainWindowExpandedSize;

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
        Func<UpdateUiSnapshot> getUpdateStatus,
        Func<NetworkStatsSnapshot> getNetworkStats,
        Func<CatalogSnapshot> getCatalog,
        Action refreshNews,
        RemoteAssetCache remoteAssets)
    {
        this.getCatalog = getCatalog;
        this.refreshNews = refreshNews;
        this.remoteAssets = remoteAssets;
        this.config = config;
        this.penumbra = penumbra;
        this.textures = textures;
        this.textureLoadSource = textureLoadSource;
        ReloadStaffProfiles();
        ReloadTarotYesNoCatalog();
        this.queueReconcile = queueReconcile;
        this.queueReconcileForce = queueReconcileForce;
        this.assignAll = assignAll;
        this.checkForUpdates = checkForUpdates;
        this.autoOpenChanged = autoOpenChanged;
        this.isPenumbraAvailable = isPenumbraAvailable;
        this.getUpdateStatus = getUpdateStatus;
        this.getNetworkStats = getNetworkStats;
    }

    public void OpenSettings()
    {
        SelectDeckView(DeckView.Settings);
        IsOpen = true;
    }

    public void Draw()
    {
        ApplyConfiguredTheme();
        UpdateTarotTellQueue();
        if (IsOpen)
            DrawCyberdeckWindow();
        if (intrusionWindowOpen)
            DrawIntrusionWindow();
        if (cipherVaultWindowOpen)
            DrawCipherVaultWindow();
        if (flyerWindowOpen)
            DrawFlyerWindow();
        if (tarotRequestWindowOpen)
            DrawTarotRequestWindow();
        if (tarotAiWindowOpen)
            DrawTarotAiWindow();
        if (tarotYesNoWindowOpen)
            DrawTarotYesNoWindow();
        if (staffDirectoryWindowOpen)
            DrawStaffDirectoryWindow();
        if (tarotDebugWindowOpen)
            DrawTarotDebugWindow();
        if (tarotCardViewerOpen)
            DrawTarotCardViewerWindow();
    }

    private void DrawCyberdeckWindow()
    {
        var uiScale = GetUiScale();
        using var fontScale = CyberdeckTheme.PushFontScale(uiScale);
        using var theme = CyberdeckTheme.Push(uiScale);
        UpdateMainWindowDragPosition();
        var initialSize = GetInitialWindowSize(uiScale);
        var titleBarHeight = GetMainTitleBarHeight(uiScale);
        if (mainWindowCollapsed)
        {
            var collapsedWidth = mainWindowExpandedSize.X > 0 ? mainWindowExpandedSize.X : initialSize.X;
            var collapsedSize = new Vector2(collapsedWidth, titleBarHeight + 2);
            ImGui.SetNextWindowSize(collapsedSize, ImGuiCond.Always);
            ImGui.SetNextWindowSizeConstraints(collapsedSize, collapsedSize);
        }
        else
        {
            ImGui.SetNextWindowSize(
                restoreMainWindowSize && mainWindowExpandedSize.X > 0
                    ? mainWindowExpandedSize
                    : initialSize,
                restoreMainWindowSize ? ImGuiCond.Always : ImGuiCond.FirstUseEver);
            restoreMainWindowSize = false;
            var (minimumSize, maximumSize) = CyberdeckTheme.ResolveWindowConstraints(
                uiScale,
                new Vector2(280, 420),
                new Vector2(720, 1000));
            ImGui.SetNextWindowSizeConstraints(minimumSize, maximumSize);
        }

        var windowFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar;
        if (mainWindowCollapsed)
            windowFlags |= ImGuiWindowFlags.NoResize;
        if (!ImGui.Begin("The Grid Cyberdeck", ref IsOpen, windowFlags))
        {
            ImGui.End();
            return;
        }

        if (!DrawMainWindowTitleBar(uiScale))
        {
            ImGui.End();
            return;
        }

        UpdateBadges();
        var updateStatus = getUpdateStatus();
        // Home owns a fixed status surface inside the banner terminal. Do not insert
        // a second rail above it and shift the whole deck while an operation runs.
        if (config.FirstRunCompleted &&
            selectedView != DeckView.Home &&
            ShouldShowUpdateStatusRail(updateStatus))
        {
            DrawUpdateStatusRail(updateStatus);
            ImGui.Spacing();
        }
        if (tarotDebugSession.Role == TarotDebugRole.Customer && tarotDebugSession.Phase == TarotDebugPhase.InviteReceived)
        {
            DrawTarotConnectionPromptInline();
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
            DrawModuleTransition(deckMin, deckMax, uiScale);
        }

        ImGui.EndChild();
        DrawTransientFeedbackOverlay();
        ImGui.End();
    }

    private static float GetMainTitleBarHeight(float uiScale)
        => MathF.Max(30 * uiScale, ImGui.GetFontSize() + (12 * uiScale));

    private bool DrawMainWindowTitleBar(float uiScale)
    {
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var titleHeight = GetMainTitleBarHeight(uiScale);
        var border = MathF.Max(1, uiScale);
        var barMin = windowPos;
        var barMax = new Vector2(windowPos.X + windowSize.X, windowPos.Y + titleHeight);
        var buttonWidth = titleHeight;
        var closeMin = new Vector2(barMax.X - buttonWidth, barMin.Y);
        var collapseMin = new Vector2(closeMin.X - buttonWidth, barMin.Y);
        var buttonSize = new Vector2(buttonWidth, barMax.Y - barMin.Y);
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(windowPos, windowPos + windowSize, false);

        drawList.AddRectFilled(
            barMin,
            barMax,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.PanelRaised, 0.98f)),
            5 * uiScale,
            ImDrawFlags.RoundCornersTop);
        var borderColor = ImGui.GetColorU32(CyberdeckTheme.Palette.Border);

        var title = "The Grid Cyberdeck";
        var titleSize = ImGui.CalcTextSize(title);
        drawList.AddText(
            new Vector2(barMin.X + (12 * uiScale), barMin.Y + ((buttonSize.Y - titleSize.Y) * 0.5f)),
            ImGui.GetColorU32(CyberdeckTheme.Palette.Text),
            title);

        ImGui.SetCursorScreenPos(barMin);
        var dragWidth = MathF.Max(1, collapseMin.X - barMin.X);
        ImGui.InvisibleButton("##cyberdeck_title_drag", new Vector2(dragWidth, buttonSize.Y));
        if (ImGui.IsItemActivated())
        {
            mainWindowDragging = true;
            mainWindowDragOffset = ImGui.GetIO().MousePos - windowPos;
        }

        ImGui.SetCursorScreenPos(collapseMin);
        var collapseClicked = ImGui.InvisibleButton("##cyberdeck_title_collapse", buttonSize);
        var collapseHovered = ImGui.IsItemHovered();
        var collapseActive = ImGui.IsItemActive();
        DrawMainTitleButtonBackground(drawList, collapseMin, collapseMin + buttonSize, collapseHovered, collapseActive, false);
        var collapseCenter = collapseMin + (buttonSize * 0.5f);
        var iconHalf = 5 * uiScale;
        if (mainWindowCollapsed)
        {
            drawList.AddRect(
                collapseCenter - new Vector2(iconHalf),
                collapseCenter + new Vector2(iconHalf),
                ImGui.GetColorU32(CyberdeckTheme.Palette.Text),
                0,
                ImDrawFlags.None,
                MathF.Max(1, 1.4f * uiScale));
        }
        else
        {
            drawList.AddLine(
                new Vector2(collapseCenter.X - iconHalf, collapseCenter.Y),
                new Vector2(collapseCenter.X + iconHalf, collapseCenter.Y),
                ImGui.GetColorU32(CyberdeckTheme.Palette.Text),
                MathF.Max(1, 1.4f * uiScale));
        }

        ImGui.SetCursorScreenPos(closeMin);
        var closeClicked = ImGui.InvisibleButton("##cyberdeck_title_close", buttonSize);
        var closeHovered = ImGui.IsItemHovered();
        var closeActive = ImGui.IsItemActive();
        DrawMainTitleButtonBackground(drawList, closeMin, closeMin + buttonSize, closeHovered, closeActive, true);
        var closeCenter = closeMin + (buttonSize * 0.5f);
        var closeHalf = 5 * uiScale;
        var closeColor = ImGui.GetColorU32(CyberdeckTheme.Palette.Text);
        var closeThickness = MathF.Max(1, 1.4f * uiScale);
        drawList.AddLine(
            closeCenter - new Vector2(closeHalf),
            closeCenter + new Vector2(closeHalf),
            closeColor,
            closeThickness);
        drawList.AddLine(
            closeCenter + new Vector2(-closeHalf, closeHalf),
            closeCenter + new Vector2(closeHalf, -closeHalf),
            closeColor,
            closeThickness);

        // Draw the outline after the controls so hover fills cannot cover it. Keeping
        // the stroke inside the clip rectangle also preserves the right edge at every
        // UI scale instead of clipping half of the line at the window boundary.
        var borderInset = border * 0.5f;
        drawList.AddRect(
            barMin + new Vector2(borderInset),
            barMax - new Vector2(borderInset),
            borderColor,
            5 * uiScale,
            ImDrawFlags.RoundCornersTop,
            border);
        drawList.PopClipRect();

        if (closeClicked)
            IsOpen = false;
        if (collapseClicked)
        {
            if (mainWindowCollapsed)
            {
                mainWindowCollapsed = false;
                restoreMainWindowSize = true;
            }
            else
            {
                mainWindowExpandedSize = windowSize;
                mainWindowCollapsed = true;
            }
        }
        else if (!mainWindowCollapsed)
        {
            mainWindowExpandedSize = windowSize;
        }

        ImGui.SetCursorPos(new Vector2(
            ImGui.GetStyle().WindowPadding.X,
            titleHeight + ImGui.GetStyle().WindowPadding.Y));
        return IsOpen && !mainWindowCollapsed && !collapseClicked;
    }

    private void UpdateMainWindowDragPosition()
    {
        if (!mainWindowDragging)
            return;

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            mainWindowDragging = false;
            return;
        }

        ImGui.SetNextWindowPos(
            ImGui.GetIO().MousePos - mainWindowDragOffset,
            ImGuiCond.Always);
    }

    private static void DrawMainTitleButtonBackground(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        bool hovered,
        bool active,
        bool closeButton)
    {
        if (!hovered && !active)
            return;

        var color = closeButton
            ? active
                ? new Vector4(0.85f, 0.08f, 0.20f, 0.96f)
                : new Vector4(0.62f, 0.06f, 0.17f, 0.90f)
            : active
                ? CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.48f)
                : CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.28f);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(color));
    }

    private void DrawIntrusionWindow()
    {
        var uiScale = GetUiScale();
        using var fontScale = CyberdeckTheme.PushFontScale(uiScale);
        using var theme = CyberdeckTheme.Push(uiScale);
        ImGui.SetNextWindowSize(new Vector2(620, 700) * uiScale, ImGuiCond.FirstUseEver);
        var (minimumSize, maximumSize) = CyberdeckTheme.ResolveWindowConstraints(
            uiScale,
            new Vector2(430, 520),
            new Vector2(900, 1000));
        ImGui.SetNextWindowSizeConstraints(minimumSize, maximumSize);
        if (focusIntrusionWindow)
        {
            ImGui.SetNextWindowFocus();
            focusIntrusionWindow = false;
        }

        if (!ImGui.Begin("BLACK ICE // INTRUSION###grid_intrusion", ref intrusionWindowOpen, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.End();
            if (!intrusionWindowOpen)
                showIntrusionPayload = false;
            return;
        }

        if (ImGui.BeginChild("intrusion_body", Vector2.Zero, true))
            DrawIntrusionView();
        ImGui.EndChild();
        ImGui.End();
        if (!intrusionWindowOpen)
            showIntrusionPayload = false;
    }

    private void OpenIntrusionGame()
    {
        intrusionWindowOpen = true;
        focusIntrusionWindow = true;
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
        ImGui.TextColored(CyberdeckTheme.Palette.Cyan, "Set up The Grid venue mod");
        DrawNeonSeparator();
        ImGui.Spacing();

        if (!isPenumbraAvailable())
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "Penumbra is required");
            ImGui.TextWrapped("Install and enable Penumbra before installing the venue mod.");
            ImGui.Spacing();
            if (ImGui.Button("Open Plugin Installer", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
                PluginService.Commands.ProcessCommand("/xlplugins");
            ImGui.Spacing();
            if (ImGui.Button("Set Up Later", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
            {
                config.FirstRunCompleted = true;
                config.Save();
            }
            return;
        }

        ImGui.TextWrapped("This downloads the venue mod, installs it in Penumbra, and completes the required setup automatically.");
        ImGui.Spacing();
        var automaticUpdates = config.FullAuto;
        if (ImGui.Checkbox("Install future updates automatically", ref automaticUpdates))
        {
            config.FullAuto = automaticUpdates;
            config.Save();
        }
        DrawMutedWrapped("When disabled, you will be notified and can choose when to install each update.");

        ImGui.Spacing();
        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("Install", new Vector2(ImGui.GetContentRegionAvail().X, 36 * GetUiScale())))
            {
                config.FirstRunCompleted = true;
                config.Save();
                queueReconcile();
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Not Now", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
        {
            config.FirstRunCompleted = true;
            config.Save();
        }
    }

    private void DrawUpdateStatusRail(UpdateUiSnapshot status)
    {
        var uiScale = GetUiScale();
        var statusColor = GetUpdateStatusColor(status);
        var isFailure = status.Phase is UpdateOperationPhase.Error or UpdateOperationPhase.NeedsAttention;
        var detail = isFailure ? GetVisibleStatusDetail(status) : null;
        var technicalError = isFailure ? GetDistinctTechnicalError(status, detail) : null;
        var wrapWidth = MathF.Max(1, ImGui.GetContentRegionAvail().X - (20 * uiScale));
        var height = status.IsBusy
            ? 82f * uiScale
            : isFailure
                ? (58f * uiScale) +
                  GetWrappedTextHeight(detail, wrapWidth) +
                  GetWrappedTextHeight(technicalError is null ? null : $"Reason: {technicalError}", wrapWidth)
                : 42f * uiScale;

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

                if (isFailure)
                {
                    if (status.FailureStage is { } failureStage)
                        ImGui.TextDisabled($"Step: {GetUpdateStageLabel(failureStage)}");

                    if (detail is not null)
                        ImGui.TextWrapped(detail);

                    if (technicalError is not null)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.Palette.TextMuted);
                        ImGui.TextWrapped($"Reason: {technicalError}");
                        ImGui.PopStyleColor();
                    }
                }
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private static string? GetVisibleStatusDetail(UpdateUiSnapshot status)
        => string.IsNullOrWhiteSpace(status.Detail) || status.Detail == "No update operation is active."
            ? status.ErrorMessage
            : status.Detail;

    private static string? GetDistinctTechnicalError(UpdateUiSnapshot status, string? visibleDetail)
        => string.IsNullOrWhiteSpace(status.ErrorMessage) ||
           string.Equals(status.ErrorMessage.Trim(), visibleDetail?.Trim(), StringComparison.Ordinal)
            ? null
            : status.ErrorMessage.Trim();

    private static float GetWrappedTextHeight(string? text, float wrapWidth)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : ImGui.CalcTextSize(text, false, wrapWidth).Y + ImGui.GetStyle().ItemSpacing.Y;

    private static string GetUpdateStageLabel(UpdateOperationPhase phase)
        => phase switch
        {
            UpdateOperationPhase.Queued or UpdateOperationPhase.Checking => "Check for updates",
            UpdateOperationPhase.Downloading => "Download",
            UpdateOperationPhase.Importing or UpdateOperationPhase.WaitingForPenumbra => "Install in Penumbra",
            UpdateOperationPhase.Configuring or UpdateOperationPhase.Assigning => "Finish setup",
            _ => "Venue mod setup",
        };

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
            UpdateOperationPhase.Checking => "CHECKING",
            UpdateOperationPhase.Downloading => "DOWNLOADING",
            UpdateOperationPhase.Importing or UpdateOperationPhase.WaitingForPenumbra => "INSTALLING",
            UpdateOperationPhase.Configuring or UpdateOperationPhase.Assigning => "FINISHING SETUP",
            _ => "VENUE MOD",
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
        DrawNewsBanner(ImGui.GetContentRegionAvail().X);
        DrawDeckButtons(ImGui.GetContentRegionAvail().X);
    }

    private void DrawAppScreen()
    {
        if (ImGui.Button("< HOME"))
            SelectDeckView(DeckView.Home);

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
            case DeckView.News:
                DrawNewsView();
                break;
            case DeckView.Network:
                DrawNetworkView();
                break;
            case DeckView.Services:
                DrawServicesView();
                break;
            case DeckView.Settings:
                DrawSettingsView();
                break;
        }
    }

    private void SelectDeckView(DeckView view)
    {
        if (selectedView == view)
            return;

        selectedView = view;
        moduleTransitionStartedAt = config.ReduceMotion ? 0 : Environment.TickCount64;
    }

    private void DrawModuleTransition(Vector2 min, Vector2 max, float uiScale)
    {
        if (config.ReduceMotion || moduleTransitionStartedAt == 0)
            return;

        const float durationMs = 180f;
        var elapsed = Environment.TickCount64 - moduleTransitionStartedAt;
        if (elapsed >= durationMs)
        {
            moduleTransitionStartedAt = 0;
            return;
        }

        var progress = Math.Clamp(elapsed / durationMs, 0f, 1f);
        var scanX = min.X + ((max.X - min.X) * progress);
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(min, max, true);
        drawList.AddRectFilled(
            new Vector2(scanX, min.Y),
            max,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Background, 0.94f)));
        drawList.AddRectFilled(
            new Vector2(MathF.Max(min.X, scanX - (10 * uiScale)), min.Y),
            new Vector2(MathF.Min(max.X, scanX + (2 * uiScale)), max.Y),
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.20f)));
        drawList.AddLine(
            new Vector2(scanX, min.Y),
            new Vector2(scanX, max.Y),
            ImGui.GetColorU32(CyberdeckTheme.Palette.Cyan),
            MathF.Max(1.5f, 2 * uiScale));
        drawList.PopClipRect();
    }

    private string GetAddressTelemetry()
    {
        var segments = config.VenueAddress
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', segments.TakeLast(Math.Min(3, segments.Length))).ToUpperInvariant();
    }

    private static string GetSettingsTelemetry(UpdateUiSnapshot status)
    {
        if (status.IsBusy)
        {
            return status.Phase switch
            {
                UpdateOperationPhase.Checking => "CHECKING",
                UpdateOperationPhase.Downloading => "DOWNLOADING",
                UpdateOperationPhase.Importing => "INSTALLING",
                UpdateOperationPhase.Assigning or UpdateOperationPhase.Configuring or UpdateOperationPhase.WaitingForPenumbra => "FINISHING SETUP",
                _ => "QUEUED",
            };
        }

        if (status.Phase == UpdateOperationPhase.Error)
            return "SYSTEM FAULT";
        if (status.Phase == UpdateOperationPhase.NeedsAttention)
            return "ACTION REQUIRED";
        if (status.ReleaseAvailability == UpdateReleaseAvailability.UpdateAvailable)
            return status.AvailableVersion is { Length: > 0 } version ? $"UPDATE v{version}" : "UPDATE READY";
        return status.InstalledVersion is { Length: > 0 } installed ? $"INSTALLED v{installed}" : "READY";
    }

    private static int GetNetworkSignalCount()
    {
        try
        {
            return NetworkGuestScanner.Capture().Count +
                   (NetworkGuestScanner.CaptureLocal() is null ? 0 : 1);
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsVenueManager()
    {
        try
        {
            return IsVenueManager(NetworkGuestScanner.CaptureLocal());
        }
        catch
        {
            return false;
        }
    }

    private static bool IsVenueManager(NetworkGuestObservation? localPlayer)
        => localPlayer is not null && VenueManagerIdentities.Contains(localPlayer.Identity);

    private static string GetDeckViewTitle(DeckView view)
        => view switch
        {
            DeckView.Map => "Address",
            DeckView.Wifi => "Wi-Fi",
            DeckView.Menu => "Menu",
            DeckView.News => "Broadcast",
            DeckView.Network => "Network",
            DeckView.Services => "Services",
            DeckView.Settings => "Settings",
            _ => "The Grid",
        };

    private void DrawDeckHeader()
    {
        var uiScale = GetUiScale();
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 176 * uiScale);
        var max = start + size;
        var drawList = ImGui.GetWindowDrawList();
        ImGui.Dummy(size);
        var afterHeaderCursor = ImGui.GetCursorScreenPos();

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

        var authLabelPosition = new Vector2(
            textX + ImGui.CalcTextSize("THE GRID // ").X,
            start.Y + (24 * uiScale));
        var authLabelSize = ImGui.CalcTextSize("n_root");
        var authPadding = new Vector2(3, 2) * uiScale;
        ImGui.SetCursorScreenPos(authLabelPosition - authPadding);
        if (ImGui.InvisibleButton("##cipher_vault_entry", authLabelSize + (authPadding * 2)))
            OpenCipherVault();
        if (ImGui.IsItemHovered())
        {
            drawList.AddLine(
                new Vector2(authLabelPosition.X, authLabelPosition.Y + authLabelSize.Y + uiScale),
                new Vector2(authLabelPosition.X + authLabelSize.X, authLabelPosition.Y + authLabelSize.Y + uiScale),
                ImGui.GetColorU32(CyberdeckTheme.Palette.Magenta),
                MathF.Max(1, uiScale));
            ImGui.SetTooltip("AUTH NODE // RESTRICTED");
        }
        ImGui.SetCursorScreenPos(afterHeaderCursor);

        if (ImGui.Button($"NAVIGATE // {config.VenueAddress}", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
            OpenAddress();
        DrawHoverTooltip(LifestreamNavigationTooltip);

        var updateStatus = getUpdateStatus();
        if (CyberdeckWidgets.DrawActionButton(
                "CHECK FOR UPDATES",
                updateStatus.IsBusy,
                new Vector2(ImGui.GetContentRegionAvail().X, 0)))
            checkForUpdates();
        ImGui.Spacing();
    }

    private void DrawAmbientTerminalBurst(ImDrawListPtr drawList, Vector2 start, Vector2 size, float uiScale)
    {
        var now = Environment.TickCount64;
        var updateStatus = getUpdateStatus();
        if (ShouldShowUpdateStatusRail(updateStatus))
        {
            nextAmbientTerminalLineAt = now + 500;
            DrawSystemTerminalStatus(drawList, start, size, uiScale, updateStatus);
            return;
        }

        if (transientFeedback is { } feedback && now <= transientFeedbackUntil)
        {
            nextAmbientTerminalLineAt = now + 500;
            DrawTerminalPriorityMessage(drawList, start, size, uiScale, $"> SYSTEM // {feedback}", CyberdeckTheme.Palette.Cyan);
            return;
        }

        if (config.ReduceMotion)
        {
            ambientTerminalLines.Clear();
            ambientTerminalOperation = [];
            DrawTerminalPriorityMessage(
                drawList,
                start,
                size,
                uiScale,
                "> N_ROOT HEARTBEAT // ROUTE MASK STABLE",
                CyberdeckTheme.Palette.TextMuted);
            return;
        }

        EnsureAmbientTerminalOperation(now);
        if (now >= nextAmbientTerminalLineAt)
            AdvanceAmbientTerminalOperation(now);
        UpdateAmbientTerminalGlitch(now);

        var bandMin = new Vector2(start.X + (8 * uiScale), start.Y + (99 * uiScale));
        var bandMax = new Vector2(start.X + size.X - (8 * uiScale), start.Y + (168 * uiScale));
        drawList.AddRectFilled(
            bandMin,
            bandMax,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Panel, 0.78f)),
            2 * uiScale);
        drawList.AddLine(
            bandMin,
            new Vector2(bandMax.X, bandMin.Y),
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, 0.58f)),
            MathF.Max(1, uiScale));

        const float scrollDurationMs = 170f;
        var lineHeight = 15 * uiScale;
        var scrollProgress = ambientTerminalLines.Count > 4
            ? Math.Clamp((now - ambientTerminalScrollStartedAt) / scrollDurationMs, 0f, 1f)
            : 0f;
        var scrollOffset = scrollProgress * lineHeight;
        var textOrigin = bandMin + new Vector2(7, 5) * uiScale;
        drawList.PushClipRect(bandMin + new Vector2(1), bandMax - new Vector2(1), true);
        using (PluginService.PluginInterface.UiBuilder.MonoFontHandle.Push())
        {
            var codeFont = ImGui.GetFont();
            var codeFontSize = ImGui.GetFontSize() * 0.80f;
            for (var index = 0; index < ambientTerminalLines.Count; index++)
            {
                var line = ambientTerminalLines[index];
                var glitching = now < ambientTerminalGlitchUntil && index == ambientTerminalGlitchLine;
                if (index == ambientTerminalLines.Count - 1)
                {
                    var visibleCharacters = Math.Clamp((int)((now - ambientTerminalTypingStartedAt) / 18), 0, line.Length);
                    line = line[..visibleCharacters];
                    if (visibleCharacters < ambientTerminalLines[index].Length || (now / 420) % 2 == 0)
                        line += "_";
                }
                if (glitching)
                    line = GlitchAmbientTerminalLine(line, now);

                var position = textOrigin + new Vector2(0, index * lineHeight - scrollOffset);
                DrawAmbientCodeLine(drawList, codeFont, codeFontSize, position, line, glitching);
            }
        }
        drawList.PopClipRect();
    }

    private void EnsureAmbientTerminalOperation(long now)
    {
        if (ambientTerminalOperation.Length != 0 && ambientTerminalLines.Count != 0)
            return;

        ambientTerminalOperation = BuildAmbientTerminalOperation();
        ambientTerminalOperationIndex = 0;
        ambientTerminalLines.Clear();
        while (ambientTerminalLines.Count < 4 && ambientTerminalOperationIndex < ambientTerminalOperation.Length)
            ambientTerminalLines.Add(ambientTerminalOperation[ambientTerminalOperationIndex++]);
        ambientTerminalScrollStartedAt = now - 170;
        ambientTerminalTypingStartedAt = now;
        nextAmbientTerminalLineAt = now + GetAmbientTerminalLineDuration(ambientTerminalLines[^1]);
        nextAmbientTerminalGlitchAt = now + Random.Shared.Next(6500, 12001);
    }

    private void AdvanceAmbientTerminalOperation(long now)
    {
        if (ambientTerminalOperationIndex >= ambientTerminalOperation.Length)
        {
            ambientTerminalOperation = BuildAmbientTerminalOperation();
            ambientTerminalOperationIndex = 0;
        }

        if (ambientTerminalLines.Count >= 5)
            ambientTerminalLines.RemoveAt(0);
        ambientTerminalLines.Add(ambientTerminalOperation[ambientTerminalOperationIndex++]);
        ambientTerminalScrollStartedAt = now;
        ambientTerminalTypingStartedAt = now;
        nextAmbientTerminalLineAt = now + GetAmbientTerminalLineDuration(ambientTerminalLines[^1]);
    }

    private static long GetAmbientTerminalLineDuration(string line)
        => Math.Max(900, (line.Length * 18L) + 420);

    private void UpdateAmbientTerminalGlitch(long now)
    {
        if (now < nextAmbientTerminalGlitchAt || now < ambientTerminalGlitchUntil || ambientTerminalLines.Count == 0)
            return;

        var firstVisibleLine = ambientTerminalLines.Count > 4 ? 1 : 0;
        ambientTerminalGlitchLine = Random.Shared.Next(firstVisibleLine, ambientTerminalLines.Count);
        ambientTerminalGlitchUntil = now + Random.Shared.Next(90, 161);
        nextAmbientTerminalGlitchAt = ambientTerminalGlitchUntil + Random.Shared.Next(7000, 15001);
    }

    private static string GlitchAmbientTerminalLine(string line, long now)
    {
        if (line.Length < 4)
            return line;

        var glitched = line.ToCharArray();
        var seed = unchecked((uint)(now / 32));
        var first = (int)((seed * 17u) % (uint)glitched.Length);
        var second = (int)(((seed * 31u) + 7u) % (uint)glitched.Length);
        glitched[first] = '#';
        glitched[second] = seed % 2 == 0 ? '0' : '/';
        return new string(glitched);
    }

    private static void DrawAmbientCodeLine(
        ImDrawListPtr drawList,
        ImFontPtr font,
        float fontSize,
        Vector2 position,
        string line,
        bool glitching)
    {
        if (glitching)
        {
            drawList.AddText(font, fontSize, position + new Vector2(2, 0), ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.48f)), line);
            drawList.AddText(font, fontSize, position, ImGui.GetColorU32(CyberdeckTheme.Palette.Magenta), line);
            return;
        }

        var cursor = position;
        var scale = fontSize / MathF.Max(1, ImGui.GetFontSize());
        var index = 0;
        while (index < line.Length)
        {
            var start = index;
            var color = CyberdeckTheme.Palette.TextMuted;
            if (index + 1 < line.Length && line[index] == '/' && line[index + 1] == '/')
            {
                index = line.Length;
                color = CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, 0.82f);
            }
            else if (line[index] is '"' or '\'')
            {
                var quote = line[index++];
                while (index < line.Length && line[index] != quote)
                    index++;
                if (index < line.Length)
                    index++;
                color = CyberdeckTheme.Palette.Amber;
            }
            else if (char.IsLetter(line[index]) || line[index] == '_')
            {
                index++;
                while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] is '_' or '.'))
                    index++;
                var token = line[start..index];
                var normalized = token.ToLowerInvariant();
                if (normalized is "if" or "while" or "for" or "in" or "else")
                    color = CyberdeckTheme.Palette.Magenta;
                else if (normalized is "true" or "false" or "active" or "denied")
                    color = CyberdeckTheme.Palette.Success;
                else if (index < line.Length && line[index] == '(')
                    color = CyberdeckTheme.Palette.Cyan;
                else
                    color = CyberdeckTheme.Palette.Text;
            }
            else if (char.IsDigit(line[index]) || line[index] == '#')
            {
                index++;
                while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] is '#' or '.'))
                    index++;
                color = CyberdeckTheme.Palette.Cyan;
            }
            else
            {
                index++;
                while (index < line.Length
                       && !char.IsLetterOrDigit(line[index])
                       && line[index] is not '"' and not '\'' and not '#'
                       && !(index + 1 < line.Length && line[index] == '/' && line[index + 1] == '/'))
                    index++;
            }

            var segment = line[start..index];
            drawList.AddText(font, fontSize, cursor, ImGui.GetColorU32(color), segment);
            cursor.X += ImGui.CalcTextSize(segment).X * scale;
        }
    }

    private static string[] BuildAmbientTerminalOperation()
    {
        var node = Random.Shared.Next(100, 1000);
        var accessKey = Random.Shared.Next(1000, 10000);
        var hops = Random.Shared.Next(5, 10);
        var phase = Random.Shared.Next(1, 10);
        var shard = Random.Shared.Next(1, 10);
        var pid = Random.Shared.Next(1100, 9900);
        var traceId = Random.Shared.NextInt64(0, 1L << 32).ToString("X8");
        var mission = AmbientMissionNames[Random.Shared.Next(AmbientMissionNames.Length)];
        var vector = AmbientVectors[Random.Shared.Next(AmbientVectors.Length)];
        var backdoor = AmbientBackdoors[Random.Shared.Next(AmbientBackdoors.Length)];
        var timestamp = $"{Random.Shared.Next(0, 24):00}:{Random.Shared.Next(0, 60):00}:{Random.Shared.Next(0, 60):00}";

        return
        [
            $"// node-{node} :: acquire",
            $"// mission: {mission}",
            "// uplink: spoofed / trace: scrubbed",
            $"operator = mask(\"n_root.shard{shard}\")",
            "session = init()",
            $"auth = spoof(\"KEYGEN#{accessKey}\", deepmask=true)",
            "if auth.denied:",
            "    token = forge(level=9)",
            $"    inject(\"BABEL\", vector=\"{vector}\")",
            $"    phase_shift(+{phase:00})",
            "log(\"reflection offset accepted\")",
            $"target = \"LOFT.ARKNET.U{shard}\"",
            $"route = ghost_route(hops={hops})",
            "connect(target, via=route)",
            "while link.active:",
            "    dump(\"RAM.MIRROR.VOL\")",
            "    mask_traffic(rate=256kbps)",
            $"    headers.traceid = 0x{traceId}",
            "    for table in scan(\"NODE_DB\"):",
            "        if table.has(\"CRED\"):",
            "            clone(table)",
            "            scrub(table.logs)",
            "            exfil(method=\"STEALTHDRIP\")",
            $"implant = backdoor(\"{backdoor}\")",
            "install(implant, path=\"/bin/syncd\")",
            "verify_integrity(implant)",
            "log(\"beacon awaiting remote pulse\")",
            $"proc = spawn(\"ghost_echo\", pid={pid})",
            "disguise(proc, as=\"systemd-logind\")",
            "fake_traffic(count=8, loop=true)",
            "scrub_logs(\"auth\", \"shadow_trace\")",
            "emit(\"UPLINK_COMPLETE\")",
            "session.terminate()",
            $"// node-{node}: icewalk safe",
            $"// end transmission / {timestamp}",
        ];
    }

    private static void DrawTerminalPriorityMessage(
        ImDrawListPtr drawList,
        Vector2 start,
        Vector2 size,
        float uiScale,
        string message,
        Vector4 color)
    {
        var bandMin = new Vector2(start.X + (8 * uiScale), start.Y + (99 * uiScale));
        var bandMax = new Vector2(start.X + size.X - (8 * uiScale), start.Y + (168 * uiScale));
        drawList.AddRectFilled(bandMin, bandMax, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Panel, 0.88f)), 2 * uiScale);
        drawList.AddLine(bandMin, new Vector2(bandMax.X, bandMin.Y), ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, 0.82f)), MathF.Max(1, uiScale));
        var rendered = EllipsizeToWidth(message, MathF.Max(1, size.X - (32 * uiScale)));
        drawList.AddText(bandMin + new Vector2(7, 26) * uiScale, ImGui.GetColorU32(color), rendered);
    }

    private void DrawSystemTerminalStatus(
        ImDrawListPtr drawList,
        Vector2 start,
        Vector2 size,
        float uiScale,
        UpdateUiSnapshot status)
    {
        var statusColor = GetUpdateStatusColor(status);
        var bandMin = new Vector2(start.X + (8 * uiScale), start.Y + (99 * uiScale));
        var bandMax = new Vector2(start.X + size.X - (8 * uiScale), start.Y + (168 * uiScale));
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
            bandMin + new Vector2(7, 26) * uiScale,
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
        var updateStatus = getUpdateStatus();
        var venueManager = IsVenueManager();
        var networkTelemetry = venueManager
            ? $"{GetNetworkSignalCount():00} SIGNALS"
            : "LOCAL LINK";
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var useTwoColumns = width >= (260 * uiScale);
        var buttonWidth = useTwoColumns ? (width - spacing) / 2 : width;
        var buttonHeight = (useTwoColumns ? 132f : 108f) * uiScale;
        var buttonSize = new Vector2(buttonWidth, buttonHeight);

        if (DrawImageNavButton("Menu", "menu.png", buttonSize, $"{GetMenuEntries().Count:00} ITEMS"))
            SelectDeckView(DeckView.Menu);
        if (useTwoColumns)
            ImGui.SameLine();
        if (DrawImageNavButton("Wi-Fi", "wifi.png", buttonSize, "02 RELAYS"))
            SelectDeckView(DeckView.Wifi);

        if (DrawImageNavButton("Address", "address.png", buttonSize, GetAddressTelemetry()))
            SelectDeckView(DeckView.Map);
        if (useTwoColumns)
            ImGui.SameLine();
        var networkPos = ImGui.GetCursorScreenPos();
        if (DrawImageNavButton("Network", "network.png", buttonSize, networkTelemetry))
            SelectDeckView(DeckView.Network);
        if (venueManager && badgeCounts.TryGetValue(DeckView.Network, out var networkBadge) && networkBadge > 0)
            DrawTileBadge(networkPos, buttonSize, networkBadge, badgeColors.GetValueOrDefault(DeckView.Network, CyberdeckTheme.Palette.Error));

        if (DrawImageNavButton("Services", "services.png", buttonSize, GetServicesTelemetry()))
            SelectDeckView(DeckView.Services);
        if (useTwoColumns)
            ImGui.SameLine();
        var settingsPosition = ImGui.GetCursorScreenPos();
        if (DrawImageNavButton("Settings", "settings.png", buttonSize, GetSettingsTelemetry(updateStatus)))
            SelectDeckView(DeckView.Settings);
        if (updateStatus.IsBusy)
            DrawTileActivityBadge(settingsPosition, buttonSize, GetUpdateStatusColor(updateStatus));
        else if (badgeCounts.TryGetValue(DeckView.Settings, out var settingsCount) && settingsCount > 0)
            DrawTileBadge(settingsPosition, buttonSize, settingsCount, badgeColors.GetValueOrDefault(DeckView.Settings, CyberdeckTheme.Palette.Amber));

        // A seventh tile would leave a dangling half-row, so Broadcast spans the
        // full width. It appears only when there is something to read, which
        // keeps the grid exactly as it was whenever the relay is unreachable.
        var news = getCatalog();
        if (news.HasPosts)
        {
            var newsSize = new Vector2(width, buttonHeight);
            var newsPosition = ImGui.GetCursorScreenPos();
            if (DrawImageNavButton("Broadcast", "broadcast.png", newsSize, GetNewsTelemetry(news)))
                SelectDeckView(DeckView.News);

            var unread = CountUnreadNews(news);
            if (unread > 0)
                DrawTileBadge(newsPosition, newsSize, unread, CyberdeckTheme.Palette.Magenta);
        }
    }

    private bool DrawImageNavButton(string label, string imageName, Vector2 size, string telemetry)
    {
        ImGui.BeginGroup();
        var wrap = GetTextureWrap(imageName);
        var start = ImGui.GetCursorScreenPos();
        var uiScale = GetUiScale();
        bool clicked;
        bool hovered;

        if (wrap is not null)
        {
            ImGui.Button($"##tile_{label}", size);
            clicked = ImGui.IsItemClicked();
            hovered = ImGui.IsItemHovered();
            var glitching = IsHoverGlitchActive(label, hovered);
            DrawTileGlow(start, size, hovered, glitching, uiScale);
            var iconSize = FitTileIcon(wrap, size, uiScale, hasTelemetry: true);
            var iconPos = new Vector2(start.X + (size.X - iconSize.X) / 2, start.Y + (12 * uiScale));
            var iconTint = ImGui.GetColorU32(CyberdeckTheme.Palette.Cyan);
            if (glitching)
                DrawGlitchedImage(wrap.Handle, iconPos, iconSize, iconTint, uiScale, reduceMotion: false);
            else
                ImGui.GetWindowDrawList().AddImage(wrap.Handle, iconPos, iconPos + iconSize, Vector2.Zero, Vector2.One, iconTint);
            if (glitching)
                DrawHoverGlitchOverlay(start, size, uiScale);

            DrawTileLabels(start, size, glitching ? ScrambleTileLabel(label) : label, telemetry, uiScale);
        }
        else
        {
            clicked = ImGui.Button(label, size);
            hovered = ImGui.IsItemHovered();
            var glitching = IsHoverGlitchActive(label, hovered);
            DrawTileGlow(start, size, hovered, glitching, uiScale);
            DrawTileLabels(start, size, glitching ? ScrambleTileLabel(label) : label, telemetry, uiScale);
        }
        ImGui.EndGroup();
        return clicked;
    }

    private static void DrawTileLabels(Vector2 start, Vector2 size, string label, string telemetry, float uiScale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var labelSize = ImGui.CalcTextSize(label);
        var telemetryText = EllipsizeToWidth(telemetry, MathF.Max(1, size.X - (16 * uiScale)));
        var telemetrySize = ImGui.CalcTextSize(telemetryText);
        drawList.AddText(
            new Vector2(start.X + MathF.Max(0, (size.X - labelSize.X) / 2), start.Y + size.Y - (38 * uiScale)),
            ImGui.GetColorU32(CyberdeckTheme.Palette.Text),
            label);
        drawList.AddText(
            new Vector2(start.X + MathF.Max(0, (size.X - telemetrySize.X) / 2), start.Y + size.Y - (19 * uiScale)),
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.72f)),
            telemetryText);
    }

    private static Vector2 FitTileIcon(IDalamudTextureWrap texture, Vector2 tileSize, float uiScale, bool hasTelemetry = false)
    {
        var naturalSize = GetTextureSize(texture, uiScale);
        var available = new Vector2(
            MathF.Max(1, tileSize.X - (20 * uiScale)),
            MathF.Max(1, tileSize.Y - ((hasTelemetry ? 58 : 42) * uiScale)));
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

        drawList.AddImage(textureHandle, iconPos + new Vector2(-shift - jitter, 0), iconPos + iconSize + new Vector2(-shift - jitter, 0), Vector2.Zero, Vector2.One, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, 0.20f)));
        drawList.AddImage(textureHandle, iconPos + new Vector2(shift + jitter, 0), iconPos + iconSize + new Vector2(shift + jitter, 0), Vector2.Zero, Vector2.One, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.22f)));
        drawList.AddImage(textureHandle, iconPos, iconPos + iconSize, Vector2.Zero, Vector2.One, baseColor);
    }

    private void DrawWifiView()
    {
        // A published page replaces this screen wholesale, so syncshell ids and
        // house rules can change without a plugin release. The hardcoded version
        // below is what shows until the relay supplies one.
        if (GetPage("wifi") is { } page)
        {
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(page.Title) ? "Wi-Fi / Syncshell" : page.Title);
            DrawNeonSeparator();
            ImGui.Spacing();
            DrawMarkdown(page.Body);
            ImGui.Spacing();
            if (ImGui.Button("Discord"))
                OpenDiscord();
            return;
        }

        ImGui.TextUnformatted("Wi-Fi / Syncshell");
        DrawNeonSeparator();
        ImGui.Spacing();

        DrawTerminalLine("Lightless");
        DrawCopyableTerminalLine("Id", LightlessSyncshellId, "lightless_id");
        ImGui.TextDisabled("No password required");
        ImGui.Spacing();

        DrawTerminalLine("PlayerSync");
        DrawCopyableTerminalLine("Id", PlayerSyncSyncshellId, "playersync_id");
        ImGui.TextDisabled("No password required");
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();
        ImGui.TextWrapped("Please compress your textures.");
        ImGui.TextWrapped("Please be SFW.");
        ImGui.Spacing();
        if (ImGui.Button("Discord"))
            OpenDiscord();
    }

    private void DrawServicesView()
    {
        DrawSettingsGroupHeader("TAROT");
        DrawMutedWrapped("Quick local readings and live readings with The Grid's tarot reader.");
        ImGui.Spacing();

        var uiScale = GetUiScale();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var useTwoColumns = availableWidth >= (280 * uiScale);
        var cardWidth = useTwoColumns
            ? MathF.Max(1, (availableWidth - spacing) / 2)
            : availableWidth;
        var cardSize = new Vector2(cardWidth, 232 * uiScale);

        var openArcana = DrawServiceLauncherCard(
            "ARCANA CAST",
            "Tarot reading",
            config.TarotHost ? "HOST CONSOLE" : "SELF-GUIDED / LIVE",
            "tarot.png",
            CyberdeckTheme.Palette.Magenta,
            cardSize,
            portrait: false);

        if (openArcana)
        {
            if (config.TarotHost)
                OpenTarotDebug();
            else
                OpenTarotRequestWindow();
        }

        ImGui.Spacing();
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();
        DrawSettingsGroupHeader("STAFF DIRECTORY");
        DrawMutedWrapped("Staff profiles, portfolios, and music links.");
        ImGui.Spacing();

        if (DrawServiceLauncherCard(
            "VISUAL CAPTURE",
            "Photographer profiles",
            GetStaffDirectoryStatus("photography"),
            "photo.png",
            CyberdeckTheme.Palette.Cyan,
            cardSize,
            portrait: false))
        {
            OpenStaffDirectory("VISUAL CAPTURE", "photography");
        }

        StartNextCardInSection(1);

        if (DrawServiceLauncherCard(
            "RESIDENT DJS",
            "Profiles and music",
            GetStaffDirectoryStatus("dj"),
            "dj.png",
            CyberdeckTheme.Palette.Magenta,
            cardSize,
            portrait: false))
        {
            OpenStaffDirectory("RESIDENT DJS", "dj");
        }

        StartNextCardInSection(2);

        if (DrawServiceLauncherCard(
            "BAR STAFF",
            "Bartender profiles",
            GetStaffDirectoryStatus("bar"),
            "bar.png",
            CyberdeckTheme.Palette.Amber,
            cardSize,
            portrait: false))
        {
            OpenStaffDirectory("BAR STAFF", "bar");
        }

        ImGui.Spacing();
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();
        DrawSettingsGroupHeader("HACKING ACTIVITIES");
        DrawMutedWrapped("Local challenges available through the Cyberdeck.");
        ImGui.Spacing();

        if (DrawServiceLauncherCard(
            "BREACH PROTOCOL",
            "Code matrix",
            "3 DIFFICULTIES",
            "hack.png",
            CyberdeckTheme.Palette.Cyan,
            cardSize,
            portrait: false))
            OpenIntrusionGame();

        StartNextCardInSection(1);

        if (DrawServiceLauncherCard(
            "CIPHER VAULT",
            "Cryptography challenge",
            "ENCRYPTED ARCHIVE",
            "vault.png",
            CyberdeckTheme.Palette.Amber,
            cardSize,
            portrait: false))
            OpenCipherVault();

        void StartNextCardInSection(int index)
        {
            if (useTwoColumns && index % 2 == 1)
                ImGui.SameLine();
            else
                ImGui.Spacing();
        }
    }

    private bool DrawServiceLauncherCard(
        string title,
        string description,
        string status,
        string imageName,
        Vector4 accent,
        Vector2 size,
        bool portrait,
        string actionLabel = "OPEN")
    {
        var uiScale = GetUiScale();
        var start = ImGui.GetCursorScreenPos();
        var max = start + size;
        var drawList = ImGui.GetWindowDrawList();
        var clicked = ImGui.InvisibleButton($"##service_{title}", size);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        drawList.AddRectFilled(
            start,
            max,
            ImGui.GetColorU32(active
                ? CyberdeckTheme.WithAlpha(accent, 0.18f)
                : hovered
                    ? CyberdeckTheme.WithAlpha(accent, 0.10f)
                    : CyberdeckTheme.Palette.Panel),
            5 * uiScale);
        drawList.AddRect(
            start,
            max,
            ImGui.GetColorU32(hovered
                ? CyberdeckTheme.WithAlpha(accent, 0.82f)
                : CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.TextMuted, 0.28f)),
            5 * uiScale,
            ImDrawFlags.None,
            MathF.Max(1, uiScale));

        var topLineInset = 10 * uiScale;
        drawList.AddLine(
            start + new Vector2(topLineInset, 1 * uiScale),
            new Vector2(max.X - topLineInset, start.Y + (1 * uiScale)),
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(accent, hovered ? 0.95f : 0.52f)),
            MathF.Max(1, (hovered ? 2 : 1) * uiScale));

        var imageZoneMin = start + new Vector2(1 * uiScale);
        var imageZoneMax = new Vector2(max.X - (1 * uiScale), start.Y + (122 * uiScale));
        drawList.AddRectFilled(
            imageZoneMin,
            imageZoneMax,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(accent, hovered ? 0.055f : 0.025f)),
            4 * uiScale,
            ImDrawFlags.RoundCornersTop);

        var texture = GetTextureWrap(imageName);
        if (texture is not null)
        {
            var imageLimit = portrait
                ? new Vector2(54, 106) * uiScale
                : new Vector2(88, 88) * uiScale;
            var scale = MathF.Min(
                imageLimit.X / MathF.Max(1, texture.Width),
                imageLimit.Y / MathF.Max(1, texture.Height));
            var imageSize = new Vector2(texture.Width * scale, texture.Height * scale);
            var imageMin = new Vector2(
                start.X + ((size.X - imageSize.X) * 0.5f),
                imageZoneMin.Y + ((imageZoneMax.Y - imageZoneMin.Y - imageSize.Y) * 0.5f));
            drawList.AddImage(
                texture.Handle,
                imageMin,
                imageMin + imageSize,
                Vector2.Zero,
                Vector2.One,
                ImGui.GetColorU32(accent));
        }

        var textWidth = MathF.Max(1, size.X - (14 * uiScale));
        DrawCenteredServiceText(title, start.Y + (137 * uiScale), accent);
        DrawCenteredServiceText(description, start.Y + (164 * uiScale), CyberdeckTheme.Palette.Text);
        DrawCenteredServiceText(status, start.Y + (190 * uiScale), CyberdeckTheme.Palette.TextMuted);
        DrawCenteredServiceText(hovered ? $"{actionLabel}  >" : actionLabel, start.Y + (214 * uiScale), accent);

        return clicked;

        void DrawCenteredServiceText(string value, float y, Vector4 color)
        {
            var displayed = EllipsizeToWidth(value, textWidth);
            var textSize = ImGui.CalcTextSize(displayed);
            drawList.AddText(
                new Vector2(start.X + MathF.Max(0, (size.X - textSize.X) * 0.5f), y),
                ImGui.GetColorU32(color),
                displayed);
        }
    }

    private void ReloadStaffProfiles()
    {
        staffProfiles = StaffProfileCatalog.Load(
            textureLoadSource,
            out staffProfilesSourcePath,
            out staffProfilesLoadError);
    }

    /// <summary>
    /// What the Services tile advertises.
    ///
    /// Counts what a guest can actually open, rather than the six rows the
    /// screen always draws: the tarot cast, the two hacking activities, and any
    /// staff directory that has someone published in it. An empty directory
    /// shows "COMING SOON" inside, so counting it here would promise something
    /// that is not there — and the number moves on its own as the venue
    /// publishes profiles, which is the point of it not being hardcoded.
    /// </summary>
    private string GetServicesTelemetry()
    {
        // Tarot, Breach Protocol, Cipher Vault — always present.
        const int alwaysAvailable = 3;

        var profiles = GetProfileEntries();
        var populatedDirectories = StaffDirectoryCategories.Count(category =>
            profiles.Any(profile =>
                string.Equals(profile.Category, category, StringComparison.OrdinalIgnoreCase)));

        var total = alwaysAvailable + populatedDirectories;
        return $"{total:00} {(total == 1 ? "ACTIVITY" : "ACTIVITIES")}";
    }

    /// <summary>The directories the Services screen offers, in the order it draws them.</summary>
    private static readonly string[] StaffDirectoryCategories = ["photography", "dj", "bar"];

    private string GetStaffDirectoryStatus(string category)
    {
        var count = GetProfileEntries().Count(profile =>
            string.Equals(profile.Category, category, StringComparison.OrdinalIgnoreCase));
        return count == 0 ? "COMING SOON" : $"{count:00} {(count == 1 ? "PROFILE" : "PROFILES")}";
    }

    private void OpenStaffDirectory(string title, string category)
    {
        ReloadStaffProfiles();
        staffDirectoryTitle = title;
        staffDirectoryCategory = category;
        staffDirectoryWindowOpen = true;
        focusStaffDirectoryWindow = true;
    }

    private void DrawStaffDirectoryWindow()
    {
        var uiScale = GetUiScale();
        using var fontScale = CyberdeckTheme.PushFontScale(uiScale);
        using var theme = CyberdeckTheme.Push(uiScale);
        ImGui.SetNextWindowSize(new Vector2(720, 650) * uiScale, ImGuiCond.FirstUseEver);
        var (minimumSize, maximumSize) = CyberdeckTheme.ResolveWindowConstraints(
            uiScale,
            new Vector2(540, 500),
            new Vector2(980, 900));
        ImGui.SetNextWindowSizeConstraints(minimumSize, maximumSize);
        if (focusStaffDirectoryWindow)
        {
            ImGui.SetNextWindowFocus();
            focusStaffDirectoryWindow = false;
        }

        if (!ImGui.Begin($"{staffDirectoryTitle}###grid_staff_directory", ref staffDirectoryWindowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(CyberdeckTheme.Palette.Cyan, staffDirectoryTitle);
        ImGui.SameLine();
        var reloadWidth = 126 * uiScale;
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - reloadWidth));
        if (ImGui.SmallButton("RELOAD PROFILES"))
        {
            ReloadStaffProfiles();
            refreshNews();
        }
        DrawHoverTooltip($"Source: {GetCatalogSourceLabel()} — {staffProfilesSourcePath}");
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        if (!string.IsNullOrWhiteSpace(staffProfilesLoadError) && !getCatalog().IsLoaded)
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, staffProfilesLoadError);
            ImGui.Spacing();
        }

        var profiles = GetProfileEntries()
            .Where(profile => string.Equals(profile.Category, staffDirectoryCategory, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (profiles.Length == 0)
        {
            DrawMutedWrapped("No profiles are published in this directory yet.");
        }
        else
        {
            for (var index = 0; index < profiles.Length; index++)
            {
                if (index > 0)
                {
                    ImGui.Spacing();
                    DrawNeonSeparator();
                    ImGui.Spacing();
                }

                DrawStaffProfile(profiles[index]);
            }
        }

        DrawStaffRequestConfirmation();
        ImGui.End();
    }

    private void DrawStaffProfile(StaffProfile profile)
    {
        var uiScale = GetUiScale();
        if (!ImGui.BeginTable(
                $"staff_profile_{profile.Id}",
                2,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
            return;

        ImGui.TableSetupColumn("IMAGE", ImGuiTableColumnFlags.WidthFixed, 210 * uiScale);
        ImGui.TableSetupColumn("PROFILE", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);

        var texture = ResolveArt(profile.ImageUrl, profile.Image);
        if (texture is not null)
        {
            var scale = MathF.Min(
                (190 * uiScale) / MathF.Max(1, texture.Width),
                (352 * uiScale) / MathF.Max(1, texture.Height));
            ImGui.Image(texture.Handle, new Vector2(texture.Width * scale, texture.Height * scale));
        }
        else
        {
            ImGui.Dummy(new Vector2(190, 352) * uiScale);
            ImGui.TextColored(CyberdeckTheme.Palette.Error, "PROFILE IMAGE NOT FOUND");
        }

        // A brand mark sits under the portrait, at a fraction of its size: it
        // identifies the person, it does not replace their photo.
        var logo = ResolveArt(profile.LogoUrl, profile.Logo);
        if (logo is not null)
        {
            ImGui.Spacing();
            var logoScale = MathF.Min(
                (190 * uiScale) / MathF.Max(1, logo.Width),
                (72 * uiScale) / MathF.Max(1, logo.Height));
            ImGui.Image(logo.Handle, new Vector2(logo.Width * logoScale, logo.Height * logoScale));
        }

        ImGui.TableSetColumnIndex(1);
        ImGui.TextColored(CyberdeckTheme.Palette.Magenta, profile.Name.ToUpperInvariant());
        ImGui.Spacing();
        if (!string.IsNullOrWhiteSpace(profile.Genres))
            DrawStaffProfileField("PLAYS", profile.Genres);

        if (!string.IsNullOrWhiteSpace(profile.Optional?.Pronunciation))
            DrawStaffProfileField("PRONUNCIATION", profile.Optional.Pronunciation);
        DrawStaffProfileField("CHARACTER", profile.CharacterName);
        DrawStaffProfileField("AGE", profile.Age);
        if (!string.IsNullOrWhiteSpace(profile.Optional?.Pronouns))
            DrawStaffProfileField("PRONOUNS", profile.Optional.Pronouns);
        if (!string.IsNullOrWhiteSpace(profile.Optional?.Race))
            DrawStaffProfileField("RACE", profile.Optional.Race);
        DrawStaffProfileField("AFFILIATION", profile.Affiliation);
        DrawStaffProfileField("OCCUPATION", profile.Occupation);
        if (!string.IsNullOrWhiteSpace(profile.Optional?.Availability))
            DrawStaffProfileField("AVAILABLE", profile.Optional.Availability);
        ImGui.Spacing();
        ImGui.TextWrapped(profile.Bio);
        if (!string.IsNullOrWhiteSpace(profile.Optional?.Quote))
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.Palette.TextMuted);
            ImGui.TextWrapped($"“{profile.Optional.Quote}”");
            ImGui.PopStyleColor();
        }
        ImGui.Spacing();

        var atGrid = getNetworkStats().IsActive;
        var present = atGrid && IsPlayerPresent(profile.CharacterName);
        // A DJ takes song requests, so the request carries a title rather than a
        // fixed message. Everyone else keeps the single-button form.
        var isDj = string.Equals(profile.Category, "dj", StringComparison.OrdinalIgnoreCase);

        var cooldownRemaining = GetRequestCooldownRemaining(profile);
        var coolingDown = cooldownRemaining > TimeSpan.Zero;
        var song = string.Empty;
        if (isDj)
        {
            songRequestDrafts.TryGetValue(profile.Id, out song);
            song ??= string.Empty;

            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (ImGui.InputTextWithHint($"##song_{profile.Id}", "Song or artist", ref song, SongRequestMaxLength))
                songRequestDrafts[profile.Id] = song;

            ImGui.Spacing();
        }

        var requestLabel = string.IsNullOrWhiteSpace(profile.RequestLabel)
            ? (isDj ? "REQUEST A SONG" : "SEND REQUEST")
            : profile.RequestLabel.ToUpperInvariant();

        // A DJ needs a song typed; everyone else needs the profile to carry a
        // message, since there is nothing else to send.
        var hasSomethingToSend = isDj
            ? !string.IsNullOrWhiteSpace(song)
            : !string.IsNullOrWhiteSpace(profile.RequestMessage);

        ImGui.BeginDisabled(!atGrid || !present || coolingDown || !hasSomethingToSend);
        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button($"{requestLabel}##request_{profile.Id}", new Vector2(ImGui.GetContentRegionAvail().X, 38 * uiScale)))
            {
                // Only records the intent. Opening the popup from in here would
                // register its id inside this table's ID scope, where the modal —
                // drawn outside the table — could never find it, and the click
                // would silently do nothing.
                pendingStaffRequestProfile = profile;
                staffRequestConfirmationPending = true;
            }
        }
        ImGui.EndDisabled();

        // A silent disabled button reads as broken, so the wait is stated.
        if (coolingDown)
            ImGui.TextDisabled($"Another request in {Math.Ceiling(cooldownRemaining.TotalSeconds):0}s.");
        else if (isDj && atGrid && present && string.IsNullOrWhiteSpace(song))
            ImGui.TextDisabled("Type a song to enable the request.");

        if (!atGrid)
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "Available while you are at The Grid.");
        else if (!present)
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, $"{profile.Name} is not currently at The Grid.");

        if (staffRequestFeedback.TryGetValue(profile.Id, out var feedback))
            ImGui.TextColored(feedback.Success ? CyberdeckTheme.Palette.Success : CyberdeckTheme.Palette.Error, feedback.Message);

        ImGui.EndTable();
    }

    private static void DrawStaffProfileField(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine();
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(value) ? "—" : value);
    }

    private void DrawStaffRequestConfirmation()
    {
        // Opened here rather than at the button, so the popup id is registered in
        // the same ID scope the modal is drawn in.
        if (staffRequestConfirmationPending)
        {
            staffRequestConfirmationPending = false;
            ImGui.OpenPopup("CONFIRM STAFF REQUEST");
        }

        if (!ImGui.BeginPopupModal("CONFIRM STAFF REQUEST", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var profile = pendingStaffRequestProfile;
        if (profile is null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var atGrid = getNetworkStats().IsActive;
        var present = atGrid && IsPlayerPresent(profile.CharacterName);
        ImGui.TextUnformatted($"Send a request to {profile.Name}?");
        ImGui.TextWrapped($"This sends a private message to {profile.CharacterName}.");
        ImGui.Spacing();
        if (!present)
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, $"{profile.Name} is no longer present.");
            ImGui.Spacing();
        }

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonWidth = MathF.Max(110 * GetUiScale(), (ImGui.GetContentRegionAvail().X - spacing) / 2);
        ImGui.BeginDisabled(!atGrid || !present);
        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("SEND REQUEST", new Vector2(buttonWidth, 0)))
            {
                SendStaffRequest(profile);
                pendingStaffRequestProfile = null;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("CANCEL", new Vector2(buttonWidth, 0)))
        {
            pendingStaffRequestProfile = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// How long until this profile will accept another request.
    ///
    /// Song requests get the longer wait: a guest with a playlist in mind will
    /// send one after another, and a DJ reading tells mid-set needs the room to
    /// breathe. A photoshoot request is a one-off, so it keeps the shorter one.
    /// </summary>
    private TimeSpan GetRequestCooldownRemaining(StaffProfile profile)
    {
        if (!staffRequestLastSentAt.TryGetValue(profile.Id, out var lastSentAt))
            return TimeSpan.Zero;

        var cooldown = string.Equals(profile.Category, "dj", StringComparison.OrdinalIgnoreCase)
            ? SongRequestCooldown
            : StaffRequestCooldown;

        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - lastSentAt);
        var remaining = cooldown - elapsed;

        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Builds the message a request actually sends.
    ///
    /// A DJ's request is the typed song appended to the profile's message, so
    /// the venue controls the wording and the guest supplies only the title.
    /// The song is never sent alone: without the profile's framing the DJ would
    /// receive a bare song name with no indication of what it is.
    /// </summary>
    private string ComposeRequestMessage(StaffProfile profile)
    {
        var isDj = string.Equals(profile.Category, "dj", StringComparison.OrdinalIgnoreCase);
        if (!isDj)
            return profile.RequestMessage;

        var song = songRequestDrafts.TryGetValue(profile.Id, out var draft) ? draft.Trim() : string.Empty;
        if (song.Length == 0)
            return string.Empty;

        var prefix = string.IsNullOrWhiteSpace(profile.RequestMessage)
            ? "Song request:"
            : profile.RequestMessage.Trim();

        return $"{prefix} {song}";
    }

    private void SendStaffRequest(StaffProfile profile)
    {
        // Checked here as well as on the button: the confirmation popup can be
        // open across the moment a cooldown starts, and the disabled button is a
        // hint rather than the rule.
        var cooldownRemaining = GetRequestCooldownRemaining(profile);
        if (cooldownRemaining > TimeSpan.Zero)
        {
            staffRequestFeedback[profile.Id] =
                (false, $"Wait {Math.Ceiling(cooldownRemaining.TotalSeconds):0}s before sending another request.");
            return;
        }

        if (!getNetworkStats().IsActive)
        {
            staffRequestFeedback[profile.Id] = (false, "Requests are available only while you are at The Grid.");
            return;
        }

        if (!IsPlayerPresent(profile.CharacterName))
        {
            staffRequestFeedback[profile.Id] = (false, $"{profile.Name} is not currently at The Grid.");
            return;
        }

        var message = ComposeRequestMessage(profile);
        if (string.IsNullOrWhiteSpace(message))
        {
            staffRequestFeedback[profile.Id] = (false, "There is nothing to send for this profile.");
            return;
        }

        if (TarotTellSender.TrySendMessage(profile.CharacterName, message, out var error))
        {
            staffRequestLastSentAt[profile.Id] = Environment.TickCount64;
            // Clearing the draft stops the same song being sent twice by reflex.
            songRequestDrafts.Remove(profile.Id);
            staffRequestFeedback[profile.Id] = (true, $"Request sent to {profile.Name}.");
            return;
        }

        staffRequestFeedback[profile.Id] = (false, error);
    }

    private void OpenTarotRequestWindow()
    {
        tarotRequestWindowOpen = true;
        focusTarotRequestWindow = true;
    }

    private void DrawTarotRequestWindow()
    {
        var uiScale = GetUiScale();
        using var fontScale = CyberdeckTheme.PushFontScale(uiScale);
        using var theme = CyberdeckTheme.Push(uiScale);
        ImGui.SetNextWindowSize(new Vector2(420, 370) * uiScale, ImGuiCond.FirstUseEver);
        var (minimumSize, maximumSize) = CyberdeckTheme.ResolveWindowConstraints(
            uiScale,
            new Vector2(340, 300),
            new Vector2(620, 520));
        ImGui.SetNextWindowSizeConstraints(minimumSize, maximumSize);
        if (focusTarotRequestWindow)
        {
            ImGui.SetNextWindowFocus();
            focusTarotRequestWindow = false;
        }

        if (!ImGui.Begin("ARCANA CAST // TAROT READING###grid_tarot_request", ref tarotRequestWindowOpen, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(CyberdeckTheme.Palette.Magenta, "ARCANA CAST");
        ImGui.TextWrapped("Choose a quick private reading, or request a live reading when the tarot reader is at The Grid.");
        ImGui.Spacing();

        DrawSettingsGroupHeader("SELF-GUIDED READING");
        DrawMutedWrapped("Choose a local reading. No private messages are sent.");
        ImGui.Spacing();
        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("THREE-CARD READING", new Vector2(ImGui.GetContentRegionAvail().X, 38 * uiScale)))
            {
                OpenTarotAiReading();
                tarotRequestWindowOpen = false;
            }
        }
        ImGui.Spacing();
        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("YES / NO READING", new Vector2(ImGui.GetContentRegionAvail().X, 38 * uiScale)))
            {
                OpenTarotYesNoReading();
                tarotRequestWindowOpen = false;
            }
        }

        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();
        DrawSettingsGroupHeader("LIVE TAROT READING");
        ImGui.Spacing();
        ImGui.TextDisabled("Reader");
        ImGui.SameLine();
        ImGui.TextUnformatted(TarotReaderRecipient);
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        var requestCoolingDown = tarotRequestLastSentAt > 0 &&
                                 Environment.TickCount64 - tarotRequestLastSentAt < 30_000;
        var atGrid = getNetworkStats().IsActive;
        var readerPresent = atGrid && IsTarotReaderPresent();
        ImGui.BeginDisabled(requestCoolingDown || !atGrid || !readerPresent);
        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("REQUEST A TAROT READING", new Vector2(ImGui.GetContentRegionAvail().X, 38 * uiScale)))
                ImGui.OpenPopup("CONFIRM TAROT REQUEST");
        }
        ImGui.EndDisabled();

        if (!atGrid)
        {
            ImGui.Spacing();
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "Available while you are at The Grid.");
        }
        else if (!readerPresent)
        {
            ImGui.Spacing();
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "Virginia John is not currently at The Grid.");
        }

        if (ImGui.BeginPopupModal("CONFIRM TAROT REQUEST", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Request a tarot reading?");
            ImGui.TextWrapped($"This sends a private message to {TarotReaderRecipient}.");
            ImGui.Spacing();
            var popupSpacing = ImGui.GetStyle().ItemSpacing.X;
            var popupButtonWidth = MathF.Max(110 * uiScale, (ImGui.GetContentRegionAvail().X - popupSpacing) / 2);
            if (!readerPresent)
            {
                ImGui.TextColored(CyberdeckTheme.Palette.Amber, "The reader is no longer present.");
                ImGui.Spacing();
            }
            ImGui.BeginDisabled(!atGrid || !readerPresent);
            using (CyberdeckTheme.PushAccentButton())
            {
                if (ImGui.Button("SEND REQUEST", new Vector2(popupButtonWidth, 0)))
                {
                    SendTarotReadingRequest();
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("CANCEL", new Vector2(popupButtonWidth, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (!string.IsNullOrWhiteSpace(tarotRequestFeedback))
        {
            ImGui.Spacing();
            ImGui.TextColored(
                tarotRequestSucceeded ? CyberdeckTheme.Palette.Success : CyberdeckTheme.Palette.Error,
                tarotRequestFeedback);
        }

        ImGui.End();
    }

    private void SendTarotReadingRequest()
    {
        if (!getNetworkStats().IsActive)
        {
            tarotRequestSucceeded = false;
            tarotRequestFeedback = "Requests are available only while you are at The Grid.";
            return;
        }

        if (!IsTarotReaderPresent())
        {
            tarotRequestSucceeded = false;
            tarotRequestFeedback = "Virginia John is not currently at The Grid.";
            return;
        }

        if (TarotTellSender.TrySendMessage(TarotReaderRecipient, TarotReadingRequestMessage, out var error))
        {
            tarotRequestSucceeded = true;
            tarotRequestLastSentAt = Environment.TickCount64;
            tarotRequestFeedback = "Request sent to Virginia John.";
            return;
        }

        tarotRequestSucceeded = false;
        tarotRequestFeedback = error;
    }

    private static bool IsTarotReaderPresent()
        => IsPlayerPresent(TarotReaderRecipient);

    private static bool IsPlayerPresent(string identity)
    {
        try
        {
            var local = NetworkGuestScanner.CaptureLocal();
            if (local is not null &&
                string.Equals(local.Identity, identity, StringComparison.OrdinalIgnoreCase))
                return true;

            return NetworkGuestScanner.Capture().Any(guest =>
                string.Equals(guest.Identity, identity, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Could not check whether {Identity} is present.", identity);
            return false;
        }
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

        var entries = GetMenuEntries();
        for (var i = 0; i < entries.Count; i++)
        {
            var item = entries[i];
            if (i > 0)
            {
                ImGui.Spacing();
                DrawNeonSeparator();
                ImGui.Spacing();
            }

            DrawDrinkCard(item, i);
        }
    }

    /// <summary>Width of the art column on the drinks card.</summary>
    private const float DrinkArtColumnWidth = 104f;

    /// <summary>
    /// One drink: art on the left, everything else to its right.
    ///
    /// A table rather than Image + SameLine, because the text needs a column of
    /// its own to wrap inside — with SameLine it wraps against the window edge
    /// and runs back underneath the picture.
    ///
    /// The art is scaled down to the column instead of drawn at native size. A
    /// drinks card is a list to scan, and full-size glasses turned every entry
    /// into a screenful.
    /// </summary>
    private void DrawDrinkCard(MenuEntry item, int index)
    {
        var uiScale = GetUiScale();
        var wrap = ResolveArt(item.ImageUrl, item.BundledImage);
        var columnWidth = DrinkArtColumnWidth * uiScale;

        if (!ImGui.BeginTable(
                $"drink_{index}",
                wrap is null ? 1 : 2,
                ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        if (wrap is not null)
        {
            ImGui.TableSetupColumn("ART", ImGuiTableColumnFlags.WidthFixed, columnWidth);
            ImGui.TableSetupColumn("DETAIL", ImGuiTableColumnFlags.WidthStretch);
        }
        else
        {
            ImGui.TableSetupColumn("DETAIL", ImGuiTableColumnFlags.WidthStretch);
        }

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);

        if (wrap is not null)
        {
            var scale = MathF.Min(
                columnWidth / MathF.Max(1, wrap.Width),
                (150 * uiScale) / MathF.Max(1, wrap.Height));
            ImGui.Image(wrap.Handle, new Vector2(wrap.Width * scale, wrap.Height * scale));
            ImGui.TableSetColumnIndex(1);
        }

        ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.Palette.Cyan);
        ImGui.TextWrapped(item.Name);
        ImGui.PopStyleColor();

        ImGui.TextColored(CyberdeckTheme.Palette.Amber, $"{item.PriceLabel} gil");
        ImGui.SameLine();
        if (ImGui.SmallButton($"Copy##drink_{index}"))
            CopyToClipboard(item.Name, "DRINK NAME COPIED");
        DrawHoverTooltip("Copy to clipboard");

        ImGui.TextWrapped(item.Description);

        if (ImGui.CollapsingHeader($"FLAVOR PROFILE##drink_profile_{index}"))
        {
            // TextDisabled does not wrap; in a narrow column the ingredient list
            // would run off the side. TextWrapped wraps to the column instead.
            ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.Palette.TextMuted);
            ImGui.TextWrapped($"Ingredients: {item.Ingredients}");
            ImGui.PopStyleColor();
            ImGui.TextWrapped($"Taste: {item.Taste}");
        }

        ImGui.EndTable();
    }

    private void DrawNetworkView()
    {
        var guests = NetworkGuestScanner.Capture();
        var localPlayer = NetworkGuestScanner.CaptureLocal();
        var visiblePeople = localPlayer is null
            ? guests
            : guests.Prepend(localPlayer).ToList();
        if (IsVenueManager(localPlayer))
        {
            var stats = getNetworkStats();
            var sessionLabel = stats.IsActive
                ? $"VENUE SESSION // ACTIVE // {FormatNetworkDuration(stats.StartedAt)}"
                : "VENUE SESSION // INACTIVE";
            ImGui.PushStyleColor(
                ImGuiCol.Text,
                stats.IsActive ? CyberdeckTheme.Palette.Success : CyberdeckTheme.Palette.TextMuted);
            var showVenueSession = ImGui.CollapsingHeader($"{sessionLabel}###network_venue_session");
            ImGui.PopStyleColor();
            if (showVenueSession)
            {
                DrawMutedWrapped(stats.IsActive
                    ? "Counts represent everyone client-visible at the configured Grid venue, including you."
                    : "Live signals are shown below, but statistics are recorded only at the configured Grid venue.");
                ImGui.Spacing();
                DrawNetworkMetrics(stats, visiblePeople);
                ImGui.Spacing();
                DrawNetworkTraffic(stats);
                DrawNetworkHistory();
            }
            DrawNeonSeparator();
            ImGui.Spacing();
        }

        ImGui.TextColored(CyberdeckTheme.Palette.Cyan, "LOCAL NETWORK");
        if (guests.Count == 0 && localPlayer is null)
        {
            ImGui.TextWrapped("No local player signals detected.");
            return;
        }

        var uiScale = GetUiScale();
        if (!ImGui.BeginTable("network_players", 3, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 82 * uiScale);
        ImGui.TableHeadersRow();

        if (localPlayer is not null)
            DrawNetworkPlayerRow(localPlayer, isLocal: true);
        foreach (var guest in guests)
            DrawNetworkPlayerRow(guest, isLocal: false);

        ImGui.EndTable();
    }

    private void DrawNetworkMetrics(NetworkStatsSnapshot stats, IReadOnlyList<NetworkGuestObservation> people)
    {
        var worlds = people
            .Where(guest => guest.HomeWorld != "Unknown World")
            .Select(guest => guest.HomeWorld)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var dataCenters = people
            .Where(guest => guest.DataCenter != "Unknown DC")
            .Select(guest => guest.DataCenter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var compact = ImGui.GetContentRegionAvail().X < (460 * GetUiScale());
        var columns = compact ? 2 : 4;
        if (!ImGui.BeginTable("network_metrics", columns, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            return;

        ImGui.TableNextRow();
        DrawNetworkMetricCell("VISIBLE", people.Count.ToString("00"));
        DrawNetworkMetricCell("SESSION PEAK", stats.IsActive ? stats.PeakGuests.ToString("00") : "--");
        if (compact)
            ImGui.TableNextRow();
        DrawNetworkMetricCell("HOME WORLDS", worlds.ToString("00"));
        DrawNetworkMetricCell("DATA CENTERS", dataCenters.ToString("00"));
        ImGui.EndTable();
    }

    private static void DrawNetworkMetricCell(string label, string value)
    {
        ImGui.TableNextColumn();
        var available = ImGui.GetContentRegionAvail().X;
        var valueSize = ImGui.CalcTextSize(value);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (available - valueSize.X) * 0.5f));
        ImGui.TextColored(CyberdeckTheme.Palette.Cyan, value);
        var labelSize = ImGui.CalcTextSize(label);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (available - labelSize.X) * 0.5f));
        ImGui.TextDisabled(label);
    }

    private static string FormatNetworkDuration(DateTimeOffset? startedAt)
    {
        if (startedAt is null)
            return "00:00";
        var elapsed = DateTimeOffset.UtcNow - startedAt.Value;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void DrawNetworkTraffic(NetworkStatsSnapshot stats)
    {
        if (!ImGui.CollapsingHeader("TRAFFIC HISTORY"))
            return;

        var allBuckets = config.NetworkSessionHistory
            .SelectMany(summary => summary.OccupancyBuckets ?? [])
            .Concat(stats.OccupancyBuckets)
            .Where(bucket => bucket.SampleCount > 0)
            .OrderBy(bucket => bucket.StartedAtUnixMs)
            .ToList();

        ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, "TRAFFIC // 10-MINUTE WINDOWS");
        if (allBuckets.Count == 0)
        {
            ImGui.TextDisabled(stats.IsActive
                ? "Collecting the first traffic window..."
                : "No venue traffic recorded yet.");
            return;
        }

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (stats.IsActive && stats.OccupancyBuckets.Count > 0)
        {
            var current = stats.OccupancyBuckets[^1];
            var currentStart = DateTimeOffset.FromUnixTimeMilliseconds(current.StartedAtUnixMs).ToLocalTime();
            ImGui.TextColored(
                CyberdeckTheme.Palette.Cyan,
                $"CURRENT WINDOW // {currentStart:HH:mm}-{currentStart.AddMinutes(10):HH:mm}");
            ImGui.TextDisabled($"Usually {FormatPeopleCount(current.AverageGuests)} // peak {FormatPeopleCount(current.PeakGuests)}");
        }
        else
        {
            var completedBuckets = allBuckets
                .Where(bucket => bucket.StartedAtUnixMs + (10 * 60 * 1000) <= nowUnixMs)
                .ToList();
            var primeSource = completedBuckets.Count > 0 ? completedBuckets : allBuckets;
            var prime = primeSource
                .GroupBy(bucket =>
                {
                    var local = DateTimeOffset.FromUnixTimeMilliseconds(bucket.StartedAtUnixMs).ToLocalTime();
                    return (local.Hour * 6) + (local.Minute / 10);
                })
                .Select(group => new
                {
                    Slot = group.Key,
                    SampleTotal = group.Sum(bucket => bucket.SampleTotal),
                    SampleCount = group.Sum(bucket => bucket.SampleCount),
                    Peak = group.Max(bucket => bucket.PeakGuests),
                })
                .Where(group => group.SampleCount > 0)
                .OrderByDescending(group => group.SampleTotal / (double)group.SampleCount)
                .ThenByDescending(group => group.Peak)
                .First();

            var primeAverage = prime.SampleTotal / (double)prime.SampleCount;
            ImGui.TextColored(
                CyberdeckTheme.Palette.Cyan,
                $"PRIME OBSERVED // {FormatNetworkTrafficSlot(prime.Slot)}-{FormatNetworkTrafficSlot((prime.Slot + 1) % 144)}");
            ImGui.TextDisabled($"Usually {FormatPeopleCount(primeAverage)} // peak {FormatPeopleCount(prime.Peak)}");
        }

        IReadOnlyList<NetworkOccupancyBucket> graphBuckets;
        if (stats.IsActive && stats.OccupancyBuckets.Count > 0)
        {
            graphBuckets = stats.OccupancyBuckets;
        }
        else
        {
            graphBuckets = config.NetworkSessionHistory
                .OrderByDescending(summary => summary.EndedAtUnixMs)
                .Select(summary => (IReadOnlyList<NetworkOccupancyBucket>)(summary.OccupancyBuckets ?? []))
                .FirstOrDefault(candidate => candidate.Count > 0) ?? allBuckets;
        }

        DrawNetworkTrafficGraph(graphBuckets, stats.IsActive, nowUnixMs);
    }

    private void DrawNetworkTrafficGraph(
        IReadOnlyList<NetworkOccupancyBucket> sourceBuckets,
        bool sessionActive,
        long nowUnixMs)
    {
        var uiScale = GetUiScale();
        var availableWidth = MathF.Max(120 * uiScale, ImGui.GetContentRegionAvail().X);
        var graphHeight = 132 * uiScale;
        var graphMin = ImGui.GetCursorScreenPos();
        var graphMax = graphMin + new Vector2(availableWidth, graphHeight);
        ImGui.InvisibleButton("##network_traffic_graph", new Vector2(availableWidth, graphHeight));

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            graphMin,
            graphMax,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Panel, 0.92f)),
            3 * uiScale);
        drawList.AddRect(
            graphMin,
            graphMax,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.72f)),
            3 * uiScale);

        var plotMin = graphMin + new Vector2(31, 8) * uiScale;
        var plotMax = graphMax - new Vector2(7, 22) * uiScale;
        var plotWidth = MathF.Max(1, plotMax.X - plotMin.X);
        var plotHeight = MathF.Max(1, plotMax.Y - plotMin.Y);
        var maximumBars = Math.Max(6, Math.Min(48, (int)(plotWidth / MathF.Max(4, 6 * uiScale))));
        var buckets = sourceBuckets.TakeLast(maximumBars).ToList();
        var yMaximum = Math.Max(1, buckets.Max(bucket => Math.Max(bucket.PeakGuests, (int)Math.Ceiling(bucket.AverageGuests))));

        var axisColor = ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.TextMuted, 0.72f));
        var gridColor = ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.34f));
        for (var division = 0; division <= 2; division++)
        {
            var progress = division / 2f;
            var y = plotMax.Y - (plotHeight * progress);
            drawList.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMax.X, y), gridColor, MathF.Max(1, uiScale));
            var value = (int)MathF.Round(yMaximum * progress);
            var label = value.ToString();
            var labelSize = ImGui.CalcTextSize(label);
            drawList.AddText(
                new Vector2(plotMin.X - labelSize.X - (5 * uiScale), y - (labelSize.Y * 0.5f)),
                axisColor,
                label);
        }

        drawList.AddLine(plotMin, new Vector2(plotMin.X, plotMax.Y), axisColor, MathF.Max(1, uiScale));
        drawList.AddLine(new Vector2(plotMin.X, plotMax.Y), plotMax, axisColor, MathF.Max(1, uiScale));

        var gap = MathF.Max(1, uiScale);
        var barWidth = MathF.Max(2, (plotWidth - (gap * Math.Max(0, buckets.Count - 1))) / Math.Max(1, buckets.Count));
        var mouse = ImGui.GetIO().MousePos;
        var highestAverage = buckets.Max(bucket => bucket.AverageGuests);
        for (var index = 0; index < buckets.Count; index++)
        {
            var bucket = buckets[index];
            var barLeft = plotMin.X + (index * (barWidth + gap));
            var barRight = MathF.Min(plotMax.X, barLeft + barWidth);
            var barTop = plotMax.Y - (float)(bucket.AverageGuests / yMaximum * plotHeight);
            var barMin = new Vector2(barLeft, barTop);
            var barMax = new Vector2(barRight, plotMax.Y);
            var isLive = sessionActive && bucket.StartedAtUnixMs + (10 * 60 * 1000) > nowUnixMs;
            var isPeak = Math.Abs(bucket.AverageGuests - highestAverage) < 0.001;
            var hovered = mouse.X >= barMin.X && mouse.X <= barMax.X && mouse.Y >= plotMin.Y && mouse.Y <= plotMax.Y;
            var color = hovered
                ? CyberdeckTheme.Palette.Text
                : isLive
                    ? CyberdeckTheme.Palette.Amber
                    : isPeak
                        ? CyberdeckTheme.Palette.Magenta
                        : CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.76f);
            drawList.AddRectFilled(barMin, barMax, ImGui.GetColorU32(color));

            if (!hovered)
                continue;

            var started = DateTimeOffset.FromUnixTimeMilliseconds(bucket.StartedAtUnixMs).ToLocalTime();
            ImGui.SetTooltip(
                $"{started:HH:mm}-{started.AddMinutes(10):HH:mm}\n" +
                $"Usually {FormatPeopleCount(bucket.AverageGuests)}\n" +
                $"Peak {FormatPeopleCount(bucket.PeakGuests)}" +
                (isLive ? "\nLive window" : string.Empty));
        }

        var firstStart = DateTimeOffset.FromUnixTimeMilliseconds(buckets[0].StartedAtUnixMs).ToLocalTime();
        var finalEnd = DateTimeOffset.FromUnixTimeMilliseconds(buckets[^1].StartedAtUnixMs).ToLocalTime().AddMinutes(10);
        var firstLabel = firstStart.ToString("HH:mm");
        var finalLabel = finalEnd.ToString("HH:mm");
        var finalLabelSize = ImGui.CalcTextSize(finalLabel);
        drawList.AddText(new Vector2(plotMin.X, plotMax.Y + (4 * uiScale)), axisColor, firstLabel);
        drawList.AddText(new Vector2(plotMax.X - finalLabelSize.X, plotMax.Y + (4 * uiScale)), axisColor, finalLabel);
    }

    private static string FormatNetworkTrafficSlot(int slot)
        => $"{slot / 6:00}:{(slot % 6) * 10:00}";

    private static string FormatPeopleCount(double value)
    {
        var rounded = Math.Max(0, (int)Math.Round(value, MidpointRounding.AwayFromZero));
        return $"{rounded} {(rounded == 1 ? "person" : "people")}";
    }

    private void DrawNetworkHistory()
    {
        if (config.NetworkSessionHistory.Count == 0 ||
            !ImGui.CollapsingHeader("SESSION HISTORY"))
            return;

        DrawMutedWrapped("Anonymous venue summaries retained for 90 days. Guest names are never saved.");
        foreach (var summary in config.NetworkSessionHistory
                     .OrderByDescending(item => item.EndedAtUnixMs)
                     .Take(8))
        {
            var started = DateTimeOffset.FromUnixTimeMilliseconds(summary.StartedAtUnixMs).ToLocalTime();
            var ended = DateTimeOffset.FromUnixTimeMilliseconds(summary.EndedAtUnixMs).ToLocalTime();
            var duration = ended - started;
            var durationText = duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}"
                : $"{Math.Max(0, duration.Minutes):00}:{Math.Max(0, duration.Seconds):00}";
            ImGui.TextUnformatted($"{ended:MMM dd HH:mm} // {durationText} // {summary.UniqueGuests:00} unique // peak {summary.PeakGuests:00}");
            ImGui.TextDisabled($"Usually {FormatPeopleCount(summary.AverageGuests)} // {summary.WorldsRepresented:00} worlds // {summary.DataCentersRepresented:00} data centers");
        }
        if (ImGui.SmallButton("Clear History"))
        {
            config.NetworkSessionHistory.Clear();
            config.Save();
            SetTransientFeedback("NETWORK HISTORY CLEARED");
        }
        ImGui.Spacing();
    }

    private void DrawNetworkPlayerRow(NetworkGuestObservation guest, bool isLocal)
    {

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (isLocal)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.Palette.Cyan);
        }
        else if (guest.IsFriend)
        {
            var glow = config.ReduceMotion
                ? 1.0f
                : 0.80f + MathF.Sin((float)ImGui.GetTime() * 3.0f) * 0.20f;
            ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Amber, glow));
        }
        var playerLabel = isLocal
            ? $"YOU // {guest.Player.Name.TextValue}"
            : guest.IsFriend
                ? $"★ {guest.Player.Name.TextValue}"
                : guest.Player.Name.TextValue;
        if (ImGui.Selectable($"{playerLabel}##network_{guest.Identity}"))
            PluginService.Targets.Target = guest.Player;
        if (isLocal)
        {
            ImGui.PopStyleColor();
            DrawHoverTooltip("Local player // host // excluded from guest statistics");
        }
        else if (guest.IsFriend)
        {
            ImGui.PopStyleColor();
            DrawHoverTooltip("★ Friend");
        }

        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(guest.HomeWorld);

        ImGui.TableSetColumnIndex(2);
        var hasStatus = false;
        if (guest.HasWeapon)
        {
            DrawNetworkStatusIcon(
                "weapon.png",
                "Weapon",
                guest.OffhandOut ? "Weapon/offhand drawn" : "Weapon drawn",
                CyberdeckTheme.Palette.Amber);
            hasStatus = true;
        }
        if (!string.IsNullOrWhiteSpace(guest.MinionName))
        {
            if (hasStatus)
                ImGui.SameLine();
            DrawNetworkStatusIcon(
                "minion.png",
                "Minion",
                $"Visible minion: {guest.MinionName}",
                CyberdeckTheme.Palette.Cyan);
            hasStatus = true;
        }
        if (guest.IsPartyMember)
        {
            if (hasStatus)
                ImGui.SameLine();
            ImGui.TextColored(CyberdeckTheme.Palette.Success, "P");
            DrawHoverTooltip("Party member");
            hasStatus = true;
        }
        if (!hasStatus)
            ImGui.TextDisabled("—");
    }

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

    private void DrawNetworkStatusIcon(string imageName, string fallbackText, string tooltip, Vector4 tint)
    {
        var wrap = GetTextureWrap(imageName);
        if (wrap is not null)
        {
            var maxSize = 18 * GetUiScale();
            var scale = MathF.Min(maxSize / MathF.Max(1, wrap.Width), maxSize / MathF.Max(1, wrap.Height));
            var size = new Vector2(wrap.Width * scale, wrap.Height * scale);
            ImGui.Image(wrap.Handle, size, Vector2.Zero, Vector2.One, tint);
        }
        else
            ImGui.TextUnformatted(fallbackText);

        DrawHoverTooltip(tooltip);
    }

    private void DrawIntrusionView()
    {
        var now = Environment.TickCount64;
        if (intrusionGame is not null)
        {
            intrusionGame.Tick(now);
            RecordIntrusionResultIfNeeded(intrusionGame);
        }

        if (showIntrusionPayload)
        {
            DrawIntrusionPayload();
            return;
        }

        if (intrusionGame is null)
        {
            DrawIntrusionLanding();
            return;
        }

        DrawIntrusionGame(intrusionGame, now);
    }

    private void DrawIntrusionLanding()
    {
        ImGui.TextColored(CyberdeckTheme.Palette.Magenta, "BLACK ICE // INTRUSION");
        DrawMutedWrapped("Unauthorized local simulation. Build target sequences by alternating between the selected column and row.");
        DrawNeonSeparator();
        ImGui.Spacing();

        DrawSettingsGroupHeader("THREAT LEVEL");
        var selectedDifficulty = Math.Clamp(config.IntrusionDifficulty, 0, 2);
        var stack = ImGui.GetContentRegionAvail().X < (330 * GetUiScale());
        DrawIntrusionDifficultyChoice("CASUAL", (int)IntrusionDifficulty.Casual, ref selectedDifficulty);
        if (!stack) ImGui.SameLine();
        DrawIntrusionDifficultyChoice("STANDARD", (int)IntrusionDifficulty.Standard, ref selectedDifficulty);
        if (!stack) ImGui.SameLine();
        DrawIntrusionDifficultyChoice("BLACK ICE", (int)IntrusionDifficulty.BlackIce, ref selectedDifficulty);
        if (selectedDifficulty != config.IntrusionDifficulty)
        {
            config.IntrusionDifficulty = selectedDifficulty;
            config.Save();
        }

        var difficulty = (IntrusionDifficulty)selectedDifficulty;
        DrawMutedWrapped(GetIntrusionDifficultyDescription(difficulty));
        if (difficulty != IntrusionDifficulty.BlackIce)
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "Encrypted payload authentication requires BLACK ICE.");

        ImGui.Spacing();
        DrawSettingsGroupHeader("LOCAL RECORD");
        ImGui.TextDisabled($"BEST // {GetIntrusionBestScore(difficulty):00000}");
        ImGui.TextDisabled($"SUCCESSFUL BREACHES // {config.IntrusionSuccessfulBreaches}");
        ImGui.TextDisabled("PAYLOAD AUTH // ALL SEQUENCES + CLEAN ROUTE");
        ImGui.Spacing();

        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("INITIATE INTRUSION", new Vector2(ImGui.GetContentRegionAvail().X, 38 * GetUiScale())))
                StartIntrusion(difficulty);
        }

        ImGui.Spacing();
        DrawMutedWrapped(difficulty == IntrusionDifficulty.BlackIce
            ? "BLACK ICE locks the matrix for a 2-second link countdown, then starts the trace automatically. A clean route is required."
            : "The trace timer begins only after the first token is selected.");
    }

    private static void DrawIntrusionDifficultyChoice(string label, int value, ref int selected)
    {
        if (ImGui.RadioButton(label, selected == value))
            selected = value;
    }

    private void DrawIntrusionGame(IntrusionGame game, long now)
    {
        var (statusLabel, statusColor) = GetIntrusionStatus(game);
        CyberdeckWidgets.DrawStatusChip(statusLabel, statusColor, CyberdeckTheme.Palette.Text, GetUiScale());
        if (ImGui.GetContentRegionAvail().X < (310 * GetUiScale()))
        {
            ImGui.TextDisabled($"SCORE // {game.GetCurrentScore(now):00000}");
            ImGui.TextDisabled($"THREAT // {GetIntrusionDifficultyName(game.Difficulty)}");
        }
        else
        {
            ImGui.TextDisabled($"SCORE // {game.GetCurrentScore(now):00000}    THREAT // {GetIntrusionDifficultyName(game.Difficulty)}");
        }

        if (!game.IsTerminal)
        {
            if (game.Phase == IntrusionPhase.Countdown)
            {
                ImGui.TextColored(
                    CyberdeckTheme.Palette.Magenta,
                    $"TRACE ARMING // {game.GetCountdownRemainingSeconds(now):00}");
            }
            else
            {
                ImGui.TextColored(CyberdeckTheme.Palette.Cyan, game.GetSelectionHint());
            }
            if (game.TimeLimitSeconds is { })
            {
                CyberdeckWidgets.DrawLabeledProgress(
                    "TRACE WINDOW",
                    game.GetRemainingFraction(now),
                    config.ReduceMotion,
                    CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.45f),
                    game.GetRemainingSeconds(now) <= 8 ? CyberdeckTheme.Palette.Error : CyberdeckTheme.Palette.Amber,
                    CyberdeckTheme.Palette.Text,
                    CyberdeckTheme.Palette.TextMuted,
                    $"{game.GetRemainingSeconds(now):00}s",
                    height: 7 * GetUiScale());
            }
            else
            {
                ImGui.TextDisabled("TRACE WINDOW // DISABLED");
            }
        }

        ImGui.Spacing();
        DrawSettingsGroupHeader("BUFFER");
        DrawIntrusionBuffer(game);
        ImGui.Spacing();

        var objectiveColumnWidth = 190 * GetUiScale();
        if (ImGui.BeginTable(
                "intrusion_workspace",
                2,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV,
                new Vector2(ImGui.GetContentRegionAvail().X, 0)))
        {
            ImGui.TableSetupColumn("OBJECTIVES", ImGuiTableColumnFlags.WidthFixed, objectiveColumnWidth);
            ImGui.TableSetupColumn("CODE MATRIX", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawSettingsGroupHeader("OBJECTIVES");
            foreach (var objective in game.Objectives)
            {
                DrawIntrusionObjective(game, objective);
                ImGui.Spacing();
            }

            ImGui.TableSetColumnIndex(1);
            DrawSettingsGroupHeader("CODE MATRIX");
            DrawIntrusionMatrix(game, now);
            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (game.IsTerminal)
        {
            DrawIntrusionResult(game);
        }
        else if (ImGui.Button("ABORT SESSION"))
        {
            intrusionGame = null;
            intrusionResultRecorded = false;
        }
    }

    private void DrawIntrusionBuffer(IntrusionGame game)
    {
        var uiScale = GetUiScale();
        var spacing = 3 * uiScale;
        var available = ImGui.GetContentRegionAvail().X;
        var slotWidth = Math.Clamp(
            (available - (spacing * (game.BufferCapacity - 1))) / game.BufferCapacity,
            20 * uiScale,
            46 * uiScale);
        var slotHeight = 27 * uiScale;
        var stripWidth = (slotWidth * game.BufferCapacity) + (spacing * (game.BufferCapacity - 1));
        var origin = ImGui.GetCursorScreenPos() + new Vector2(MathF.Max(0, (available - stripWidth) / 2), 0);
        var drawList = ImGui.GetWindowDrawList();

        for (var index = 0; index < game.BufferCapacity; index++)
        {
            var min = origin + new Vector2(index * (slotWidth + spacing), 0);
            var max = min + new Vector2(slotWidth, slotHeight);
            var occupied = index < game.Buffer.Count;
            var color = occupied
                ? index == game.Buffer.Count - 1 ? CyberdeckTheme.Palette.Magenta : CyberdeckTheme.Palette.Cyan
                : CyberdeckTheme.Palette.Border;
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.PanelRaised, occupied ? 0.88f : 0.42f)), 2 * uiScale);
            drawList.AddRect(min, max, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, occupied ? 0.92f : 0.45f)), 2 * uiScale);

            var text = occupied ? game.Buffer[index] : "--";
            var textSize = ImGui.CalcTextSize(text);
            drawList.AddText(
                min + new Vector2((slotWidth - textSize.X) / 2, (slotHeight - textSize.Y) / 2),
                ImGui.GetColorU32(occupied ? CyberdeckTheme.Palette.Text : CyberdeckTheme.Palette.TextMuted),
                text);
        }

        ImGui.Dummy(new Vector2(available, slotHeight));
    }

    private void DrawIntrusionObjective(IntrusionGame game, IntrusionObjective objective)
    {
        var complete = game.IsObjectiveComplete(objective);
        var matched = game.GetObjectivePrefixLength(objective);
        var isEntryVector = string.Equals(objective.Label, "ENTRY VECTOR", StringComparison.Ordinal);
        var color = complete
            ? CyberdeckTheme.Palette.Success
            : matched > 0
                ? CyberdeckTheme.Palette.Amber
                : isEntryVector
                    ? CyberdeckTheme.Palette.Cyan
                : CyberdeckTheme.Palette.TextMuted;
        var marker = complete ? "<OK>" : isEntryVector ? "<IN>" : "<..>";
        ImGui.TextColored(color, $"{marker} {objective.Label}");
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (18 * GetUiScale()));

        for (var index = 0; index < objective.Sequence.Count; index++)
        {
            if (index > 0)
                ImGui.SameLine();
            var tokenColor = complete || index < matched || (isEntryVector && game.Buffer.Count == 0 && index == 0)
                ? color
                : CyberdeckTheme.Palette.TextMuted;
            ImGui.TextColored(tokenColor, objective.Sequence[index]);
        }
    }

    private void DrawIntrusionMatrix(IntrusionGame game, long now)
    {
        var uiScale = GetUiScale();
        var columnSpacing = MathF.Max(2, 4 * uiScale);
        var rowSpacing = ImGui.GetStyle().ItemSpacing.Y;
        var available = ImGui.GetContentRegionAvail().X;
        var cellSize = Math.Clamp(
            (available - (columnSpacing * (game.GridSize - 1))) / game.GridSize,
            22 * uiScale,
            56 * uiScale);
        var gridWidth = (cellSize * game.GridSize) + (columnSpacing * (game.GridSize - 1));
        var gridHeight = (cellSize * game.GridSize) + (rowSpacing * (game.GridSize - 1));
        var rowStart = ImGui.GetCursorPosX() + MathF.Max(0, (available - gridWidth) / 2);
        var gridOrigin = ImGui.GetCursorScreenPos() + new Vector2(MathF.Max(0, (available - gridWidth) / 2), 0);
        var drawList = ImGui.GetWindowDrawList();
        Vector2? axisFrameMin = null;
        Vector2? axisFrameMax = null;

        if (!game.IsTerminal && game.Phase != IntrusionPhase.Countdown)
        {
            if (game.Selections.Count == 0 || game.Selections.Count % 2 == 0)
            {
                var row = game.Selections.Count == 0 ? 0 : game.Selections[^1].Row;
                axisFrameMin = gridOrigin + new Vector2(0, row * (cellSize + rowSpacing));
                axisFrameMax = axisFrameMin + new Vector2(gridWidth, cellSize);
            }
            else
            {
                var column = game.Selections[^1].Column;
                axisFrameMin = gridOrigin + new Vector2(column * (cellSize + columnSpacing), 0);
                axisFrameMax = axisFrameMin + new Vector2(cellSize, gridHeight);
            }
        }

        for (var row = 0; row < game.GridSize; row++)
        {
            ImGui.SetCursorPosX(rowStart);
            for (var column = 0; column < game.GridSize; column++)
            {
                if (column > 0)
                    ImGui.SameLine(0, columnSpacing);

                var selected = game.IsCellSelected(row, column);
                var legal = game.CanSelect(row, column);
                var buttonColor = selected
                    ? CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, 0.48f)
                    : CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.PanelRaised, 0.72f);
                var hoverColor = legal
                    ? CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.52f)
                    : buttonColor;
                ImGui.PushStyleColor(ImGuiCol.Button, buttonColor);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoverColor);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, 0.64f));
                ImGui.BeginDisabled(!legal);
                var clicked = ImGui.Button($"{game.GetToken(row, column)}##intrusion_{row}_{column}", new Vector2(cellSize, cellSize));
                ImGui.EndDisabled();
                ImGui.PopStyleColor(3);

                if (clicked)
                    game.Select(row, column, now);
            }
        }

        if (axisFrameMin is { } frameMin && axisFrameMax is { } frameMax)
        {
            drawList.AddRect(
                frameMin,
                frameMax,
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.88f)),
                2 * uiScale,
                ImDrawFlags.None,
                MathF.Max(1.5f, 2 * uiScale));
        }
    }

    private void DrawIntrusionResult(IntrusionGame game)
    {
        DrawNeonSeparator();
        var success = game.Phase == IntrusionPhase.Success;
        ImGui.TextColored(
            success ? CyberdeckTheme.Palette.Success : CyberdeckTheme.Palette.Error,
            success ? "BREACH COMPLETE" : game.Phase == IntrusionPhase.TimedOut ? "ICE TRACE COMPLETE" : "BUFFER LOCKED");
        ImGui.TextUnformatted($"FINAL SCORE // {game.FinalScore:00000}");

        if (game.Difficulty != IntrusionDifficulty.BlackIce)
        {
            DrawMutedWrapped("Only BLACK ICE sessions can authenticate the encrypted payload.");
        }
        else
        {
            var payloadAuthenticated = game.AllObjectivesCompleted && game.UsedOptimalBuffer;
            ImGui.TextColored(
                payloadAuthenticated ? CyberdeckTheme.Palette.Success : CyberdeckTheme.Palette.Amber,
                !game.AllObjectivesCompleted
                    ? "ROOT PAYLOAD // SEQUENCES INCOMPLETE"
                    : !game.UsedOptimalBuffer
                        ? "ROOT PAYLOAD // CLEAN ROUTE REQUIRED"
                        : "ROOT PAYLOAD // AUTHENTICATED");
        }

        ImGui.Spacing();
        var stackActions = ImGui.GetContentRegionAvail().X < (360 * GetUiScale());
        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("NEW INTRUSION"))
                StartIntrusion(game.Difficulty);
        }
        if (!stackActions)
            ImGui.SameLine();
        if (ImGui.Button("RETURN TO TERMINAL"))
        {
            intrusionGame = null;
            intrusionResultRecorded = false;
        }
    }

    private void DrawIntrusionPayload()
    {
        CyberdeckWidgets.DrawStatusChip(
            "ROOT ACCESS // GRANTED",
            CyberdeckTheme.Palette.Success,
            CyberdeckTheme.Palette.Text,
            GetUiScale());
        ImGui.Spacing();
        DrawSettingsGroupHeader("ENCRYPTED PAYLOAD RECOVERED");
        ImGui.TextColored(CyberdeckTheme.Palette.Cyan, IntrusionEncryptedPayload);
        if (ImGui.Button("COPY PAYLOAD"))
            CopyToClipboard(IntrusionEncryptedPayload, "PAYLOAD COPIED");

        ImGui.Spacing();
        DrawSettingsGroupHeader("CLUE");
        ImGui.TextWrapped(IntrusionPayloadHint);
        if (ImGui.Button("COPY CLUE"))
            CopyToClipboard(IntrusionPayloadHint, "CLUE COPIED");

        ImGui.Spacing();
        DrawMutedWrapped("Decode the recovered payload, then use the recovered password to unlock the Cipher Vault.");
        ImGui.Spacing();
        var stackActions = ImGui.GetContentRegionAvail().X < (390 * GetUiScale());
        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("RUN ANOTHER INTRUSION"))
            {
                showIntrusionPayload = false;
                StartIntrusion((IntrusionDifficulty)Math.Clamp(config.IntrusionDifficulty, 0, 2));
            }
        }
        if (!stackActions)
            ImGui.SameLine();
        if (ImGui.Button("RETURN TO TERMINAL"))
        {
            showIntrusionPayload = false;
            intrusionGame = null;
        }
    }

    private void StartIntrusion(IntrusionDifficulty difficulty)
    {
        try
        {
            intrusionGame = IntrusionGame.Create(difficulty, Environment.TickCount64);
            intrusionResultRecorded = false;
            showIntrusionPayload = false;
            config.IntrusionDifficulty = (int)difficulty;
            config.Save();
            SetTransientFeedback("INTRUSION MATRIX GENERATED");
        }
        catch (Exception ex)
        {
            PluginService.Log.Error(ex, "Could not generate the hidden intrusion puzzle.");
            SetTransientFeedback("MATRIX GENERATION FAILED");
        }
    }

    private void RecordIntrusionResultIfNeeded(IntrusionGame game)
    {
        if (!game.IsTerminal || intrusionResultRecorded)
            return;

        intrusionResultRecorded = true;
        if (game.Phase == IntrusionPhase.Success)
            config.IntrusionSuccessfulBreaches++;

        switch (game.Difficulty)
        {
            case IntrusionDifficulty.Casual:
                config.IntrusionBestCasualScore = Math.Max(config.IntrusionBestCasualScore, game.FinalScore);
                break;
            case IntrusionDifficulty.Standard:
                config.IntrusionBestStandardScore = Math.Max(config.IntrusionBestStandardScore, game.FinalScore);
                break;
            case IntrusionDifficulty.BlackIce:
                config.IntrusionBestBlackIceScore = Math.Max(config.IntrusionBestBlackIceScore, game.FinalScore);
                break;
        }

        var qualifiesForPayload = game.Phase == IntrusionPhase.Success &&
                                  game.AllObjectivesCompleted &&
                                  game.Difficulty == IntrusionDifficulty.BlackIce &&
                                  game.UsedOptimalBuffer;
        if (qualifiesForPayload)
        {
            showIntrusionPayload = true;
            SetTransientFeedback("ENCRYPTED PAYLOAD RECOVERED");
        }

        config.Save();
    }

    private int GetIntrusionBestScore(IntrusionDifficulty difficulty)
        => difficulty switch
        {
            IntrusionDifficulty.Casual => config.IntrusionBestCasualScore,
            IntrusionDifficulty.BlackIce => config.IntrusionBestBlackIceScore,
            _ => config.IntrusionBestStandardScore,
        };

    private static string GetIntrusionDifficultyName(IntrusionDifficulty difficulty)
        => difficulty switch
        {
            IntrusionDifficulty.Casual => "CASUAL",
            IntrusionDifficulty.BlackIce => "BLACK ICE",
            _ => "STANDARD",
        };

    private static string GetIntrusionDifficultyDescription(IntrusionDifficulty difficulty)
        => difficulty switch
        {
            IntrusionDifficulty.Casual => "5x5 matrix // 8-slot buffer // trace timer disabled",
            IntrusionDifficulty.BlackIce => "8x8 matrix // 12-slot zero-error buffer // 4/4/5/5 daemons // 18-second trace",
            _ => "6x6 matrix // 8-slot buffer // 3/4/4 daemons // 45-second trace",
        };

    private static (string Label, Vector4 Color) GetIntrusionStatus(IntrusionGame game)
        => game.Phase switch
        {
            IntrusionPhase.Ready => ("AWAITING FIRST TOKEN", CyberdeckTheme.Palette.Cyan),
            IntrusionPhase.Countdown => ("NEURAL LINK // ARMING", CyberdeckTheme.Palette.Magenta),
            IntrusionPhase.Playing => ("TRACE ACTIVE", CyberdeckTheme.Palette.Amber),
            IntrusionPhase.Success => ("ROOT ACCESS // GRANTED", CyberdeckTheme.Palette.Success),
            IntrusionPhase.TimedOut => ("ICE TRACE COMPLETE", CyberdeckTheme.Palette.Error),
            _ => ("BUFFER LOCKED", CyberdeckTheme.Palette.Error),
        };

    private void DrawSettingsView()
    {
        var mapping = config.GetPrimaryMapping();
        var penumbraAvailable = isPenumbraAvailable();
        var collection = penumbraAvailable ? FindCollectionSafely(mapping.CollectionName) : null;
        var modDirectory = GetImportedModDirectory(mapping, penumbraAvailable);
        var updateStatus = getUpdateStatus();

        DrawSettingsGroupHeader("VENUE MOD");
        DrawUpdateOperationDetails(updateStatus, mapping.LastStatus);
        ImGui.Spacing();
        DrawUpdaterActions(updateStatus, modDirectory, penumbraAvailable);
        ImGui.Spacing();

        var automaticUpdates = config.FullAuto;
        ImGui.BeginDisabled(updateStatus.IsBusy);
        if (ImGui.Checkbox("Install updates automatically", ref automaticUpdates))
        {
            config.FullAuto = automaticUpdates;
            config.Save();
            if (automaticUpdates)
                queueReconcile();
        }
        ImGui.EndDisabled();
        DrawMutedWrapped("When disabled, updates are only installed after you press Update.");

        ImGui.Spacing();
        if (ImGui.SmallButton(showInstallationDetails ? "Hide Details" : "Show Details"))
            showInstallationDetails = !showInstallationDetails;
        if (showInstallationDetails)
        {
            ImGui.Spacing();
            DrawInstallationDetails(mapping, penumbraAvailable, modDirectory, collection);
        }

        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        DrawSettingsGroupHeader("SERVICES");
        var tarotHost = config.TarotHost;
        if (ImGui.Checkbox("Tarot Host", ref tarotHost))
        {
            config.TarotHost = tarotHost;
            config.Save();
        }
        DrawMutedWrapped("Enables the Arcana Cast reader console. Leave this off to request readings as a guest.");

        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        DrawSettingsGroupHeader("INTERFACE");
        DrawInterfaceSettings();
    }

    private void DrawInstallationDetails(
        ModMapping mapping,
        bool penumbraAvailable,
        string? modDirectory,
        (Guid Id, string Name)? collection)
    {
        ImGui.TextDisabled($"Penumbra: {(penumbraAvailable ? "Available" : "Unavailable")}");
        ImGui.TextDisabled($"Venue mod: {(modDirectory is null ? "Not installed" : "Installed")}");
        ImGui.TextDisabled(collection is not null
            ? $"Collection: {collection.Value.Name}"
            : "Collection: Managed automatically");

        if (!penumbraAvailable)
        {
            ImGui.Spacing();
            if (ImGui.SmallButton("Open Plugin Installer"))
                PluginService.Commands.ProcessCommand("/xlplugins");
            return;
        }

        if (modDirectory is not null)
            ImGui.TextDisabled(IsVenueMannequinInRange(mapping)
                ? "Venue mannequin: Detected"
                : "Venue mannequin: Activates automatically at The Grid");
    }

    private void DrawSystemHealthTopology(
        ModMapping mapping,
        bool penumbraAvailable,
        string? modDirectory,
        (Guid Id, string Name)? collection,
        UpdateUiSnapshot updateStatus)
    {
        bool? modEnabled = null;
        if (penumbraAvailable && modDirectory is not null && collection is not null)
        {
            try { modEnabled = penumbra.IsModEnabled(collection.Value.Id, modDirectory, mapping.ModName); }
            catch { modEnabled = false; }
        }

        var prerequisitesReady = penumbraAvailable && modDirectory is not null && collection is not null && modEnabled == true;
        var targetInRange = prerequisitesReady && IsVenueMannequinInRange(mapping);
        var cachedTarget = InstallStatusItems.FirstOrDefault(status => status.Label.Contains(mapping.NpcName));
        var targetState = !prerequisitesReady || !targetInRange
            ? DiagnosticNodeState.Inactive
            : !config.FullAuto
                ? DiagnosticNodeState.Attention
                : cachedTarget == default || cachedTarget.Ok is null
                    ? DiagnosticNodeState.Attention
                    : cachedTarget.Ok == true
                        ? DiagnosticNodeState.Healthy
                        : DiagnosticNodeState.Fault;
        var states = new[]
        {
            penumbraAvailable ? DiagnosticNodeState.Healthy : DiagnosticNodeState.Fault,
            !penumbraAvailable
                ? DiagnosticNodeState.Inactive
                : modDirectory is not null ? DiagnosticNodeState.Healthy : DiagnosticNodeState.Attention,
            !penumbraAvailable || modDirectory is null
                ? DiagnosticNodeState.Inactive
                : collection is null || modEnabled != true ? DiagnosticNodeState.Attention : DiagnosticNodeState.Healthy,
            targetState,
        };
        string[] labels = ["PENUMBRA", "MOD", "COLLECT", "TARGET"];
        DrawDiagnosticChain(labels, states);
        ImGui.Spacing();

        if (!penumbraAvailable)
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Error, "LINK 01 // PENUMBRA IPC OFFLINE");
            DrawMutedWrapped("Enable Penumbra before attempting package or collection operations.");
            if (ImGui.SmallButton("Open Penumbra"))
                PluginService.Commands.ProcessCommand("/penumbra");
            return;
        }

        if (modDirectory is null)
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "LINK 02 // VENUE MOD MISSING");
            DrawMutedWrapped("The managed venue package has not been detected.");
            if (CyberdeckWidgets.DrawActionButton("Install Now", updateStatus.IsBusy))
                queueReconcileForce();
            return;
        }

        if (collection is null)
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "LINK 03 // COLLECTION NOT FOUND");
            DrawMutedWrapped($"Create or restore the '{mapping.CollectionName}' collection in Penumbra.");
            if (ImGui.SmallButton("Open Penumbra"))
                PluginService.Commands.ProcessCommand("/penumbra");
            return;
        }

        if (modEnabled != true)
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "LINK 03 // MOD NOT BOUND");
            DrawMutedWrapped($"The venue mod is present but disabled in '{collection.Value.Name}'.");
            if (CyberdeckWidgets.DrawActionButton("Enable + Bind", updateStatus.IsBusy))
                assignAll();
            return;
        }

        if (!targetInRange)
        {
            ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, "LINK 04 // TARGET OUT OF RANGE");
            DrawMutedWrapped("Move within range of the venue mannequin to complete the final link.");
            return;
        }

        if (!config.FullAuto)
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "LINK 04 // TARGET READY");
            DrawMutedWrapped("The mannequin is detected and ready for collection assignment.");
            if (CyberdeckWidgets.DrawActionButton("Assign Target", updateStatus.IsBusy))
                assignAll();
            return;
        }

        if (targetState == DiagnosticNodeState.Healthy)
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Success, "ALL LINKS // OPERATIONAL");
            DrawMutedWrapped(!string.IsNullOrWhiteSpace(mapping.LastAppliedVersion)
                ? $"Venue package v{mapping.LastAppliedVersion} is installed, bound and assigned."
                : "Venue package is installed, bound and assigned.");
        }
        else
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "LINK 04 // AUTO-BIND PENDING");
            DrawMutedWrapped("Target detected; waiting for automatic assignment confirmation.");
        }
    }

    private void DrawDiagnosticChain(IReadOnlyList<string> labels, IReadOnlyList<DiagnosticNodeState> states)
    {
        var uiScale = GetUiScale();
        var width = MathF.Max(1, ImGui.GetContentRegionAvail().X);
        var segmentWidth = width / labels.Count;
        var origin = ImGui.GetCursorScreenPos();
        var centerY = origin.Y + (8 * uiScale);
        var nodeHalfSize = 5 * uiScale;
        var drawList = ImGui.GetWindowDrawList();

        for (var index = 0; index < labels.Count - 1; index++)
        {
            var from = new Vector2(origin.X + (segmentWidth * (index + 0.5f)) + nodeHalfSize, centerY);
            var to = new Vector2(origin.X + (segmentWidth * (index + 1.5f)) - nodeHalfSize, centerY);
            var connected = states[index] == DiagnosticNodeState.Healthy && states[index + 1] != DiagnosticNodeState.Inactive;
            drawList.AddLine(
                from,
                to,
                ImGui.GetColorU32(connected
                    ? CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Success, 0.68f)
                    : CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.48f)),
                MathF.Max(1, uiScale));
        }

        for (var index = 0; index < labels.Count; index++)
        {
            var state = states[index];
            var center = new Vector2(origin.X + (segmentWidth * (index + 0.5f)), centerY);
            var color = state switch
            {
                DiagnosticNodeState.Healthy => CyberdeckTheme.Palette.Success,
                DiagnosticNodeState.Attention => CyberdeckTheme.Palette.Amber,
                DiagnosticNodeState.Fault => CyberdeckTheme.Palette.Error,
                _ => CyberdeckTheme.Palette.TextMuted,
            };
            var alpha = state == DiagnosticNodeState.Inactive ? 0.42f : 1f;
            drawList.AddRectFilled(
                center - new Vector2(nodeHalfSize),
                center + new Vector2(nodeHalfSize),
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, alpha)),
                1.5f * uiScale);
            drawList.AddRect(
                center - new Vector2(nodeHalfSize + (2 * uiScale)),
                center + new Vector2(nodeHalfSize + (2 * uiScale)),
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, state == DiagnosticNodeState.Inactive ? 0.20f : 0.56f)),
                2 * uiScale);

            if (state is DiagnosticNodeState.Attention or DiagnosticNodeState.Fault && !config.ReduceMotion)
            {
                var pulse = 1 + ((0.5f + (0.5f * MathF.Sin((float)ImGui.GetTime() * 4.5f))) * 3);
                drawList.AddRect(
                    center - new Vector2(nodeHalfSize + (pulse * uiScale)),
                    center + new Vector2(nodeHalfSize + (pulse * uiScale)),
                    ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, 0.24f)),
                    2 * uiScale);
            }

            var labelSize = ImGui.CalcTextSize(labels[index]);
            drawList.AddText(
                new Vector2(center.X - (labelSize.X / 2), centerY + (12 * uiScale)),
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, state == DiagnosticNodeState.Inactive ? 0.50f : 0.90f)),
                labels[index]);
        }

        ImGui.Dummy(new Vector2(width, 34 * uiScale));
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

        ImGui.Spacing();
        DrawUpdatePipeline(status);
        ImGui.Spacing();

        if (status.Phase is UpdateOperationPhase.Error or UpdateOperationPhase.NeedsAttention &&
            status.FailureStage is { } failureStage)
        {
            ImGui.TextDisabled($"Affected step: {GetUpdateStageLabel(failureStage)}");
            ImGui.Spacing();
        }

        var detail = !string.IsNullOrWhiteSpace(status.Detail) && status.Detail != "No update operation is active."
            ? status.Detail
            : fallbackDetail;
        if (!string.IsNullOrWhiteSpace(detail))
            ImGui.TextWrapped(detail);

        var technicalError = GetDistinctTechnicalError(status, detail);
        if (technicalError is not null)
        {
            ImGui.Spacing();
            ImGui.TextColored(CyberdeckTheme.Palette.Error, "Technical details");
            ImGui.TextWrapped(technicalError);
        }

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

    private void DrawUpdatePipeline(UpdateUiSnapshot status)
    {
        string[] labels = ["CHECK", "DOWNLOAD", "INSTALL", "FINISH"];
        var uiScale = GetUiScale();
        var width = MathF.Max(1, ImGui.GetContentRegionAvail().X);
        var origin = ImGui.GetCursorScreenPos();
        var centerY = origin.Y + (7 * uiScale);
        var segmentWidth = width / labels.Length;
        var radius = 4.5f * uiScale;
        var drawList = ImGui.GetWindowDrawList();
        var states = labels
            .Select((_, index) => GetUpdatePipelineState(status, index))
            .ToArray();

        for (var index = 0; index < labels.Length - 1; index++)
        {
            var from = new Vector2(origin.X + (segmentWidth * (index + 0.5f)) + radius, centerY);
            var to = new Vector2(origin.X + (segmentWidth * (index + 1.5f)) - radius, centerY);
            var connected = states[index] == UpdatePipelineState.Complete;
            drawList.AddLine(
                from,
                to,
                ImGui.GetColorU32(connected
                    ? CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Success, 0.72f)
                    : CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.52f)),
                MathF.Max(1, uiScale));
        }

        for (var index = 0; index < labels.Length; index++)
        {
            var state = states[index];
            var center = new Vector2(origin.X + (segmentWidth * (index + 0.5f)), centerY);
            var color = state switch
            {
                UpdatePipelineState.Complete => CyberdeckTheme.Palette.Success,
                UpdatePipelineState.Active => GetUpdateStatusColor(status),
                UpdatePipelineState.Fault => status.Phase == UpdateOperationPhase.Error
                    ? CyberdeckTheme.Palette.Error
                    : CyberdeckTheme.Palette.Amber,
                _ => CyberdeckTheme.Palette.TextMuted,
            };
            var alpha = state == UpdatePipelineState.Pending ? 0.48f : 1f;
            drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, alpha)));
            drawList.AddCircle(
                center,
                radius + (2 * uiScale),
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, state == UpdatePipelineState.Pending ? 0.22f : 0.58f)),
                0,
                MathF.Max(1, uiScale));

            if (state == UpdatePipelineState.Active && !config.ReduceMotion)
            {
                var pulse = 2 + ((0.5f + (0.5f * MathF.Sin((float)ImGui.GetTime() * 5f))) * 3);
                drawList.AddCircle(
                    center,
                    radius + (pulse * uiScale),
                    ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, 0.28f)),
                    0,
                    MathF.Max(1, uiScale));
            }

            var labelSize = ImGui.CalcTextSize(labels[index]);
            drawList.AddText(
                new Vector2(center.X - (labelSize.X / 2), centerY + (10 * uiScale)),
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, state == UpdatePipelineState.Pending ? 0.58f : 0.92f)),
                labels[index]);
        }

        ImGui.Dummy(new Vector2(width, 30 * uiScale));
    }

    private static UpdatePipelineState GetUpdatePipelineState(UpdateUiSnapshot status, int stageIndex)
    {
        if (status.Phase == UpdateOperationPhase.Idle)
            return UpdatePipelineState.Pending;

        if (status.Phase == UpdateOperationPhase.Success)
        {
            var completedThrough = status.Operation == UpdateOperationKind.UpdateCheck ? 0 : 3;
            return stageIndex <= completedThrough ? UpdatePipelineState.Complete : UpdatePipelineState.Pending;
        }

        if (status.Phase is UpdateOperationPhase.Error or UpdateOperationPhase.NeedsAttention)
        {
            var faultStage = GetUpdatePipelineStage(status.FailureStage) ??
                             (status.Operation switch
                             {
                                 UpdateOperationKind.UpdateCheck => 0,
                                 UpdateOperationKind.Assignment => 3,
                                 _ when status.ReleaseAvailability == UpdateReleaseAvailability.Unknown => 0,
                                 _ when status.ReleaseAvailability == UpdateReleaseAvailability.UpToDate => 3,
                                 _ => 2,
                             });
            if (stageIndex < faultStage)
                return UpdatePipelineState.Complete;
            return stageIndex == faultStage ? UpdatePipelineState.Fault : UpdatePipelineState.Pending;
        }

        var activeStage = status.Phase switch
        {
            UpdateOperationPhase.Queued or UpdateOperationPhase.Checking => 0,
            UpdateOperationPhase.Downloading => 1,
            UpdateOperationPhase.Importing or UpdateOperationPhase.WaitingForPenumbra => 2,
            UpdateOperationPhase.Configuring or UpdateOperationPhase.Assigning => 3,
            _ => 0,
        };
        if (stageIndex < activeStage)
            return UpdatePipelineState.Complete;
        return stageIndex == activeStage ? UpdatePipelineState.Active : UpdatePipelineState.Pending;
    }

    private static int? GetUpdatePipelineStage(UpdateOperationPhase? phase)
        => phase switch
        {
            UpdateOperationPhase.Queued or UpdateOperationPhase.Checking => 0,
            UpdateOperationPhase.Downloading => 1,
            UpdateOperationPhase.Importing or UpdateOperationPhase.WaitingForPenumbra => 2,
            UpdateOperationPhase.Configuring or UpdateOperationPhase.Assigning => 3,
            _ => null,
        };

    private void DrawUpdaterActions(UpdateUiSnapshot status, string? modDirectory, bool penumbraAvailable)
    {
        var availableVersion = status.AvailableVersion;
        var needsInstall = modDirectory is null;
        var primaryLabel = needsInstall
            ? "Install"
            : status.Operation == UpdateOperationKind.Assignment &&
              status.Phase is UpdateOperationPhase.Error or UpdateOperationPhase.NeedsAttention
                ? "Finish Setup"
                : availableVersion is not null
                    ? $"Update to v{availableVersion}"
                    : status.Phase == UpdateOperationPhase.Error
                        ? "Try Again"
                        : "Check for Updates";
        var actionsDisabled = status.IsBusy || !penumbraAvailable;
        var availableWidth = ImGui.GetContentRegionAvail().X;

        using (CyberdeckTheme.PushAccentButton())
        {
            if (CyberdeckWidgets.DrawActionButton(primaryLabel, actionsDisabled, new Vector2(availableWidth, 0)))
            {
                if (status.Operation == UpdateOperationKind.Assignment &&
                    status.Phase is UpdateOperationPhase.Error or UpdateOperationPhase.NeedsAttention)
                    assignAll();
                else if (status.Operation == UpdateOperationKind.Repair && status.Phase == UpdateOperationPhase.Error)
                    queueReconcileForce();
                else if (!needsInstall &&
                         availableVersion is null &&
                         status.Phase != UpdateOperationPhase.Error)
                    checkForUpdates();
                else
                    queueReconcile();
            }
        }

        ImGui.Spacing();
        if (CyberdeckWidgets.DrawActionButton(
                "Repair Installation...",
                actionsDisabled,
                new Vector2(ImGui.GetContentRegionAvail().X, 0)))
            ImGui.OpenPopup("confirm_reinstall");

        if (ImGui.BeginPopup("confirm_reinstall"))
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "Repair installation");
            ImGui.TextWrapped("This downloads and installs the latest venue mod again. Use it when files are missing or the mod is not working correctly.");
            ImGui.Spacing();
            var popupSpacing = ImGui.GetStyle().ItemSpacing.X;
            var popupButtonWidth = MathF.Max(100, (ImGui.GetContentRegionAvail().X - popupSpacing) / 2);
            using (CyberdeckTheme.PushAccentButton())
            {
                if (CyberdeckWidgets.DrawActionButton("Repair", status.IsBusy, new Vector2(popupButtonWidth, 0)))
                {
                    queueReconcileForce();
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(popupButtonWidth, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

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
        ImGui.TextUnformatted("Theme");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo("##cyberdeck_theme", CyberdeckTheme.GetThemeName(config.Theme)))
        {
            foreach (var theme in ThemeOptions)
            {
                var selected = config.Theme == theme;
                if (ImGui.Selectable(CyberdeckTheme.GetThemeName(theme), selected))
                {
                    if (theme == CyberdeckThemeId.Custom && config.Theme != CyberdeckThemeId.Custom)
                        customThemeSource = config.Theme;
                    config.Theme = theme;
                    ApplyConfiguredTheme();
                    config.Save();
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        DrawThemePreview(config.Theme);
        DrawMutedWrapped(CyberdeckTheme.GetThemeDescription(config.Theme));

        if (config.Theme == CyberdeckThemeId.Custom)
            DrawCustomThemeEditor();

        ImGui.Spacing();
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

        if (IsVenueManager())
        {
            ImGui.Spacing();
            var networkAlert = config.NetworkAlertBadge;
            if (ImGui.Checkbox("Network alert badge", ref networkAlert))
            {
                config.NetworkAlertBadge = networkAlert;
                config.Save();
            }
            DrawHoverTooltip("Show the number of visible guests with drawn weapons on the Network tile");
        }

        var animationsEnabled = !config.ReduceMotion;
        if (ImGui.Checkbox("Animations & ambient FX", ref animationsEnabled))
        {
            config.ReduceMotion = !animationsEnabled;
            config.Save();
        }
        DrawMutedWrapped("Controls operations-feed scrolling, terminal glitches, pulses, scanlines, and loading animation together.");

        var messageTone = config.MessageToneEnabled;
        if (ImGui.Checkbox("Message tone", ref messageTone))
        {
            config.MessageToneEnabled = messageTone;
            config.Save();
            if (messageTone)
                VenueSounds.PlayMessageTone();
        }
        DrawMutedWrapped("Plays a chime when a tell arrives from the venue. Ticking it plays the tone once.");

        var autoOpenOnEntrance = config.AutoOpenOnVenueAddress;
        if (ImGui.Checkbox("Auto-open Cyberdeck", ref autoOpenOnEntrance))
        {
            config.AutoOpenOnVenueAddress = autoOpenOnEntrance;
            config.Save();
            autoOpenChanged(autoOpenOnEntrance);
        }
        DrawMutedWrapped("Opens automatically when you enter the venue address.");

        DrawBroadcastSettings();
    }

    private static void DrawThemePreview(CyberdeckThemeId theme)
    {
        var preview = theme == CyberdeckThemeId.Custom
            ? (
                Background: CyberdeckTheme.Palette.Background,
                Primary: CyberdeckTheme.Palette.Cyan,
                Secondary: CyberdeckTheme.Palette.Magenta,
                Text: CyberdeckTheme.Palette.Text)
            : CyberdeckTheme.GetThemePreview(theme);
        ReadOnlySpan<Vector4> colors =
        [
            preview.Background,
            preview.Primary,
            preview.Secondary,
            preview.Text,
        ];
        var start = ImGui.GetCursorScreenPos();
        var spacing = MathF.Max(2, ImGui.GetStyle().ItemInnerSpacing.X * 0.5f);
        var width = MathF.Max(24, (ImGui.GetContentRegionAvail().X - (spacing * (colors.Length - 1))) / colors.Length);
        var height = MathF.Max(7, ImGui.GetFrameHeight() * 0.28f);
        var drawList = ImGui.GetWindowDrawList();
        for (var index = 0; index < colors.Length; index++)
        {
            var min = start + new Vector2(index * (width + spacing), 0);
            drawList.AddRectFilled(min, min + new Vector2(width, height), ImGui.GetColorU32(colors[index]), 1);
        }
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, height));
    }

    private void DrawCustomThemeEditor()
    {
        ImGui.Spacing();
        var background = config.CustomThemeBackground;
        var primary = config.CustomThemePrimary;
        var secondary = config.CustomThemeSecondary;
        var text = config.CustomThemeText;
        var changed = false;
        changed |= DrawCustomThemeColor("Background", ref background);
        changed |= DrawCustomThemeColor("Primary", ref primary);
        changed |= DrawCustomThemeColor("Secondary", ref secondary);
        changed |= DrawCustomThemeColor("Text", ref text);

        if (changed)
        {
            config.CustomThemeBackground = background;
            config.CustomThemePrimary = primary;
            config.CustomThemeSecondary = secondary;
            config.CustomThemeText = text;
            ApplyConfiguredTheme();
            config.Save();
        }

        ImGui.Spacing();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(100, (ImGui.GetContentRegionAvail().X - spacing) / 2);
        var source = customThemeSource == CyberdeckThemeId.Custom
            ? CyberdeckThemeId.Grid
            : customThemeSource;
        if (ImGui.Button($"Start from {CyberdeckTheme.GetThemeName(source)}##custom_theme_copy", new Vector2(width, 0)))
            CopyPresetToCustom(source);
        ImGui.SameLine();
        if (ImGui.Button("Reset##custom_theme_reset", new Vector2(width, 0)))
            CopyPresetToCustom(CyberdeckThemeId.Grid);
    }

    private static bool DrawCustomThemeColor(string label, ref Vector4 value)
    {
        var rgb = new Vector3(value.X, value.Y, value.Z);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (!ImGui.ColorEdit3(label, ref rgb, ImGuiColorEditFlags.NoInputs))
            return false;

        value = new Vector4(rgb, 1f);
        return true;
    }

    private void CopyPresetToCustom(CyberdeckThemeId source)
    {
        var preview = CyberdeckTheme.GetThemePreview(source);
        config.CustomThemeBackground = preview.Background;
        config.CustomThemePrimary = preview.Primary;
        config.CustomThemeSecondary = preview.Secondary;
        config.CustomThemeText = preview.Text;
        ApplyConfiguredTheme();
        config.Save();
    }

    private void ApplyConfiguredTheme()
    {
        if (config.Theme == CyberdeckThemeId.Custom)
        {
            CyberdeckTheme.SetCustomTheme(
                config.CustomThemeBackground,
                config.CustomThemePrimary,
                config.CustomThemeSecondary,
                config.CustomThemeText);
            return;
        }

        CyberdeckTheme.SetTheme(config.Theme);
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
        transientFeedbackUntil = Environment.TickCount64 + 2800;
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

        if (selectedView == DeckView.Home)
            return;

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

        if (config.NetworkAlertBadge && IsVenueManager())
        {
            try
            {
                var flagged = NetworkGuestScanner.Capture().Count(guest => guest.HasWeapon);

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
        News,
        Network,
        Services,
        Settings,
    }

    private enum UpdatePipelineState
    {
        Pending,
        Active,
        Complete,
        Fault,
    }

    private enum DiagnosticNodeState
    {
        Inactive,
        Healthy,
        Attention,
        Fault,
    }

    private sealed record DrinkMenuItem(string Name, string Price, string ImageName, string Ingredients, string Description, string Taste);
}
