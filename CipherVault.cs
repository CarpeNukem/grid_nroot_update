using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GridNrootUpdate;

internal sealed record CipherVaultSecrets(
    string Title,
    string Clearance,
    string StandardMessage,
    string RedemptionTokenS,
    string RedemptionTokenA,
    string RedemptionTokenB,
    string RedemptionTokenC,
    string RedemptionHint);

internal sealed record CipherVaultContent(
    string Title,
    string Clearance,
    IReadOnlyList<CipherIntercept> Intercepts,
    CipherDecoy Decoy,
    string AssemblyHint,
    string FinalAnswerHash,
    string StandardMessage,
    string RedemptionTokenS,
    string RedemptionTokenA,
    string RedemptionTokenB,
    string RedemptionTokenC,
    string RedemptionHint);

internal sealed record CipherIntercept(
    string Id,
    string Label,
    string Ciphertext,
    IReadOnlyList<string> Metadata,
    IReadOnlyList<string> Hints,
    string AnswerHash,
    string Fragment);

internal sealed record CipherDecoy(
    string Id,
    string Label,
    string Ciphertext,
    IReadOnlyList<string> Metadata,
    IReadOnlyList<string> Hints,
    string AnswerHash,
    int InsertAfterIndex);

internal enum CipherLabMode
{
    Hex,
    Base64,
    Xor,
    Vigenere,
    Columnar,
}

internal static class CipherVaultGenerator
{
    private static readonly string[] Subsystems = ["KERN", "MMU", "DMA", "TTY", "IPC", "HEAP", "PROC", "NET", "TLS", "CACHE", "PAGE"];
    private static readonly string[] Operations = ["NMI", "RST", "ACK", "SYN", "XFER", "FLUSH", "TRAP", "EXEC", "MAP", "FORK", "HALT", "REVOKE", "DROP", "BIND", "SWAP"];
    private static readonly string[] Labels = ["RX BUFFER", "KERNEL FRAME", "ROUTE TABLE", "AUTH CACHE", "PAGE MAP", "PROCESS FRAME", "DMA QUEUE", "CERT BUFFER"];

    public static CipherVaultContent Generate(CipherVaultSecrets secrets, int seed)
    {
        var random = new VaultRandom((uint)Math.Max(1, seed));
        var fragments = new[]
        {
            $"UID{random.Next(1, 10)}",
            random.HexKey(4),
            random.Pick(Operations),
            $"{random.Pick(Subsystems)}{random.Next(1, 10)}",
        };

        var sourceOrder = new[] { 0, 1, 2, 3 };
        random.Shuffle(sourceOrder);
        var sequenceBySource = new int[4];
        for (var sequence = 0; sequence < sourceOrder.Length; sequence++)
            sequenceBySource[sourceOrder[sequence]] = sequence + 1;
        var reversedSource = random.Next(2, 4);

        var ids = GenerateDistinctIds(random, 5);
        var labels = Labels.ToArray();
        random.Shuffle(labels);
        var answers = Enumerable.Range(0, 5).Select(_ => GenerateFrame(random)).ToArray();
        var packets = new[]
        {
            new GeneratedPacket(EncodeHex(answers[0]),
                ["The payload alphabet is limited to complete byte pairs.", "No external key material is required.", "Decode as HEX."],
                [new CipherStep(CipherLabMode.Hex, string.Empty)]),
            BuildTwoLayerPacket(answers[1], fragments[0], $"Use the fragment recovered from {ids[0]} as key material.", random),
            BuildTwoLayerPacket(answers[2], Reverse(fragments[1]), $"Reverse the fragment recovered from {ids[1]} before using it as key material.", random),
            BuildThreeLayerPacket(answers[3], fragments[0] + fragments[2], $"Concatenate fragments from {ids[0]} and {ids[2]} without a separator.", random),
        };

        var intercepts = new List<CipherIntercept>
        {
            CreateIntercept(
                ids[0], labels[0], packets[0].Ciphertext, answers[0], fragments[0],
                BuildMetadata(random, answers[0], 1, sequenceBySource[0], reversedSource == 0),
                packets[0].Hints),
            CreateIntercept(
                ids[1], labels[1], packets[1].Ciphertext, answers[1], fragments[1],
                BuildMetadata(random, answers[1], 2, sequenceBySource[1], reversedSource == 1, $"KEY SOURCE // {ids[0]}"),
                packets[1].Hints),
            CreateIntercept(
                ids[2], labels[2], packets[2].Ciphertext, answers[2], fragments[2],
                BuildMetadata(random, answers[2], 2, sequenceBySource[2], reversedSource == 2, $"KEY SOURCE // REV({ids[1]})"),
                packets[2].Hints),
            CreateIntercept(
                ids[3], labels[3], packets[3].Ciphertext, answers[3], fragments[3],
                BuildMetadata(random, answers[3], 3, sequenceBySource[3], reversedSource == 3, $"KEY SOURCE // {ids[0]}+{ids[2]}"),
                packets[3].Hints),
        };

        var decoyInsertAfter = random.Next(1, 3);
        var decoyKey = fragments[decoyInsertAfter];
        var decoyPacket = BuildTwoLayerPacket(answers[4], decoyKey, $"Use the fragment recovered from {ids[decoyInsertAfter]} as key material.", random);
        var decoy = new CipherDecoy(
            ids[4],
            labels[4],
            decoyPacket.Ciphertext,
            BuildDecoyMetadata(random, answers[4], ids[decoyInsertAfter]),
            [
                decoyPacket.Hints[0],
                $"Use the fragment recovered from {ids[decoyInsertAfter]} and audit the routing header.",
                "TTL:00 cannot arrive after multiple routed hops. Do not authenticate this frame.",
            ],
            CipherVaultCrypto.HashAnswer(answers[4]),
            decoyInsertAfter);

        var finalParts = sourceOrder
            .Select(source => source == reversedSource ? Reverse(fragments[source]) : fragments[source])
            .ToArray();
        var finalAnswer = $"AUTH::{string.Join('/', finalParts)}";

        var content = new CipherVaultContent(
            secrets.Title,
            secrets.Clearance,
            intercepts,
            decoy,
            "FRAME // AUTH::<S01>/<S02>/<S03>/<S04> // ORDER=ASSEMBLY SLOT ASC // APPLY DIR FLAGS",
            CipherVaultCrypto.HashAnswer(finalAnswer),
            secrets.StandardMessage,
            secrets.RedemptionTokenS,
            secrets.RedemptionTokenA,
            secrets.RedemptionTokenB,
            secrets.RedemptionTokenC,
            secrets.RedemptionHint);
        ValidateGeneratedRun(content, answers, packets, decoyPacket, finalAnswer);
        return content;
    }

    private static void ValidateGeneratedRun(
        CipherVaultContent content,
        IReadOnlyList<string> answers,
        IReadOnlyList<GeneratedPacket> packets,
        GeneratedPacket decoyPacket,
        string finalAnswer)
    {
        static string Decode(CipherLabMode mode, string input, string key = "")
        {
            if (!CipherVaultCrypto.TryRunLab(mode, input, key, out var output))
                throw new InvalidOperationException($"Generated Cipher Vault run failed {mode} validation.");
            return output;
        }

        static string DecodePacket(string ciphertext, GeneratedPacket packet, Func<CipherLabMode, string, string, string> decode)
        {
            var output = ciphertext;
            foreach (var step in packet.Steps)
                output = decode(step.Mode, output, step.Key);
            return output;
        }

        var decoded = packets
            .Select((packet, index) => DecodePacket(content.Intercepts[index].Ciphertext, packet, Decode))
            .Append(DecodePacket(content.Decoy.Ciphertext, decoyPacket, Decode))
            .ToArray();
        if (!decoded.SequenceEqual(answers, StringComparer.Ordinal)
            || !CipherVaultCrypto.VerifyAnswerHash(content.FinalAnswerHash, finalAnswer))
            throw new InvalidOperationException("Generated Cipher Vault run did not round-trip exactly.");
    }

    private static GeneratedPacket BuildTwoLayerPacket(string answer, string key, string keyHint, VaultRandom random)
        => random.Next(3) switch
        {
            0 => new GeneratedPacket(
                EncodeXor(Convert.ToBase64String(Encoding.UTF8.GetBytes(answer)), key),
                ["The outer frame is byte-oriented and key-dependent.", keyHint, "Decode XOR, then BASE64."],
                [new CipherStep(CipherLabMode.Xor, key), new CipherStep(CipherLabMode.Base64, string.Empty)]),
            1 => new GeneratedPacket(
                EncodeColumnar(EncodeHex(answer), key),
                ["Column widths follow the ordering of external key material.", keyHint, "Decode COLUMNAR, then HEX."],
                [new CipherStep(CipherLabMode.Columnar, key), new CipherStep(CipherLabMode.Hex, string.Empty)]),
            _ => new GeneratedPacket(
                EncodeVigenere(EncodeHex(answer), key),
                ["Alphabetic positions shift while numeric positions remain fixed.", keyHint, "Decode VIGENERE, then HEX."],
                [new CipherStep(CipherLabMode.Vigenere, key), new CipherStep(CipherLabMode.Hex, string.Empty)]),
        };

    private static GeneratedPacket BuildThreeLayerPacket(string answer, string key, string keyHint, VaultRandom random)
        => random.Next(3) switch
        {
            0 => new GeneratedPacket(
                EncodeColumnar(EncodeHex(Convert.ToBase64String(Encoding.UTF8.GetBytes(answer))), key),
                ["Column widths are irregular; duplicate key characters retain source order.", keyHint, "Decode COLUMNAR, then HEX, then BASE64."],
                [new CipherStep(CipherLabMode.Columnar, key), new CipherStep(CipherLabMode.Hex, string.Empty), new CipherStep(CipherLabMode.Base64, string.Empty)]),
            1 => new GeneratedPacket(
                EncodeXor(Convert.ToBase64String(Encoding.UTF8.GetBytes(EncodeHex(answer))), key),
                ["The outer layer is a keyed byte frame surrounding two transport encodings.", keyHint, "Decode XOR, then BASE64, then HEX."],
                [new CipherStep(CipherLabMode.Xor, key), new CipherStep(CipherLabMode.Base64, string.Empty), new CipherStep(CipherLabMode.Hex, string.Empty)]),
            _ => new GeneratedPacket(
                EncodeColumnar(Convert.ToBase64String(Encoding.UTF8.GetBytes(EncodeHex(answer))), key),
                ["Column ordering protects a transport frame containing byte pairs.", keyHint, "Decode COLUMNAR, then BASE64, then HEX."],
                [new CipherStep(CipherLabMode.Columnar, key), new CipherStep(CipherLabMode.Base64, string.Empty), new CipherStep(CipherLabMode.Hex, string.Empty)]),
        };

    private sealed record CipherStep(CipherLabMode Mode, string Key);
    private sealed record GeneratedPacket(string Ciphertext, IReadOnlyList<string> Hints, IReadOnlyList<CipherStep> Steps);

    private static CipherIntercept CreateIntercept(
        string id,
        string label,
        string ciphertext,
        string answer,
        string fragment,
        IReadOnlyList<string> metadata,
        IReadOnlyList<string> hints)
        => new(id, label, ciphertext, metadata, hints, CipherVaultCrypto.HashAnswer(answer), fragment);

    private static IReadOnlyList<string> BuildMetadata(
        VaultRandom random,
        string answer,
        int layers,
        int keySlot,
        bool reversed,
        string? keyReference = null)
    {
        var metadata = new List<string>
        {
            $"FLOW // RX:{random.Hex(2)} HOPS:{random.Next(1, 5):00} TTL:{random.Next(24, 96):X2}",
            $"FRAME // LEN:{Encoding.UTF8.GetByteCount(answer):000} CRC:{ExtractCrc(answer)}",
            $"PROFILE // ENTROPY:{random.Next(310, 489) / 100f:0.00} LAYERS:{layers}",
            $"ASSEMBLY // SLOT:{keySlot:00} DIR:{(reversed ? "REV" : "FWD")}",
        };
        if (keyReference is not null)
            metadata.Add(keyReference);
        return metadata;
    }

    private static IReadOnlyList<string> BuildDecoyMetadata(VaultRandom random, string answer, string keyPacketId)
        =>
        [
            $"FLOW // RX:{random.Hex(2)} HOPS:{random.Next(2, 5):00} TTL:00",
            $"FRAME // LEN:{Encoding.UTF8.GetByteCount(answer):000} CRC:{ExtractCrc(answer)}",
            $"PROFILE // ENTROPY:{random.Next(340, 480) / 100f:0.00} LAYERS:2",
            $"ASSEMBLY // SLOT:{random.Next(1, 5):00} DIR:FWD",
            $"KEY SOURCE // {keyPacketId}",
        ];

    private static string GenerateFrame(VaultRandom random)
    {
        var prefix = $"RX{random.Hex(2)}::{random.Pick(Subsystems)}/{random.Pick(Operations)}@{random.Hex(4)}";
        return $"{prefix}#{ComputeCrc(prefix)}";
    }

    private static string[] GenerateDistinctIds(VaultRandom random, int count)
    {
        var ids = new List<string>();
        while (ids.Count < count)
        {
            var candidate = $"PKT-{random.Hex(2)}";
            if (!ids.Contains(candidate, StringComparer.Ordinal))
                ids.Add(candidate);
        }
        return ids.ToArray();
    }

    private static string ExtractCrc(string answer)
        => answer[(answer.LastIndexOf('#') + 1)..];

    private static string ComputeCrc(string input)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..4];

    private static string EncodeHex(string input)
        => Convert.ToHexString(Encoding.UTF8.GetBytes(input));

    private static string EncodeXor(string input, string key)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] ^= keyBytes[index % keyBytes.Length];
        return Convert.ToHexString(bytes);
    }

    private static string EncodeVigenere(string input, string key)
    {
        var normalizedKey = new string(key.ToUpperInvariant().Where(character => character is >= 'A' and <= 'Z').ToArray());
        if (normalizedKey.Length == 0)
            normalizedKey = "NULL";
        var output = new StringBuilder(input.Length);
        var keyIndex = 0;
        foreach (var character in input.ToUpperInvariant())
        {
            if (character is < 'A' or > 'Z')
            {
                output.Append(character);
                continue;
            }
            var shift = normalizedKey[keyIndex++ % normalizedKey.Length] - 'A';
            output.Append((char)('A' + ((character - 'A' + shift) % 26)));
        }
        return output.ToString();
    }

    private static string EncodeColumnar(string input, string key)
    {
        var normalizedKey = new string(key.Where(character => !char.IsWhiteSpace(character)).ToArray());
        var output = new StringBuilder(input.Length);
        foreach (var item in normalizedKey
                     .Select((character, index) => (Character: char.ToUpperInvariant(character), Index: index))
                     .OrderBy(item => item.Character)
                     .ThenBy(item => item.Index))
        {
            for (var index = item.Index; index < input.Length; index += normalizedKey.Length)
                output.Append(input[index]);
        }
        return output.ToString();
    }

    private static string Reverse(string value)
        => new(value.Reverse().ToArray());

    private sealed class VaultRandom(uint seed)
    {
        private uint state = seed == 0 ? 0x9E3779B9u : seed;

        public int Next(int minimum, int maximum)
            => minimum + Next(maximum - minimum);

        public int Next(int maximum)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (int)(state % (uint)maximum);
        }

        public T Pick<T>(IReadOnlyList<T> values)
            => values[Next(values.Count)];

        public string Hex(int length)
        {
            const string alphabet = "0123456789ABCDEF";
            var output = new char[length];
            for (var index = 0; index < length; index++)
                output[index] = alphabet[Next(alphabet.Length)];
            return new string(output);
        }

        public string HexKey(int length)
        {
            const string letters = "ABCDEF";
            return letters[Next(letters.Length)] + Hex(Math.Max(0, length - 1));
        }

        public void Shuffle<T>(T[] values)
        {
            for (var index = values.Length - 1; index > 0; index--)
            {
                var target = Next(index + 1);
                (values[index], values[target]) = (values[target], values[index]);
            }
        }
    }
}

internal static class CipherVaultCrypto
{
    private const int Pbkdf2Iterations = 210_000;
    private const string SaltBase64 = "Fg0xf4EoOftgncbuhCr0VQ==";
    private const string NonceBase64 = "TMEHLZI/m0PiFzvJ";
    private const string TagBase64 = "ZpQtc6b9BqDs6JQwiy89kw==";
    private const string CiphertextBase64 = "ByA/nYl05p7BtVwJfYi/8S7NijcY94NSWgwONX8svifSdlQ95hrQ70/Yd1Biplf7sZhxi1FQrlFbOIq4AZphkAESgMnUjUVSMfpcQDApdZ7hZadD1Mb5z2Nf75EdR+NsOCbogjJMaMUo+QAlLLwQdiRSXCtryRwscMYhMQIFm7edhQTaZ2lZ4tRC9xYn/2Dc51YahQ51e3qH93MsqjVKI840+0cQjtDLuLBPEE9OpyShysXhnDJuGM4c9LZQmN0DD7TnXV8wFe4qvzofDjlLc59mQ3UqVKQkP/AorO2gkpCOG+CauiURm1GA64wAqJ5CNZCbUIewq7hceCgedm8YgHkVVTSCCECifzuPzKJ4uvVRXdQ4AuCDLmT7AioGDK48PmOw91RJ76KCzOrLr1ZlK/ubO77VkFMyXehbmhbKsAbT4nHpBnTDzg3C4Dih3+nrpsm100QhdmejNaUuSDumPbKB5aOFtHZDVjermbzrvVCWQlcqGgyM485VEY07h/Rmjuryf0dUuqEXWP+x9/aEfsMHDBQYSPQU";

    public static bool TryUnlock(string password, out CipherVaultSecrets? secrets)
    {
        secrets = null;
        if (string.IsNullOrEmpty(password))
            return false;

        var salt = Convert.FromBase64String(SaltBase64);
        var nonce = Convert.FromBase64String(NonceBase64);
        var tag = Convert.FromBase64String(TagBase64);
        var ciphertext = Convert.FromBase64String(CiphertextBase64);
        var plaintext = new byte[ciphertext.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);

        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            secrets = JsonSerializer.Deserialize<CipherVaultSecrets>(plaintext, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return secrets is not null
                && !string.IsNullOrWhiteSpace(secrets.RedemptionTokenS)
                && !string.IsNullOrWhiteSpace(secrets.RedemptionTokenA)
                && !string.IsNullOrWhiteSpace(secrets.RedemptionTokenB)
                && !string.IsNullOrWhiteSpace(secrets.RedemptionTokenC);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static bool VerifyAnswer(CipherIntercept intercept, string answer)
        => VerifyAnswerHash(intercept.AnswerHash, answer);

    public static string HashAnswer(string answer)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeAnswer(answer))));

    public static bool VerifyAnswerHash(string answerHash, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return false;
        var expected = Convert.FromBase64String(answerHash);
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeAnswer(answer)));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static bool TryRunLab(CipherLabMode mode, string input, string key, out string output)
    {
        output = string.Empty;
        try
        {
            output = mode switch
            {
                CipherLabMode.Hex => Encoding.UTF8.GetString(ParseHex(input)),
                CipherLabMode.Base64 => Encoding.UTF8.GetString(Convert.FromBase64String(input.Trim())),
                CipherLabMode.Xor => DecodeXor(input, key),
                CipherLabMode.Vigenere => DecodeVigenere(input, key),
                CipherLabMode.Columnar => DecodeColumnar(input, key),
                _ => string.Empty,
            };
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            var fault = Random.Shared.Next(3) switch
            {
                0 => "DECODER FAULT",
                1 => "BUFFER DESYNC",
                _ => "FRAME REJECTED",
            };
            output = $"DECODE ERROR // {fault} // {mode.ToString().ToUpperInvariant()}";
            return false;
        }
    }

    private static string NormalizeAnswer(string answer)
        => string.Join(' ', answer.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static byte[] ParseHex(string input)
    {
        var compact = new string(input.Where(character => !char.IsWhiteSpace(character) && character != '-').ToArray());
        if (compact.Length == 0 || compact.Length % 2 != 0)
            throw new FormatException("Hex input must contain complete byte pairs.");
        return Convert.FromHexString(compact);
    }

    private static string DecodeXor(string input, string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("XOR requires a text key.");
        var bytes = ParseHex(input);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] ^= keyBytes[index % keyBytes.Length];
        return Encoding.UTF8.GetString(bytes);
    }

    private static string DecodeVigenere(string input, string key)
    {
        var normalizedKey = new string(key.ToUpperInvariant().Where(character => character is >= 'A' and <= 'Z').ToArray());
        if (normalizedKey.Length == 0)
            throw new ArgumentException("Vigenere requires an alphabetic key.");
        var output = new StringBuilder(input.Length);
        var keyIndex = 0;
        foreach (var character in input.ToUpperInvariant())
        {
            if (character is < 'A' or > 'Z')
            {
                output.Append(character);
                continue;
            }
            var shift = normalizedKey[keyIndex++ % normalizedKey.Length] - 'A';
            output.Append((char)('A' + ((character - 'A' - shift + 26) % 26)));
        }
        return output.ToString();
    }

    private static string DecodeColumnar(string input, string key)
    {
        var normalizedKey = new string(key.Where(character => !char.IsWhiteSpace(character)).ToArray());
        if (normalizedKey.Length < 2)
            throw new ArgumentException("Columnar decoding requires a key of at least two characters.");
        if (input.Length == 0)
            throw new ArgumentException("Columnar decoding requires ciphertext.");
        var order = normalizedKey
            .Select((character, index) => (Character: char.ToUpperInvariant(character), Index: index))
            .OrderBy(item => item.Character)
            .ThenBy(item => item.Index)
            .ToArray();
        var rows = (int)Math.Ceiling(input.Length / (double)normalizedKey.Length);
        var remainder = input.Length % normalizedKey.Length;
        var columns = new string[normalizedKey.Length];
        var offset = 0;
        foreach (var item in order)
        {
            var columnLength = remainder == 0 || item.Index < remainder ? rows : rows - 1;
            columns[item.Index] = input.Substring(offset, columnLength);
            offset += columnLength;
        }
        var output = new StringBuilder(input.Length);
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns.Length; column++)
            if (row < columns[column].Length)
                output.Append(columns[column][row]);
        return output.ToString();
    }
}
