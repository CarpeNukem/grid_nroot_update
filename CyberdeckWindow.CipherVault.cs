using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace GridNrootUpdate;

internal sealed partial class CyberdeckWindow
{
    private bool cipherVaultWindowOpen;
    private bool focusCipherVaultWindow;
    private bool focusCipherPassword;
    private CipherVaultSecrets? cipherVaultSecrets;
    private CipherVaultContent? cipherVaultContent;
    private Task<CipherVaultSecrets?>? cipherUnlockTask;
    private CipherVaultSection cipherVaultSection = CipherVaultSection.Intercepts;
    private readonly float[] cipherVaultSectionScroll = new float[3];
    private CipherLabMode cipherLabMode = CipherLabMode.Hex;
    private readonly Dictionary<string, string> cipherAnswerInputs = new(StringComparer.Ordinal);
    private string cipherVaultPasswordInput = string.Empty;
    private string cipherVaultAuthFeedback = "AES-GCM ARCHIVE // CREDENTIAL REQUIRED";
    private string cipherPuzzleFeedback = string.Empty;
    private string cipherLabInput = string.Empty;
    private string cipherLabKey = string.Empty;
    private string cipherLabOutput = "AWAITING INPUT";
    private string cipherLabSubmitFeedback = string.Empty;
    private string cipherLabLoadedPacketId = string.Empty;
    private string cipherDecoyAnswerInput = string.Empty;
    private string cipherDecoyFeedback = string.Empty;
    private string cipherFinalAnswerInput = "AUTH::";
    private string cipherLabPendingOutput = string.Empty;
    private string cipherLabTelemetry = string.Empty;
    private long cipherLabExecutionReadyAt;
    private bool cipherLabExecutionPending;
    private long cipherLabRevealStartedAt;
    private bool cipherLabRevealPending;
    private long cipherTraceFxUntil;
#if DEBUG
    private char? cipherDebugPendingGrade;
#endif

    private static readonly string[] CipherExecutionTelemetry =
    [
        "ROUTING THROUGH TTY BRIDGE...",
        "ENTROPY PROFILE LOCKED...",
        "KEYSTREAM WINDOW OPEN...",
        "CACHE LINE ISOLATED...",
        "REMOTE WATCHDOG SUPPRESSED...",
        "DECODER RING SYNCHRONIZED...",
        "PAGE BUFFER MAPPED RWX...",
    ];

    public void OpenCipherVault()
    {
        cipherVaultWindowOpen = true;
        focusCipherVaultWindow = true;
        focusCipherPassword = cipherVaultContent is null;
    }

    private void DrawCipherVaultWindow()
    {
        var uiScale = GetUiScale();
        using var theme = CyberdeckTheme.Push(uiScale);
        ImGui.SetNextWindowSize(new Vector2(680, 700) * uiScale, ImGuiCond.FirstUseEver);
        var (minimumSize, maximumSize) = CyberdeckTheme.ResolveWindowConstraints(
            uiScale,
            new Vector2(480, 500),
            new Vector2(920, 1000));
        ImGui.SetNextWindowSizeConstraints(minimumSize, maximumSize);
        if (focusCipherVaultWindow)
        {
            ImGui.SetNextWindowFocus();
            focusCipherVaultWindow = false;
        }

        if (!ImGui.Begin("CIPHER VAULT // GHOST ARCHIVE###grid_cipher_vault", ref cipherVaultWindowOpen, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.End();
            if (!cipherVaultWindowOpen)
                LockCipherVault();
            return;
        }

        ImGui.SetWindowFontScale(uiScale);
        if (ImGui.BeginChild("cipher_vault_body", Vector2.Zero, true))
        {
            ResolveCipherUnlockTask();
            if (cipherVaultContent is null)
                DrawCipherVaultAuthentication();
            else
                DrawCipherVaultArchive(cipherVaultContent);
        }
        ImGui.EndChild();
        DrawCipherTraceFx();
        ImGui.SetWindowFontScale(1f);
        ImGui.End();

        if (!cipherVaultWindowOpen)
            LockCipherVault();
    }

    private void DrawCipherVaultAuthentication()
    {
        var cooldownSeconds = GetCipherLockoutSeconds();
        var throttled = cooldownSeconds > 0;
        var deriving = cipherUnlockTask is not null;

        CyberdeckWidgets.DrawStatusChip(
            throttled ? $"AUTH THROTTLED // {cooldownSeconds:00}s" : deriving ? "DERIVING SESSION KEY" : "ARCHIVE SEALED",
            throttled ? CyberdeckTheme.Palette.Error : deriving ? CyberdeckTheme.Palette.Amber : CyberdeckTheme.Palette.Magenta,
            CyberdeckTheme.Palette.Text,
            GetUiScale());
        ImGui.Spacing();
        ImGui.TextColored(CyberdeckTheme.Palette.Magenta, "GHOST ARCHIVE // AUTH NODE");
        DrawNeonSeparator();
        ImGui.Spacing();
        DrawMutedWrapped("The archive is encrypted at rest. Supply the correct credential to derive its session key and authenticate the payload.");
        ImGui.Spacing();

        DrawSettingsGroupHeader("CREDENTIAL");
        if (focusCipherPassword && !deriving && !throttled)
        {
            ImGui.SetKeyboardFocusHere();
            focusCipherPassword = false;
        }
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.BeginDisabled(deriving || throttled);
        var submitted = ImGui.InputText(
            "##cipher_vault_password",
            ref cipherVaultPasswordInput,
            96,
            ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.EndDisabled();

        using (CyberdeckTheme.PushAccentButton())
        {
            ImGui.BeginDisabled(deriving || throttled || string.IsNullOrEmpty(cipherVaultPasswordInput));
            if (ImGui.Button("AUTHENTICATE", new Vector2(ImGui.GetContentRegionAvail().X, 38 * GetUiScale())) || submitted)
                BeginCipherUnlock();
            ImGui.EndDisabled();
        }

        ImGui.Spacing();
        if (deriving)
        {
            CyberdeckWidgets.DrawIndeterminateScanner(
                config.ReduceMotion,
                CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.45f),
                CyberdeckTheme.Palette.Amber,
                height: 7 * GetUiScale());
        }
        ImGui.TextColored(
            cipherVaultAuthFeedback.StartsWith("ACCESS DENIED", StringComparison.Ordinal)
                || cipherVaultAuthFeedback.StartsWith("TRACE TRIPPED", StringComparison.Ordinal)
                ? CyberdeckTheme.Palette.Error
                : CyberdeckTheme.Palette.TextMuted,
            cipherVaultAuthFeedback);
        ImGui.Spacing();
        DrawMutedWrapped("Three rejected credentials trigger a 20-second local authentication cooldown. Closing this window destroys decrypted session access.");
    }

    private void DrawCipherTraceFx()
    {
        if (config.ReduceMotion || Environment.TickCount64 >= cipherTraceFxUntil)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var phase = (int)(Environment.TickCount64 / 45);
        for (var index = 0; index < 7; index++)
        {
            var y = start.Y + ((phase * 37 + index * 83) % Math.Max(1, (int)size.Y));
            var height = 2 + ((phase + index) % 5);
            var color = index % 2 == 0
                ? CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Error, 0.24f)
                : CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, 0.18f);
            drawList.AddRectFilled(new Vector2(start.X, y), new Vector2(start.X + size.X, y + height), ImGui.GetColorU32(color));
        }
        drawList.AddText(start + new Vector2(28, 42) * GetUiScale(), ImGui.GetColorU32(CyberdeckTheme.Palette.Error), "COUNTER-INTRUSION // TRACE DAEMON ACTIVE");
    }

    private void BeginCipherUnlock()
    {
        if (cipherUnlockTask is not null || GetCipherLockoutSeconds() > 0)
            return;

        var attempt = cipherVaultPasswordInput;
        cipherVaultPasswordInput = string.Empty;
        cipherVaultAuthFeedback = "PBKDF2-SHA256 // 210000 ROUNDS";
        cipherUnlockTask = Task.Run(() =>
            CipherVaultCrypto.TryUnlock(attempt, out var content) ? content : null);
    }

    private void ResolveCipherUnlockTask()
    {
        if (cipherUnlockTask is not { IsCompleted: true } completed)
            return;

        cipherUnlockTask = null;
        CipherVaultSecrets? secrets;
        try { secrets = completed.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            PluginService.Log.Error(ex, "Cipher Vault key derivation failed.");
            secrets = null;
        }

        if (secrets is not null)
        {
            cipherVaultSecrets = secrets;
            EnsureCipherRunState();
            cipherVaultContent = CipherVaultGenerator.Generate(secrets, config.CipherRunSeed);
            config.CipherAuthFailedAttempts = 0;
            config.Save();
            cipherVaultAuthFeedback = "ACCESS GRANTED // ARCHIVE DECRYPTED";
            SwitchCipherVaultSection(CipherVaultSection.Intercepts);
            cipherAnswerInputs.Clear();
            SetTransientFeedback("GHOST ARCHIVE DECRYPTED");
#if DEBUG
            if (cipherDebugPendingGrade is { } debugGrade)
            {
                cipherDebugPendingGrade = null;
                ApplyDebugCipherCompletion(debugGrade);
            }
#endif
            return;
        }

        config.CipherAuthFailedAttempts++;
        if (config.CipherAuthFailedAttempts >= 3)
        {
            config.CipherAuthFailedAttempts = 0;
            SetCipherLockout(20);
            cipherVaultAuthFeedback = "ACCESS DENIED // AUTH THROTTLED";
        }
        else
        {
            cipherVaultAuthFeedback = $"ACCESS DENIED // {3 - config.CipherAuthFailedAttempts} ATTEMPTS BEFORE THROTTLE";
            focusCipherPassword = true;
        }
        config.Save();
    }

    private void DrawCipherVaultArchive(CipherVaultContent content)
    {
        CyberdeckWidgets.DrawStatusChip(
            config.CipherRunCompromised ? "TRACE SATURATED // RUN COMPROMISED" : $"DECRYPTED // {content.Clearance}",
            config.CipherRunCompromised ? CyberdeckTheme.Palette.Error : CyberdeckTheme.Palette.Success,
            CyberdeckTheme.Palette.Text,
            GetUiScale());
        ImGui.SameLine();
        if (ImGui.SmallButton("LOCK SESSION"))
        {
            LockCipherVault();
            return;
        }
        ImGui.SameLine();
        if (!config.CipherRunCompleted && ImGui.SmallButton("ABORT INTRUSION"))
            ImGui.OpenPopup("ABORT CIPHER RUN");

        if (ImGui.BeginPopupModal("ABORT CIPHER RUN", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Error, "ACTIVE RUN DATA WILL BE DESTROYED");
            ImGui.TextDisabled("Current score and recovered fragments will be forfeited.");
            if (ImGui.Button("CONFIRM ABORT", new Vector2(150 * GetUiScale(), 0)))
            {
                config.CipherAbortedRuns++;
                StartNewCipherRun();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("CANCEL", new Vector2(100 * GetUiScale(), 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.Spacing();
        ImGui.TextColored(CyberdeckTheme.Palette.Magenta, content.Title);
        ImGui.TextDisabled($"RUN // {config.CipherRunSeed:X8}    SCORE // {GetCipherArchiveScore():0000}");
        CyberdeckWidgets.DrawLabeledProgress(
            "TRACE",
            config.CipherTraceLevel / 100f,
            config.ReduceMotion,
            CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.44f),
            config.CipherTraceLevel >= 75 ? CyberdeckTheme.Palette.Error : config.CipherTraceLevel >= 40 ? CyberdeckTheme.Palette.Amber : CyberdeckTheme.Palette.Cyan,
            CyberdeckTheme.Palette.Text,
            CyberdeckTheme.Palette.TextMuted,
            $"{config.CipherTraceLevel:000}%",
            height: 7 * GetUiScale());
        DrawNeonSeparator();
        ImGui.Spacing();
        DrawCipherVaultNavigation();
        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();

        switch (cipherVaultSection)
        {
            case CipherVaultSection.Intercepts:
                DrawCipherIntercepts(content);
                break;
            case CipherVaultSection.Lab:
                DrawCipherLab();
                break;
            case CipherVaultSection.Keyring:
                DrawCipherKeyring(content);
                break;
        }
    }

    private void DrawCipherVaultNavigation()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(1, (ImGui.GetContentRegionAvail().X - (spacing * 2)) / 3);
        DrawCipherVaultNavigationButton("INTERCEPTS", CipherVaultSection.Intercepts, width);
        ImGui.SameLine();
        DrawCipherVaultNavigationButton("CIPHER LAB", CipherVaultSection.Lab, width);
        ImGui.SameLine();
        DrawCipherVaultNavigationButton("KEYRING", CipherVaultSection.Keyring, width);
    }

    private void DrawCipherVaultNavigationButton(string label, CipherVaultSection section, float width)
    {
        var active = cipherVaultSection == section;
        if (active)
            ImGui.PushStyleColor(ImGuiCol.Button, CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Cyan, 0.30f));
        if (ImGui.Button(label, new Vector2(width, 0)))
        {
            SwitchCipherVaultSection(section);
        }
        if (active)
            ImGui.PopStyleColor();
    }

    private void SwitchCipherVaultSection(CipherVaultSection section)
    {
        cipherVaultSectionScroll[(int)cipherVaultSection] = ImGui.GetScrollY();
        cipherVaultSection = section;
        ImGui.SetScrollY(cipherVaultSectionScroll[(int)section]);
    }

    private void DrawCipherIntercepts(CipherVaultContent content)
    {
        EnsureCipherRunState();
        DrawSettingsGroupHeader("ENCRYPTED INTERCEPTS");
        DrawMutedWrapped("Inspect frame metadata, recover each plaintext manually, then submit it to the archive. Verification increases trace when wrong.");
        DrawMutedWrapped("Accepted packets yield KEY MATERIAL. When a later frame names KEY SOURCE // PKT-XX, use the fragment recovered from that source packet as its decoder key.");
        ImGui.Spacing();

        for (var index = 0; index < content.Intercepts.Count; index++)
        {
            var intercept = content.Intercepts[index];
            var solved = config.CipherSolvedIntercepts.Contains(intercept.Id, StringComparer.Ordinal);
            var available = index == 0 || config.CipherSolvedIntercepts.Contains(content.Intercepts[index - 1].Id, StringComparer.Ordinal);
            if (index > 0)
            {
                ImGui.Spacing();
                DrawNeonSeparator();
                ImGui.Spacing();
            }

            ImGui.TextColored(
                solved ? CyberdeckTheme.Palette.Success : available ? CyberdeckTheme.Palette.Amber : CyberdeckTheme.Palette.TextMuted,
                solved ? $"<OK> {intercept.Id} // {intercept.Label}" : available ? $"<..> {intercept.Id} // {intercept.Label}" : $"<XX> {intercept.Id} // LOCKED");
            if (!available)
            {
                DrawMutedWrapped("Previous key fragment required.");
                continue;
            }

            DrawCipherPacketMetadata(intercept.Metadata);
            ImGui.TextWrapped(intercept.Ciphertext);
            if (ImGui.SmallButton($"COPY PACKET##copy_{intercept.Id}"))
                CopyToClipboard(intercept.Ciphertext, "CIPHER PACKET COPIED");
            ImGui.SameLine();
            if (ImGui.SmallButton($"LOAD LAB##lab_{intercept.Id}"))
                LoadInterceptIntoCipherLab(intercept);

            if (solved)
            {
                CyberdeckWidgets.DrawStatusChip(
                    "KEY MATERIAL RECOVERED",
                    CyberdeckTheme.Palette.Success,
                    CyberdeckTheme.Palette.Text,
                    GetUiScale());
                ImGui.SameLine();
                ImGui.TextColored(CyberdeckTheme.Palette.Success, intercept.Fragment);
                ImGui.TextDisabled($"KEY HANDLE // {intercept.Id}    TOKEN // {intercept.Fragment}");
            }
            else
            {
                DrawCipherHints(intercept);

                if (!cipherAnswerInputs.TryGetValue(intercept.Id, out var answer))
                    answer = string.Empty;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                var submit = ImGui.InputText(
                    $"##answer_{intercept.Id}",
                    ref answer,
                    128,
                    ImGuiInputTextFlags.EnterReturnsTrue);
                cipherAnswerInputs[intercept.Id] = answer;
                if (DrawCipherTransmitButton($"SUBMIT PLAINTEXT##submit_{intercept.Id}") || submit)
                    SubmitCipherAnswer(intercept, answer);
            }

            if (index == content.Decoy.InsertAfterIndex && solved)
            {
                ImGui.Spacing();
                DrawNeonSeparator();
                ImGui.Spacing();
                DrawCipherDecoy(content.Decoy);
            }
        }

        if (!string.IsNullOrWhiteSpace(cipherPuzzleFeedback))
        {
            ImGui.Spacing();
            ImGui.TextColored(
                cipherPuzzleFeedback.StartsWith("ACCEPTED", StringComparison.Ordinal)
                    ? CyberdeckTheme.Palette.Success
                    : CyberdeckTheme.Palette.Error,
                cipherPuzzleFeedback);
        }
    }

    private void DrawCipherDecoy(CipherDecoy decoy)
    {
        ImGui.TextColored(CyberdeckTheme.Palette.Amber, $"<..> {decoy.Id} // {decoy.Label}");
        DrawCipherPacketMetadata(decoy.Metadata);
        ImGui.TextWrapped(decoy.Ciphertext);
        if (ImGui.SmallButton($"COPY PACKET##copy_{decoy.Id}"))
            CopyToClipboard(decoy.Ciphertext, "CIPHER PACKET COPIED");
        ImGui.SameLine();
        if (ImGui.SmallButton($"LOAD LAB##lab_{decoy.Id}"))
            LoadPacketIntoCipherLab(decoy.Id, decoy.Ciphertext);

        if (config.CipherDecoyTriggered)
        {
            ImGui.TextColored(CyberdeckTheme.Palette.Error, "HONEYPOT BURNED // TRACE PENALTY -250");
            return;
        }

        DrawCipherHints(decoy.Id, decoy.Hints);

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        var submit = ImGui.InputText(
            $"##decoy_answer_{decoy.Id}",
            ref cipherDecoyAnswerInput,
            128,
            ImGuiInputTextFlags.EnterReturnsTrue);
        if (DrawCipherTransmitButton($"SUBMIT PLAINTEXT##verify_{decoy.Id}") || submit)
            SubmitCipherDecoyAnswer(cipherDecoyAnswerInput);

        if (!string.IsNullOrWhiteSpace(cipherDecoyFeedback))
            ImGui.TextColored(CyberdeckTheme.Palette.Error, cipherDecoyFeedback);
    }

    private static void DrawCipherPacketMetadata(IReadOnlyList<string> metadata)
    {
        foreach (var line in metadata)
            ImGui.TextDisabled(line);
    }

    private static bool DrawCipherTransmitButton(string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Amber, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Error, 0.52f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Error, 0.72f));
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    private void DrawCipherHints(CipherIntercept intercept)
        => DrawCipherHints(intercept.Id, intercept.Hints);

    private void DrawCipherHints(string packetId, IReadOnlyList<string> hints)
    {
        EnsureCipherRunState();
        var level = Math.Clamp(config.CipherHintLevels.GetValueOrDefault(packetId), 0, hints.Count);
        for (var index = 0; index < level; index++)
            DrawMutedWrapped($"INTEL {index + 1} // {hints[index]}");

        if (level >= hints.Count)
            return;
        if (ImGui.SmallButton($"PURCHASE INTEL -100##hint_{packetId}"))
        {
            config.CipherHintLevels[packetId] = level + 1;
            config.Save();
        }
    }

    private void SubmitCipherAnswer(CipherIntercept intercept, string answer)
    {
        if (!CipherVaultCrypto.VerifyAnswer(intercept, answer))
        {
            cipherPuzzleFeedback = $"REJECTED // {intercept.Id} PLAINTEXT MISMATCH";
            AddCipherTrace(10, "PLAINTEXT VERIFICATION FAILED");
            return;
        }

        EnsureCipherRunState();
        if (!config.CipherSolvedIntercepts.Contains(intercept.Id, StringComparer.Ordinal))
        {
            config.CipherSolvedIntercepts.Add(intercept.Id);
            config.Save();
        }
        cipherAnswerInputs[intercept.Id] = string.Empty;
        cipherPuzzleFeedback = $"ACCEPTED // {intercept.Fragment} ADDED TO KEYRING";
    }

    private void LoadInterceptIntoCipherLab(CipherIntercept intercept)
        => LoadPacketIntoCipherLab(intercept.Id, intercept.Ciphertext);

    private void LoadPacketIntoCipherLab(string packetId, string ciphertext)
    {
        cipherLabInput = ciphertext;
        cipherLabKey = string.Empty;
        cipherLabOutput = "PACKET LOADED // SELECT DECODER AND PARAMETERS";
        cipherLabSubmitFeedback = string.Empty;
        cipherLabLoadedPacketId = packetId;
        cipherLabPendingOutput = string.Empty;
        cipherLabTelemetry = string.Empty;
        cipherLabExecutionPending = false;
        cipherLabRevealPending = false;
        SwitchCipherVaultSection(CipherVaultSection.Lab);
    }

    private void DrawCipherLab()
    {
        ResolveCipherLabExecution();
        DrawSettingsGroupHeader("LOCAL CIPHER WORKBENCH");
        ImGui.TextDisabled($"LOADED SOURCE // {(string.IsNullOrWhiteSpace(cipherLabLoadedPacketId) ? "MANUAL" : cipherLabLoadedPacketId)}");
        DrawMutedWrapped("Decoder operations are local and do not increase trace. Verification transmits the result to the archive.");
        ImGui.Spacing();

        ImGui.BeginDisabled(cipherLabExecutionPending || cipherLabRevealPending);
        DrawCipherLabModeButton("HEX", CipherLabMode.Hex);
        ImGui.SameLine();
        DrawCipherLabModeButton("BASE64", CipherLabMode.Base64);
        ImGui.SameLine();
        DrawCipherLabModeButton("XOR", CipherLabMode.Xor);
        ImGui.SameLine();
        DrawCipherLabModeButton("VIGENERE", CipherLabMode.Vigenere);
        ImGui.SameLine();
        DrawCipherLabModeButton("COLUMNAR", CipherLabMode.Columnar);

        ImGui.Spacing();
        ImGui.TextDisabled("CIPHERTEXT / ENCODED INPUT");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputText("##cipher_lab_input", ref cipherLabInput, 2048);

        if (cipherLabMode is CipherLabMode.Xor or CipherLabMode.Vigenere or CipherLabMode.Columnar)
        {
            ImGui.TextDisabled("KEY");
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            ImGui.InputText("##cipher_lab_key", ref cipherLabKey, 256);
        }

        using (CyberdeckTheme.PushAccentButton())
        {
            if (ImGui.Button("EXECUTE DECODE", new Vector2(ImGui.GetContentRegionAvail().X, 34 * GetUiScale())))
                BeginCipherLabExecution();
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        DrawSettingsGroupHeader("PLAINTEXT OUTPUT");
        if (cipherLabExecutionPending)
        {
            CyberdeckWidgets.DrawIndeterminateScanner(
                false,
                CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.45f),
                CyberdeckTheme.Palette.Magenta,
                height: 7 * GetUiScale());
            ImGui.TextColored(CyberdeckTheme.Palette.Amber, cipherLabTelemetry);
        }
        else
        {
            ImGui.TextWrapped(cipherLabRevealPending ? $"{cipherLabOutput} █" : cipherLabOutput);
        }
        var hasCandidateOutput = HasCipherLabCandidateOutput();
        if (hasCandidateOutput && ImGui.SmallButton("COPY OUTPUT"))
            CopyToClipboard(cipherLabOutput, "PLAINTEXT COPIED");
        ImGui.Spacing();
        var verifyLabel = cipherVaultContent is not null && cipherLabLoadedPacketId == cipherVaultContent.Decoy.Id
            ? "AUTHENTICATE PACKET"
            : "VERIFY / SUBMIT OUTPUT";
        ImGui.BeginDisabled(!hasCandidateOutput);
        if (DrawCipherTransmitButton(verifyLabel, new Vector2(ImGui.GetContentRegionAvail().X, 34 * GetUiScale())))
            SubmitCipherLabOutput();
        ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(cipherLabSubmitFeedback))
        {
            ImGui.Spacing();
            ImGui.TextColored(
                cipherLabSubmitFeedback.StartsWith("ACCEPTED", StringComparison.Ordinal)
                    ? CyberdeckTheme.Palette.Success
                    : CyberdeckTheme.Palette.Error,
                cipherLabSubmitFeedback);
        }
    }

    private bool HasCipherLabCandidateOutput()
        => !cipherLabExecutionPending
           && !cipherLabRevealPending
           && !string.IsNullOrWhiteSpace(cipherLabOutput)
           && !cipherLabOutput.StartsWith("AWAITING", StringComparison.Ordinal)
           && !cipherLabOutput.StartsWith("PACKET LOADED", StringComparison.Ordinal)
           && !cipherLabOutput.StartsWith("DECODE ERROR", StringComparison.Ordinal);

    private void BeginCipherLabExecution()
    {
        CipherVaultCrypto.TryRunLab(cipherLabMode, cipherLabInput, cipherLabKey, out var output);
        cipherLabSubmitFeedback = string.Empty;
        cipherLabRevealPending = false;
        if (config.ReduceMotion)
        {
            cipherLabOutput = output;
            return;
        }

        cipherLabPendingOutput = output;
        cipherLabTelemetry = CipherExecutionTelemetry[Random.Shared.Next(CipherExecutionTelemetry.Length)];
        cipherLabExecutionReadyAt = Environment.TickCount64 + Random.Shared.Next(420, 920);
        cipherLabExecutionPending = true;
    }

    private void ResolveCipherLabExecution()
    {
        var now = Environment.TickCount64;
        if (cipherLabExecutionPending && now >= cipherLabExecutionReadyAt)
        {
            cipherLabExecutionPending = false;
            cipherLabRevealPending = true;
            cipherLabRevealStartedAt = now;
            cipherLabOutput = string.Empty;
        }

        if (!cipherLabRevealPending)
            return;
        var visibleCharacters = Math.Min(cipherLabPendingOutput.Length, (int)((now - cipherLabRevealStartedAt) / 9) + 1);
        cipherLabOutput = cipherLabPendingOutput[..visibleCharacters];
        if (visibleCharacters < cipherLabPendingOutput.Length)
            return;
        cipherLabRevealPending = false;
        cipherLabPendingOutput = string.Empty;
    }

    private void SubmitCipherLabOutput()
    {
        if (cipherVaultContent is null)
            return;

        if (cipherLabLoadedPacketId == cipherVaultContent.Decoy.Id)
        {
            SubmitCipherDecoyAnswer(cipherLabOutput);
            return;
        }

        EnsureCipherRunState();
        var activeIntercept = cipherVaultContent.Intercepts.FirstOrDefault(intercept =>
            !config.CipherSolvedIntercepts.Contains(intercept.Id, StringComparer.Ordinal));
        if (activeIntercept is null)
        {
            cipherLabSubmitFeedback = "NO ACTIVE INTERCEPT // CHECK KEYRING";
            return;
        }

        if (!CipherVaultCrypto.VerifyAnswer(activeIntercept, cipherLabOutput))
        {
            cipherLabSubmitFeedback = "CHECKSUM MISMATCH // MORE LAYERS OR WRONG PACKET";
            AddCipherTrace(10, "LAB OUTPUT REJECTED");
            return;
        }

        SubmitCipherAnswer(activeIntercept, cipherLabOutput);
        cipherLabSubmitFeedback = $"ACCEPTED // {activeIntercept.Fragment} ADDED TO KEYRING";
        SwitchCipherVaultSection(CipherVaultSection.Intercepts);
    }

    private void SubmitCipherDecoyAnswer(string answer)
    {
        if (cipherVaultContent is null)
            return;

        if (!CipherVaultCrypto.VerifyAnswerHash(cipherVaultContent.Decoy.AnswerHash, answer))
        {
            const string feedback = "REJECTED // PACKET AUTHENTICATION MISMATCH";
            cipherDecoyFeedback = feedback;
            cipherLabSubmitFeedback = feedback;
            AddCipherTrace(10, "PACKET AUTHENTICATION FAILED");
            return;
        }

        if (config.CipherDecoyTriggered)
        {
            const string feedback = "REJECTED // KNOWN HONEYPOT SIGNATURE";
            cipherDecoyFeedback = feedback;
            cipherLabSubmitFeedback = feedback;
            return;
        }

        config.CipherDecoyTriggered = true;
        config.CipherTracePenalty += 250;
        config.Save();
        cipherTraceFxUntil = Environment.TickCount64 + 1800;
        if (AddCipherTrace(50, "HONEYPOT SIGNATURE ACCEPTED"))
            return;
        SetCipherLockout(10);
        LockCipherVault();
        cipherVaultAuthFeedback = "TRACE TRIPPED // HONEYPOT COUNTERMEASURE // -250";
    }

    private void DrawCipherLabModeButton(string label, CipherLabMode mode)
    {
        var active = cipherLabMode == mode;
        if (active)
            ImGui.PushStyleColor(ImGuiCol.Button, CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Magenta, 0.28f));
        if (ImGui.SmallButton($"{label}##cipher_mode"))
            cipherLabMode = mode;
        if (active)
            ImGui.PopStyleColor();
    }

    private void DrawCipherKeyring(CipherVaultContent content)
    {
        EnsureCipherRunState();
        var solved = content.Intercepts.Count(intercept => config.CipherSolvedIntercepts.Contains(intercept.Id, StringComparer.Ordinal));
        var finalUnlocked = config.CipherSolvedIntercepts.Contains("VAULT-V3-FINAL", StringComparer.Ordinal);
        DrawSettingsGroupHeader("RECOVERED KEY FRAGMENTS");
        DrawMutedWrapped("Acquisition order is not assembly order. Arrange recovered material by each packet's ASSEMBLY SLOT and apply its DIR flag.");
        CyberdeckWidgets.DrawLabeledProgress(
            "ARCHIVE CHAIN",
            solved / (float)content.Intercepts.Count,
            config.ReduceMotion,
            CyberdeckTheme.WithAlpha(CyberdeckTheme.Palette.Border, 0.44f),
            solved == content.Intercepts.Count ? CyberdeckTheme.Palette.Success : CyberdeckTheme.Palette.Cyan,
            CyberdeckTheme.Palette.Text,
            CyberdeckTheme.Palette.TextMuted,
            $"{solved}/{content.Intercepts.Count}",
            height: 7 * GetUiScale());
        ImGui.TextDisabled($"ARCHIVE SCORE // {GetCipherArchiveScore():0000}");
        ImGui.TextDisabled($"LOSS // INTEL:{config.CipherHintLevels.Values.Sum() * 100:000} TRACE:{config.CipherTraceLevel * 2:000} ICE:{config.CipherTracePenalty:000}");
        ImGui.Spacing();

        foreach (var intercept in content.Intercepts)
        {
            var recovered = config.CipherSolvedIntercepts.Contains(intercept.Id, StringComparer.Ordinal);
            var assembly = intercept.Metadata.FirstOrDefault(line => line.StartsWith("ASSEMBLY //", StringComparison.Ordinal)) ?? "ASSEMBLY // [UNKNOWN]";
            ImGui.TextColored(
                recovered ? CyberdeckTheme.Palette.Success : CyberdeckTheme.Palette.TextMuted,
                recovered ? $"<KEY> SOURCE:{intercept.Id} // MATERIAL:{intercept.Fragment}" : $"<---> SOURCE:{intercept.Id} // [ENCRYPTED]");
            if (recovered)
                ImGui.TextDisabled($"      {assembly}");
        }

        ImGui.Spacing();
        DrawNeonSeparator();
        ImGui.Spacing();
        if (solved == content.Intercepts.Count && finalUnlocked)
        {
            var score = GetCipherArchiveScore();
            var grade = GetCipherGrade(score);
            CyberdeckWidgets.DrawStatusChip(
                $"CLEARANCE // {grade}",
                GetCipherGradeColor(grade),
                CyberdeckTheme.Palette.Text,
                GetUiScale());
            ImGui.TextColored(GetCipherGradeColor(grade), GetCipherGradeMessage(grade, content.StandardMessage));
            ImGui.TextDisabled($"FINAL SCORE // {score:0000}    BEST // {config.CipherBestScore:0000} {config.CipherBestGrade}");
            ImGui.Spacing();
            var redemptionToken = GetCipherRedemptionToken(content, grade);
            DrawSettingsGroupHeader($"{grade[0]}-CLEARANCE ROLE TOKEN // CONTACT CARPE NUKEM");
            ImGui.TextColored(GetCipherGradeColor(grade), redemptionToken);
            DrawMutedWrapped(content.RedemptionHint);
            if (ImGui.SmallButton("COPY REDEMPTION TOKEN"))
                CopyToClipboard(redemptionToken, "CARPE NUKEM ROLE TOKEN COPIED");

            ImGui.Spacing();
            if (ImGui.Button("START NEW INTRUSION"))
                StartNewCipherRun();
        }
        else if (solved == content.Intercepts.Count)
        {
            CyberdeckWidgets.DrawStatusChip(
                "FINAL KEY // ASSEMBLY REQUIRED",
                CyberdeckTheme.Palette.Amber,
                CyberdeckTheme.Palette.Text,
                GetUiScale());
            ImGui.Spacing();
            DrawMutedWrapped(content.AssemblyHint);
            if (string.IsNullOrWhiteSpace(cipherFinalAnswerInput))
                cipherFinalAnswerInput = "AUTH::";
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            var submit = ImGui.InputText(
                "##cipher_final_answer",
                ref cipherFinalAnswerInput,
                256,
                ImGuiInputTextFlags.EnterReturnsTrue);
            if (DrawCipherTransmitButton("AUTHENTICATE ASSEMBLED KEY") || submit)
            {
                if (CipherVaultCrypto.VerifyAnswerHash(content.FinalAnswerHash, cipherFinalAnswerInput))
                {
                    config.CipherSolvedIntercepts.Add("VAULT-V3-FINAL");
                    cipherFinalAnswerInput = string.Empty;
                    cipherPuzzleFeedback = "ACCEPTED // FINAL DEAD DROP AUTHENTICATED";
                    CompleteCipherRun();
                }
                else
                {
                    cipherPuzzleFeedback = "REJECTED // FRAGMENT ORDER OR TRANSFORM INVALID";
                    AddCipherTrace(20, "FINAL AUTH FRAME REJECTED");
                }
            }
            if (!string.IsNullOrWhiteSpace(cipherPuzzleFeedback))
                ImGui.TextColored(
                    cipherPuzzleFeedback.StartsWith("ACCEPTED", StringComparison.Ordinal)
                        ? CyberdeckTheme.Palette.Success
                        : CyberdeckTheme.Palette.Error,
                    cipherPuzzleFeedback);
        }
        else
        {
            DrawMutedWrapped("Complete the remaining intercepts to authenticate the final dead drop.");
        }
    }

    private int GetCipherArchiveScore()
    {
        EnsureCipherRunState();
        return Math.Max(0, 1000
            - (config.CipherHintLevels.Values.Sum() * 100)
            - config.CipherTracePenalty
            - (config.CipherTraceLevel * 2));
    }

    private string GetCipherGrade(int score)
        => config.CipherRunCompromised ? "C // TRACED" : score switch
        {
            >= 900 => "S // GHOST",
            >= 750 => "A // ROOT",
            >= 500 => "B // RUNNER",
            _ => "C // TRACED",
        };

    private static Vector4 GetCipherGradeColor(string grade)
        => grade[0] switch
        {
            'S' => CyberdeckTheme.Palette.Success,
            'A' => CyberdeckTheme.Palette.Cyan,
            'B' => CyberdeckTheme.Palette.Amber,
            _ => CyberdeckTheme.Palette.Error,
        };

    private static string GetCipherGradeMessage(string grade, string standardMessage)
        => grade[0] switch
        {
            'S' => "GHOST ROUTE // ZERO-TRUST ARCHIVE FULLY COMPROMISED",
            'A' => "ROOT CLEARANCE // ARCHIVE VERIFIED // S-PAYLOAD REMAINS SEALED",
            'B' => standardMessage,
            _ => "TRACE FORENSICS ATTACHED // ARCHIVE SESSION COMPROMISED",
        };

    private static string GetCipherRedemptionToken(CipherVaultContent content, string grade)
        => grade[0] switch
        {
            'S' => content.RedemptionTokenS,
            'A' => content.RedemptionTokenA,
            'B' => content.RedemptionTokenB,
            _ => content.RedemptionTokenC,
        };

    private void CompleteCipherRun()
    {
        var score = GetCipherArchiveScore();
        var grade = GetCipherGrade(score);
        config.CipherRunActive = false;
        config.CipherRunCompleted = true;
        if (score > config.CipherBestScore || string.IsNullOrWhiteSpace(config.CipherBestGrade))
        {
            config.CipherBestScore = score;
            config.CipherBestGrade = grade;
        }
        if (score >= 900)
            config.CipherPrizeUnlocked = true;
        config.Save();
    }

#if DEBUG
    public void DebugClearBlackIce()
    {
        intrusionGame = null;
        intrusionResultRecorded = true;
        showIntrusionPayload = true;
        intrusionWindowOpen = true;
        focusIntrusionWindow = true;
        SetTransientFeedback("DEBUG // BLACK ICE PAYLOAD FORCED");
    }

    public void DebugClearCipherVault(char grade)
    {
        cipherDebugPendingGrade = char.ToUpperInvariant(grade);
        OpenCipherVault();
        if (cipherVaultContent is null)
            return;

        var pendingGrade = cipherDebugPendingGrade.Value;
        cipherDebugPendingGrade = null;
        ApplyDebugCipherCompletion(pendingGrade);
    }

    private void ApplyDebugCipherCompletion(char grade)
    {
        StartNewCipherRun();
        if (cipherVaultContent is null)
            return;

        config.CipherSolvedIntercepts.Clear();
        config.CipherSolvedIntercepts.AddRange(cipherVaultContent.Intercepts.Select(intercept => intercept.Id));
        config.CipherSolvedIntercepts.Add("VAULT-V3-FINAL");
        config.CipherHintLevels.Clear();
        config.CipherTraceLevel = 0;
        config.CipherRunCompromised = false;
        config.CipherTracePenalty = grade switch
        {
            'S' => 0,
            'A' => 200,
            'B' => 400,
            _ => 700,
        };

        CompleteCipherRun();
        cipherVaultSection = CipherVaultSection.Keyring;
        cipherPuzzleFeedback = $"DEBUG // {GetCipherGrade(GetCipherArchiveScore())} CLEAR INJECTED";
    }
#endif

    private bool AddCipherTrace(int amount, string source)
    {
        var previous = config.CipherTraceLevel;
        config.CipherTraceLevel = Math.Clamp(previous + amount, 0, 100);
        cipherPuzzleFeedback = $"TRACE +{config.CipherTraceLevel - previous:00}% // {source}";
        if (config.CipherTraceLevel < 100 || config.CipherRunCompromised)
        {
            config.Save();
            return false;
        }

        config.CipherRunCompromised = true;
        config.CipherTracePenalty += 200;
        cipherTraceFxUntil = Environment.TickCount64 + 2400;
        SetCipherLockout(30);
        LockCipherVault();
        cipherVaultAuthFeedback = "TRACE TRIPPED // SESSION TERMINATED // -200";
        return true;
    }

    private int GetCipherLockoutSeconds()
    {
        var remaining = config.CipherLockoutUntilUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Math.Max(0, (int)Math.Ceiling(remaining / 1000d));
    }

    private void SetCipherLockout(int seconds)
    {
        config.CipherLockoutUntilUnixMs = DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeMilliseconds();
        config.Save();
    }

    private void EnsureCipherRunState()
    {
        config.CipherSolvedIntercepts ??= [];
        config.CipherHintLevels ??= [];
        if (config.CipherVaultVersion == 3 && config.CipherRunSeed != 0)
            return;

        config.CipherSolvedIntercepts.Clear();
        config.CipherHintLevels.Clear();
        config.CipherDecoyTriggered = false;
        config.CipherTracePenalty = 0;
        config.CipherTraceLevel = 0;
        config.CipherRunCompromised = false;
        config.CipherRunActive = true;
        config.CipherRunCompleted = false;
        config.CipherRunSeed = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        config.CipherRunStartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        config.CipherVaultVersion = 3;
        config.Save();
    }

    private void StartNewCipherRun()
    {
        config.CipherSolvedIntercepts.Clear();
        config.CipherHintLevels.Clear();
        config.CipherDecoyTriggered = false;
        config.CipherTracePenalty = 0;
        config.CipherTraceLevel = 0;
        config.CipherRunCompromised = false;
        config.CipherRunActive = true;
        config.CipherRunCompleted = false;
        config.CipherRunSeed = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        config.CipherRunStartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        config.CipherVaultVersion = 3;
        config.Save();

        cipherAnswerInputs.Clear();
        cipherFinalAnswerInput = "AUTH::";
        cipherLabInput = string.Empty;
        cipherLabKey = string.Empty;
        cipherLabOutput = "AWAITING INPUT";
        cipherLabLoadedPacketId = string.Empty;
        cipherLabPendingOutput = string.Empty;
        cipherLabExecutionPending = false;
        cipherLabRevealPending = false;
        cipherDecoyAnswerInput = string.Empty;
        cipherDecoyFeedback = string.Empty;
        cipherPuzzleFeedback = "NEW RUN GENERATED // PACKET MAP RANDOMIZED";
        Array.Clear(cipherVaultSectionScroll);
        cipherVaultSection = CipherVaultSection.Intercepts;
        if (cipherVaultSecrets is not null)
            cipherVaultContent = CipherVaultGenerator.Generate(cipherVaultSecrets, config.CipherRunSeed);
    }

    private void LockCipherVault()
    {
        cipherVaultSecrets = null;
        cipherVaultContent = null;
        cipherUnlockTask = null;
        cipherVaultPasswordInput = string.Empty;
        cipherAnswerInputs.Clear();
        cipherLabInput = string.Empty;
        cipherLabKey = string.Empty;
        cipherLabOutput = "AWAITING INPUT";
        cipherLabSubmitFeedback = string.Empty;
        cipherLabLoadedPacketId = string.Empty;
        cipherLabPendingOutput = string.Empty;
        cipherLabTelemetry = string.Empty;
        cipherLabExecutionPending = false;
        cipherLabRevealPending = false;
        cipherDecoyAnswerInput = string.Empty;
        cipherDecoyFeedback = string.Empty;
        cipherFinalAnswerInput = "AUTH::";
        cipherPuzzleFeedback = string.Empty;
        cipherVaultAuthFeedback = "AES-GCM ARCHIVE // CREDENTIAL REQUIRED";
        focusCipherPassword = true;
    }

    private enum CipherVaultSection
    {
        Intercepts,
        Lab,
        Keyring,
    }
}
