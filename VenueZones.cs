using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using GameControl = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using RenderDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace GridNrootUpdate;

/// <summary>What a zone opens when it is activated.</summary>
internal enum VenueZoneKind
{
    VendingMachine,
    Arcade,
}

/// <summary>The icon drawn in a zone's marker.</summary>
internal enum VenueZoneGlyph
{
    Vending,
    Arcade,
}

/// <summary>
/// One activation zone: what it opens and how it is drawn.
/// </summary>
/// <param name="Id">Zones with the same id count as one place, for closing the window when you walk away.</param>
/// <param name="Radius">The ring on the floor, in yalms.</param>
/// <param name="Compact">
/// For zones standing close together, like a row of cabinets: the marker
/// takes clicks only over its own icon and shows its text only while
/// hovered, so neighbours do not pile labels on top of each other.
/// </param>
/// <param name="Cabinet">For an arcade cabinet, its number in the row, counted from the left as you face it.</param>
internal sealed record VenueZoneDefinition(
    string Id,
    VenueZoneKind Kind,
    string Label,
    VenueZoneGlyph Glyph,
    float Radius,
    bool Compact,
    int Cabinet = 0);

/// <summary>
/// A zone pinned to a piece of housing furniture.
///
/// Found rather than placed: the deck looks through the room's furniture for
/// the item and puts the zone on the floor in front of it, so moving the
/// furniture moves the zone and every guest sees the same thing.
/// </summary>
/// <param name="FurnitureRowId">The item's row in the HousingFurniture sheet.</param>
/// <param name="FurnitureItemId">The same item's row in the Item sheet.</param>
/// <param name="FrontOffset">Yalms from the item's origin to the zone centre, along the way it faces.</param>
/// <param name="KeepHighest">
/// How many of the item get a zone, counted from the highest placed down;
/// zero for every one. The venue has more of some machines than it wants
/// zones on, and a rule about where they stand, unlike a list of positions,
/// still holds when the furniture is rearranged.
/// </param>
internal sealed record FurnitureZoneAnchor(VenueZoneDefinition Zone, uint FurnitureRowId, uint FurnitureItemId, float FrontOffset, int KeepHighest = 0);

/// <summary>
/// A zone on a prop the venue mod draws as part of a mannequin's gear.
///
/// Such a prop is a model in the mannequin's own space, so its pieces are
/// placed by their coordinates in that model and carried into the world by
/// the mannequin's draw transform: wherever the mannequin stands and faces,
/// and at whatever size, the zones land on the prop.
/// </summary>
/// <param name="EarringModelId">The accessory model (aNNNN) the prop replaces, any variant.</param>
/// <param name="Pad">Centre of the floor ring, in the prop model's coordinates.</param>
/// <param name="Marker">Where the marker floats, in the prop model's coordinates.</param>
internal sealed record MannequinZoneAnchor(VenueZoneDefinition Zone, ushort EarringModelId, Vector3 Pad, Vector3 Marker);

/// <summary>A zone found in the current room, in world coordinates.</summary>
internal readonly record struct ResolvedVenueZone(VenueZoneDefinition Definition, Vector3 Center, Vector3 Marker, bool ShowLeader);

/// <summary>
/// Draws the venue's activation zones and opens what they lead to.
///
/// Each zone is a ring on the floor with a holographic marker over it. The
/// marker is the only thing that takes the mouse — a small invisible window
/// over it — so the camera, the game's own targeting and everything else in
/// the room behave exactly as they would without the deck. Clicking the
/// marker checks how far away the player is: close enough opens the window,
/// too far says so on the marker and does nothing else.
///
/// The overlay runs every frame, and the venue has several machines, so its
/// cost is bounded however many the room holds: only the nearest few zones
/// are drawn, only the near ones are animated, points are projected with one
/// camera matrix read per frame, and the wall test — a collision raycast, the
/// one expensive call here — runs for one zone a frame at most.
/// </summary>
internal sealed class VenueZones
{
    // Vending machine activation zones are disabled for now.
    internal static readonly FurnitureZoneAnchor[] FurnitureZones = [];

    // The arcade row the venue mod draws in place of a mannequin's earrings
    // (a0001, Copper Earrings, any variant). Measured from c0901a0001_ear.mdl,
    // whose only bone is n_root, so the row is rigid in the mannequin's space:
    // six cabinets 1.104 apart along X, standing on Y 15.903 and 1.87 tall,
    // their control panels facing +Z out to Z 2.057. What each cabinet runs
    // is up to the arcade module (see ArcadeModule); a cabinet it has no game
    // for gets no zone. If the model is rebuilt, these are the numbers to
    // measure again.
    private const ushort ArcadeEarringModelId = 1;
    private const float ArcadeFloorY = 15.903f;
    private const float ArcadeTopY = 17.769f;
    private const float ArcadeFrontZ = 2.057f;
    private const float ArcadeMiddleZ = 1.65f;
    private const float ArcadePadDepth = 0.42f;
    private const float ArcadeMarkerLift = 0.3f;
    private static readonly float[] ArcadeCabinetX = [-16.159f, -15.055f, -13.952f, -12.848f, -11.745f, -10.641f];

    /// <summary>One zone per arcade cabinet, on the floor in front of its controls.</summary>
    internal static readonly MannequinZoneAnchor[] MannequinZones = BuildArcadeZones();

    /// <summary>HousingFurniture rows start here; some furniture records store the row without it.</summary>
    private const uint HousingFurnitureRowBase = 0x30000;

    /// <summary>How close, in yalms from the zone centre, a click has to be made from.</summary>
    internal const float ActivationRange = 3.0f;

    /// <summary>Walking this far from the zone closes what it opened, like any interaction window in the game.</summary>
    private const float AutoCloseRange = 6.0f;

    /// <summary>A zone further than this is not drawn at all.</summary>
    private const float DrawDistance = 20.0f;

    /// <summary>Closer than this a zone is drawn in full; further out, only its ring and marker.</summary>
    private const float DetailDistance = 8.0f;

    /// <summary>At most this many zones are drawn, the nearest ones.</summary>
    private const int MaxDrawnZones = 10;

    /// <summary>A zone's wall test is not redone more often than this.</summary>
    private const long OcclusionIntervalMs = 66;

    /// <summary>The click window appears this many pixels before the cursor reaches the marker, so it is there when it arrives.</summary>
    private const float HitMargin = 12f;

    /// <summary>More height difference than this is another floor of the house.</summary>
    private const float MaxHeightGap = 3.5f;

    private const float FurnitureMarkerHeight = 1.55f;
    private const int RingSegments = 48;
    private const int ArcSteps = 8;
    private const long ScanIntervalMs = 1000;
    private const long TooFarFlashMs = 1800;

    private readonly PluginConfig config;
    private readonly Func<bool> isAtVenue;
    private readonly Func<IGameObject, bool> isMannequin;
    private readonly Func<int, string?> cabinetTitle;
    private readonly Action<VenueZoneDefinition> open;
    private readonly Action<VenueZoneKind> close;
    private readonly Func<VenueZoneKind, bool> isOpen;
    private readonly List<ResolvedVenueZone> zones = [];
    private readonly List<string> mannequinNotes = [];
    private readonly List<ZoneCandidate> candidates = [];
    private readonly List<ZoneOnScreen> onScreen = [];
    private readonly List<int> forgotten = [];
    private readonly List<VenueZoneKind> closing = [];
    private readonly Dictionary<int, float> visibility = new();
    private readonly Dictionary<int, OcclusionCheck> occlusion = new();
    private readonly Vector2[] ringPoints = new Vector2[RingSegments];
    private readonly bool[] ringInFront = new bool[RingSegments];

    /// <summary>
    /// Which zone opened each window, so walking away from it closes that
    /// window, and not one opened by command or from another zone.
    /// </summary>
    private readonly Dictionary<VenueZoneKind, string> openedBy = new();

    private long nextScanAt;
    private int tooFarZone = -1;
    private long tooFarUntil;
    private float tooFarDistance;

    /// <summary>What the cursor was over last frame, for the debug description: why a click would or would not land.</summary>
    private string pointerNote = "not on a marker";

    /// <summary>The game window under the cursor this frame, if any.</summary>
    private string? frameGameUi;

    private Matrix4x4 viewProjection;
    private Vector2 screenOrigin;
    private Vector2 screenSize;

    /// <summary>Whether this frame's points are projected with the matrix read at its start, rather than asked of the game one by one.</summary>
    private bool ownProjection;
    private bool projectionChecked;
    private string projectionMode = "not used yet";

    private double drawMsAverage;
    private double drawMsPeak;

    public VenueZones(
        PluginConfig config,
        Func<bool> isAtVenue,
        Func<IGameObject, bool> isMannequin,
        Func<int, string?> cabinetTitle,
        Action<VenueZoneDefinition> open,
        Action<VenueZoneKind> close,
        Func<VenueZoneKind, bool> isOpen)
    {
        this.config = config;
        this.isAtVenue = isAtVenue;
        this.isMannequin = isMannequin;
        this.cabinetTitle = cabinetTitle;
        this.open = open;
        this.close = close;
        this.isOpen = isOpen;
    }

    /// <summary>Shows zones in any house, for trying them out somewhere other than the venue.</summary>
    public bool ShowAnywhere { get; set; }

    public void Draw()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            DrawZones();
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            drawMsAverage = drawMsAverage <= 0 ? elapsed : (drawMsAverage * 0.95) + (elapsed * 0.05);
            drawMsPeak = Math.Max(drawMsPeak, elapsed);
        }
    }

    /// <summary>A line for chat describing what the zones can see, for setting them up.</summary>
    public unsafe string Describe()
    {
        var player = PluginService.Objects.LocalPlayer;
        var housing = HousingManager.Instance();
        var inside = housing is not null && housing->IndoorTerritory is not null;
        var parts = new List<string>
        {
            $"enabled {config.VenueZonesEnabled}",
            $"at venue {isAtVenue()}",
            $"anywhere {ShowAnywhere}",
            $"indoors {inside}",
            $"zones {zones.Count}, drawn {onScreen.Count}",
            $"cursor {pointerNote}",
            $"cost {drawMsAverage:0.000} ms a frame, peak {drawMsPeak:0.000} ms",
            $"projection {projectionMode}",
        };
        drawMsPeak = 0;
        if (player is not null)
            parts.Add($"you at {Format(player.Position)}");
        parts.AddRange(mannequinNotes);
        foreach (var zone in zones)
        {
            var distance = player is null ? float.NaN : HorizontalDistance(player.Position, zone.Center);
            parts.Add($"{zone.Definition.Id} at {Format(zone.Center)} ({distance:0.0}y)");
        }

        return string.Join(" | ", parts);
    }

    private static MannequinZoneAnchor[] BuildArcadeZones()
    {
        var anchors = new MannequinZoneAnchor[ArcadeCabinetX.Length];
        for (var index = 0; index < anchors.Length; index++)
        {
            var cabinet = index + 1;
            var zone = new VenueZoneDefinition(
                $"arcade-{cabinet}",
                VenueZoneKind.Arcade,
                "ARCADE",
                VenueZoneGlyph.Arcade,
                Radius: 0.3f,
                Compact: true,
                cabinet);
            var x = ArcadeCabinetX[index];
            anchors[index] = new MannequinZoneAnchor(
                zone,
                ArcadeEarringModelId,
                Pad: new Vector3(x, ArcadeFloorY, ArcadeFrontZ + ArcadePadDepth),
                Marker: new Vector3(x, ArcadeTopY + ArcadeMarkerLift, ArcadeMiddleZ));
        }

        return anchors;
    }

    private void DrawZones()
    {
        ForgetClosedWindows();

        if (!config.VenueZonesEnabled || !CanShowZones())
        {
            Reset();
            return;
        }

        var player = PluginService.Objects.LocalPlayer;
        if (player is null)
        {
            Reset();
            return;
        }

        var now = Environment.TickCount64;
        if (now >= nextScanAt)
        {
            nextScanAt = now + ScanIntervalMs;
            Scan();
        }

        var playerPosition = player.Position;
        candidates.Clear();
        for (var index = 0; index < zones.Count; index++)
        {
            var zone = zones[index];
            if (MathF.Abs(playerPosition.Y - zone.Center.Y) > MaxHeightGap)
                continue;

            var distance = HorizontalDistance(playerPosition, zone.Center);
            if (distance <= DrawDistance)
                candidates.Add(new ZoneCandidate(index, distance));
        }

        // Done before anything is drawn, because drawing is where a click
        // opens a window: the zone that opened it is then counted from the
        // next frame on. Measured mid-draw, the frame of the click could find
        // nothing open yet and close the window before it was ever shown.
        CloseWindowsWalkedAwayFrom(playerPosition);

        pointerNote = "not on a marker";
        candidates.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        if (candidates.Count > MaxDrawnZones)
            candidates.RemoveRange(MaxDrawnZones, candidates.Count - MaxDrawnZones);

        var seconds = config.ReduceMotion ? 0f : (now % 3_600_000) / 1000f;
        var bob = config.ReduceMotion ? 0f : MathF.Sin(seconds * 2.1f) * 0.05f;
        CaptureProjection();
        onScreen.Clear();
        foreach (var candidate in candidates)
        {
            var markerWorld = zones[candidate.Index].Marker + new Vector3(0f, bob, 0f);
            var inFront = PluginService.GameGui.WorldToScreen(markerWorld, out var marker, out var inView);
            if (inFront && ownProjection && !projectionChecked)
                CheckProjection(markerWorld, marker);
            if (inFront && inView)
                onScreen.Add(new ZoneOnScreen(candidate.Index, candidate.Distance, markerWorld, marker));
        }

        ForgetZonesNotOnScreen();
        if (onScreen.Count == 0)
            return;

        projectionMode = ownProjection ? "one matrix a frame" : "per point (matrix disagreed with the game)";
        var hasCamera = TryGetCameraPosition(out var cameraPosition);
        if (hasCamera)
            RefreshOcclusion(cameraPosition, now);

        frameGameUi = GameUiUnderMouse();

        // Furthest first, so nearer zones draw over the ones behind them.
        for (var position = onScreen.Count - 1; position >= 0; position--)
            DrawZone(onScreen[position], hasCamera, cameraPosition, now, seconds);
    }

    /// <summary>Stops tracking windows the player closed themselves.</summary>
    private void ForgetClosedWindows()
    {
        closing.Clear();
        foreach (var kind in openedBy.Keys)
        {
            if (!isOpen(kind))
                closing.Add(kind);
        }

        foreach (var kind in closing)
            openedBy.Remove(kind);
    }

    private void CloseWindowsWalkedAwayFrom(Vector3 playerPosition)
    {
        closing.Clear();
        foreach (var (kind, zoneId) in openedBy)
        {
            var nearest = float.MaxValue;
            foreach (var zone in zones)
            {
                if (zone.Definition.Id == zoneId && MathF.Abs(playerPosition.Y - zone.Center.Y) <= MaxHeightGap)
                    nearest = MathF.Min(nearest, HorizontalDistance(playerPosition, zone.Center));
            }

            if (nearest > AutoCloseRange)
                closing.Add(kind);
        }

        foreach (var kind in closing)
        {
            close(kind);
            openedBy.Remove(kind);
        }
    }

    private void Reset()
    {
        zones.Clear();
        candidates.Clear();
        onScreen.Clear();
        visibility.Clear();
        occlusion.Clear();
        nextScanAt = 0;
        tooFarZone = -1;
        pointerNote = "not on a marker";
        foreach (var kind in openedBy.Keys)
            close(kind);
        openedBy.Clear();
    }

    private bool CanShowZones()
    {
        if (!PluginService.ClientState.IsLoggedIn ||
            PluginService.ClientState.IsGPosing ||
            PluginService.GameGui.GameUiHidden)
            return false;

        try
        {
            if (PluginService.Condition[ConditionFlag.BetweenAreas] ||
                PluginService.Condition[ConditionFlag.BetweenAreas51] ||
                PluginService.Condition[ConditionFlag.WatchingCutscene] ||
                PluginService.Condition[ConditionFlag.WatchingCutscene78] ||
                PluginService.Condition[ConditionFlag.OccupiedInCutSceneEvent])
                return false;
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Could not read conditions; venue zones hidden.");
            return false;
        }

        return IsIndoors() && (ShowAnywhere || isAtVenue());
    }

    private static unsafe bool IsIndoors()
    {
        var housing = HousingManager.Instance();
        return housing is not null && housing->IndoorTerritory is not null;
    }

    /// <summary>
    /// Finds the zones in the room: furniture first, then props on mannequins.
    ///
    /// Positions are copied out rather than the objects kept: the room can
    /// unload between frames, and a held pointer would outlive it.
    /// </summary>
    private void Scan()
    {
        zones.Clear();
        mannequinNotes.Clear();
        ScanFurniture();
        ScanMannequins();

        foreach (var stale in visibility.Keys.Where(index => index >= zones.Count).ToList())
            visibility.Remove(stale);
        foreach (var stale in occlusion.Keys.Where(index => index >= zones.Count).ToList())
            occlusion.Remove(stale);
    }

    private unsafe void ScanFurniture()
    {
        var found = new List<(FurnitureZoneAnchor Anchor, ResolvedVenueZone Zone)>();
        try
        {
            var housing = HousingManager.Instance();
            if (housing is null || housing->IndoorTerritory is null)
                return;

            var furniture = &housing->IndoorTerritory->FurnitureManager;
            var memory = furniture->FurnitureMemory;
            foreach (var pointer in furniture->ObjectManager.ObjectArray.Objects)
            {
                var gameObject = pointer.Value;
                if (gameObject is null)
                    continue;

                var housingObject = (HousingObject*)gameObject;
                var furnitureIndex = housingObject->HousingFurnitureIndex;
                var recordId = furnitureIndex >= 0 && furnitureIndex < memory.Length
                    ? memory[furnitureIndex].Id
                    : 0;

                foreach (var anchor in FurnitureZones)
                {
                    if (!Matches(anchor, gameObject->BaseId) && !Matches(anchor, recordId))
                        continue;

                    Vector3 position = gameObject->Position;
                    var rotation = gameObject->Rotation;
                    var facing = new Vector3(MathF.Sin(rotation), 0f, MathF.Cos(rotation));
                    var center = position + (facing * anchor.FrontOffset);
                    found.Add((anchor, new ResolvedVenueZone(anchor.Zone, center, center + new Vector3(0f, FurnitureMarkerHeight, 0f), ShowLeader: true)));
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Could not read the room's furniture for venue zones.");
            return;
        }

        foreach (var anchor in FurnitureZones)
        {
            var matches = found.Where(match => match.Anchor == anchor).Select(match => match.Zone);
            if (anchor.KeepHighest > 0)
                matches = matches.OrderByDescending(zone => zone.Center.Y).Take(anchor.KeepHighest);
            zones.AddRange(matches);
        }
    }

    /// <summary>
    /// Finds props the venue mod draws on mannequins, by the gear they replace.
    ///
    /// Placed with the mannequin's draw transform rather than its object
    /// position: the draw transform is what the prop is actually rendered
    /// with, scale included, so a mannequin set taller or shorter still puts
    /// the zones on the cabinets.
    /// </summary>
    private unsafe void ScanMannequins()
    {
        try
        {
            for (var index = 0; index < PluginService.Objects.Length; index++)
            {
                var gameObject = PluginService.Objects[index];
                if (gameObject is null || !IsCharacterLike(gameObject) || !isMannequin(gameObject))
                    continue;

                var character = (NativeCharacter*)gameObject.Address;
                if (character is null)
                    continue;

                var ears = character->DrawData.Equipment(DrawDataContainer.EquipmentSlot.Ears);
                var drawObject = ((GameObject*)character)->DrawObject;
                if (drawObject is null)
                {
                    mannequinNotes.Add($"mannequin #{gameObject.ObjectIndex} ears a{ears.Id:0000} v{ears.Variant}, not drawn");
                    continue;
                }

                var transform = (SceneObject*)drawObject;
                Vector3 position = transform->Position;
                Quaternion rotation = transform->Rotation;
                Vector3 scale = transform->Scale;
                mannequinNotes.Add($"mannequin #{gameObject.ObjectIndex} ears a{ears.Id:0000} v{ears.Variant} at {Format(position)} scale {scale.X:0.000}");

                foreach (var anchor in MannequinZones)
                {
                    if (anchor.EarringModelId != ears.Id)
                        continue;

                    var title = anchor.Zone.Kind == VenueZoneKind.Arcade ? cabinetTitle(anchor.Zone.Cabinet) : anchor.Zone.Label;
                    if (title is null)
                        continue;

                    zones.Add(new ResolvedVenueZone(
                        anchor.Zone with { Label = title },
                        ToWorld(anchor.Pad, position, rotation, scale),
                        ToWorld(anchor.Marker, position, rotation, scale),
                        ShowLeader: false));
                }
            }
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Could not read the room's mannequins for venue zones.");
        }
    }

    /// <summary>
    /// Whether the object is laid out as a character, so its gear can be read.
    /// The same kinds the deck reads a mannequin's title from.
    /// </summary>
    private static bool IsCharacterLike(IGameObject gameObject)
        => gameObject is ICharacter ||
           gameObject.ObjectKind == DalamudObjectKind.EventNpc ||
           gameObject.ObjectKind == DalamudObjectKind.BattleNpc;

    private static Vector3 ToWorld(Vector3 local, Vector3 position, Quaternion rotation, Vector3 scale)
        => position + Vector3.Transform(local * scale, rotation);

    private static bool Matches(FurnitureZoneAnchor anchor, uint key)
        => key != 0 &&
           (key == anchor.FurnitureRowId ||
            key + HousingFurnitureRowBase == anchor.FurnitureRowId ||
            key == anchor.FurnitureItemId);

    /// <summary>
    /// Drops what is remembered about zones not drawn this frame, so one that
    /// comes back fades in again and is tested for walls before it shows.
    /// </summary>
    private void ForgetZonesNotOnScreen()
    {
        forgotten.Clear();
        foreach (var index in visibility.Keys)
        {
            if (!IsOnScreen(index))
                forgotten.Add(index);
        }

        foreach (var index in occlusion.Keys)
        {
            if (!IsOnScreen(index))
                forgotten.Add(index);
        }

        foreach (var index in forgotten)
        {
            visibility.Remove(index);
            occlusion.Remove(index);
        }
    }

    private bool IsOnScreen(int index)
    {
        foreach (var zone in onScreen)
        {
            if (zone.Index == index)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Runs the wall test for the one zone tested longest ago.
    ///
    /// A test is a collision raycast through the room. One per zone every
    /// frame is what made a room with several machines lag; one a frame in
    /// all, with the answer kept until the zone's turn comes round again, is
    /// still more than quick enough for a marker that fades in and out.
    /// </summary>
    private void RefreshOcclusion(Vector3 cameraPosition, long now)
    {
        var chosen = -1;
        var oldest = long.MaxValue;
        for (var position = 0; position < onScreen.Count; position++)
        {
            var checkedAt = occlusion.TryGetValue(onScreen[position].Index, out var previous) ? previous.CheckedAt : 0;
            if (checkedAt < oldest)
            {
                oldest = checkedAt;
                chosen = position;
            }
        }

        if (chosen < 0 || now - oldest < OcclusionIntervalMs)
            return;

        var zone = onScreen[chosen];
        var distance = Vector3.Distance(cameraPosition, zone.MarkerWorld);
        occlusion[zone.Index] = new OcclusionCheck(IsOccluded(zone.Marker, cameraPosition, distance), now);
    }

    private void DrawZone(ZoneOnScreen entry, bool hasCamera, Vector3 cameraPosition, long now, float seconds)
    {
        var index = entry.Index;
        var zone = zones[index];
        var definition = zone.Definition;
        var distance = entry.Distance;
        var marker = entry.Marker;
        var inRange = distance <= ActivationRange;
        var detailed = distance <= DetailDistance && !definition.Compact;

        // A zone not tested yet counts as behind a wall, so it never flashes through one.
        var occluded = hasCamera && (!occlusion.TryGetValue(index, out var check) || check.Occluded);
        var cameraDistance = hasCamera ? Vector3.Distance(cameraPosition, entry.MarkerWorld) : distance;
        var alpha = visibility.GetValueOrDefault(index, 0f);
        var step = ImGui.GetIO().DeltaTime * 6f;
        alpha = occluded ? MathF.Max(0f, alpha - step) : MathF.Min(1f, alpha + step);
        visibility[index] = alpha;
        if (alpha <= 0.01f)
            return;

        var uiScale = CyberdeckTheme.ResolveUiScale(config.UiScale);
        var size = (definition.Compact ? 13f : 17f) * uiScale * Math.Clamp(6f / MathF.Max(cameraDistance, 0.5f), 0.8f, 1.3f);
        var tooFar = tooFarZone == index && now < tooFarUntil;
        var label = definition.Label;
        var status = inRange
            ? definition.Kind == VenueZoneKind.Arcade ? "CLICK TO PLAY" : "CLICK TO ACCESS"
            : tooFar
                ? $"MOVE CLOSER // {tooFarDistance:0.0}y"
                : "STEP INTO THE ZONE";

        var labelSize = ImGui.CalcTextSize(label);
        var statusSize = ImGui.CalcTextSize(status);
        Vector2 hitMin;
        Vector2 hitMax;
        if (definition.Compact)
        {
            // Just the icon: the neighbouring cabinet's marker is close by.
            var reach = new Vector2(size * 1.25f);
            hitMin = marker - reach;
            hitMax = marker + reach;
        }
        else
        {
            var hitWidth = MathF.Max(size * 2.6f, MathF.Max(labelSize.X, statusSize.X) + (12f * uiScale));
            hitMin = new Vector2(marker.X - (hitWidth / 2f), marker.Y - size - (6f * uiScale));
            hitMax = new Vector2(marker.X + (hitWidth / 2f), marker.Y + size + (10f * uiScale) + labelSize.Y + statusSize.Y);
        }

        var mouse = ImGui.GetMousePos();
        if (Contains(hitMin, hitMax, mouse))
        {
            pointerNote = occluded || alpha <= 0.6f
                ? $"on {definition.Id}, behind scenery"
                : frameGameUi is not null
                    ? $"on {definition.Id}, game UI '{frameGameUi}' in the way"
                    : $"on {definition.Id}, clickable, {(inRange ? "in range" : $"{distance:0.0}y away")}";
        }

        // The click window exists only while the cursor is near the marker,
        // rather than one per zone every frame.
        var hovered = false;
        var margin = new Vector2(HitMargin * uiScale);
        if (!occluded &&
            alpha > 0.6f &&
            frameGameUi is null &&
            Contains(hitMin - margin, hitMax + margin, mouse) &&
            DrawHitTarget(index, hitMin, hitMax - hitMin, out hovered))
        {
            if (inRange)
            {
                tooFarZone = -1;
                openedBy[definition.Kind] = definition.Id;
                open(definition);
            }
            else
            {
                tooFarZone = index;
                tooFarUntil = now + TooFarFlashMs;
                tooFarDistance = distance;
            }
        }

        var drawList = ImGui.GetBackgroundDrawList();
        var primary = CyberdeckTheme.Palette.Cyan;
        var secondary = CyberdeckTheme.Palette.Magenta;
        var ringColor = inRange ? primary : Vector4.Lerp(primary, CyberdeckTheme.Palette.TextMuted, 0.45f);
        DrawGroundRing(drawList, zone.Center, definition.Radius, ringColor, secondary, inRange, detailed, definition.Compact && hovered, alpha, seconds, uiScale);

        if (zone.ShowLeader && Project(zone.Center, out var ground) && ground.Y > marker.Y + size)
        {
            var leaderStart = new Vector2(marker.X, marker.Y + size);
            if (detailed)
                DrawLeader(drawList, leaderStart, ground, ringColor, alpha, seconds, uiScale);
            else
                drawList.AddLine(leaderStart, ground, Color(ringColor, 0.3f * alpha), 1f * uiScale);
        }

        DrawMarker(drawList, definition.Glyph, marker, hovered ? size * 1.12f : size, ringColor, secondary, inRange, hovered, alpha, seconds, uiScale);

        if (definition.Compact && !hovered && !tooFar)
            return;

        var statusColor = inRange
            ? CyberdeckTheme.Palette.Success
            : tooFar
                ? CyberdeckTheme.Palette.Amber
                : CyberdeckTheme.Palette.TextMuted;
        if (tooFar && !config.ReduceMotion)
            statusColor = CyberdeckTheme.WithAlpha(statusColor, 0.55f + (0.45f * MathF.Abs(MathF.Sin(seconds * 9f))));

        var textY = marker.Y + size + (8f * uiScale);
        DrawShadowedText(drawList, new Vector2(marker.X - (labelSize.X / 2f), textY), label, primary, alpha);
        DrawShadowedText(drawList, new Vector2(marker.X - (statusSize.X / 2f), textY + labelSize.Y), status, statusColor, alpha);
    }

    /// <summary>
    /// A borderless window exactly over the marker, so the click lands on the
    /// deck instead of the game and the cursor turns into a hand over it.
    /// </summary>
    private static bool DrawHitTarget(int index, Vector2 position, Vector2 size, out bool hovered)
    {
        hovered = false;
        var clicked = false;
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoDocking;
        if (ImGui.Begin($"##grid_venue_zone_{index}", flags))
        {
            ImGui.SetCursorPos(Vector2.Zero);
            clicked = ImGui.InvisibleButton("##activate", size);
            hovered = ImGui.IsItemHovered();
            if (hovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        ImGui.End();
        ImGui.PopStyleVar(3);
        return clicked;
    }

    private void DrawGroundRing(
        ImDrawListPtr drawList,
        Vector3 center,
        float radius,
        Vector4 color,
        Vector4 secondary,
        bool inRange,
        bool detailed,
        bool highlighted,
        float alpha,
        float seconds,
        float uiScale)
    {
        if (!ProjectCircle(center, radius, out var allInFront))
            return;

        if (allInFront)
            drawList.AddConvexPolyFilled(ref ringPoints[0], RingSegments, Color(highlighted ? secondary : color, (inRange ? 0.16f : 0.07f) * alpha));

        if (detailed || highlighted)
            DrawRing(drawList, allInFront, Color(highlighted ? secondary : color, 0.22f * alpha), 6f * uiScale);
        DrawRing(drawList, allInFront, Color(highlighted ? secondary : color, 0.92f * alpha), 1.6f * uiScale);
        if (!detailed)
            return;

        // Rotating arcs inside the edge, the part that reads as "active".
        var spin = seconds * (inRange ? 1.1f : 0.45f);
        for (var arc = 0; arc < 4; arc++)
        {
            var start = spin + (arc * MathF.PI / 2f);
            DrawWorldArc(drawList, center, radius * 0.8f, start, start + 0.85f, Color(secondary, 0.85f * alpha), 2.4f * uiScale);
        }

        // Ticks around the rim.
        for (var tick = 0; tick < 12; tick++)
        {
            var angle = tick * MathF.PI / 6f;
            var direction = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            if (Project(center + (direction * radius), out var from) &&
                Project(center + (direction * radius * 1.12f), out var to))
                drawList.AddLine(from, to, Color(color, 0.75f * alpha), 1.4f * uiScale);
        }

        // A pulse running outward from the centre, quicker while in range.
        if (!config.ReduceMotion)
        {
            var period = inRange ? 1.1f : 2.2f;
            var phase = (seconds % period) / period;
            if (ProjectCircle(center, radius * (0.15f + (0.85f * phase)), out var pulseInFront))
                DrawRing(drawList, pulseInFront, Color(color, (1f - phase) * 0.6f * alpha), 1.4f * uiScale);
        }
    }

    /// <summary>The ring last projected: one polyline when all of it is in front of the camera, which is nearly always.</summary>
    private void DrawRing(ImDrawListPtr drawList, bool allInFront, uint color, float thickness)
    {
        if (allInFront)
        {
            drawList.AddPolyline(ref ringPoints[0], RingSegments, color, ImDrawFlags.Closed, thickness);
            return;
        }

        for (var index = 0; index < RingSegments; index++)
        {
            var next = (index + 1) % RingSegments;
            if (ringInFront[index] && ringInFront[next])
                drawList.AddLine(ringPoints[index], ringPoints[next], color, thickness);
        }
    }

    private void DrawWorldArc(ImDrawListPtr drawList, Vector3 center, float radius, float from, float to, uint color, float thickness)
    {
        Span<Vector2> points = stackalloc Vector2[ArcSteps + 1];
        var count = 0;
        for (var step = 0; step <= ArcSteps; step++)
        {
            var angle = from + ((to - from) * step / ArcSteps);
            var world = center + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            if (Project(world, out var point))
            {
                points[count++] = point;
                continue;
            }

            if (count > 1)
                drawList.AddPolyline(ref points[0], count, color, ImDrawFlags.None, thickness);
            count = 0;
        }

        if (count > 1)
            drawList.AddPolyline(ref points[0], count, color, ImDrawFlags.None, thickness);
    }

    private static void DrawLeader(ImDrawListPtr drawList, Vector2 from, Vector2 to, Vector4 color, float alpha, float seconds, float uiScale)
    {
        var length = Vector2.Distance(from, to);
        if (length < 4f)
            return;

        var direction = (to - from) / length;
        var dash = 5f * uiScale;
        var offset = (seconds * 18f * uiScale) % (dash * 2f);
        for (var travelled = -offset; travelled < length; travelled += dash * 2f)
        {
            var start = MathF.Max(0f, travelled);
            var end = MathF.Min(length, travelled + dash);
            if (end > start)
                drawList.AddLine(from + (direction * start), from + (direction * end), Color(color, 0.5f * alpha), 1.2f * uiScale);
        }
    }

    private static void DrawMarker(
        ImDrawListPtr drawList,
        VenueZoneGlyph glyph,
        Vector2 center,
        float size,
        Vector4 color,
        Vector4 secondary,
        bool inRange,
        bool hovered,
        float alpha,
        float seconds,
        float uiScale)
    {
        Span<Vector2> hexagon = stackalloc Vector2[6];
        for (var corner = 0; corner < 6; corner++)
        {
            var angle = (MathF.PI / 6f) + (corner * MathF.PI / 3f);
            hexagon[corner] = center + (new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * size);
        }

        var glow = hovered ? 0.5f : inRange ? 0.32f : 0.18f;
        drawList.AddNgon(center, size + (4f * uiScale), Color(color, glow * alpha), 6, 5f * uiScale);
        drawList.AddConvexPolyFilled(ref hexagon[0], 6, Color(CyberdeckTheme.Palette.Background, 0.86f * alpha));
        drawList.AddPolyline(ref hexagon[0], 6, Color(color, alpha), ImDrawFlags.Closed, (hovered ? 2.4f : 1.8f) * uiScale);

        if (glyph == VenueZoneGlyph.Arcade)
            DrawArcadeGlyph(drawList, center, size * 0.62f, color, secondary, alpha, uiScale);
        else
            DrawVendingGlyph(drawList, center, size * 0.62f, color, secondary, alpha, uiScale);

        if (!hovered && !inRange)
            return;

        // Targeting brackets, turning slowly, when the marker is live.
        var reach = size + (9f * uiScale);
        var arm = size * 0.42f;
        var turn = seconds * 0.6f;
        for (var corner = 0; corner < 4; corner++)
        {
            var angle = turn + (MathF.PI / 4f) + (corner * MathF.PI / 2f);
            var tip = center + (new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * reach);
            var left = new Vector2(MathF.Cos(angle + (MathF.PI * 0.75f)), MathF.Sin(angle + (MathF.PI * 0.75f)));
            var right = new Vector2(MathF.Cos(angle - (MathF.PI * 0.75f)), MathF.Sin(angle - (MathF.PI * 0.75f)));
            var bracketColor = Color(hovered ? secondary : color, (hovered ? 1f : 0.7f) * alpha);
            drawList.AddLine(tip, tip + (left * arm), bracketColor, 1.6f * uiScale);
            drawList.AddLine(tip, tip + (right * arm), bracketColor, 1.6f * uiScale);
        }
    }

    /// <summary>A vending machine in a few lines: the cabinet, its window and shelves, the coin slot and the tray.</summary>
    private static void DrawVendingGlyph(ImDrawListPtr drawList, Vector2 center, float size, Vector4 color, Vector4 secondary, float alpha, float uiScale)
    {
        var line = MathF.Max(1f, 1.3f * uiScale);
        var body = Color(color, alpha);
        var accent = Color(secondary, alpha);
        drawList.AddRect(center + new Vector2(-0.62f, -0.9f) * size, center + new Vector2(0.62f, 0.9f) * size, body, 1.5f * uiScale, ImDrawFlags.None, line);
        drawList.AddRect(center + new Vector2(-0.44f, -0.7f) * size, center + new Vector2(0.18f, 0.32f) * size, accent, 0f, ImDrawFlags.None, line);
        for (var shelf = 1; shelf <= 2; shelf++)
        {
            var y = -0.7f + (shelf * 0.34f);
            drawList.AddLine(center + new Vector2(-0.44f, y) * size, center + new Vector2(0.18f, y) * size, accent, line);
        }

        drawList.AddLine(center + new Vector2(0.38f, -0.6f) * size, center + new Vector2(0.38f, -0.25f) * size, body, line);
        drawList.AddRectFilled(center + new Vector2(-0.4f, 0.5f) * size, center + new Vector2(0.4f, 0.7f) * size, body);
    }

    /// <summary>An arcade cabinet in a few lines: the marquee, the screen, and a joystick and two buttons on the panel.</summary>
    private static void DrawArcadeGlyph(ImDrawListPtr drawList, Vector2 center, float size, Vector4 color, Vector4 secondary, float alpha, float uiScale)
    {
        var line = MathF.Max(1f, 1.3f * uiScale);
        var body = Color(color, alpha);
        var accent = Color(secondary, alpha);
        Span<Vector2> outline = stackalloc Vector2[8];
        outline[0] = center + new Vector2(-0.5f, -0.95f) * size;
        outline[1] = center + new Vector2(0.5f, -0.95f) * size;
        outline[2] = center + new Vector2(0.5f, 0.1f) * size;
        outline[3] = center + new Vector2(0.64f, 0.32f) * size;
        outline[4] = center + new Vector2(0.64f, 0.95f) * size;
        outline[5] = center + new Vector2(-0.64f, 0.95f) * size;
        outline[6] = center + new Vector2(-0.64f, 0.32f) * size;
        outline[7] = center + new Vector2(-0.5f, 0.1f) * size;
        drawList.AddPolyline(ref outline[0], outline.Length, body, ImDrawFlags.Closed, line);
        drawList.AddRectFilled(center + new Vector2(-0.38f, -0.85f) * size, center + new Vector2(0.38f, -0.64f) * size, accent);
        drawList.AddRect(center + new Vector2(-0.36f, -0.5f) * size, center + new Vector2(0.36f, 0.0f) * size, body, 1f * uiScale, ImDrawFlags.None, line);
        drawList.AddLine(center + new Vector2(-0.3f, 0.6f) * size, center + new Vector2(-0.3f, 0.36f) * size, accent, line);
        drawList.AddCircleFilled(center + new Vector2(-0.3f, 0.34f) * size, MathF.Max(1.5f, 0.1f * size), accent);
        drawList.AddCircleFilled(center + new Vector2(0.12f, 0.52f) * size, MathF.Max(1.2f, 0.08f * size), body);
        drawList.AddCircleFilled(center + new Vector2(0.34f, 0.52f) * size, MathF.Max(1.2f, 0.08f * size), body);
    }

    private static void DrawShadowedText(ImDrawListPtr drawList, Vector2 position, string text, Vector4 color, float alpha)
    {
        drawList.AddText(position + Vector2.One, Color(new Vector4(0f, 0f, 0f, 1f), 0.85f * alpha), text);
        drawList.AddText(position, Color(color, color.W * alpha), text);
    }

    /// <summary>Projects a circle on the floor into <see cref="ringPoints"/>.</summary>
    /// <returns>Whether any of the circle is in front of the camera.</returns>
    private bool ProjectCircle(Vector3 center, float radius, out bool allInFront)
    {
        var any = false;
        allInFront = true;
        for (var index = 0; index < RingSegments; index++)
        {
            var angle = index * MathF.Tau / RingSegments;
            var world = center + new Vector3(MathF.Cos(angle) * radius, 0.02f, MathF.Sin(angle) * radius);
            ringInFront[index] = Project(world, out ringPoints[index]);
            any |= ringInFront[index];
            allInFront &= ringInFront[index];
        }

        return any;
    }

    /// <summary>
    /// Reads the camera matrix and screen size once for the frame.
    ///
    /// Asking the game for every point of every ring was a call into it per
    /// point, a few hundred a frame per zone. The matrix is the same one the
    /// game's own projection uses; <see cref="CheckProjection"/> compares the
    /// two on the first marker each frame and falls back to asking the game
    /// per point if they ever disagree.
    /// </summary>
    private unsafe void CaptureProjection()
    {
        ownProjection = false;
        projectionChecked = false;
        var control = GameControl.Instance();
        var device = RenderDevice.Instance();
        if (control is null || device is null || device->Width == 0 || device->Height == 0)
            return;

        viewProjection = control->ViewProjectionMatrix;
        screenOrigin = ImGui.GetMainViewport().Pos;
        screenSize = new Vector2(device->Width, device->Height);
        ownProjection = true;
    }

    private void CheckProjection(Vector3 world, Vector2 expected)
    {
        projectionChecked = true;
        if (ProjectWithMatrix(world, out var own) && Vector2.DistanceSquared(own, expected) <= 2.25f)
            return;

        ownProjection = false;
    }

    /// <returns>Whether the point is in front of the camera.</returns>
    private bool Project(Vector3 world, out Vector2 screen)
        => ownProjection
            ? ProjectWithMatrix(world, out screen)
            : PluginService.GameGui.WorldToScreen(world, out screen, out _);

    private bool ProjectWithMatrix(Vector3 world, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (MathF.Abs(clip.W) < float.Epsilon)
        {
            screen = Vector2.Zero;
            return false;
        }

        var scale = MathF.Abs(1f / clip.W);
        screen = new Vector2(
            screenOrigin.X + (0.5f * screenSize.X * ((clip.X * scale) + 1f)),
            screenOrigin.Y + (0.5f * screenSize.Y * (1f - (clip.Y * scale))));
        return clip.W > 0f;
    }

    /// <summary>
    /// Whether a wall or other scenery stands between the camera and the marker.
    ///
    /// The overlay has no depth of its own, so without this a zone would show
    /// through the wall of the next room.
    /// </summary>
    private static bool IsOccluded(Vector2 marker, Vector3 cameraPosition, float markerDistance)
    {
        try
        {
            if (!PluginService.GameGui.ScreenToWorld(marker, out var hit, markerDistance + 1f))
                return false;

            return Vector3.Distance(cameraPosition, hit) < markerDistance - 0.35f;
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Could not test a venue zone for occlusion.");
            return false;
        }
    }

    private static unsafe bool TryGetCameraPosition(out Vector3 position)
    {
        position = default;
        var manager = SceneCameraManager.Instance();
        if (manager is null)
            return false;

        var camera = manager->CurrentCamera;
        if (camera is null)
            return false;

        position = camera->Position;
        return true;
    }

    /// <summary>
    /// The game window under the cursor, if any.
    ///
    /// The marker must never take a click meant for a hotbar or a chat window
    /// that happens to sit in front of it.
    /// </summary>
    private static unsafe string? GameUiUnderMouse()
    {
        var stage = AtkStage.Instance();
        if (stage is null || stage->AtkCollisionManager is null)
            return null;

        var addon = stage->AtkCollisionManager->IntersectingAddon;
        return addon is null ? null : addon->NameString;
    }

    private static bool Contains(Vector2 min, Vector2 max, Vector2 point)
        => point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;

    private static float HorizontalDistance(Vector3 from, Vector3 to)
        => Vector2.Distance(new Vector2(from.X, from.Z), new Vector2(to.X, to.Z));

    private static uint Color(Vector4 color, float alpha)
        => ImGui.GetColorU32(CyberdeckTheme.WithAlpha(color, Math.Clamp(alpha, 0f, 1f)));

    private static string Format(Vector3 position)
        => $"({position.X:0.00}, {position.Y:0.00}, {position.Z:0.00})";

    private readonly record struct ZoneCandidate(int Index, float Distance);

    private readonly record struct ZoneOnScreen(int Index, float Distance, Vector3 MarkerWorld, Vector2 Marker);

    private readonly record struct OcclusionCheck(bool Occluded, long CheckedAt);
}
