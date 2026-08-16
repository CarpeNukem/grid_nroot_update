using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Textures.TextureWraps;

namespace GridNrootUpdate;

internal sealed partial class CyberdeckWindow
{
    private const float TarotBackGlitchDurationMs = 3000f;
    private const float TarotFlipDurationMs = 850f;
    private const float TarotSettleDurationMs = 650f;
    private const long TarotTellIntervalMs = 1750;
    private const long TarotSessionIdleTimeoutMs = 15 * 60 * 1000;

    private sealed record TarotQueuedTell(
        string SessionId,
        string Recipient,
        string Message,
        string Packet,
        string SuccessMessage,
        bool ShowRevealedCard);

    private readonly TarotDebugSession tarotDebugSession = new();
    private readonly Queue<TarotQueuedTell> tarotTellQueue = new();
    private long nextTarotTellAt;
    private long tarotLastActivityAt;
    private bool tarotDebugWindowOpen;
    private bool tarotCardViewerOpen;
    private bool focusTarotCardViewer;
    private bool tarotDebugLoopback;
    private bool focusTarotDebugWindow;
    private string tarotDebugPartnerInput = string.Empty;
    private string tarotDebugIncomingSender = "Debug Operator@Local";
    private string tarotDebugIncomingPacket = string.Empty;
    private string tarotDebugFeedback = "DEBUG TRANSPORT IDLE";
    private string tarotDebugLastSentPacket = string.Empty;
    private int tarotDebugSimulatedPeerSequence = 1;
    private readonly Dictionary<int, long> tarotDebugCardPlacedAt = [];
    private readonly Dictionary<int, long> tarotDebugCardRevealedAt = [];
    private int tarotDebugCustomerFocusedSlot = -1;
    private bool tarotAiWindowOpen;
    private bool focusTarotAiWindow;
    private TarotReadingCard[] tarotAiCards = [];
    private bool tarotYesNoWindowOpen;
    private bool focusTarotYesNoWindow;
    private TarotReadingCard tarotYesNoCard = new(1100, null, false, false);
    private IReadOnlyDictionary<string, TarotYesNoEntry> tarotYesNoEntries =
        new Dictionary<string, TarotYesNoEntry>(StringComparer.OrdinalIgnoreCase);
    private string tarotYesNoSourcePath = string.Empty;
    private string? tarotYesNoLoadError;

    private static readonly string[] TarotAiPositions = ["PAST", "PRESENT", "FUTURE"];

    public void OpenTarotDebug()
    {
        if (PluginService.Targets.Target is IPlayerCharacter player)
            tarotDebugPartnerInput = GetPlayerTellName(player);

        tarotDebugWindowOpen = true;
        focusTarotDebugWindow = true;
    }

    private void OpenTarotAiReading()
    {
        GenerateTarotAiReading();
        tarotAiWindowOpen = true;
        focusTarotAiWindow = true;
    }

    private void GenerateTarotAiReading()
    {
        const int aiSlotBase = 1000;
        foreach (var slot in tarotDebugCardPlacedAt.Keys.Where(slot => slot >= aiSlotBase).ToArray())
            tarotDebugCardPlacedAt.Remove(slot);
        foreach (var slot in tarotDebugCardRevealedAt.Keys.Where(slot => slot >= aiSlotBase).ToArray())
            tarotDebugCardRevealedAt.Remove(slot);

        var availableCards = Enumerable.Range(0, TarotDeck.CardCount).ToArray();
        var now = Environment.TickCount64;
        tarotAiCards = new TarotReadingCard[3];
        for (var index = 0; index < tarotAiCards.Length; index++)
        {
            var selected = Random.Shared.Next(index, availableCards.Length);
            (availableCards[index], availableCards[selected]) = (availableCards[selected], availableCards[index]);

            var slot = aiSlotBase + index;
            tarotAiCards[index] = new TarotReadingCard(
                slot,
                availableCards[index],
                Random.Shared.Next(2) == 1,
                true);
            tarotDebugCardPlacedAt[slot] = now + (index * 350);
            tarotDebugCardRevealedAt[slot] = now + (index * 700);
        }
    }

    private void DrawTarotAiWindow()
    {
        var uiScale = GetUiScale();
        using var fontScale = CyberdeckTheme.PushFontScale(uiScale);
        using var theme = CyberdeckTheme.Push(uiScale);
        ImGui.SetNextWindowSize(new Vector2(780, 650) * uiScale, ImGuiCond.FirstUseEver);
        var (minimumSize, maximumSize) = CyberdeckTheme.ResolveWindowConstraints(
            uiScale,
            new Vector2(620, 560),
            new Vector2(1100, 900));
        ImGui.SetNextWindowSizeConstraints(minimumSize, maximumSize);
        if (focusTarotAiWindow)
        {
            ImGui.SetNextWindowFocus();
            focusTarotAiWindow = false;
        }

        if (!ImGui.Begin("SELF-GUIDED TAROT###grid_tarot_ai", ref tarotAiWindowOpen))
        {
            ImGui.End();
            return;
        }

        if (tarotAiCards.Length != 3)
            GenerateTarotAiReading();

        ImGui.TextColored(CyberdeckTheme.Palette.Magenta, "THREE-CARD SPREAD");
        DrawMutedWrapped("A local randomized Past / Present / Future reading.");
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        if (ImGui.BeginTable(
                "tarot_ai_spread",
                3,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            for (var index = 0; index < tarotAiCards.Length; index++)
                ImGui.TableSetupColumn(TarotAiPositions[index], ImGuiTableColumnFlags.WidthStretch);

            ImGui.TableNextRow();
            for (var index = 0; index < tarotAiCards.Length; index++)
            {
                ImGui.TableSetColumnIndex(index);
                var card = tarotAiCards[index];
                var cardIndex = card.CardIndex ?? -1;

                DrawCenteredTarotAiText(TarotAiPositions[index], CyberdeckTheme.Palette.Cyan);
                ImGui.Spacing();
                DrawTarotCardVisual(
                    card,
                    new Vector2(ImGui.GetContentRegionAvail().X, 320 * uiScale),
                    156 * uiScale);
                ImGui.Spacing();

                DrawCenteredTarotAiText(TarotDeck.CardName(cardIndex).ToUpperInvariant(), CyberdeckTheme.Palette.Text);
                DrawCenteredTarotAiText(
                    card.Reversed ? "R ↓  REVERSED" : "U ↑  UPRIGHT",
                    card.Reversed ? CyberdeckTheme.Palette.Error : CyberdeckTheme.Palette.Success);
                ImGui.Spacing();
                ImGui.TextWrapped(TarotDeck.CardMeaning(cardIndex, card.Reversed));
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();
        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("DRAW AGAIN", new Vector2(ImGui.GetContentRegionAvail().X, 38 * uiScale)))
                GenerateTarotAiReading();
        }

        ImGui.End();

        static void DrawCenteredTarotAiText(string text, Vector4 color)
        {
            var availableWidth = ImGui.GetContentRegionAvail().X;
            var displayed = EllipsizeToWidth(text, availableWidth);
            var textWidth = ImGui.CalcTextSize(displayed).X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (availableWidth - textWidth) * 0.5f));
            ImGui.TextColored(color, displayed);
        }
    }

    private void OpenTarotYesNoReading()
    {
        ReloadTarotYesNoCatalog();
        ResetTarotYesNoReading();
        tarotYesNoWindowOpen = true;
        focusTarotYesNoWindow = true;
    }

    private void ReloadTarotYesNoCatalog()
    {
        tarotYesNoEntries = TarotYesNoCatalog.Load(
            textureLoadSource,
            out tarotYesNoSourcePath,
            out tarotYesNoLoadError);
    }

    private void ResetTarotYesNoReading()
    {
        const int slot = 1100;
        tarotYesNoCard = new TarotReadingCard(slot, null, false, false);
        tarotDebugCardPlacedAt.Remove(slot);
        tarotDebugCardRevealedAt.Remove(slot);
    }

    private void GenerateTarotYesNoReading()
    {
        const int slot = 1100;
        var now = Environment.TickCount64;
        tarotYesNoCard = new TarotReadingCard(
            slot,
            Random.Shared.Next(TarotDeck.CardCount),
            Random.Shared.Next(2) == 1,
            true);
        tarotDebugCardPlacedAt[slot] = now;
        tarotDebugCardRevealedAt[slot] = now;
    }

    private void DrawTarotYesNoWindow()
    {
        var uiScale = GetUiScale();
        using var fontScale = CyberdeckTheme.PushFontScale(uiScale);
        using var theme = CyberdeckTheme.Push(uiScale);
        ImGui.SetNextWindowSize(new Vector2(380, 720) * uiScale, ImGuiCond.FirstUseEver);
        var (minimumSize, maximumSize) = CyberdeckTheme.ResolveWindowConstraints(
            uiScale,
            new Vector2(330, 580),
            new Vector2(520, 860));
        ImGui.SetNextWindowSizeConstraints(minimumSize, maximumSize);
        if (focusTarotYesNoWindow)
        {
            ImGui.SetNextWindowFocus();
            focusTarotYesNoWindow = false;
        }

        if (!ImGui.Begin("YES / NO TAROT###grid_tarot_yes_no", ref tarotYesNoWindowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(CyberdeckTheme.Palette.Magenta, "YES / NO READING");
        DrawMutedWrapped("Focus on a yes-or-no question, then draw one card.");
        if (!string.IsNullOrWhiteSpace(tarotYesNoLoadError))
        {
            ImGui.Spacing();
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, tarotYesNoLoadError);
        }
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        DrawTarotCardVisual(
            tarotYesNoCard,
            new Vector2(ImGui.GetContentRegionAvail().X, 460 * uiScale),
            230 * uiScale);
        ImGui.Spacing();

        var hasCard = tarotYesNoCard.CardIndex is int;
        var answerVisible = hasCard &&
                            (config.ReduceMotion ||
                             !tarotDebugCardRevealedAt.TryGetValue(tarotYesNoCard.Slot, out var revealedAt) ||
                             Environment.TickCount64 - revealedAt >= 500);
        if (answerVisible && tarotYesNoCard.CardIndex is int cardIndex)
        {
            var outcome = TarotYesNoCatalog.Resolve(tarotYesNoEntries, cardIndex, tarotYesNoCard.Reversed);
            var outcomeColor = outcome switch
            {
                TarotYesNoOutcome.StrongYes or TarotYesNoOutcome.Yes => CyberdeckTheme.Palette.Success,
                TarotYesNoOutcome.No or TarotYesNoOutcome.StrongNo => CyberdeckTheme.Palette.Error,
                _ => CyberdeckTheme.Palette.Amber,
            };
            DrawCenteredYesNoText(
                TarotYesNoCatalog.Label(outcome),
                outcomeColor);
            DrawCenteredYesNoText(TarotDeck.CardName(cardIndex).ToUpperInvariant(), CyberdeckTheme.Palette.Text);
            DrawCenteredYesNoText(
                tarotYesNoCard.Reversed ? "R ↓  REVERSED" : "U ↑  UPRIGHT",
                tarotYesNoCard.Reversed ? CyberdeckTheme.Palette.Error : CyberdeckTheme.Palette.Success);
            ImGui.Spacing();
            ImGui.TextWrapped(TarotDeck.CardMeaning(cardIndex, tarotYesNoCard.Reversed));
        }
        else if (hasCard)
        {
            DrawCenteredYesNoText("DRAWING…", CyberdeckTheme.Palette.Amber);
        }
        else
        {
            DrawCenteredYesNoText("QUESTION HELD // CARD READY", CyberdeckTheme.Palette.TextMuted);
        }

        ImGui.Spacing();
        using (CyberdeckTheme.PushAccentButton())
        {
            if (!hasCard)
            {
                if (ImGui.Button("DRAW CARD", new Vector2(ImGui.GetContentRegionAvail().X, 38 * uiScale)))
                    GenerateTarotYesNoReading();
            }
            else if (answerVisible &&
                     ImGui.Button("ASK ANOTHER QUESTION", new Vector2(ImGui.GetContentRegionAvail().X, 38 * uiScale)))
            {
                ResetTarotYesNoReading();
            }
        }

        ImGui.Spacing();
        DrawMutedWrapped("The answer follows the card's traditional Yes / No / Maybe tendency. Reversal modifies that tendency rather than simply flipping it.");
        DrawHoverTooltip(tarotYesNoSourcePath);
        ImGui.End();

        static void DrawCenteredYesNoText(string text, Vector4 color)
        {
            var availableWidth = ImGui.GetContentRegionAvail().X;
            var displayed = EllipsizeToWidth(text, availableWidth);
            var textWidth = ImGui.CalcTextSize(displayed).X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (availableWidth - textWidth) * 0.5f));
            ImGui.TextColored(color, displayed);
        }
    }

    public void StartTarotCustomerLoopback()
    {
        tarotTellQueue.Clear();
        tarotDebugSession.BeginCustomerWaiting();
        tarotDebugLoopback = true;
        tarotDebugLastSentPacket = string.Empty;
        tarotDebugSimulatedPeerSequence = 1;
        tarotDebugCardPlacedAt.Clear();
        tarotDebugCardRevealedAt.Clear();
        tarotDebugCustomerFocusedSlot = -1;
        tarotCardViewerOpen = false;
        tarotDebugWindowOpen = false;
        TouchTarotSession();

        const string sender = "Debug Reader@Raiden";
        var invitation = new TarotPacket(TarotPacketKind.Invite, TarotDeck.CreateSessionId(), 1, []);
        ReceiveTarotPacket(sender, invitation);
        tarotDebugFeedback = "LOCAL LOOPBACK // INCOMING INVITATION READY";
    }

    public void StartTarotHostLoopback()
    {
        const string partner = "Debug Customer@Raiden";
        tarotTellQueue.Clear();
        tarotDebugLoopback = true;
        tarotDebugLastSentPacket = string.Empty;
        tarotDebugSimulatedPeerSequence = 1;
        tarotDebugCardPlacedAt.Clear();
        tarotDebugCardRevealedAt.Clear();
        tarotDebugCustomerFocusedSlot = -1;
        tarotCardViewerOpen = false;

        if (!tarotDebugSession.BeginHost(partner, out var error))
        {
            tarotDebugFeedback = $"LOOPBACK REJECTED // {error}";
            return;
        }

        tarotDebugLastSentPacket = tarotDebugSession.LastOutboundPacket;
        ReceiveTarotPacket(partner, new TarotPacket(TarotPacketKind.Join, tarotDebugSession.SessionId, 1, []));
        tarotDebugWindowOpen = true;
        focusTarotDebugWindow = true;
        tarotDebugFeedback = "LOCAL LOOPBACK // READER AND CUSTOMER LINKED";
        TouchTarotSession();
    }

    public string AdvanceTarotCustomerLoopback()
    {
        if (!tarotDebugLoopback || tarotDebugSession.Role != TarotDebugRole.Customer)
            return "Start with /grid debug tarot-invite.";
        if (tarotDebugSession.Phase == TarotDebugPhase.InviteReceived)
            return "Click YES or NO in the Cyberdeck first.";
        if (tarotDebugSession.Phase is not (TarotDebugPhase.Ready or TarotDebugPhase.Reading))
            return "The local Tarot session is not receiving.";

        tarotDebugSimulatedPeerSequence++;
        TarotPacket packet;
        packet = new TarotPacket(
            TarotPacketKind.Reveal,
            tarotDebugSession.SessionId,
            tarotDebugSimulatedPeerSequence,
            [
                Random.Shared.Next(TarotDeck.MajorArcanaCount).ToString(),
                Random.Shared.Next(2) == 0 ? "U" : "R",
            ]);

        ReceiveTarotPacket(tarotDebugSession.Partner, packet);
        return "Random Major Arcana reveal delivered locally.";
    }

    public void ResetTarotLoopback()
    {
        tarotTellQueue.Clear();
        tarotDebugSession.Reset();
        tarotDebugLoopback = false;
        tarotDebugWindowOpen = false;
        tarotCardViewerOpen = false;
        tarotDebugLastSentPacket = string.Empty;
        tarotDebugCardPlacedAt.Clear();
        tarotDebugCardRevealedAt.Clear();
        tarotDebugCustomerFocusedSlot = -1;
        tarotDebugFeedback = "DEBUG SESSION RESET";
        tarotLastActivityAt = 0;
    }

    public bool TryReceiveTarotTell(string sender, string text)
    {
        if (!text.Contains(TarotPacket.Marker, StringComparison.Ordinal))
            return false;

        tarotDebugIncomingSender = sender;
        tarotDebugIncomingPacket = text;
        if (!TarotPacket.TryParse(text, out var packet, out var error) || packet is null)
        {
            tarotDebugFeedback = $"RX REJECTED // {error}";
            tarotDebugWindowOpen = true;
            focusTarotDebugWindow = true;
            return true;
        }

        if (packet.Kind == TarotPacketKind.Invite)
            tarotDebugLoopback = false;
        ReceiveTarotPacket(sender, packet);
        return true;
    }

    private void ReceiveTarotPacket(string sender, TarotPacket packet)
    {
        var result = tarotDebugSession.Receive(sender, packet);
        tarotDebugFeedback = result.Accepted
            ? $"RX ACCEPTED // {result.Message}"
            : $"RX REJECTED // {result.Message}";
        if (!result.Accepted)
        {
            PluginService.Log.Warning(
                "Rejected GRID-TAROT packet {Kind} for session {Session} from {Sender}: {Reason}",
                packet.Kind,
                packet.SessionId,
                sender,
                result.Message);
        }
        if (result.Accepted)
        {
            if (packet.Kind is TarotPacketKind.Invite or TarotPacketKind.Join or TarotPacketKind.Refuse)
                tarotTellQueue.Clear();
            tarotDebugSimulatedPeerSequence = Math.Max(tarotDebugSimulatedPeerSequence, packet.Sequence);
            TrackTarotCardEffect(packet);
            TouchTarotSession();

            // Every new reveal is a fresh visual event. Reopen and focus the
            // receiver even if the customer dismissed the previous card.
            if (packet.Kind == TarotPacketKind.Reveal && tarotDebugSession.Role == TarotDebugRole.Customer)
            {
                tarotCardViewerOpen = true;
                focusTarotCardViewer = true;
            }
        }
        if (result.OpenWindow)
        {
            if (tarotDebugSession.Role == TarotDebugRole.Customer &&
                tarotDebugSession.Phase == TarotDebugPhase.InviteReceived)
            {
                IsOpen = true;
                tarotDebugWindowOpen = false;
            }
            else if (tarotDebugSession.Role == TarotDebugRole.Customer &&
                     tarotDebugSession.Phase is TarotDebugPhase.Ready or TarotDebugPhase.Reading)
            {
                tarotCardViewerOpen = true;
                focusTarotCardViewer = true;
            }
            else
            {
                tarotDebugWindowOpen = true;
                focusTarotDebugWindow = true;
            }
        }
    }

    private void TrackTarotCardEffect(TarotPacket packet)
    {
        switch (packet.Kind)
        {
            case TarotPacketKind.Invite:
                tarotDebugCardPlacedAt.Clear();
                tarotDebugCardRevealedAt.Clear();
                tarotDebugCustomerFocusedSlot = -1;
                break;
            case TarotPacketKind.Reveal:
                tarotDebugCardPlacedAt.Clear();
                tarotDebugCardRevealedAt.Clear();
                tarotDebugCardRevealedAt[0] = Environment.TickCount64;
                tarotDebugCustomerFocusedSlot = 0;
                break;
        }
    }

    private void UpdateTarotTellQueue()
    {
        if (tarotTellQueue.Count == 0 || Environment.TickCount64 < nextTarotTellAt)
        {
            UpdateTarotSessionTimeout();
            return;
        }

        var queued = tarotTellQueue.Dequeue();
        if (!string.Equals(queued.SessionId, tarotDebugSession.SessionId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!PluginService.ClientState.IsLoggedIn)
        {
            tarotDebugFeedback = "MESSAGE NOT SENT // Log in before sending messages.";
            return;
        }

        if (!TarotTellSender.TrySend(queued.Recipient, queued.Message, queued.Packet, out var error))
        {
            tarotDebugFeedback = string.IsNullOrWhiteSpace(error)
                ? "MESSAGE NOT SENT"
                : $"MESSAGE NOT SENT // {error}";
            nextTarotTellAt = Environment.TickCount64 + TarotTellIntervalMs;
            return;
        }

        tarotDebugLastSentPacket = queued.Packet;
        tarotDebugFeedback = queued.SuccessMessage;
        nextTarotTellAt = Environment.TickCount64 + TarotTellIntervalMs;
        TouchTarotSession();
        if (queued.ShowRevealedCard)
            ShowLatestTarotRevealLocally(queued.Packet);
    }

    private void TouchTarotSession()
        => tarotLastActivityAt = Environment.TickCount64;

    private void UpdateTarotSessionTimeout()
    {
        if (tarotLastActivityAt <= 0 ||
            tarotDebugSession.Role == TarotDebugRole.None ||
            tarotDebugSession.Phase is TarotDebugPhase.Idle or TarotDebugPhase.Ended ||
            Environment.TickCount64 - tarotLastActivityAt < TarotSessionIdleTimeoutMs)
        {
            return;
        }

        tarotTellQueue.Clear();
        tarotDebugSession.EndSession();
        tarotCardViewerOpen = false;
        tarotDebugFeedback = "Reading closed after 15 minutes of inactivity.";
        tarotLastActivityAt = 0;
    }

    private void DrawTarotDebugWindow()
    {
        var uiScale = GetUiScale();
        using var fontScale = CyberdeckTheme.PushFontScale(uiScale);
        using var theme = CyberdeckTheme.Push(uiScale);
        ImGui.SetNextWindowSize(new Vector2(760, 820) * uiScale, ImGuiCond.FirstUseEver);
        var (minimumSize, maximumSize) = CyberdeckTheme.ResolveWindowConstraints(
            uiScale,
            new Vector2(540, 560),
            new Vector2(1040, 1120));
        ImGui.SetNextWindowSizeConstraints(minimumSize, maximumSize);
        if (focusTarotDebugWindow)
        {
            ImGui.SetNextWindowFocus();
            focusTarotDebugWindow = false;
        }

        var windowTitle = tarotDebugSession.Role == TarotDebugRole.Customer
            ? "TAROT LINK // ARCANA RECEIVER###grid_tarot_debug"
            : "TAROT READING // READER###grid_tarot_debug";
        if (!ImGui.Begin(windowTitle, ref tarotDebugWindowOpen, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.End();
            return;
        }

        if (ImGui.BeginChild("tarot_debug_body", Vector2.Zero, true))
        {
            DrawTarotDebugHeader();
            DrawNeonSeparator();
            ImGui.Spacing();

            if (tarotDebugSession.Role == TarotDebugRole.None || tarotDebugSession.Phase == TarotDebugPhase.Idle)
                DrawTarotDebugRoleSelection();
            else
                DrawTarotDebugActiveSession();
        }
        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawTarotCardViewerWindow()
    {
        var uiScale = GetUiScale();
        using var fontScale = CyberdeckTheme.PushFontScale(uiScale);
        using var theme = CyberdeckTheme.Push(uiScale);
        ImGui.SetNextWindowSize(new Vector2(300, 610) * uiScale, ImGuiCond.FirstUseEver);
        var fixedSize = new Vector2(286, 590) * uiScale;
        ImGui.SetNextWindowSizeConstraints(fixedSize, new Vector2(360, 680) * uiScale);
        if (focusTarotCardViewer)
        {
            ImGui.SetNextWindowFocus();
            focusTarotCardViewer = false;
        }

        var title = tarotDebugSession.Role == TarotDebugRole.Host
            ? "ARCANA CAST // LOCAL PREVIEW###grid_tarot_card_viewer"
            : "ARCANA CAST // RECEIVING###grid_tarot_card_viewer";
        if (!ImGui.Begin(title, ref tarotCardViewerOpen, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.End();
            return;
        }

        TarotReadingCard card;
        if (tarotDebugSession.Cards.Count == 0)
        {
            card = new TarotReadingCard(-1, null, false, false);
        }
        else
        {
            var slot = tarotDebugCustomerFocusedSlot < 0 || tarotDebugCustomerFocusedSlot >= tarotDebugSession.Cards.Count
                ? tarotDebugSession.Cards.Count - 1
                : tarotDebugCustomerFocusedSlot;
            card = tarotDebugSession.Cards[slot];
        }

        DrawTarotStreamCard(card);
        ImGui.Spacing();
        var frontVisible = IsTarotFrontVisible(card);
        if (frontVisible && card.CardIndex is int cardIndex)
        {
            var label = $"{TarotDeck.CardName(cardIndex).ToUpperInvariant()} // {(card.Reversed ? "REVERSED" : "UPRIGHT")}";
            DrawCenteredTarotViewerText(label, card.Reversed ? CyberdeckTheme.Palette.Magenta : CyberdeckTheme.Palette.Cyan);
        }
        else if (card.Revealed)
        {
            DrawCenteredTarotViewerText("DECRYPTING ARCANA FRAME", CyberdeckTheme.Palette.Amber);
        }
        else
        {
            DrawCenteredTarotViewerText("AWAITING ARCANA FRAME", CyberdeckTheme.Palette.TextMuted);
        }

        ImGui.End();
    }

    private void DrawTarotStreamCard(TarotReadingCard card)
    {
        var uiScale = GetUiScale();
        var frameSize = new Vector2(256, 512) * uiScale;
        var canvasSize = new Vector2(ImGui.GetContentRegionAvail().X, frameSize.Y);
        var canvasStart = ImGui.GetCursorScreenPos();
        ImGui.Dummy(canvasSize);
        var drawList = ImGui.GetWindowDrawList();

        GetTarotRevealTimeline(card, out var glitchProgress, out var flipProgress, out var settleProgress, out var showFront);
        var easedFlip = 1f - MathF.Pow(1f - flipProgress, 3f);
        var horizontalScale = card.Revealed && flipProgress > 0f
            ? MathF.Max(0.025f, MathF.Abs(MathF.Cos(easedFlip * MathF.PI)))
            : 1f;
        var renderedSize = new Vector2(frameSize.X * horizontalScale, frameSize.Y);
        var start = canvasStart + new Vector2((canvasSize.X - renderedSize.X) * 0.5f, 0);
        var end = start + renderedSize;

        var flipFlash = card.Revealed ? MathF.Max(0f, 1f - MathF.Abs(flipProgress - 0.5f) * 4f) : 0f;
        var settleFlash = card.Revealed ? MathF.Max(0f, 1f - settleProgress) * 0.55f : 0f;
        var flash = MathF.Max(flipFlash, settleFlash);
        var glow = showFront ? CyberdeckTheme.Palette.Cyan : CyberdeckTheme.Palette.Magenta;
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(CyberdeckTheme.Palette.Background), 3f);
        for (var layer = 3; layer >= 1; layer--)
        {
            var expansion = layer * 3 * uiScale;
            drawList.AddRect(
                start - new Vector2(expansion),
                end + new Vector2(expansion),
                ImGui.GetColorU32(new Vector4(glow.X, glow.Y, glow.Z, (0.16f + (flash * 0.62f)) / layer)),
                3f,
                ImDrawFlags.None,
                1f);
        }

        if (card.Revealed && !showFront)
            _ = GetTarotCardTexture(card, showFront: true);
        var texture = GetTarotCardTexture(card, showFront);
        if (texture is not null)
        {
            DrawTarotCardTexture(
                drawList,
                texture,
                start,
                end,
                showFront && card.Reversed,
                1f,
                fillBounds: !showFront);
            if (!showFront && card.Revealed && !config.ReduceMotion)
                DrawTarotBackInterference(drawList, texture, start, end, glitchProgress, flipProgress, uiScale);
        }
        else
        {
            var revealAnimating = card.Revealed && !config.ReduceMotion && settleProgress < 1f;
            if (!revealAnimating)
            {
                const string unavailable = "ARCANA ART\nNOT LOADED";
                var textSize = ImGui.CalcTextSize(unavailable);
                drawList.AddText(start + ((renderedSize - textSize) * 0.5f), ImGui.GetColorU32(CyberdeckTheme.Palette.TextMuted), unavailable);
            }
        }

        if (!config.ReduceMotion && card.Revealed && settleProgress < 1f)
        {
            var scanY = start.Y + (renderedSize.Y * ((float)ImGui.GetTime() * 2.8f % 1f));
            drawList.AddLine(
                new Vector2(start.X - (6 * uiScale), scanY),
                new Vector2(end.X + (6 * uiScale), scanY),
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.85f)),
                2f);
        }
    }

    private bool IsTarotFrontVisible(TarotReadingCard card)
    {
        GetTarotRevealTimeline(card, out _, out _, out _, out var showFront);
        return showFront;
    }

    private void GetTarotRevealTimeline(
        TarotReadingCard card,
        out float glitchProgress,
        out float flipProgress,
        out float settleProgress,
        out bool showFront)
    {
        if (!card.Revealed)
        {
            glitchProgress = 0f;
            flipProgress = 0f;
            settleProgress = 0f;
            showFront = false;
            return;
        }

        if (config.ReduceMotion || !tarotDebugCardRevealedAt.TryGetValue(card.Slot, out var revealedAt))
        {
            glitchProgress = 1f;
            flipProgress = 1f;
            settleProgress = 1f;
            showFront = true;
            return;
        }

        var elapsed = MathF.Max(0f, (float)(Environment.TickCount64 - revealedAt));
        glitchProgress = Math.Clamp(elapsed / TarotBackGlitchDurationMs, 0f, 1f);
        flipProgress = Math.Clamp(
            (elapsed - TarotBackGlitchDurationMs) / TarotFlipDurationMs,
            0f,
            1f);
        settleProgress = Math.Clamp(
            (elapsed - TarotBackGlitchDurationMs - TarotFlipDurationMs) / TarotSettleDurationMs,
            0f,
            1f);
        showFront = flipProgress >= 0.5f;
    }

    private static void DrawTarotBackInterference(
        ImDrawListPtr drawList,
        IDalamudTextureWrap texture,
        Vector2 start,
        Vector2 end,
        float glitchProgress,
        float flipProgress,
        float uiScale)
    {
        var size = end - start;
        if (size.X <= 1f || size.Y <= 1f)
            return;

        var time = (float)ImGui.GetTime();
        var intensity = Math.Clamp((glitchProgress * 0.82f) + (flipProgress * 0.4f), 0.08f, 1f);
        var burstWave = MathF.Sin((time * (17f + (intensity * 31f))) + (glitchProgress * 11f));
        var burst = burstWave > (0.72f - (intensity * 0.48f));
        var displacement = (1.5f + (intensity * 7f)) * uiScale * (burst ? 1f : 0.35f);
        var (uvMin, uvMax) = GetCoverUv(texture, size);

        drawList.AddImage(
            texture.Handle,
            start + new Vector2(-displacement, 0),
            end + new Vector2(-displacement, 0),
            uvMin,
            uvMax,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, 0.10f + (intensity * 0.13f))));
        drawList.AddImage(
            texture.Handle,
            start + new Vector2(displacement, 0),
            end + new Vector2(displacement, 0),
            uvMin,
            uvMax,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.10f + (intensity * 0.13f))));

        if (burst || glitchProgress > 0.82f)
        {
            for (var band = 0; band < 6; band++)
            {
                var normalizedY = ((time * (0.31f + (band * 0.047f))) + (band * 0.173f)) % 1f;
                var bandHeight = 0.012f + (((band + 1) % 3) * 0.009f);
                var y0 = Math.Clamp(normalizedY, 0f, 1f - bandHeight);
                var y1 = y0 + bandHeight;
                var offset = MathF.Sin((time * 43f) + (band * 2.7f)) * (4f + (intensity * 13f)) * uiScale;
                var sliceStart = new Vector2(start.X + offset, start.Y + (size.Y * y0));
                var sliceEnd = new Vector2(end.X + offset, start.Y + (size.Y * y1));
                var sliceUvMin = new Vector2(uvMin.X, uvMin.Y + ((uvMax.Y - uvMin.Y) * y0));
                var sliceUvMax = new Vector2(uvMax.X, uvMin.Y + ((uvMax.Y - uvMin.Y) * y1));
                drawList.AddImage(
                    texture.Handle,
                    sliceStart,
                    sliceEnd,
                    sliceUvMin,
                    sliceUvMax,
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.82f)));
            }
        }

        var scanY = start.Y + (size.Y * ((time * (1.4f + intensity)) % 1f));
        drawList.AddLine(
            new Vector2(start.X - (6 * uiScale), scanY),
            new Vector2(end.X + (6 * uiScale), scanY),
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.35f + (intensity * 0.5f))),
            MathF.Max(1f, 2f * uiScale));
    }

    private static void DrawCenteredTarotViewerText(string text, Vector4 color)
    {
        var textWidth = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (ImGui.GetContentRegionAvail().X - textWidth) * 0.5f));
        ImGui.TextColored(color, text);
    }

    private void DrawTarotDebugHeader()
    {
        if (tarotDebugSession.Role == TarotDebugRole.Customer &&
            tarotDebugSession.Phase is TarotDebugPhase.Ready or TarotDebugPhase.Reading or TarotDebugPhase.Ended)
        {
            var linked = tarotDebugSession.Phase != TarotDebugPhase.Ended;
            CyberdeckWidgets.DrawStatusChip(
                linked ? "VISUAL UPLINK // SYNCHRONIZED" : "VISUAL UPLINK // CLOSED",
                linked ? CyberdeckTheme.Palette.Success : CyberdeckTheme.Palette.Error,
                CyberdeckTheme.Palette.Text,
                GetUiScale());
            ImGui.Spacing();
            ImGui.TextColored(CyberdeckTheme.Palette.Magenta, "ARCANA-CAST // ENCRYPTED DIVINATION CHANNEL");
            DrawMutedWrapped(linked
                ? "Incoming visual frames are isolated from ordinary Cyberdeck traffic."
                : "The reader has terminated this private channel.");
            ImGui.Spacing();
            return;
        }

        if (tarotDebugSession.Role == TarotDebugRole.Host)
        {
            var connected = tarotDebugSession.Phase is TarotDebugPhase.Ready or TarotDebugPhase.Reading;
            var closed = tarotDebugSession.Phase == TarotDebugPhase.Ended;
            CyberdeckWidgets.DrawStatusChip(
                closed ? "READING ENDED" : connected ? "CUSTOMER CONNECTED" : "WAITING FOR CUSTOMER",
                closed ? CyberdeckTheme.Palette.TextMuted : connected ? CyberdeckTheme.Palette.Success : CyberdeckTheme.Palette.Amber,
                CyberdeckTheme.Palette.Text,
                GetUiScale());
            ImGui.Spacing();
            ImGui.TextColored(CyberdeckTheme.Palette.Magenta, "TAROT READER");
            ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, "Customer");
            ImGui.SameLine();
            ImGui.TextColored(CyberdeckTheme.Palette.Cyan, tarotDebugSession.Partner);
            return;
        }

        var statusColor = tarotDebugFeedback.Contains("REJECTED", StringComparison.Ordinal)
            ? CyberdeckTheme.Palette.Error
            : tarotDebugSession.Phase == TarotDebugPhase.Reading
                ? CyberdeckTheme.Palette.Success
                : CyberdeckTheme.Palette.Amber;
        CyberdeckWidgets.DrawStatusChip(
            tarotDebugLoopback ? "LOCAL TEST" : "TAROT READING",
            statusColor,
            CyberdeckTheme.Palette.Text,
            GetUiScale());
        ImGui.Spacing();
        ImGui.TextColored(CyberdeckTheme.Palette.Magenta, "START A TAROT READING");
        DrawMutedWrapped(tarotDebugLoopback
            ? "Test the card flow without a second player."
            : "Invite a customer, select the physical cards you draw, and reveal them when ready.");
    }

    private void DrawTarotDebugRoleSelection()
    {
        ImGui.TextColored(CyberdeckTheme.Palette.Cyan, "CHOOSE CUSTOMER");
        DrawMutedWrapped("Target the customer or enter their full character name and World.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##tarot_partner", "Customer First Last@World", ref tarotDebugPartnerInput, 64);
        if (ImGui.SmallButton("USE CURRENT TARGET") && PluginService.Targets.Target is IPlayerCharacter player)
            tarotDebugPartnerInput = GetPlayerTellName(player);

        ImGui.Spacing();
        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("START READING", new Vector2(ImGui.GetContentRegionAvail().X, 40 * GetUiScale())))
            {
                tarotTellQueue.Clear();
                tarotDebugLoopback = false;
                if (tarotDebugSession.BeginHost(tarotDebugPartnerInput, out var error))
                {
                    tarotDebugIncomingSender = tarotDebugPartnerInput;
                    tarotDebugSimulatedPeerSequence = 0;
                    tarotDebugCardPlacedAt.Clear();
                    tarotDebugCardRevealedAt.Clear();
                    tarotDebugCustomerFocusedSlot = -1;
                    TouchTarotSession();
                    SendCurrentTarotMessage($"Invitation sent to {tarotDebugSession.Partner}.");
                }
                else
                {
                    tarotDebugFeedback = $"COULDN'T START READING // {error}";
                }
            }
        }

        if (tarotDebugFeedback.StartsWith("COULDN'T", StringComparison.Ordinal))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(tarotDebugFeedback);
        }
    }

    private void DrawTarotDebugActiveSession()
    {
        if (tarotDebugSession.Role == TarotDebugRole.Customer)
        {
            DrawTarotCustomerExperience();
            return;
        }

        if (IsTarotMessagePending())
        {
            DrawTarotHostRetry();
            ImGui.Spacing();
        }

        DrawTarotDebugHostControls();

        if (!IsTarotMessagePending() && tarotDebugFeedback.StartsWith("COULDN'T", StringComparison.Ordinal))
        {
            ImGui.Spacing();
            ImGui.TextColored(CyberdeckTheme.Palette.Error, tarotDebugFeedback);
        }

        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();
        ImGui.BeginDisabled(IsTarotMessagePending());
        if (tarotDebugSession.Phase == TarotDebugPhase.Ended)
        {
            if (ImGui.Button("START ANOTHER READING", new Vector2(220 * GetUiScale(), 0)))
                ResetTarotReader();
        }
        else if (ImGui.SmallButton("END READING"))
        {
            tarotDebugSession.EndSession();
            tarotTellQueue.Clear();
            tarotDebugFeedback = "Reading ended locally.";
            tarotCardViewerOpen = false;
            tarotLastActivityAt = 0;
        }
        ImGui.EndDisabled();

#if DEBUG
        if (ImGui.CollapsingHeader("TROUBLESHOOTING"))
        {
            DrawTarotDebugSessionSummary();
            ImGui.Spacing();
            if (!string.IsNullOrWhiteSpace(tarotDebugSession.LastOutboundPacket))
                DrawTarotDebugOutbox();
            ImGui.Spacing();
            DrawTarotDebugAuditLog();
            ImGui.Spacing();
            DrawTarotDebugTransportTools();
        }
#endif
    }

    private void ResetTarotReader()
    {
        tarotTellQueue.Clear();
        tarotDebugSession.Reset();
        tarotDebugLoopback = false;
        tarotCardViewerOpen = false;
        tarotDebugLastSentPacket = string.Empty;
        tarotDebugSimulatedPeerSequence = 0;
        tarotDebugCardPlacedAt.Clear();
        tarotDebugCardRevealedAt.Clear();
        tarotDebugCustomerFocusedSlot = -1;
        tarotDebugFeedback = string.Empty;
        tarotLastActivityAt = 0;
    }

    private void DrawTarotCustomerExperience()
    {
        if (tarotDebugSession.Phase == TarotDebugPhase.InviteReceived)
            DrawTarotUnauthorizedConnection();
        else
            DrawTarotReceivingMode();

#if DEBUG
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("DEBUG // LINK DIAGNOSTICS"))
        {
            DrawTarotDebugSessionSummary();
            if (!string.IsNullOrWhiteSpace(tarotDebugSession.LastOutboundPacket) &&
                !string.Equals(tarotDebugLastSentPacket, tarotDebugSession.LastOutboundPacket, StringComparison.Ordinal))
            {
                ImGui.Spacing();
                DrawTarotDebugOutbox();
            }
            ImGui.Spacing();
            DrawTarotDebugAuditLog();
            ImGui.Spacing();
            DrawTarotDebugTransportTools();
            if (ImGui.SmallButton("RESET LOCAL SESSION"))
            {
                tarotDebugSession.Reset();
                tarotDebugLoopback = false;
                tarotCardViewerOpen = false;
                tarotDebugLastSentPacket = string.Empty;
                tarotDebugSimulatedPeerSequence = 0;
                tarotDebugCardPlacedAt.Clear();
                tarotDebugCardRevealedAt.Clear();
                tarotDebugCustomerFocusedSlot = -1;
                tarotDebugFeedback = "DEBUG SESSION RESET";
            }
        }
#endif
    }

    private void DrawTarotDebugSessionSummary()
    {
        var role = tarotDebugSession.Role == TarotDebugRole.Host ? "READER" : "CUSTOMER";
        CyberdeckWidgets.DrawStatusChip(
            $"{role} // {tarotDebugSession.Phase.ToString().ToUpperInvariant()}",
            tarotDebugSession.Phase == TarotDebugPhase.Reading
                ? CyberdeckTheme.Palette.Success
                : CyberdeckTheme.Palette.Cyan,
            CyberdeckTheme.Palette.Text,
            GetUiScale());
        ImGui.Spacing();
        ImGui.TextColored(CyberdeckTheme.Palette.Cyan, $"SESSION // {tarotDebugSession.SessionId}");
        ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, $"PEER // {tarotDebugSession.Partner}");
        ImGui.TextColored(CyberdeckTheme.Palette.Text, tarotDebugSession.LastStatus);
    }

    private void DrawTarotDebugHostControls()
    {
        switch (tarotDebugSession.Phase)
        {
            case TarotDebugPhase.WaitingForJoin:
                ImGui.TextColored(CyberdeckTheme.Palette.Amber, "WAITING FOR CUSTOMER");
                DrawMutedWrapped(IsCurrentTarotMessageQueued()
                    ? $"The invitation to {tarotDebugSession.Partner} is waiting for the chat cooldown."
                    : IsTarotMessagePending()
                        ? $"The invitation to {tarotDebugSession.Partner} could not be sent."
                    : $"An invitation was sent to {tarotDebugSession.Partner}. The reading will open here when they accept.");
                ImGui.Spacing();
                ImGui.BeginDisabled(IsTarotMessagePending());
                if (ImGui.SmallButton("SEND INVITATION AGAIN"))
                    SendCurrentTarotMessage($"Invitation sent again to {tarotDebugSession.Partner}.", force: true);
                ImGui.EndDisabled();
                break;
            case TarotDebugPhase.Ready:
            case TarotDebugPhase.Reading:
                if (tarotDebugSession.Cards.Count > 0)
                {
                    DrawTarotHostSpread();
                    if (!IsTarotControlTransmissionPending() &&
                        tarotDebugSession.LastOutboundKind == TarotPacketKind.Reveal)
                    {
                        ImGui.Spacing();
                        if (ImGui.SmallButton("RESEND LAST CARD"))
                            SendCurrentTarotMessage("Last card sent again.", force: true);
                        DrawHoverTooltip("Use only if the customer says the last card did not appear");
                    }
                    ImGui.Spacing();
                }
                DrawTarotReaderCardTable();
                if (tarotDebugSession.Cards.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.BeginDisabled(IsTarotControlTransmissionPending());
                    if (ImGui.SmallButton("CLEAR SPREAD"))
                    {
                        if (tarotDebugSession.ResetReading(out var error))
                        {
                            tarotDebugCardPlacedAt.Clear();
                            tarotDebugCardRevealedAt.Clear();
                            tarotDebugCustomerFocusedSlot = -1;
                            tarotCardViewerOpen = false;
                            tarotDebugFeedback = "Spread cleared.";
                        }
                        else
                        {
                            tarotDebugFeedback = $"COULDN'T CLEAR SPREAD // {error}";
                        }
                    }
                    ImGui.EndDisabled();
                }
                break;
            case TarotDebugPhase.Ended:
                ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, "This reading has ended.");
                break;
        }
    }

    private void DrawTarotUnauthorizedConnection()
    {
        CyberdeckWidgets.DrawStatusChip(
            "UNAUTHORIZED CONNECTION // ARCANA-CAST",
            CyberdeckTheme.Palette.Error,
            CyberdeckTheme.Palette.Text,
            GetUiScale());
        ImGui.Spacing();
        ImGui.TextColored(CyberdeckTheme.Palette.Magenta, "FOREIGN DIVINATION NODE DETECTED");
        DrawMutedWrapped("An encrypted visual channel is requesting access to your Cyberdeck.");
        ImGui.Spacing();
        ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, "ORIGIN //");
        ImGui.SameLine();
        ImGui.TextColored(CyberdeckTheme.Palette.Cyan, tarotDebugSession.Partner);
        ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, "PAYLOAD // ARCANA IMAGE STREAM");
        ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, $"CHANNEL // {tarotDebugSession.SessionId}");
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();
        ImGui.TextColored(CyberdeckTheme.Palette.Amber, "ALLOW THE UNKNOWN SIGNAL THROUGH?");
        ImGui.Spacing();

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonSize = new Vector2(
            MathF.Max(140, (ImGui.GetContentRegionAvail().X - spacing) / 2),
            48 * GetUiScale());
        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("YES // OPEN CHANNEL", buttonSize))
                AcceptTarotConnection();
        }
        ImGui.SameLine();
        if (DrawGlitchedTarotNoButton("tarot_no_legacy", buttonSize))
            RefuseTarotConnection();
    }

    private void DrawTarotConnectionPromptInline()
    {
        var uiScale = GetUiScale();
        if (ImGui.BeginChild("tarot_connection_prompt", new Vector2(0, 132 * uiScale), true, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Magenta, "UNAUTHORIZED ARCANA UPLINK");
            ImGui.SameLine();
            ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, $"// {tarotDebugSession.Partner}");
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "ALLOW ENCRYPTED VISUAL CONNECTION?");
            ImGui.Spacing();

            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var buttonSize = new Vector2(
                MathF.Max(96, (ImGui.GetContentRegionAvail().X - spacing) / 2),
                38 * uiScale);
            using (CyberdeckTheme.PushAccentButton())
            {
                if (ImGui.Button("YES // ACCEPT", buttonSize))
                    AcceptTarotConnection();
            }
            ImGui.SameLine();
            if (DrawGlitchedTarotNoButton("tarot_no_inline", buttonSize))
                RefuseTarotConnection();
        }
        ImGui.EndChild();
    }

    private void AcceptTarotConnection()
    {
        if (!tarotDebugSession.AcceptInvite(out var error))
        {
            tarotDebugFeedback = $"CONNECTION REJECTED // {error}";
            return;
        }

        tarotDebugCardPlacedAt.Clear();
        tarotDebugCardRevealedAt.Clear();
        tarotDebugCustomerFocusedSlot = -1;
        TouchTarotSession();
        if (tarotDebugLoopback)
        {
            tarotDebugLastSentPacket = tarotDebugSession.LastOutboundPacket;
            tarotDebugFeedback = "LOCAL LOOPBACK // CONNECTION ACCEPTED";
        }
        else if (PluginService.ClientState.IsLoggedIn &&
            TarotTellSender.TrySend(
                tarotDebugSession.Partner,
                "Connection accepted.",
                tarotDebugSession.LastOutboundPacket,
                out error))
        {
            tarotDebugLastSentPacket = tarotDebugSession.LastOutboundPacket;
            tarotDebugFeedback = "CONNECTION ACCEPTED // RECEIVING MODE ACTIVE";
        }
        else
        {
            tarotDebugFeedback = string.IsNullOrWhiteSpace(error)
                ? "CONNECTION OPEN // ACCEPTANCE RESPONSE WAITING TO SEND"
                : $"CONNECTION OPEN // RESPONSE NOT SENT // {error}";
        }

        IsOpen = false;
        tarotDebugWindowOpen = false;
        tarotCardViewerOpen = true;
        focusTarotCardViewer = true;
    }

    private void RefuseTarotConnection()
    {
        if (!tarotDebugSession.RefuseInvite(out var error))
        {
            tarotDebugFeedback = $"REFUSAL REJECTED // {error}";
            return;
        }

        if (tarotDebugLoopback)
        {
            tarotDebugLastSentPacket = tarotDebugSession.LastOutboundPacket;
            tarotDebugFeedback = "LOCAL LOOPBACK // CONNECTION REFUSED AND SESSION CLOSED";
        }
        else if (PluginService.ClientState.IsLoggedIn &&
            TarotTellSender.TrySend(
                tarotDebugSession.Partner,
                "Connection refused.",
                tarotDebugSession.LastOutboundPacket,
                out error))
        {
            tarotDebugLastSentPacket = tarotDebugSession.LastOutboundPacket;
            tarotDebugFeedback = "CONNECTION REFUSED // REMOTE SESSION TERMINATION SENT";
        }
        else
        {
            tarotDebugFeedback = string.IsNullOrWhiteSpace(error)
                ? "CONNECTION REFUSED // RESPONSE NOT SENT"
                : $"CONNECTION REFUSED // RESPONSE NOT SENT // {error}";
        }

        IsOpen = false;
        tarotDebugWindowOpen = false;
        tarotCardViewerOpen = false;
        tarotLastActivityAt = 0;
    }

    private bool DrawGlitchedTarotNoButton(string id, Vector2 size)
    {
        var start = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##{id}", size);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var drawList = ImGui.GetWindowDrawList();
        var time = ImGui.GetTime();
        var burst = !config.ReduceMotion && (hovered || (int)(time * 3.2) % 7 is 0 or 1);
        var jitter = burst ? new Vector2(MathF.Sin((float)time * 61f) * 4f, MathF.Cos((float)time * 47f) * 2f) : Vector2.Zero;
        var end = start + size;
        var fill = active
            ? CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Error, 0.30f)
            : CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.PanelRaised, 0.92f);
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(fill), 2f);
        drawList.AddRect(start + jitter, end + jitter, ImGui.GetColorU32(CyberdeckTheme.Palette.Magenta), 2f, ImDrawFlags.None, 1f);

        const string label = "NO // REFUSE";
        var textSize = ImGui.CalcTextSize(label);
        var textPos = start + ((size - textSize) * 0.5f) + jitter;
        if (burst)
        {
            drawList.AddText(textPos + new Vector2(-3, 0), ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.55f)), "N0 // REFU5E");
            drawList.AddText(textPos + new Vector2(3, 0), ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, 0.65f)), label);
        }
        drawList.AddText(textPos, ImGui.GetColorU32(CyberdeckTheme.Palette.TextMuted), label);
        return clicked;
    }

    private void DrawTarotReceivingMode()
    {
        CyberdeckWidgets.DrawStatusChip(
            tarotDebugSession.Phase == TarotDebugPhase.Ended
                ? "ARCANA LINK // TERMINATED"
                : "ARCANA LINK // RECEIVING",
            tarotDebugSession.Phase == TarotDebugPhase.Ended
                ? CyberdeckTheme.Palette.Error
                : CyberdeckTheme.Palette.Success,
            CyberdeckTheme.Palette.Text,
            GetUiScale());
        ImGui.Spacing();
        ImGui.TextColored(CyberdeckTheme.Palette.Cyan, $"READER // {tarotDebugSession.Partner}");
        ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, $"ENCRYPTED CHANNEL // {tarotDebugSession.SessionId}");
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        if (tarotDebugSession.Phase == TarotDebugPhase.Ended)
        {
            ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, "REMOTE NODE CLOSED THE READING.");
            return;
        }

        if (tarotDebugSession.Cards.Count == 0)
        {
            DrawTarotReceivingIdleSignal();
            return;
        }

        DrawTarotCustomerFocusedFrame();
        ImGui.Spacing();
        DrawMutedWrapped("The reader controls each reveal. Continue the interpretation through ordinary private chat.");
    }

    private void DrawTarotCustomerFocusedFrame()
    {
        if (tarotDebugCustomerFocusedSlot < 0 || tarotDebugCustomerFocusedSlot >= tarotDebugSession.Cards.Count)
            tarotDebugCustomerFocusedSlot = tarotDebugSession.Cards.Count - 1;

        DrawSettingsGroupHeader("RECEIVED FRAMES");
        var tabWidth = 54 * GetUiScale();
        foreach (var card in tarotDebugSession.Cards.ToArray())
        {
            if (ImGui.GetContentRegionAvail().X < tabWidth && card.Slot > 0)
                ImGui.NewLine();

            var selected = card.Slot == tarotDebugCustomerFocusedSlot;
            if (selected)
            {
                using var accent = CyberdeckTheme.PushAccentButton();
                if (ImGui.Button($"{card.Slot + 1:00}##tarot_customer_frame_{card.Slot}", new Vector2(tabWidth, 30 * GetUiScale())))
                    tarotDebugCustomerFocusedSlot = card.Slot;
            }
            else if (ImGui.Button($"{card.Slot + 1:00}##tarot_customer_frame_{card.Slot}", new Vector2(tabWidth, 30 * GetUiScale())))
            {
                tarotDebugCustomerFocusedSlot = card.Slot;
            }

            if (card.Slot < tarotDebugSession.Cards.Count - 1)
                ImGui.SameLine();
        }

        ImGui.Spacing();
        var focused = tarotDebugSession.Cards[tarotDebugCustomerFocusedSlot];
        var frameColor = focused.Revealed ? CyberdeckTheme.Palette.Success : CyberdeckTheme.Palette.Amber;
        ImGui.TextColored(
            frameColor,
            focused.Revealed
                ? $"FRAME {focused.Slot + 1:00} // DECRYPTED"
                : $"FRAME {focused.Slot + 1:00} // ENCRYPTED");
        ImGui.Spacing();

        DrawTarotCardVisual(
            focused,
            new Vector2(ImGui.GetContentRegionAvail().X, 430 * GetUiScale()),
            230 * GetUiScale());
        ImGui.Spacing();

        if (focused.Revealed && focused.CardIndex is int cardIndex)
        {
            var cardName = TarotDeck.CardName(cardIndex).ToUpperInvariant();
            var orientation = focused.Reversed ? "REVERSED" : "UPRIGHT";
            var label = $"{cardName} // {orientation}";
            var textWidth = ImGui.CalcTextSize(label).X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (ImGui.GetContentRegionAvail().X - textWidth) * 0.5f));
            ImGui.TextColored(focused.Reversed ? CyberdeckTheme.Palette.Magenta : CyberdeckTheme.Palette.Cyan, label);
        }
        else
        {
            const string sealedLabel = "IDENTITY SEALED // AWAITING READER";
            var textWidth = ImGui.CalcTextSize(sealedLabel).X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (ImGui.GetContentRegionAvail().X - textWidth) * 0.5f));
            ImGui.TextColored(CyberdeckTheme.Palette.TextMuted, sealedLabel);
        }
    }

    private void DrawTarotReceivingIdleSignal()
    {
        var height = 220 * GetUiScale();
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, height);
        ImGui.Dummy(size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + size, ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Panel, 0.92f)), 3f);
        drawList.AddRect(start, start + size, ImGui.GetColorU32(CyberdeckTheme.Palette.Cyan), 3f);

        var time = config.ReduceMotion ? 0f : (float)ImGui.GetTime();
        for (var index = 0; index < 7; index++)
        {
            var y = start.Y + 24 * GetUiScale() + (index * 24 * GetUiScale());
            var phase = (MathF.Sin((time * 2.4f) + index) + 1f) * 0.5f;
            var lineWidth = size.X * (0.2f + (phase * 0.55f));
            drawList.AddLine(
                new Vector2(start.X + 24 * GetUiScale(), y),
                new Vector2(start.X + 24 * GetUiScale() + lineWidth, y),
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.12f + (phase * 0.25f))),
                1f);
        }

        const string waiting = "AWAITING ENCRYPTED ARCANA FRAME";
        var textSize = ImGui.CalcTextSize(waiting);
        drawList.AddText(
            start + ((size - textSize) * 0.5f),
            ImGui.GetColorU32(CyberdeckTheme.Palette.Amber),
            waiting);
    }

    private void DrawTarotHostSpread()
    {
        var revealedCount = tarotDebugSession.Cards.Count(card => card.Revealed);
        DrawSettingsGroupHeader($"CURRENT SPREAD  ·  {revealedCount} OF {tarotDebugSession.Cards.Count} SHOWN");
        var pending = IsTarotControlTransmissionPending();
        if (ImGui.BeginTable(
                "tarot_host_spread",
                5,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30 * GetUiScale());
            ImGui.TableSetupColumn("CARD", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("STATE", ImGuiTableColumnFlags.WidthFixed, 82 * GetUiScale());
            ImGui.TableSetupColumn("CUSTOMER", ImGuiTableColumnFlags.WidthFixed, 122 * GetUiScale());
            ImGui.TableSetupColumn("REMOVE", ImGuiTableColumnFlags.WidthFixed, 72 * GetUiScale());
            ImGui.TableHeadersRow();

            foreach (var card in tarotDebugSession.Cards.ToArray())
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.AlignTextToFramePadding();
                ImGui.Text($"{card.Slot + 1:00}");

                ImGui.TableSetColumnIndex(1);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(
                    card.Revealed ? CyberdeckTheme.Palette.Cyan : CyberdeckTheme.Palette.Text,
                    card.CardIndex is int cardIndex ? TarotDeck.CardName(cardIndex) : "Unknown card");

                ImGui.TableSetColumnIndex(2);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(
                    card.Reversed ? CyberdeckTheme.Palette.Magenta : CyberdeckTheme.Palette.Success,
                    card.Reversed ? "REVERSED" : "UPRIGHT");

                ImGui.TableSetColumnIndex(3);
                if (card.Revealed)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(CyberdeckTheme.Palette.Success, "SHOWN");
                }
                else
                {
                    ImGui.BeginDisabled(pending);
                    if (ImGui.Button($"SHOW CARD##tarot_host_reveal_{card.Slot}", new Vector2(-1, 0)))
                        RevealTarotCardFromHost(card.Slot);
                    ImGui.EndDisabled();
                }

                ImGui.TableSetColumnIndex(4);
                ImGui.BeginDisabled(pending);
                if (ImGui.Button($"X##tarot_host_remove_{card.Slot}", new Vector2(-1, 0)))
                    RemoveTarotCardFromHost(card.Slot);
                DrawHoverTooltip($"Remove {TarotDeck.CardName(card.CardIndex ?? -1)} from the spread");
                ImGui.EndDisabled();
            }

            ImGui.EndTable();
        }
    }

    private void RevealTarotCardFromHost(int slot)
    {
        if (!tarotDebugSession.FlipCard(slot, out var error))
        {
            tarotDebugFeedback = $"REVEAL REJECTED // {error}";
            return;
        }

        tarotDebugCustomerFocusedSlot = slot;
        SendCurrentTarotMessage($"Card {slot + 1} shown to {tarotDebugSession.Partner}.", showRevealedCard: true);
    }

    private void RemoveTarotCardFromHost(int slot)
    {
        var removedName = slot >= 0 && slot < tarotDebugSession.Cards.Count &&
                          tarotDebugSession.Cards[slot].CardIndex is int cardIndex
            ? TarotDeck.CardName(cardIndex)
            : $"Card {slot + 1:00}";
        if (!tarotDebugSession.RemoveCard(slot, out var error))
        {
            tarotDebugFeedback = $"REMOVE REJECTED // {error}";
            return;
        }

        tarotDebugCardPlacedAt.Clear();
        tarotDebugCardRevealedAt.Clear();
        if (tarotDebugCustomerFocusedSlot > slot)
            tarotDebugCustomerFocusedSlot--;
        else if (tarotDebugCustomerFocusedSlot == slot)
            tarotDebugCustomerFocusedSlot = Math.Min(slot, tarotDebugSession.Cards.Count - 1);
        if (tarotDebugSession.Cards.Count == 0)
            tarotCardViewerOpen = false;
        tarotDebugFeedback = $"{removedName} removed from the spread.";
    }

    private void DrawTarotReaderCardTable()
    {
        var pending = IsTarotControlTransmissionPending();
        if (pending)
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, "The last change still needs to be sent.");
        DrawMutedWrapped("Find the physical card you drew, then choose upright or reversed. It stays face down for the customer until you show it.");
        ImGui.Spacing();

        DrawSettingsGroupHeader("MINOR ARCANA // FOUR SUITS");
        if (ImGui.BeginTable(
                "tarot_minor_arcana",
                5,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("RANK", ImGuiTableColumnFlags.WidthFixed, 62 * GetUiScale());
            for (var suit = 0; suit < TarotDeck.SuitCount; suit++)
                ImGui.TableSetupColumn(TarotDeck.SuitName(suit), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            ReadOnlySpan<int> rankOrder = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 0];
            foreach (var rank in rankOrder)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(CyberdeckTheme.Palette.Cyan, TarotDeck.RankName(rank).ToUpperInvariant());
                for (var suit = 0; suit < TarotDeck.SuitCount; suit++)
                {
                    ImGui.TableSetColumnIndex(suit + 1);
                    var cardIndex = TarotDeck.MinorCardIndex(suit, rank);
                    DrawTarotCardOrientationButtons(cardIndex, $"minor_{suit}_{rank}", pending);
                }
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        DrawSettingsGroupHeader("MAJOR ARCANA");
        if (ImGui.BeginTable(
                "tarot_major_arcana",
                3,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            for (var majorIndex = 0; majorIndex < TarotDeck.MajorArcanaCount; majorIndex++)
            {
                ImGui.TableNextColumn();
                ImGui.TextColored(
                    CyberdeckTheme.Palette.Cyan,
                    $"{majorIndex:00} // {TarotDeck.MajorArcanaName(majorIndex)}");
                DrawTarotCardOrientationButtons(majorIndex, $"major_{majorIndex}", pending);
            }

            ImGui.EndTable();
        }
    }

    private void DrawTarotCardOrientationButtons(int cardIndex, string id, bool pending)
    {
        var alreadyPlaced = tarotDebugSession.Cards.Any(card => card.CardIndex == cardIndex);
        if (alreadyPlaced)
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Success, "IN SPREAD");
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(24 * GetUiScale(), (ImGui.GetContentRegionAvail().X - spacing) / 2);
        ImGui.BeginDisabled(pending || tarotDebugSession.Cards.Count >= TarotDebugSession.MaximumReadingCards);
        if (DrawTarotOrientationButton($"U ↑##tarot_u_{id}", new Vector2(width, 0), CyberdeckTheme.Palette.Success))
            PlaceTarotCardFromTable(cardIndex, reversed: false);
        DrawHoverTooltip($"Place {TarotDeck.CardName(cardIndex)} upright");
        ImGui.SameLine();
        if (DrawTarotOrientationButton($"R ↓##tarot_r_{id}", new Vector2(width, 0), CyberdeckTheme.Palette.Error))
            PlaceTarotCardFromTable(cardIndex, reversed: true);
        DrawHoverTooltip($"Place {TarotDeck.CardName(cardIndex)} reversed");
        ImGui.EndDisabled();
    }

    private static bool DrawTarotOrientationButton(string label, Vector2 size, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(color.X * 0.16f, color.Y * 0.16f, color.Z * 0.16f, 0.96f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(color.X * 0.34f, color.Y * 0.34f, color.Z * 0.34f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(color.X * 0.52f, color.Y * 0.52f, color.Z * 0.52f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(4);
        return clicked;
    }

    private void PlaceTarotCardFromTable(int cardIndex, bool reversed)
    {
        if (tarotDebugSession.PlaceCard(cardIndex, reversed, out var error))
        {
            tarotDebugCardPlacedAt[tarotDebugSession.Cards.Count - 1] = Environment.TickCount64;
            tarotDebugFeedback = $"{TarotDeck.CardName(cardIndex)} added to the spread.";
        }
        else
        {
            tarotDebugFeedback = $"COULDN'T ADD CARD // {error}";
        }
    }

    private void DrawTarotCardVisual(TarotReadingCard card, Vector2 canvasSize, float maximumCardWidth)
    {
        var canvasStart = ImGui.GetCursorScreenPos();
        ImGui.Dummy(canvasSize);
        var drawList = ImGui.GetWindowDrawList();

        var revealProgress = 1f;
        if (card.Revealed && !config.ReduceMotion && tarotDebugCardRevealedAt.TryGetValue(card.Slot, out var revealedAt))
            revealProgress = Math.Clamp((Environment.TickCount64 - revealedAt) / 900f, 0f, 1f);

        var placementProgress = 1f;
        if (!config.ReduceMotion && tarotDebugCardPlacedAt.TryGetValue(card.Slot, out var placedAt))
            placementProgress = Math.Clamp((Environment.TickCount64 - placedAt) / 520f, 0f, 1f);

        var easedReveal = 1f - MathF.Pow(1f - revealProgress, 3f);
        var showFront = card.Revealed && (config.ReduceMotion || easedReveal >= 0.5f);
        var horizontalScale = card.Revealed
            ? MathF.Max(0.035f, MathF.Abs(MathF.Cos(easedReveal * MathF.PI)))
            : 1f;
        var fullCardSize = new Vector2(
            MathF.Min(canvasSize.X - (16 * GetUiScale()), maximumCardWidth),
            MathF.Min(canvasSize.Y - (8 * GetUiScale()), maximumCardWidth / 0.52f));
        fullCardSize.X = MathF.Min(fullCardSize.X, fullCardSize.Y * 0.56f);
        var cardSize = new Vector2(fullCardSize.X * horizontalScale, fullCardSize.Y);
        var rise = (1f - placementProgress) * 24 * GetUiScale();
        var cardStart = canvasStart + new Vector2(
            (canvasSize.X - cardSize.X) * 0.5f,
            ((canvasSize.Y - cardSize.Y) * 0.5f) + rise);
        var cardEnd = cardStart + cardSize;

        var revealFlash = card.Revealed ? MathF.Max(0f, 1f - MathF.Abs(revealProgress - 0.5f) * 4f) : 0f;
        var placementGlow = 1f - placementProgress;
        var glowAlpha = 0.18f + (revealFlash * 0.62f) + (placementGlow * 0.28f);
        var glowColor = showFront ? CyberdeckTheme.Palette.Cyan : CyberdeckTheme.Palette.Magenta;
        for (var layer = 3; layer >= 1; layer--)
        {
            var expansion = layer * 4 * GetUiScale();
            drawList.AddRect(
                cardStart - new Vector2(expansion),
                cardEnd + new Vector2(expansion),
                ImGui.GetColorU32(new Vector4(glowColor.X, glowColor.Y, glowColor.Z, glowAlpha / layer)),
                4f,
                ImDrawFlags.None,
                1f);
        }

        var texture = GetTarotCardTexture(card, showFront);
        drawList.AddRectFilled(cardStart, cardEnd, ImGui.GetColorU32(CyberdeckTheme.Palette.Background), 3f);
        if (texture is not null)
        {
            DrawTarotCardTexture(
                drawList,
                texture,
                cardStart,
                cardEnd,
                showFront && card.Reversed,
                Math.Clamp(placementProgress + 0.18f, 0f, 1f));
        }
        else
        {
            drawList.AddRect(cardStart, cardEnd, ImGui.GetColorU32(CyberdeckTheme.Palette.Cyan), 3f);
            const string unavailable = "ARCANA ART\nNOT YET LOADED";
            var textSize = ImGui.CalcTextSize(unavailable);
            drawList.AddText(
                cardStart + ((cardSize - textSize) * 0.5f),
                ImGui.GetColorU32(CyberdeckTheme.Palette.TextMuted),
                unavailable);
        }

        if (!config.ReduceMotion && (revealProgress < 1f || placementProgress < 1f))
        {
            var scanY = cardStart.Y + (cardSize.Y * ((float)ImGui.GetTime() * 2.7f % 1f));
            drawList.AddLine(
                new Vector2(cardStart.X - (8 * GetUiScale()), scanY),
                new Vector2(cardEnd.X + (8 * GetUiScale()), scanY),
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.8f)),
                2f);
        }
    }

    private IDalamudTextureWrap? GetTarotCardTexture(TarotReadingCard card, bool showFront)
    {
        if (!showFront)
            return GetTextureWrap("card_back.png");
        if (card.CardIndex is not int cardIndex || TarotDeck.CardImageName(cardIndex) is not { } imageName)
            return null;
        return GetTextureWrap(imageName);
    }

    private static void DrawTarotCardTexture(
        ImDrawListPtr drawList,
        IDalamudTextureWrap texture,
        Vector2 start,
        Vector2 end,
        bool reversed,
        float alpha,
        bool fillBounds = false)
    {
        var boundsSize = end - start;
        var tint = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha));
        if (fillBounds)
        {
            var (uvMin, uvMax) = GetCoverUv(texture, boundsSize);
            drawList.AddImage(texture.Handle, start, end, uvMin, uvMax, tint);
            return;
        }

        var scale = MathF.Min(boundsSize.X / texture.Width, boundsSize.Y / texture.Height);
        var imageSize = new Vector2(texture.Width * scale, texture.Height * scale);
        var imageStart = start + ((boundsSize - imageSize) * 0.5f);
        var imageEnd = imageStart + imageSize;
        if (!reversed)
        {
            drawList.AddImage(texture.Handle, imageStart, imageEnd, Vector2.Zero, Vector2.One, tint);
            return;
        }

        drawList.AddImageQuad(
            texture.Handle,
            imageStart,
            new Vector2(imageEnd.X, imageStart.Y),
            imageEnd,
            new Vector2(imageStart.X, imageEnd.Y),
            Vector2.One,
            new Vector2(0f, 1f),
            Vector2.Zero,
            new Vector2(1f, 0f),
            tint);
    }

    private bool IsTarotControlTransmissionPending()
        => tarotDebugSession.LastOutboundKind == TarotPacketKind.Reveal &&
           !string.Equals(
               tarotDebugLastSentPacket,
               tarotDebugSession.LastOutboundPacket,
               StringComparison.Ordinal);

    private bool IsTarotMessagePending()
        => !tarotDebugLoopback &&
           !string.IsNullOrWhiteSpace(tarotDebugSession.LastOutboundPacket) &&
           !string.Equals(
               tarotDebugLastSentPacket,
               tarotDebugSession.LastOutboundPacket,
               StringComparison.Ordinal);

    private bool IsCurrentTarotMessageQueued()
        => tarotTellQueue.Any(queued =>
            string.Equals(queued.SessionId, tarotDebugSession.SessionId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(queued.Packet, tarotDebugSession.LastOutboundPacket, StringComparison.Ordinal));

    private void DrawTarotHostRetry()
    {
        if (IsCurrentTarotMessageQueued())
        {
            CyberdeckWidgets.DrawStatusChip(
                "SENDING…",
                CyberdeckTheme.Palette.Amber,
                CyberdeckTheme.Palette.Text,
                GetUiScale());
            ImGui.Spacing();
            DrawMutedWrapped("Waiting for the game chat cooldown before sending the next update.");
            return;
        }

        CyberdeckWidgets.DrawStatusChip(
            "MESSAGE NOT SENT",
            CyberdeckTheme.Palette.Error,
            CyberdeckTheme.Palette.Text,
            GetUiScale());
        ImGui.Spacing();
        DrawMutedWrapped($"The latest change did not reach {tarotDebugSession.Partner}. Check the character name and try again.");
        ImGui.Spacing();
        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("TRY AGAIN", new Vector2(180 * GetUiScale(), 36 * GetUiScale())))
                SendCurrentTarotMessage(GetTarotSendSuccessMessage(), showRevealedCard: tarotDebugSession.LastOutboundKind == TarotPacketKind.Reveal, force: true);
        }
    }

    private string GetTarotSendSuccessMessage()
        => tarotDebugSession.LastOutboundKind switch
        {
            TarotPacketKind.Invite => $"Invitation sent to {tarotDebugSession.Partner}.",
            TarotPacketKind.Reveal => $"Card shown to {tarotDebugSession.Partner}.",
            TarotPacketKind.Join => "Connection accepted.",
            TarotPacketKind.Refuse => "Connection refused.",
            _ => "Message sent.",
        };

    private bool SendCurrentTarotMessage(string successMessage, bool showRevealedCard = false, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(tarotDebugSession.LastOutboundPacket))
        {
            tarotDebugFeedback = "NOTHING TO SEND";
            return false;
        }

        if (!force &&
            string.Equals(tarotDebugLastSentPacket, tarotDebugSession.LastOutboundPacket, StringComparison.Ordinal))
        {
            tarotDebugFeedback = successMessage;
            return true;
        }

        if (tarotDebugLoopback)
        {
            tarotDebugLastSentPacket = tarotDebugSession.LastOutboundPacket;
            tarotDebugFeedback = successMessage;
            if (showRevealedCard)
                ShowLatestTarotRevealLocally(tarotDebugSession.LastOutboundPacket);
            return true;
        }

        if (!PluginService.ClientState.IsLoggedIn)
        {
            tarotDebugFeedback = "MESSAGE NOT SENT // Log in before sending messages.";
            return false;
        }

        if (IsCurrentTarotMessageQueued())
            return true;

        tarotTellQueue.Enqueue(new TarotQueuedTell(
            tarotDebugSession.SessionId,
            tarotDebugSession.Partner,
            tarotDebugSession.LastOutboundMessage,
            tarotDebugSession.LastOutboundPacket,
            successMessage,
            showRevealedCard));
        tarotDebugFeedback = "WAITING FOR CHAT COOLDOWN";
        return true;
    }

    private void ShowLatestTarotRevealLocally(string packet)
    {
        if (TarotPacket.TryParse(packet, out var sentPacket, out _) &&
            sentPacket is not null && sentPacket.Fields.Count >= 1 &&
            int.TryParse(sentPacket.Fields[0], out var cardIndex))
        {
            var displaySlot = 0;
            if (tarotDebugSession.Role == TarotDebugRole.Host)
            {
                for (var index = tarotDebugSession.Cards.Count - 1; index >= 0; index--)
                {
                    if (tarotDebugSession.Cards[index].CardIndex != cardIndex)
                        continue;
                    displaySlot = tarotDebugSession.Cards[index].Slot;
                    break;
                }
            }

            tarotDebugCustomerFocusedSlot = displaySlot;
            tarotDebugCardRevealedAt[displaySlot] = Environment.TickCount64;
        }
        tarotCardViewerOpen = true;
        focusTarotCardViewer = true;
    }

    private void DrawTarotDebugOutbox()
    {
        DrawSettingsGroupHeader("OUTBOUND /TELL");
        ImGui.TextWrapped(tarotDebugSession.LastOutboundPacket);
        ImGui.Spacing();
        var tellCommand = tarotDebugSession.BuildTellCommand();
        var wasSent = string.Equals(
            tarotDebugLastSentPacket,
            tarotDebugSession.LastOutboundPacket,
            StringComparison.Ordinal);
        using (CyberdeckTheme.PushAccentButton())
        {
            ImGui.BeginDisabled(
                string.IsNullOrWhiteSpace(tellCommand) ||
                !PluginService.ClientState.IsLoggedIn ||
                IsCurrentTarotMessageQueued());
            if (ImGui.Button(
                    wasSent ? "RESEND LAST /TELL" : "SEND /TELL NOW",
                    new Vector2(ImGui.GetContentRegionAvail().X, 40 * GetUiScale())))
            {
                SendCurrentTarotMessage(
                    GetTarotSendSuccessMessage(),
                    showRevealedCard: tarotDebugSession.LastOutboundKind == TarotPacketKind.Reveal,
                    force: true);
            }
            ImGui.EndDisabled();
        }
        DrawHoverTooltip(PluginService.ClientState.IsLoggedIn
            ? "Submits this one tell through the normal game chat command path"
            : "Log in before sending tells");
        ImGui.Spacing();

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(120, (ImGui.GetContentRegionAvail().X - spacing) / 2);
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(tellCommand));
        if (ImGui.Button("COPY COMPLETE /TELL", new Vector2(width, 0)))
        {
            ImGui.SetClipboardText(tellCommand);
            tarotDebugFeedback = "COPIED // PASTE INTO GAME CHAT AND PRESS ENTER";
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("COPY PACKET ONLY", new Vector2(width, 0)))
        {
            ImGui.SetClipboardText(tarotDebugSession.LastOutboundPacket);
            tarotDebugFeedback = "PACKET COPIED";
        }
    }

    private void DrawTarotDebugAuditLog()
    {
        DrawSettingsGroupHeader("SESSION TRACE");
        if (ImGui.BeginChild("tarot_audit", new Vector2(0, 108 * GetUiScale()), true))
        {
            foreach (var line in tarotDebugSession.AuditLog)
                DrawTerminalLine(line);
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - (8 * GetUiScale()))
                ImGui.SetScrollHereY(1f);
        }
        ImGui.EndChild();
    }

    private void DrawTarotDebugTransportTools()
    {
        DrawSettingsGroupHeader("DEBUG TRANSPORT");
        DrawMutedWrapped("Inject a copied V3 packet, or simulate the next expected remote action without touching game chat.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##tarot_incoming_sender", "Incoming sender First Last@World", ref tarotDebugIncomingSender, 64);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextMultiline("##tarot_incoming_packet", ref tarotDebugIncomingPacket, 768, new Vector2(0, 58 * GetUiScale()));

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(120, (ImGui.GetContentRegionAvail().X - spacing) / 2);
        if (ImGui.Button("INJECT INCOMING PACKET", new Vector2(width, 0)))
        {
            if (!TarotPacket.TryParse(tarotDebugIncomingPacket, out var packet, out var error) || packet is null)
                tarotDebugFeedback = $"INJECT REJECTED // {error}";
            else
                ReceiveTarotPacket(tarotDebugIncomingSender, packet);
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!CanSimulateTarotPeer());
        if (ImGui.Button("SIMULATE NEXT PEER ACTION", new Vector2(width, 0)))
            SimulateNextTarotPeerAction();
        ImGui.EndDisabled();
    }

    private bool CanSimulateTarotPeer()
        => tarotDebugSession.Role switch
        {
            TarotDebugRole.Host => tarotDebugSession.Phase == TarotDebugPhase.WaitingForJoin,
            TarotDebugRole.Customer => tarotDebugSession.Phase is
                TarotDebugPhase.WaitingForInvite or TarotDebugPhase.Ready or TarotDebugPhase.Reading,
            _ => false,
        };

    private void SimulateNextTarotPeerAction()
    {
        var sender = !string.IsNullOrWhiteSpace(tarotDebugSession.Partner)
            ? tarotDebugSession.Partner
            : !string.IsNullOrWhiteSpace(tarotDebugIncomingSender)
                ? tarotDebugIncomingSender
                : "Debug Operator@Local";
        TarotPacket? packet = null;

        switch (tarotDebugSession.Role, tarotDebugSession.Phase)
        {
            case (TarotDebugRole.Customer, TarotDebugPhase.WaitingForInvite):
                tarotDebugSimulatedPeerSequence = 1;
                packet = new TarotPacket(TarotPacketKind.Invite, TarotDeck.CreateSessionId(), 1, []);
                break;
            case (TarotDebugRole.Host, TarotDebugPhase.WaitingForJoin):
                packet = new TarotPacket(TarotPacketKind.Join, tarotDebugSession.SessionId, 1, []);
                break;
            case (TarotDebugRole.Customer, TarotDebugPhase.Ready):
            case (TarotDebugRole.Customer, TarotDebugPhase.Reading):
            {
                tarotDebugSimulatedPeerSequence++;
                var cardIndex = Random.Shared.Next(TarotDeck.MajorArcanaCount);
                var reversed = Random.Shared.Next(2) == 1;
                packet = new TarotPacket(
                    TarotPacketKind.Reveal,
                    tarotDebugSession.SessionId,
                    tarotDebugSimulatedPeerSequence,
                    [cardIndex.ToString(), reversed ? "R" : "U"]);
                break;
            }
        }

        if (packet is null)
        {
            tarotDebugFeedback = "SIMULATION BLOCKED // NO VALID NEXT PEER ACTION";
            return;
        }

        tarotDebugIncomingSender = sender;
        tarotDebugIncomingPacket = packet.Serialize();
        ReceiveTarotPacket(sender, packet);
    }

    private void RunTarotAction(TarotAction action, string success)
    {
        if (action(out var error))
            tarotDebugFeedback = success;
        else
            tarotDebugFeedback = $"ACTION REJECTED // {error}";
    }

    private delegate bool TarotAction(out string error);
}
