using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Utility;

namespace GridNrootUpdate;

internal sealed partial class CyberdeckWindow
{
    /// <summary>
    /// Announcements: a collapsible banner on the home screen and a full feed.
    ///
    /// Every announcement is remote — there is no bundled announcements file the
    /// way there is for staff profiles — so when the backend has never been
    /// reached there is simply nothing to show. The nav tile and the banner both
    /// disappear entirely in that case, leaving the deck exactly as it was
    /// before this feature existed.
    /// </summary>

    /// <summary>Fixed height of the home-screen broadcast strip, art or no art.</summary>
    private const float BroadcastPanelHeight = 82f;

    /// <summary>Flyer height cap inside the Broadcast feed.</summary>
    private const float FeedFlyerMaxHeight = 190f;

    private bool flyerWindowOpen;
    private bool focusFlyerWindow;
    private NewsPost? flyerWindowPost;

    private int CountUnreadNews(CatalogSnapshot news)
    {
        var lastSeen = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, config.LastSeenNewsUnixMs));
        return news.Posts.Count(post => post.PublishedAtUtc > lastSeen);
    }

    private void MarkNewsSeen(CatalogSnapshot news)
    {
        if (!news.HasPosts)
            return;

        var newest = news.Posts.Max(post => post.PublishedAtUtc);
        var newestUnixMs = newest.ToUnixTimeMilliseconds();
        if (newestUnixMs <= config.LastSeenNewsUnixMs)
            return;

        config.LastSeenNewsUnixMs = newestUnixMs;
        config.Save();
    }

    private static string GetNewsTelemetry(CatalogSnapshot news)
    {
        var next = news.Posts
            .Select(post => post.EventAtUtc)
            .Where(eventAt => eventAt is not null && eventAt > DateTimeOffset.UtcNow)
            .OrderBy(eventAt => eventAt)
            .FirstOrDefault();

        // The next scheduled night is more useful than a count when there is one.
        return next is { } upcoming
            ? FormatEventDate(upcoming).ToUpperInvariant()
            : $"{news.Posts.Count:00} POSTS";
    }

    /// <summary>
    /// Formats an event time in the player's timezone but not their locale.
    ///
    /// The conversion to local time is the point — a guest should see the hour
    /// the doors open where they are. The month and weekday names are pinned to
    /// invariant English so they match the rest of the deck, which is not
    /// localised; without this they render in the operating system's language.
    /// </summary>
    private static string FormatEventDate(DateTimeOffset eventAt)
        => eventAt.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>How far away an event is, or null once it has passed.</summary>
    private static string? FormatEventCountdown(DateTimeOffset eventAt)
    {
        var delta = eventAt - DateTimeOffset.UtcNow;

        // A finished event keeps its date but loses the countdown: "(past)" adds
        // nothing a guest needs and reads as an error when it is highlighted.
        return delta.TotalSeconds switch
        {
            < 0 => null,
            < 3600 => $"in {Math.Max(1, (int)delta.TotalMinutes)} min",
            < 86400 => $"in {(int)delta.TotalHours} h",
            _ => $"in {(int)delta.TotalDays} d",
        };
    }

    /// <summary>Draws the event line, dimmed once the event is over.</summary>
    private static void DrawEventLine(DateTimeOffset eventAt)
    {
        var countdown = FormatEventCountdown(eventAt);
        if (countdown is null)
        {
            ImGui.TextDisabled(FormatEventDate(eventAt));
            return;
        }

        ImGui.TextColored(CyberdeckTheme.Palette.Amber, $"{FormatEventDate(eventAt)}  ({countdown})");
    }

    /// <summary>
    /// The pinned announcement, folded into the home screen above the tiles.
    ///
    /// Collapsing is remembered in the config rather than per-session: someone
    /// who folds it away has said they do not want it, and re-expanding it on
    /// every launch would be nagging.
    /// </summary>
    private void DrawNewsBanner(float width)
    {
        var news = getCatalog();
        if (news.Banner is not { } post)
            return;

        var uiScale = GetUiScale();
        var collapsed = config.NewsBannerCollapsed;
        var caret = collapsed ? "+" : "-";

        ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.Palette.Magenta);
        ImGui.TextUnformatted("BROADCAST");
        ImGui.PopStyleColor();

        // Right-aligned from the measured button width rather than a guess, so
        // it stays put across UI scales and font sizes.
        ImGui.SameLine();
        var toggleWidth = ImGui.CalcTextSize(caret).X + (ImGui.GetStyle().FramePadding.X * 2);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - toggleWidth));
        if (ImGui.SmallButton($"{caret}##news_banner_toggle"))
        {
            config.NewsBannerCollapsed = !collapsed;
            config.Save();
        }
        DrawHoverTooltip(collapsed ? "Show the current broadcast" : "Fold the broadcast away");

        if (collapsed)
        {
            // Folded away, but the headline stays so it is never wholly hidden.
            ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Text, 0.70f));
            ImGui.TextWrapped(post.Title);
            ImGui.PopStyleColor();
            ImGui.Spacing();
            DrawNeonSeparator();
            ImGui.Spacing();
            return;
        }

        DrawBroadcastPanel(post, width, uiScale);

        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();
    }

    /// <summary>
    /// The broadcast as a single panel, with the flyer behind the text.
    ///
    /// This mirrors the deck header: the art is a cover-cropped background
    /// under a dark scrim rather than a block of its own, so the panel costs a
    /// fixed strip of height no matter how tall the flyer is. Showing the flyer
    /// full size on the home screen pushed the nav tiles off the bottom.
    ///
    /// The whole panel is a click target that opens the flyer full size.
    /// </summary>
    private void DrawBroadcastPanel(NewsPost post, float width, float uiScale)
    {
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, BroadcastPanelHeight * uiScale);
        var max = start + size;
        var drawList = ImGui.GetWindowDrawList();

        // The button occupies the panel, so hover and click come from ImGui
        // rather than from hand-rolled bounds tests.
        ImGui.InvisibleButton($"##broadcast_panel_{post.Id}", size);
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked();

        drawList.PushClipRect(start, max, true);

        var flyer = GetNewsFlyer(post);
        if (flyer is not null)
        {
            var (uvMin, uvMax) = GetCoverUv(flyer, size);
            drawList.AddImage(
                flyer.Handle,
                start,
                max,
                uvMin,
                uvMax,
                ImGui.GetColorU32(CyberdeckTheme.WithAlpha(Vector4.One, hovered ? 0.85f : 0.66f)));
        }

        // The scrim is what keeps the title readable over arbitrary art. Without
        // it a bright flyer would swallow the text entirely.
        drawList.AddRectFilled(
            start,
            max,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Background, flyer is null ? 0.55f : 0.72f)),
            6 * uiScale);
        drawList.AddRect(
            start,
            max,
            ImGui.GetColorU32(CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, hovered ? 0.95f : 0.70f)),
            6 * uiScale,
            ImDrawFlags.None,
            MathF.Max(1, uiScale));

        var textX = start.X + (12 * uiScale);
        var textWidth = MathF.Max(1, size.X - (24 * uiScale));

        drawList.AddText(
            new Vector2(textX, start.Y + (12 * uiScale)),
            ImGui.GetColorU32(CyberdeckTheme.Palette.Cyan),
            EllipsizeToWidth(post.Title, textWidth));

        if (post.EventAtUtc is { } eventAt)
        {
            var countdown = FormatEventCountdown(eventAt);
            var eventText = countdown is null
                ? FormatEventDate(eventAt)
                : $"{FormatEventDate(eventAt)}  ({countdown})";

            drawList.AddText(
                new Vector2(textX, start.Y + (34 * uiScale)),
                ImGui.GetColorU32(countdown is null ? CyberdeckTheme.Palette.TextMuted : CyberdeckTheme.Palette.Amber),
                EllipsizeToWidth(eventText, textWidth));
        }

        if (!string.IsNullOrWhiteSpace(post.Summary))
        {
            drawList.AddText(
                new Vector2(textX, start.Y + (56 * uiScale)),
                ImGui.GetColorU32(CyberdeckTheme.Palette.TextMuted),
                EllipsizeToWidth(post.Summary, textWidth));
        }

        drawList.PopClipRect();

        if (hovered)
            DrawHoverTooltip(flyer is null ? "Open this broadcast" : "View the flyer");

        if (clicked)
            OpenFlyerWindow(post);

        // Actions stay outside the panel: a button inside a click target that
        // does something else is a trap.
        if (DrawNewsActions(post, "banner"))
            ImGui.SameLine();

        if (ImGui.SmallButton("READ MORE##news_banner_more"))
            SelectDeckView(DeckView.News);
    }

    private void OpenFlyerWindow(NewsPost post)
    {
        flyerWindowPost = post;
        flyerWindowOpen = true;
        focusFlyerWindow = true;
    }

    /// <summary>
    /// The flyer at full size, in its own window.
    ///
    /// Kept separate from the deck so the art can be as large as it wants
    /// without the Cyberdeck having to grow around it.
    /// </summary>
    private void DrawFlyerWindow()
    {
        if (flyerWindowPost is not { } post)
        {
            flyerWindowOpen = false;
            return;
        }

        var uiScale = GetUiScale();
        using var fontScale = CyberdeckTheme.PushFontScale(uiScale);
        using var theme = CyberdeckTheme.Push(uiScale);

        ImGui.SetNextWindowSize(new Vector2(520, 560) * uiScale, ImGuiCond.FirstUseEver);
        var (minimumSize, maximumSize) = CyberdeckTheme.ResolveWindowConstraints(
            uiScale,
            new Vector2(320, 260),
            new Vector2(1100, 1000));
        ImGui.SetNextWindowSizeConstraints(minimumSize, maximumSize);

        if (focusFlyerWindow)
        {
            ImGui.SetNextWindowFocus();
            focusFlyerWindow = false;
        }

        if (!ImGui.Begin("BROADCAST // FLYER###grid_broadcast_flyer", ref flyerWindowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.Palette.Cyan);
        ImGui.TextWrapped(post.Title);
        ImGui.PopStyleColor();

        if (post.EventAtUtc is { } eventAt)
        {
            DrawEventLine(eventAt);
            if (!string.IsNullOrWhiteSpace(post.EventDiscord))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Copy##flyer_time_{post.Id}"))
                    CopyToClipboard(post.EventDiscord, "DISCORD TIMESTAMP COPIED");
                DrawHoverTooltip("Copy as a Discord timestamp");
            }
        }

        DrawNeonSeparator();
        ImGui.Spacing();

        var available = ImGui.GetContentRegionAvail();
        var flyer = GetNewsFlyer(post);
        if (flyer is not null)
        {
            // Reserve room for the text below rather than letting the art push it out.
            var artHeight = MathF.Max(120 * uiScale, available.Y - (110 * uiScale));
            var flyerSize = FitFlyer(flyer, available.X, artHeight);
            var offsetX = MathF.Max(0, (available.X - flyerSize.X) / 2);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);
            ImGui.Image(flyer.Handle, flyerSize);
            ImGui.Spacing();
        }
        else
        {
            ImGui.TextDisabled("No flyer art available for this broadcast.");
            ImGui.Spacing();
        }

        DrawAnimatedFlyerNotice(post);

        if (!string.IsNullOrWhiteSpace(post.Summary))
            ImGui.TextWrapped(post.Summary);

        if (!string.IsNullOrWhiteSpace(post.Body))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(post.Body);
        }

        ImGui.Spacing();
        if (DrawNewsActions(post, $"flyer_{post.Id}"))
            ImGui.SameLine();

        if (ImGui.SmallButton("ALL BROADCASTS##flyer_all"))
        {
            SelectDeckView(DeckView.News);
            flyerWindowOpen = false;
        }

        ImGui.End();
    }

    /// <summary>
    /// Broadcast settings and diagnostics.
    ///
    /// The relay is off by default, so this is the switch that turns the feature
    /// on. The diagnostics below it answer the only question that matters when
    /// something looks wrong: is the deck showing live data, stale data, or
    /// nothing at all?
    /// </summary>
    private void DrawBroadcastSettings()
    {
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Broadcast relay");

        var enabled = config.BackendEnabled;
        if (ImGui.Checkbox("Receive venue broadcasts", ref enabled))
        {
            config.BackendEnabled = enabled;
            config.Save();
            if (enabled)
                refreshNews();
        }
        DrawMutedWrapped("Fetches venue announcements from the relay. Everything else in the deck works with this off.");

        if (!enabled)
            return;

        // The address is fixed rather than editable. The deck renders whatever
        // the relay returns and downloads the media it points at, so letting it
        // be repointed would hand that trust to whoever supplied the new URL.
        ImGui.TextDisabled(config.BackendBaseUrl);

        var news = getCatalog();
        if (ImGui.SmallButton("CHECK NOW##broadcast_check"))
        {
            refreshNews();
            SetTransientFeedback("BROADCAST REFRESH QUEUED");
        }

        ImGui.SameLine();
        if (news.IsRefreshing)
            ImGui.TextDisabled("checking...");
        else
            ImGui.TextDisabled($"source: {news.SourceLabel}");

        var lastSync = news.LastSync is { } synced
            ? synced.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "never";
        ImGui.TextDisabled($"Last successful check: {lastSync}   Posts: {news.Posts.Count}");

        if (news.LastError is { } error)
        {
            // Wrapped, not truncated: a diagnostic cut off mid-sentence is worse
            // than one that takes two lines. Amber only when it actually cost
            // the player something — with cached posts on screen, an unreachable
            // relay is an expected state, not a fault.
            ImGui.PushStyleColor(
                ImGuiCol.Text,
                news.HasPosts ? CyberdeckTheme.Palette.TextMuted : CyberdeckTheme.Palette.Amber);
            ImGui.TextWrapped(news.HasPosts ? $"{error} Showing saved broadcasts." : error);
            ImGui.PopStyleColor();
        }
    }

    /// <summary>The full announcement feed.</summary>
    private void DrawNewsView()
    {
        var news = getCatalog();
        MarkNewsSeen(news);

        ImGui.TextUnformatted("Broadcast");
        ImGui.SameLine();
        if (ImGui.SmallButton("REFRESH##news_refresh"))
        {
            refreshNews();
            SetTransientFeedback("BROADCAST REFRESH QUEUED");
        }
        DrawHoverTooltip("Check the venue relay for new broadcasts");
        DrawNeonSeparator();
        ImGui.Spacing();

        if (!news.HasPosts)
        {
            ImGui.TextDisabled(news.LastError is null
                ? "No broadcasts on file."
                : "No broadcasts on file. The venue relay is unreachable.");
            return;
        }

        if (news.Source == CatalogSource.Cache)
            ImGui.TextDisabled("Showing the last broadcast received.");

        var width = ImGui.GetContentRegionAvail().X;
        var uiScale = GetUiScale();

        for (var i = 0; i < news.Posts.Count; i++)
        {
            var post = news.Posts[i];
            if (i > 0)
            {
                ImGui.Spacing();
                DrawNeonSeparator();
                ImGui.Spacing();
            }

            if (post.Pinned)
            {
                ImGui.TextColored(CyberdeckTheme.Palette.Magenta, "PINNED");
            }

            var flyer = GetNewsFlyer(post);
            if (flyer is not null)
            {
                // Clickable here too, so the flyer opens full size from either place.
                ImGui.Image(flyer.Handle, FitFlyer(flyer, width, FeedFlyerMaxHeight * uiScale));
                if (ImGui.IsItemHovered())
                {
                    DrawHoverTooltip("View the flyer full size");
                    if (ImGui.IsItemClicked())
                        OpenFlyerWindow(post);
                }

                ImGui.Spacing();
            }

            ImGui.PushStyleColor(ImGuiCol.Text, CyberdeckTheme.Palette.Cyan);
            ImGui.TextWrapped(post.Title);
            ImGui.PopStyleColor();

            if (post.EventAtUtc is { } eventAt)
            {
                DrawEventLine(eventAt);
                if (!string.IsNullOrWhiteSpace(post.EventDiscord))
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Copy##news_time_{post.Id}"))
                        CopyToClipboard(post.EventDiscord, "DISCORD TIMESTAMP COPIED");
                    DrawHoverTooltip("Copy as a Discord timestamp");
                }
            }

            if (!string.IsNullOrWhiteSpace(post.Summary))
                ImGui.TextWrapped(post.Summary);

            if (!string.IsNullOrWhiteSpace(post.Body))
            {
                ImGui.Spacing();
                ImGui.TextWrapped(post.Body);
            }

            DrawNewsActions(post, post.Id);
        }
    }

    /// <summary>
    /// Offers an animated flyer for viewing outside the game.
    ///
    /// ImGui draws static textures; it has no frame timeline for a GIF and no
    /// decoder for an MP4. Rather than show a frozen first frame and pretend,
    /// the deck says what it is and opens the verified local file.
    /// </summary>
    private void DrawAnimatedFlyerNotice(NewsPost post)
    {
        if (!HasAnimatedFlyer(post))
            return;

        var asset = remoteAssets.TryGet(post.FlyerUrl);
        var isVideo = post.FlyerUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        var noun = isVideo ? "video" : "animation";

        if (asset is null)
        {
            ImGui.TextDisabled($"Downloading {noun} flyer...");
            return;
        }

        if (ImGui.SmallButton($"OPEN {noun.ToUpperInvariant()} FLYER##news_animated_{post.Id}"))
            Util.OpenLink(asset.LocalPath);

        DrawHoverTooltip("Opens the downloaded flyer outside the game");
        ImGui.SameLine();
        ImGui.TextDisabled($"({noun} — shown above as the still flyer)");
    }

    /// <summary>
    /// The link button, shared by the banner and the feed.
    /// Returns whether anything was drawn, so callers can lay out what follows.
    /// </summary>
    private bool DrawNewsActions(NewsPost post, string scope)
    {
        if (!post.HasSafeLink)
            return false;

        var label = string.IsNullOrWhiteSpace(post.LinkLabel) ? "OPEN LINK" : post.LinkLabel;
        if (ImGui.SmallButton($"{label}##news_link_{scope}"))
            Util.OpenLink(post.Link);

        DrawHoverTooltip(post.Link);
        return true;
    }

    /// <summary>
    /// Resolves flyer art.
    ///
    /// Order matters: a verified remote still wins, then bundled art, then
    /// nothing. A remote flyer that has not finished downloading simply falls
    /// through to the bundled image for a frame or two rather than flashing an
    /// empty panel, and an animated flyer never resolves here at all — the deck
    /// cannot draw a GIF or an MP4, so those are offered as a link instead.
    /// </summary>
    private IDalamudTextureWrap? GetNewsFlyer(NewsPost post)
    {
        if (!string.IsNullOrWhiteSpace(post.FlyerUrl) && !RemoteAssetCache.IsAnimated(post.FlyerUrl))
        {
            var asset = remoteAssets.TryGet(post.FlyerUrl);
            if (asset is not null)
            {
                var remote = PluginService.TextureProvider.GetFromFile(asset.LocalPath).GetWrapOrDefault();
                if (remote is not null)
                    return remote;
            }
        }

        return string.IsNullOrWhiteSpace(post.FlyerImage) ? null : GetTextureWrap(post.FlyerImage);
    }

    /// <summary>
    /// Whether this post has an animated flyer the deck cannot draw.
    ///
    /// It is still downloaded and hash-checked, so opening it is opening a file
    /// that has already been verified rather than following the link blind.
    /// </summary>
    private bool HasAnimatedFlyer(NewsPost post)
        => !string.IsNullOrWhiteSpace(post.FlyerUrl) && RemoteAssetCache.IsAnimated(post.FlyerUrl);

    /// <summary>Fits a flyer inside the available width and a height cap, never upscaling.</summary>
    private static Vector2 FitFlyer(IDalamudTextureWrap flyer, float width, float maxHeight)
    {
        if (flyer.Width <= 0 || flyer.Height <= 0)
            return Vector2.Zero;

        var scale = MathF.Min(width / flyer.Width, maxHeight / flyer.Height);
        scale = MathF.Min(scale, 1.0f);

        return new Vector2(flyer.Width * scale, flyer.Height * scale);
    }
}
