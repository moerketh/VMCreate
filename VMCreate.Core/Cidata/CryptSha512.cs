using System;
using System.Security.Cryptography;
using System.Text;

namespace VMCreate.Cidata;

/// <summary>
/// Pure C# implementation of the SHA-512 based Unix crypt algorithm ("$6$"),
/// compatible with "openssl passwd -6" and glibc's crypt(), per Ulrich Drepper's
/// "Unix crypt using SHA-256 and SHA-512" specification. Used to generate
/// password hashes consumed by the Omarchy cidata seed for unattended installs.
/// </summary>
public static class CryptSha512
{
    /// <summary>Default round count used when the salt carries no explicit rounds= specifier.</summary>
    public const int DefaultRounds = 5000;

    private const int SaltLength = 16;
    private const int DigestLength = 64;
    private const string B64 = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// Compute a "$6$" password hash for the given password and salt. When
    /// <paramref name="rounds"/> is null the default round count of 5000 is used without a
    /// "rounds=" prefix; supplying a value mirrors an explicit "rounds=N" salt specifier,
    /// including clamping to the [1000, 999999999] range.
    /// </summary>
    public static string Hash(string password, string salt, int? rounds = null) =>
        Hash(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt), rounds);

    /// <summary>Compute a "$6$" password hash for the given password and salt bytes.</summary>
    public static string Hash(byte[] password, byte[] salt, int? rounds = null)
    {
        bool roundsCustom = rounds.HasValue;
        int roundsUsed = Math.Clamp(rounds ?? DefaultRounds, 1000, 999_999_999);

        // The salt is truncated at the first '$' and to at most 16 characters.
        int dollarIndex = Array.IndexOf(salt, (byte)'$');
        int maxSaltLength = dollarIndex < 0 ? salt.Length : dollarIndex;
        int saltLength = Math.Min(maxSaltLength, SaltLength);
        byte[] saltBytes = saltLength == salt.Length ? salt : salt[..saltLength];

        // 4-8: digest B = SHA512(password + salt + password).
        byte[] digestB = HashData(password, saltBytes, password);

        // 1-3: digest A starts with password + salt.
        using var digestA = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        AddBytes(digestA, password);
        AddBytes(digestA, saltBytes);

        // 9-10: for each 64-byte block of the password add digest B, then the first
        // N bytes of digest B for the remaining N bytes of the password.
        int fullBlocks = password.Length / DigestLength;
        for (int i = 0; i < fullBlocks; i++)
            AddBytes(digestA, digestB);
        int remaining = password.Length % DigestLength;
        if (remaining > 0)
            AddBytes(digestA, digestB, remaining);

        // 11: for each bit of the binary representation of the password length up to
        // and including the highest 1-digit, starting from the lowest bit position.
        for (int bit = 1; bit <= password.Length && bit > 0; bit <<= 1)
        {
            if ((password.Length & bit) != 0)
                AddBytes(digestA, digestB);
            else
                AddBytes(digestA, password);
        }

        // 12: finish digest A.
        byte[] digestABytes = digestA.GetHashAndReset();

        // 13-15: digest DP = for every byte in the password, add the password again.
        int dpRounds = password.Length;
        byte[][] dpParts = new byte[dpRounds][];
        for (int i = 0; i < dpRounds; i++)
            dpParts[i] = password;
        byte[] digestDP = HashData(dpParts);

        // 16: byte sequence P of the same length as the password, repeating digest DP.
        byte[] pBytes = RepeatDigest(password.Length, digestDP);

        // 17-19: digest DS = salt added 16+A[0] times.
        int dsRounds = 16 + digestABytes[0];
        byte[][] dsParts = new byte[dsRounds][];
        for (int i = 0; i < dsRounds; i++)
            dsParts[i] = saltBytes;
        byte[] digestDS = HashData(dsParts);

        // 20: byte sequence S of the same length as the salt, repeating digest DS.
        byte[] sBytes = RepeatDigest(saltBytes.Length, digestDS);

        // 21: rounds of digest C. First round uses digest A; later rounds use the
        // previous round's digest.
        byte[] current = digestABytes;
        for (int round = 0; round < roundsUsed; round++)
        {
            bool odd = (round & 1) != 0;
            bool notDivBy3 = (round % 3) != 0;
            bool notDivBy7 = (round % 7) != 0;

            using var digestC = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            if (odd)
                AddBytes(digestC, pBytes);
            else
                AddBytes(digestC, current);
            if (notDivBy3)
                AddBytes(digestC, sBytes);
            if (notDivBy7)
                AddBytes(digestC, pBytes);
            if (odd)
                AddBytes(digestC, current);
            else
                AddBytes(digestC, pBytes);
            current = digestC.GetHashAndReset();
        }

        // 22: assemble the output string.
        return string.Concat(
            "$6$",
            roundsCustom ? $"rounds={roundsUsed}$" : "",
            Encoding.ASCII.GetString(saltBytes),
            "$",
            EncodeBase64Permuted(current));
    }

    private static byte[] HashData(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var part in parts)
            hash.AppendData(part);
        return hash.GetHashAndReset();
    }

    private static void AddBytes(IncrementalHash hash, byte[] data, int count) => hash.AppendData(data, 0, count);

    private static void AddBytes(IncrementalHash hash, byte[] data) => hash.AppendData(data);

    /// <summary>
    /// Produce a byte sequence of <paramref name="length"/> bytes by repeating
    /// <paramref name="digest"/>, truncated at the end (steps 16b and 20b).
    /// </summary>
    private static byte[] RepeatDigest(int length, byte[] digest)
    {
        var result = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int n = Math.Min(digest.Length, length - offset);
            Array.Copy(digest, 0, result, offset, n);
            offset += n;
        }
        return result;
    }

    /// <summary>
    /// Base64-like encoding using the crypt alphabet with the SHA-512 byte
    /// permutation specified in step 22e.
    /// </summary>
    private static string EncodeBase64Permuted(byte[] digest)
    {
        var sb = new StringBuilder(21 * 4 + 2);
        int p1 = 0, p2 = 21, p3 = 42;
        for (int group = 0; group < 21; group++)
        {
            uint word = ((uint)digest[p1] << 16) | ((uint)digest[p2] << 8) | digest[p3];
            AppendWordChars(sb, word, 4);
            int previous = p1;
            p1 = p2 + 1;
            p2 = p3 + 1;
            p3 = previous + 1;
        }

        AppendWordChars(sb, digest[p3], 2);
        return sb.ToString();
    }

    /// <summary>Append the low <paramref name="count"/> 6-bit groups of <paramref name="word"/> as characters.</summary>
    private static void AppendWordChars(StringBuilder sb, uint word, int count)
    {
        for (int i = 0; i < count; i++)
        {
            sb.Append(B64[(int)(word & 0x3f)]);
            word >>= 6;
        }
    }
}