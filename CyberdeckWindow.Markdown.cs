using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Utility;

namespace GridNrootUpdate;

internal sealed partial class CyberdeckWindow
{
    /// <summary>
    /// A markdown subset, rendered with ImGui text primitives.
    ///
    /// ImGui has no rich-text widget and no markdown support, so this covers the
    /// constructs venue writing actually needs — headings, bold, italic, bullet
    /// and numbered lists, links, inline code, and horizontal rules — and
    /// deliberately nothing else. Anything unrecognised renders as its own plain
    /// text rather than showing raw syntax, so an editor who reaches for a
    /// table gets readable prose instead of pipes and dashes.
    ///
    /// Inline emphasis is rendered by colour and case rather than by swapping
    /// fonts: the deck ships one font, and a fake bold drawn by overprinting
    /// looks worse than a colour shift at this size.
    /// </summary>
    private void DrawMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return;

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();

            if (line.Length == 0)
            {
                ImGui.Spacing();
                continue;
            }

            // Horizontal rule.
            if (line is "---" or "***" or "___")
            {
                ImGui.Spacing();
                DrawNeonSeparator();
                ImGui.Spacing();
                continue;
            }

            var trimmed = line.TrimStart();
            var heading = CountLeading(trimmed, '#');
            if (heading is > 0 and <= 3 && trimmed.Length > heading && trimmed[heading] == ' ')
            {
                ImGui.Spacing();
                ImGui.TextColored(
                    heading == 1 ? CyberdeckTheme.Palette.Cyan : CyberdeckTheme.Palette.Magenta,
                    trimmed[(heading + 1)..].Trim());
                continue;
            }

            // Bullet list.
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                ImGui.Bullet();
                DrawInline(trimmed[2..].Trim());
                continue;
            }

            // Numbered list: keep the author's own numbering rather than renumbering.
            var numbered = ParseOrderedMarker(trimmed);
            if (numbered is { } marker)
            {
                ImGui.TextColored(CyberdeckTheme.Palette.Amber, marker.Marker);
                ImGui.SameLine();
                DrawInline(marker.Content);
                continue;
            }

            // Blockquote.
            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                ImGui.TextDisabled("|");
                ImGui.SameLine();
                DrawInline(trimmed[2..].Trim());
                continue;
            }

            DrawInline(trimmed);
        }
    }

    private static int CountLeading(string value, char character)
    {
        var count = 0;
        while (count < value.Length && value[count] == character)
            count++;

        return count;
    }

    private static (string Marker, string Content)? ParseOrderedMarker(string line)
    {
        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
            digits++;

        if (digits == 0 || digits + 1 >= line.Length || line[digits] != '.' || line[digits + 1] != ' ')
            return null;

        return (line[..(digits + 1)], line[(digits + 2)..].Trim());
    }

    /// <summary>
    /// Renders one line's inline spans.
    ///
    /// Segments are laid out with SameLine rather than wrapped, so a long line
    /// with emphasis in it can overflow. Venue copy is short lines; the trade is
    /// deliberate, because ImGui cannot wrap a run of differently-styled spans
    /// without measuring and breaking text by hand.
    /// </summary>
    private void DrawInline(string text)
    {
        var spans = ParseInline(text);
        if (spans.Count == 0)
        {
            ImGui.TextUnformatted(string.Empty);
            return;
        }

        // A single plain span is the common case and can wrap properly.
        if (spans.Count == 1 && spans[0].Style == InlineStyle.Plain)
        {
            ImGui.TextWrapped(spans[0].Text);
            return;
        }

        for (var i = 0; i < spans.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, 0);

            var span = spans[i];
            switch (span.Style)
            {
                case InlineStyle.Strong:
                    ImGui.TextColored(CyberdeckTheme.Palette.Cyan, span.Text);
                    break;
                case InlineStyle.Emphasis:
                    ImGui.TextColored(CyberdeckTheme.Palette.Amber, span.Text);
                    break;
                case InlineStyle.Code:
                    ImGui.TextColored(CyberdeckTheme.Palette.Magenta, span.Text);
                    if (ImGui.IsItemClicked())
                        CopyToClipboard(span.Text, "COPIED");
                    if (ImGui.IsItemHovered())
                        DrawHoverTooltip("Click to copy");
                    break;
                case InlineStyle.Link:
                    ImGui.TextColored(CyberdeckTheme.Palette.Cyan, span.Text);
                    if (ImGui.IsItemClicked() && IsSafeLink(span.Href))
                        Util.OpenLink(span.Href);
                    if (ImGui.IsItemHovered())
                        DrawHoverTooltip(span.Href);
                    break;
                default:
                    ImGui.TextUnformatted(span.Text);
                    break;
            }
        }
    }

    /// <summary>https only, matching the rule the relay enforces on stored links.</summary>
    private static bool IsSafeLink(string href)
        => Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private enum InlineStyle
    {
        Plain,
        Strong,
        Emphasis,
        Code,
        Link,
    }

    private readonly record struct InlineSpan(string Text, InlineStyle Style, string Href);

    /// <summary>
    /// Splits a line into styled spans.
    ///
    /// A hand-rolled scan rather than a regex: the markers nest badly and an
    /// unclosed one has to fall back to literal text, which is fiddly to express
    /// as a pattern and easy to get subtly wrong.
    /// </summary>
    private static List<InlineSpan> ParseInline(string text)
    {
        var spans = new List<InlineSpan>();
        var plain = new System.Text.StringBuilder();
        var index = 0;

        void FlushPlain()
        {
            if (plain.Length == 0)
                return;

            spans.Add(new InlineSpan(plain.ToString(), InlineStyle.Plain, string.Empty));
            plain.Clear();
        }

        while (index < text.Length)
        {
            // [label](https://...)
            if (text[index] == '[')
            {
                var close = text.IndexOf(']', index + 1);
                if (close > index && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var end = text.IndexOf(')', close + 2);
                    if (end > close)
                    {
                        var label = text[(index + 1)..close];
                        var href = text[(close + 2)..end].Trim();
                        FlushPlain();
                        spans.Add(new InlineSpan(label, InlineStyle.Link, href));
                        index = end + 1;
                        continue;
                    }
                }
            }

            if (TryDelimited(text, index, "**", out var strong, out var afterStrong))
            {
                FlushPlain();
                spans.Add(new InlineSpan(strong, InlineStyle.Strong, string.Empty));
                index = afterStrong;
                continue;
            }

            if (TryDelimited(text, index, "`", out var code, out var afterCode))
            {
                FlushPlain();
                spans.Add(new InlineSpan(code, InlineStyle.Code, string.Empty));
                index = afterCode;
                continue;
            }

            if (TryDelimited(text, index, "*", out var emphasis, out var afterEmphasis))
            {
                FlushPlain();
                spans.Add(new InlineSpan(emphasis, InlineStyle.Emphasis, string.Empty));
                index = afterEmphasis;
                continue;
            }

            plain.Append(text[index]);
            index++;
        }

        FlushPlain();
        return spans;
    }

    private static bool TryDelimited(string text, int start, string marker, out string content, out int next)
    {
        content = string.Empty;
        next = start;

        if (!text.AsSpan(start).StartsWith(marker, StringComparison.Ordinal))
            return false;

        var contentStart = start + marker.Length;
        var close = text.IndexOf(marker, contentStart, StringComparison.Ordinal);
        if (close <= contentStart)
            return false;

        content = text[contentStart..close];
        next = close + marker.Length;
        return true;
    }
}
