using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GridNrootUpdate;

/// <summary>
/// Plays the deck's notification sounds.
///
/// Uses the Win32 waveform API directly rather than a decoding library: the
/// plugin otherwise has no third-party dependencies, and one P/Invoke against
/// winmm is a smaller commitment than shipping an audio stack to play a
/// one-second chime. That is why the bundled sounds are WAV — this API cannot
/// decode MP3, and converting once at packaging time costs 180 KB rather than
/// a megabyte of decoder.
///
/// Playback is asynchronous and best-effort. A missing file, a device with no
/// output, or a failed call is never allowed to disturb the game; the sound is
/// a courtesy, not a feature anything depends on.
/// </summary>
internal static class VenueSounds
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_NODEFAULT = 0x0002;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? soundName, IntPtr module, uint flags);

    /// <summary>Short chime for an incoming venue message.</summary>
    public static void PlayMessageTone() => Play("message.wav");

    /// <summary>
    /// Longer ring for an incoming broadcast, which is an event rather than a
    /// line of text and deserves to be noticed across a room.
    ///
    /// Trimmed from the seventeen-second source to four, mono at 22 kHz, which
    /// is 172 KB rather than the three megabytes a full-length stereo WAV would
    /// have cost. See the note above on why this cannot simply ship the MP3.
    /// </summary>
    public static void PlayCallTone() => Play("call.wav");

    /// <summary>Stops whatever is currently playing.</summary>
    public static void Stop()
    {
        try
        {
            PlaySound(null, IntPtr.Zero, 0);
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Could not stop the current sound.");
        }
    }

    private static void Play(string fileName)
    {
        try
        {
            var path = ResolvePath(fileName);
            if (path is null)
            {
                PluginService.Log.Debug("Sound {File} was not found; skipping playback.", fileName);
                return;
            }

            // SND_NODEFAULT keeps Windows from substituting its own beep when
            // the file cannot be played, which would be worse than silence.
            PlaySound(path, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Could not play {File}.", fileName);
        }
    }

    private static string? ResolvePath(string fileName)
    {
        var directory = PluginService.PluginInterface.AssemblyLocation.DirectoryName;
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        var path = Path.Combine(directory, "snd", fileName);
        return File.Exists(path) ? path : null;
    }
}
