using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;

namespace GridNrootUpdate;

internal enum TarotDebugRole
{
    None,
    Host,
    Customer,
}

internal enum TarotDebugPhase
{
    Idle,
    WaitingForInvite,
    InviteReceived,
    WaitingForJoin,
    Ready,
    Reading,
    Ended,
}

internal enum TarotPacketKind
{
    Invite,
    Join,
    Refuse,
    Reveal,
}

internal sealed record TarotPacket(
    TarotPacketKind Kind,
    string SessionId,
    int Sequence,
    IReadOnlyList<string> Fields)
{
    private const string Prefix = "GRID-TAROT/3";
    public const string Marker = "[GRID-TAROT/3|";

    public string Serialize()
    {
        var kind = Kind switch
        {
            TarotPacketKind.Invite => "INV",
            TarotPacketKind.Join => "JOIN",
            TarotPacketKind.Refuse => "REFUSE",
            TarotPacketKind.Reveal => "SHOW",
            _ => throw new ArgumentOutOfRangeException(),
        };

        var suffix = Fields.Count == 0 ? string.Empty : $"|{string.Join('|', Fields)}";
        return $"[{Prefix}|{kind}|{SessionId}|{Sequence}{suffix}]";
    }

    public static bool TryParse(string text, out TarotPacket? packet, out string error)
    {
        packet = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Packet is empty.";
            return false;
        }

        var start = text.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            error = "GRID-TAROT/3 marker was not found.";
            return false;
        }

        var end = text.IndexOf(']', start);
        if (end < 0)
        {
            error = "Packet terminator is missing.";
            return false;
        }

        var body = text[(start + 1)..end];
        var parts = body.Split('|', StringSplitOptions.None);
        if (parts.Length < 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            error = "Packet header is invalid.";
            return false;
        }

        var kind = parts[1] switch
        {
            "INV" => TarotPacketKind.Invite,
            "JOIN" => TarotPacketKind.Join,
            "REFUSE" => TarotPacketKind.Refuse,
            "SHOW" => TarotPacketKind.Reveal,
            _ => (TarotPacketKind?)null,
        };
        if (kind is null)
        {
            error = "Packet operation is unknown.";
            return false;
        }

        var sessionId = parts[2];
        if (sessionId.Length is < 4 or > 12 || sessionId.Any(ch => !char.IsAsciiLetterOrDigit(ch)))
        {
            error = "Session identifier is invalid.";
            return false;
        }

        if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 1)
        {
            error = "Packet sequence is invalid.";
            return false;
        }

        packet = new TarotPacket(kind.Value, sessionId, sequence, parts.Skip(4).ToArray());
        return true;
    }
}

internal readonly record struct TarotReadingCard(
    int Slot,
    int? CardIndex,
    bool Reversed,
    bool Revealed);

internal static class TarotDeck
{
    public const int MajorArcanaCount = 22;

    private static readonly string[] MajorArcana =
    [
        "The Fool", "The Magician", "The High Priestess", "The Empress", "The Emperor",
        "The Hierophant", "The Lovers", "The Chariot", "Strength", "The Hermit",
        "Wheel of Fortune", "Justice", "The Hanged Man", "Death", "Temperance",
        "The Devil", "The Tower", "The Star", "The Moon", "The Sun", "Judgement", "The World",
    ];

    private static readonly string[] MajorArcanaImages =
    [
        "fool.png", "magician.png", "high_priestess.png", "empress.png", "emperor.png",
        "hierophant.png", "lovers.png", "chariot.png", "strength.png", "hermit.png",
        "wheel_of_fortune.png", "justice.png", "hanged_man.png", "death.png", "temperance.png",
        "devil.png", "tower.png", "star.png", "moon.png", "sun.png", "judgement.png", "world.png",
    ];

    private static readonly string[] MajorArcanaUprightMeanings =
    [
        "A leap of faith and a new beginning.",
        "Skill, initiative, and making things happen.",
        "Intuition, mystery, and knowledge beneath the surface.",
        "Growth, care, creativity, and abundance.",
        "Structure, authority, and dependable leadership.",
        "Tradition, guidance, and established values.",
        "Connection, alignment, and an important choice.",
        "Direction, discipline, and determined progress.",
        "Courage, patience, and quiet self-control.",
        "Solitude, reflection, and inner guidance.",
        "A turning point, changing fortune, and new momentum.",
        "Truth, fairness, and consequences.",
        "Pause, surrender, and seeing things differently.",
        "An ending that clears the way for transformation.",
        "Balance, moderation, and patient integration.",
        "Attachment, temptation, or a limiting pattern.",
        "Sudden disruption that exposes the truth.",
        "Hope, renewal, and trust in what comes next.",
        "Uncertainty, dreams, and hidden influences.",
        "Joy, clarity, confidence, and success.",
        "Awakening, reckoning, and answering a calling.",
        "Completion, integration, and a successful transition.",
    ];

    private static readonly string[] MajorArcanaReversedMeanings =
    [
        "Hesitation, recklessness, or a poorly timed beginning.",
        "Unused ability, manipulation, or scattered intent.",
        "Ignored intuition, secrecy, or inner confusion.",
        "Creative blockage, dependence, or neglected care.",
        "Rigid control, instability, or misuse of authority.",
        "Restrictive convention or the need to find your own way.",
        "Disharmony, disconnection, or a difficult choice.",
        "Lost direction, aggression, or stalled progress.",
        "Self-doubt, impatience, or strength used poorly.",
        "Isolation, withdrawal, or avoiding needed reflection.",
        "Resistance to change or a repeating setback.",
        "Unfairness, denial, or avoiding accountability.",
        "Delay, resistance, or sacrifice without purpose.",
        "Fear of change or an ending being prolonged.",
        "Excess, imbalance, or competing priorities.",
        "Breaking a harmful attachment or denying its hold.",
        "Avoided disaster, delayed upheaval, or fear of change.",
        "Discouragement, disconnection, or fading confidence.",
        "Confusion, fear, or a hidden truth beginning to surface.",
        "Temporary gloom, overconfidence, or delayed success.",
        "Self-doubt, ignored lessons, or fear of a necessary decision.",
        "Incomplete closure, delay, or unfinished business.",
    ];

    private static readonly string[] Suits = ["Wands", "Cups", "Swords", "Pentacles"];
    private static readonly string[] SuitImageCodes = ["w", "c", "s", "p"];
    private static readonly string[] Ranks =
    [
        "Ace", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
        "Page", "Knight", "Queen", "King",
    ];
    private static readonly string[] RankImageCodes =
    [
        "a", "2", "3", "4", "5", "6", "7", "8", "9", "10", "p", "kn", "q", "k",
    ];

    private static readonly string[] SuitUprightThemes =
    [
        "action, creativity, and ambition",
        "emotion, relationships, and intuition",
        "thought, truth, and conflict",
        "work, resources, and stability",
    ];

    private static readonly string[] SuitReversedThemes =
    [
        "blocked energy, haste, or burnout",
        "emotional imbalance, distance, or denial",
        "confusion, avoidance, or unresolved conflict",
        "instability, neglect, or material strain",
    ];

    private static readonly string[] RankUprightMeanings =
    [
        "A new beginning", "A choice or need for balance", "Growth through cooperation",
        "Stability, structure, or a pause", "Disruption, conflict, or loss",
        "Adjustment, support, or progress", "A test of resolve or strategy",
        "Movement, skill, or sustained effort", "Near completion and self-reliance",
        "Completion and the weight of results", "Curiosity, news, or a first practical step",
        "Determined pursuit and momentum", "Mature, inward mastery",
        "Authority, command, and responsibility",
    ];

    private static readonly string[] RankReversedMeanings =
    [
        "A delayed or missed beginning", "Indecision or imbalance", "Poor coordination or delayed growth",
        "Stagnation or excessive control", "Unresolved conflict or loss",
        "Uneven exchange or delayed progress", "Doubt or poor planning",
        "Blocked movement or effort without purpose", "Exhaustion, isolation, or incomplete work",
        "A burden or an ending being resisted", "Immaturity or an unreliable message",
        "Recklessness or stalled momentum", "Misused or neglected inner strength",
        "Rigid control or poor leadership",
    ];

    private static readonly string[] Cards = BuildCardNames();

    public static int CardCount => Cards.Length;
    public static int SuitCount => Suits.Length;
    public static int RankCount => Ranks.Length;

    public static string CreateSessionId()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    public static string CardName(int cardIndex)
        => cardIndex >= 0 && cardIndex < Cards.Length ? Cards[cardIndex] : "Unknown card";

    public static string MajorArcanaName(int majorIndex)
        => majorIndex >= 0 && majorIndex < MajorArcana.Length ? MajorArcana[majorIndex] : "Unknown card";

    public static string SuitName(int suitIndex)
        => suitIndex >= 0 && suitIndex < Suits.Length ? Suits[suitIndex] : "Unknown suit";

    public static string RankName(int rankIndex)
        => rankIndex >= 0 && rankIndex < Ranks.Length ? Ranks[rankIndex] : "Unknown rank";

    public static int MinorCardIndex(int suitIndex, int rankIndex)
        => MajorArcana.Length + (suitIndex * Ranks.Length) + rankIndex;

    public static string CardKey(int cardIndex)
    {
        if (cardIndex < 0 || cardIndex >= Cards.Length)
            return "unknown";
        if (cardIndex < MajorArcana.Length)
            return $"major_{cardIndex:00}";

        var minor = cardIndex - MajorArcana.Length;
        var suit = Suits[minor / Ranks.Length].ToLowerInvariant();
        var rank = Ranks[minor % Ranks.Length].ToLowerInvariant();
        return $"{suit}_{rank}";
    }

    public static string? CardImageName(int cardIndex)
    {
        if (cardIndex < 0 || cardIndex >= Cards.Length)
            return null;
        if (cardIndex < MajorArcanaImages.Length)
            return MajorArcanaImages[cardIndex];

        var minor = cardIndex - MajorArcana.Length;
        var suitIndex = minor / Ranks.Length;
        var rankIndex = minor % Ranks.Length;
        return $"{RankImageCodes[rankIndex]}{SuitImageCodes[suitIndex]}.png";
    }

    public static string CardMeaning(int cardIndex, bool reversed)
    {
        if (cardIndex < 0 || cardIndex >= Cards.Length)
            return "No interpretation is available.";
        if (cardIndex < MajorArcana.Length)
            return reversed
                ? MajorArcanaReversedMeanings[cardIndex]
                : MajorArcanaUprightMeanings[cardIndex];

        var minor = cardIndex - MajorArcana.Length;
        var suitIndex = minor / Ranks.Length;
        var rankIndex = minor % Ranks.Length;
        var rankMeaning = reversed ? RankReversedMeanings[rankIndex] : RankUprightMeanings[rankIndex];
        var suitTheme = reversed ? SuitReversedThemes[suitIndex] : SuitUprightThemes[suitIndex];
        return $"{rankMeaning} around {suitTheme}.";
    }

    private static string[] BuildCardNames()
    {
        var cards = new List<string>(78);
        cards.AddRange(MajorArcana);
        foreach (var suit in Suits)
        foreach (var rank in Ranks)
            cards.Add($"{rank} of {suit}");
        return cards.ToArray();
    }
}

internal readonly record struct TarotReceiveResult(bool Accepted, bool OpenWindow, string Message);

internal sealed class TarotDebugSession
{
    public const int MaximumReadingCards = 10;

    private readonly List<string> auditLog = [];
    private readonly List<TarotReadingCard> cards = [];
    private int nextOutgoingSequence = 1;
    private int lastIncomingSequence;

    public TarotDebugRole Role { get; private set; }
    public TarotDebugPhase Phase { get; private set; }
    public string Partner { get; private set; } = string.Empty;
    public string SessionId { get; private set; } = string.Empty;
    public IReadOnlyList<TarotReadingCard> Cards => cards;
    public IReadOnlyList<string> AuditLog => auditLog;
    public string LastOutboundPacket { get; private set; } = string.Empty;
    public TarotPacketKind? LastOutboundKind { get; private set; }
    public string LastOutboundMessage => LastOutboundKind switch
    {
        TarotPacketKind.Join => "Connection accepted.",
        TarotPacketKind.Refuse => "Connection refused.",
        _ => string.Empty,
    };
    public string LastStatus { get; private set; } = "NO SESSION";

    public bool BeginHost(string partner, out string error)
    {
        error = ValidatePartner(partner);
        if (error.Length > 0)
            return false;

        Reset();
        Role = TarotDebugRole.Host;
        Phase = TarotDebugPhase.WaitingForJoin;
        Partner = NormalizePartnerIdentity(partner);
        SessionId = TarotDeck.CreateSessionId();
        QueueOutbound(TarotPacketKind.Invite);
        SetStatus("INVITATION READY // WAITING FOR CUSTOMER");
        return true;
    }

    public void BeginCustomerWaiting()
    {
        Reset();
        Role = TarotDebugRole.Customer;
        Phase = TarotDebugPhase.WaitingForInvite;
        SetStatus("LISTENING FOR TAROT INVITATION");
    }

    public bool AcceptInvite(out string error)
    {
        error = string.Empty;
        if (Role != TarotDebugRole.Customer || Phase != TarotDebugPhase.InviteReceived)
        {
            error = "No invitation is waiting for acceptance.";
            return false;
        }

        QueueOutbound(TarotPacketKind.Join);
        Phase = TarotDebugPhase.Ready;
        SetStatus("LINKED // WAITING FOR READER TO SHOW A CARD");
        return true;
    }

    public bool RefuseInvite(out string error)
    {
        error = string.Empty;
        if (Role != TarotDebugRole.Customer || Phase != TarotDebugPhase.InviteReceived)
        {
            error = "No invitation is waiting for a response.";
            return false;
        }

        QueueOutbound(TarotPacketKind.Refuse);
        Phase = TarotDebugPhase.Ended;
        SetStatus("CONNECTION REFUSED // SESSION CLOSED");
        return true;
    }

    public bool PlaceCard(int cardIndex, bool reversed, out string error)
    {
        error = string.Empty;
        if (Role != TarotDebugRole.Host || Phase is not (TarotDebugPhase.Ready or TarotDebugPhase.Reading))
        {
            error = "The customer has not joined this reading.";
            return false;
        }
        if (cardIndex < 0 || cardIndex >= TarotDeck.CardCount)
        {
            error = "Select a valid Tarot card first.";
            return false;
        }
        if (cards.Any(card => card.CardIndex == cardIndex))
        {
            error = "That card is already in this reading.";
            return false;
        }
        if (cards.Count >= MaximumReadingCards)
        {
            error = $"A debug reading is limited to {MaximumReadingCards} cards.";
            return false;
        }

        var slot = cards.Count;
        cards.Add(new TarotReadingCard(slot, cardIndex, reversed, false));
        Phase = TarotDebugPhase.Reading;
        SetStatus($"CARD {slot + 1:00} ADDED TO LOCAL SPREAD");
        return true;
    }

    public bool FlipCard(int slot, out string error)
    {
        error = string.Empty;
        if (Role != TarotDebugRole.Host || Phase != TarotDebugPhase.Reading)
        {
            error = "No active reading is available.";
            return false;
        }
        if (slot < 0 || slot >= cards.Count)
        {
            error = "Reading slot does not exist.";
            return false;
        }

        var card = cards[slot];
        if (card.Revealed || card.CardIndex is null)
        {
            error = "That card is already revealed.";
            return false;
        }

        cards[slot] = card with { Revealed = true };
        QueueOutbound(
            TarotPacketKind.Reveal,
            card.CardIndex.Value.ToString(CultureInfo.InvariantCulture),
            card.Reversed ? "R" : "U");
        SetStatus($"CARD {slot + 1:00} READY TO SHOW");
        return true;
    }

    public bool ResetReading(out string error)
    {
        error = string.Empty;
        if (Role != TarotDebugRole.Host || cards.Count == 0)
        {
            error = "There are no reading cards to clear.";
            return false;
        }

        cards.Clear();
        Phase = TarotDebugPhase.Ready;
        SetStatus("LOCAL SPREAD CLEARED");
        return true;
    }

    public bool RemoveCard(int slot, out string error)
    {
        error = string.Empty;
        if (Role != TarotDebugRole.Host || Phase != TarotDebugPhase.Reading)
        {
            error = "No active reading is available.";
            return false;
        }
        if (slot < 0 || slot >= cards.Count)
        {
            error = "Reading slot does not exist.";
            return false;
        }

        cards.RemoveAt(slot);
        ReindexCards();
        Phase = cards.Count == 0 ? TarotDebugPhase.Ready : TarotDebugPhase.Reading;
        SetStatus($"CARD {slot + 1:00} REMOVED FROM LOCAL SPREAD");
        return true;
    }

    public void EndSession()
    {
        Phase = TarotDebugPhase.Ended;
        SetStatus("SESSION CLOSED");
    }

    public TarotReceiveResult Receive(string sender, TarotPacket packet)
    {
        if (packet.Kind == TarotPacketKind.Invite &&
            (Role == TarotDebugRole.None || Phase is TarotDebugPhase.Idle or TarotDebugPhase.WaitingForInvite or TarotDebugPhase.Ended))
            return ReceiveInvite(sender, packet);

        if (Role == TarotDebugRole.None || SessionId.Length == 0)
            return Reject("No local Tarot session is active.");
        if (!string.Equals(SessionId, packet.SessionId, StringComparison.OrdinalIgnoreCase))
            return Reject($"Packet belongs to session {packet.SessionId}, not {SessionId}.");
        if (!PartnerMatches(sender, Partner))
            return Reject($"Packet sender '{sender}' does not match bound partner '{Partner}'.");
        if (packet.Sequence <= lastIncomingSequence)
            return Reject($"Duplicate or stale packet {packet.Sequence:00} ignored.");

        var result = Role == TarotDebugRole.Host
            ? ReceiveAsHost(packet)
            : ReceiveAsCustomer(packet);

        if (result.Accepted)
        {
            lastIncomingSequence = packet.Sequence;
            AddAudit($"RX {packet.Sequence:00} {packet.Kind.ToString().ToUpperInvariant()} // {sender}");
        }

        return result;
    }

    public string BuildTellCommand()
        => !HasWorldAddress(Partner) || string.IsNullOrWhiteSpace(LastOutboundPacket)
            ? string.Empty
            : $"/tell {Partner} {(LastOutboundMessage.Length > 0 ? $"{LastOutboundMessage} " : string.Empty)}{LastOutboundPacket}";

    public void Reset()
    {
        Role = TarotDebugRole.None;
        Phase = TarotDebugPhase.Idle;
        Partner = string.Empty;
        SessionId = string.Empty;
        cards.Clear();
        LastOutboundPacket = string.Empty;
        LastOutboundKind = null;
        LastStatus = "NO SESSION";
        nextOutgoingSequence = 1;
        lastIncomingSequence = 0;
        auditLog.Clear();
    }

    private TarotReceiveResult ReceiveInvite(string sender, TarotPacket packet)
    {
        if (string.IsNullOrWhiteSpace(sender))
            return Reject("Invitation sender is missing.");
        if (packet.Fields.Count != 0)
            return Reject("Invitation payload is invalid.");

        Reset();
        Role = TarotDebugRole.Customer;
        Phase = TarotDebugPhase.InviteReceived;
        Partner = NormalizePartnerIdentity(sender);
        SessionId = packet.SessionId;
        lastIncomingSequence = packet.Sequence;
        SetStatus("INCOMING TAROT READING INVITATION");
        AddAudit($"RX {packet.Sequence:00} INVITE // {sender}");
        return new TarotReceiveResult(true, true, "Tarot reading invitation received.");
    }

    private TarotReceiveResult ReceiveAsHost(TarotPacket packet)
    {
        switch (packet.Kind)
        {
            case TarotPacketKind.Join when Phase == TarotDebugPhase.WaitingForJoin && packet.Fields.Count == 0:
                Phase = TarotDebugPhase.Ready;
                SetStatus("CUSTOMER LINKED // READER MAY PLACE A CARD");
                return Accept("Customer joined the reading.");
            case TarotPacketKind.Refuse when Phase == TarotDebugPhase.WaitingForJoin && packet.Fields.Count == 0:
                Phase = TarotDebugPhase.Ended;
                SetStatus("CUSTOMER REFUSED CONNECTION // LOCAL SESSION CLOSED");
                return Accept("Customer refused the reading connection.");
            default:
                return Reject($"{packet.Kind} is not valid while host is in {Phase}.");
        }
    }

    private TarotReceiveResult ReceiveAsCustomer(TarotPacket packet)
    {
        switch (packet.Kind)
        {
            case TarotPacketKind.Reveal when Phase is TarotDebugPhase.Ready or TarotDebugPhase.Reading:
                return ReceiveReveal(packet.Fields);
            default:
                return Reject($"{packet.Kind} is not valid while customer is in {Phase}.");
        }
    }

    private TarotReceiveResult ReceiveReveal(IReadOnlyList<string> fields)
    {
        if (fields.Count != 2 ||
            !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var cardIndex) ||
            cardIndex < 0 || cardIndex >= TarotDeck.CardCount ||
            fields[1] is not ("U" or "R"))
        {
            return Reject("Card reveal payload is invalid.");
        }

        cards.Clear();
        cards.Add(new TarotReadingCard(0, cardIndex, fields[1] == "R", true));
        Phase = TarotDebugPhase.Reading;
        SetStatus($"CARD REVEALED // {TarotDeck.CardName(cardIndex).ToUpperInvariant()}");
        return Accept("Card revealed.");
    }

    private void ReindexCards()
    {
        for (var index = 0; index < cards.Count; index++)
            cards[index] = cards[index] with { Slot = index };
    }

    private void QueueOutbound(TarotPacketKind kind, params string[] fields)
    {
        var packet = new TarotPacket(kind, SessionId, nextOutgoingSequence++, fields);
        LastOutboundPacket = packet.Serialize();
        LastOutboundKind = kind;
        AddAudit($"TX {packet.Sequence:00} {kind.ToString().ToUpperInvariant()} // READY TO SEND");
    }

    private void SetStatus(string value)
    {
        LastStatus = value;
        AddAudit(value);
    }

    private void AddAudit(string value)
    {
        auditLog.Add(value);
        if (auditLog.Count > 18)
            auditLog.RemoveAt(0);
    }

    private static string ValidatePartner(string partner)
    {
        if (string.IsNullOrWhiteSpace(partner))
            return "Enter the customer character name, preferably as Name@World.";
        if (partner.Length > 64 || partner.IndexOfAny(['\r', '\n', '|', '[', ']']) >= 0)
            return "The customer name contains unsupported characters.";
        if (!HasWorldAddress(partner))
            return "Add the home World after the character name: Name@World.";
        return string.Empty;
    }

    private static bool HasWorldAddress(string value)
    {
        var separator = value.LastIndexOf('@');
        return separator > 0 && separator < value.Length - 1;
    }

    private static bool PartnerMatches(string sender, string partner)
    {
        var left = NormalizePartnerIdentity(sender);
        var right = NormalizePartnerIdentity(partner);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        var leftAt = left.IndexOf('@');
        var rightAt = right.IndexOf('@');
        if (leftAt >= 0) left = left[..leftAt];
        if (rightAt >= 0) right = right[..rightAt];
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePartnerIdentity(string value)
    {
        var normalized = value.Trim();
        var nameStart = 0;
        while (nameStart < normalized.Length && !char.IsLetter(normalized[nameStart]))
            nameStart++;
        return nameStart < normalized.Length ? normalized[nameStart..].Trim() : normalized;
    }

    private static TarotReceiveResult Accept(string message)
        => new(true, true, message);

    private static TarotReceiveResult Reject(string message, bool openWindow = false)
        => new(false, openWindow, message);
}
