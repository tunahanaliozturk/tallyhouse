using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Tallyhouse.Infrastructure.Catalog;

/// <summary>
/// Project keys: 256 random bits with a prefix that says what the key is for, so one pasted into the wrong
/// place is recognisable in a log or a secret scanner. Only the SHA-256 is stored.
/// </summary>
/// <remarks>
/// A key is found by looking its hash up in a dictionary rather than by comparing it to every stored hash in
/// constant time. That is safe here and would not be for a password: timing can at best reveal a prefix of
/// the hash of a guess, and an attacker cannot choose guesses whose hashes share a prefix with a real key's.
/// </remarks>
public static class ApiKeys
{
    public const string WritePrefix = "thw_";
    public const string ReadPrefix = "thr_";

    public static string NewWriteKey() => WritePrefix + Random();

    public static string NewReadKey() => ReadPrefix + Random();

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public static string HashHex(ReadOnlySpan<char> key)
    {
        // Keys are 47 ASCII characters; anything much longer is not a key and is not worth hashing.
        if (key.Length > 128)
        {
            return string.Empty;
        }

        Span<byte> utf8 = stackalloc byte[key.Length * 3];
        int length = Encoding.UTF8.GetBytes(key, utf8);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(utf8[..length], hash);
        return Convert.ToHexString(hash);
    }

    /// <summary>Compares a presented token to a configured one without leaking where they differ.</summary>
    public static bool FixedTimeEquals(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        // Hashing first makes the comparison length-independent as well.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    private static string Random()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }
}
