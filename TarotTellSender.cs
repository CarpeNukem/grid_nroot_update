using System;
using System.Linq;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace GridNrootUpdate;

internal static unsafe class TarotTellSender
{
    private const int MaximumChatBytes = 500;

    /// <summary>
    /// Marks every tell this plugin sends.
    ///
    /// Applied at the single point all outgoing messages pass through, so a new
    /// kind of message cannot be added without carrying it. Receiving decks use
    /// it to recognise venue traffic and chime; it is deliberately distinct from
    /// the tarot packet marker, which is "[GRID-TAROT/3|" and has no space, so
    /// the two can never be confused for one another.
    /// </summary>
    public const string MessagePrefix = "[GRID] ";

    public static bool TrySend(string recipient, string serializedPacket, out string error)
        => TrySend(recipient, string.Empty, serializedPacket, out error);

    public static bool TrySend(string recipient, string message, string serializedPacket, out string error)
    {
        error = string.Empty;
        if (!TryNormalizeRecipient(recipient, out var normalizedRecipient, out error))
            return false;
        if (!TarotPacket.TryParse(serializedPacket, out var packet, out error) || packet is null)
            return false;

        var canonicalPacket = packet.Serialize();
        if (!string.Equals(serializedPacket, canonicalPacket, StringComparison.Ordinal))
        {
            error = "The outbound packet contains unexpected text.";
            return false;
        }

        var normalizedMessage = message.Trim();
        if (normalizedMessage.IndexOfAny(['\r', '\n']) >= 0)
        {
            error = "The tell message contains an unsupported line break.";
            return false;
        }

        var payload = string.IsNullOrWhiteSpace(normalizedMessage)
            ? canonicalPacket
            : $"{normalizedMessage} {canonicalPacket}";

        // No venue prefix on packets. They already announce themselves with
        // "[GRID-TAROT/3|", and prepending anything risks the parser on the
        // other deck — which may be an older build than this one.
        return TrySubmitTell(normalizedRecipient, payload, prefix: false, out error);
    }

    public static bool TrySendMessage(string recipient, string message, out string error)
    {
        error = string.Empty;
        if (!TryNormalizeRecipient(recipient, out var normalizedRecipient, out error))
            return false;

        var normalizedMessage = message.Trim();
        if (string.IsNullOrWhiteSpace(normalizedMessage))
        {
            error = "Enter a message before sending the tell.";
            return false;
        }

        return TrySubmitTell(normalizedRecipient, normalizedMessage, prefix: true, out error);
    }

    private static bool TrySubmitTell(string normalizedRecipient, string payload, bool prefix, out string error)
    {
        error = string.Empty;
        if (!PluginService.ClientState.IsLoggedIn)
        {
            error = "Log in before sending a tell.";
            return false;
        }

        if (payload.IndexOfAny(['\r', '\n']) >= 0)
        {
            error = "The tell message contains an unsupported line break.";
            return false;
        }

        // Prefixed here rather than at each call site, so a new kind of plain
        // message cannot be added without it. Already-prefixed text is left
        // alone in case a caller composed one itself.
        var prefixed = !prefix || payload.StartsWith(MessagePrefix, StringComparison.Ordinal)
            ? payload
            : $"{MessagePrefix}{payload}";

        var command = $"/tell {normalizedRecipient} {prefixed}";
        var bytes = Encoding.UTF8.GetBytes(command);
        if (bytes.Length > MaximumChatBytes)
        {
            error = $"The tell is {bytes.Length} bytes; the game limit is {MaximumChatBytes}.";
            return false;
        }

        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            error = "The game chat module is unavailable.";
            return false;
        }

        Utf8String* nativeCommand = null;
        try
        {
            nativeCommand = Utf8String.FromSequence(bytes);
            if (nativeCommand == null)
            {
                error = "Could not allocate the game chat command.";
                return false;
            }

            uiModule->ProcessChatBoxEntry(nativeCommand);
            return true;
        }
        catch (Exception exception)
        {
            PluginService.Log.Warning(exception, "Could not submit a tell to the game chat module.");
            error = "The game chat module rejected the command.";
            return false;
        }
        finally
        {
            if (nativeCommand != null)
                nativeCommand->Dtor(true);
        }
    }

    private static bool TryNormalizeRecipient(string value, out string recipient, out string error)
    {
        // Character names use a plain apostrophe, but text copied out of a
        // browser or Discord often carries the typographic one. Folding it here
        // means a pasted name like Rhas J’ae works instead of being rejected as
        // "unsupported characters".
        recipient = value.Trim().Replace('‘', '\'').Replace('’', '\'');
        error = string.Empty;
        if (recipient.Length is < 3 or > 64)
        {
            error = "Enter a valid character name, preferably as First Last@World.";
            return false;
        }

        var atParts = recipient.Split('@');
        if (atParts.Length > 2 || atParts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 2)
        {
            error = "Recipient must use First Last or First Last@World.";
            return false;
        }

        var nameParts = atParts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (nameParts.Any(part => part.Length == 0 || part.Any(ch => !char.IsLetter(ch) && ch is not ('\'' or '-'))))
        {
            error = "Recipient name contains unsupported characters.";
            return false;
        }

        if (atParts.Length == 2 &&
            (atParts[1].Length == 0 || atParts[1].Any(ch => !char.IsLetterOrDigit(ch) && ch != '-')))
        {
            error = "Recipient world contains unsupported characters.";
            return false;
        }

        recipient = string.Join(' ', nameParts) + (atParts.Length == 2 ? $"@{atParts[1]}" : string.Empty);
        return true;
    }
}
