using System.IO.Hashing;
using System.Text;

namespace Tallyhouse.Kernel.Ingestion;

/// <summary>
/// Every query groups by user, and grouping a hundred million rows by a 64-bit integer is several times
/// cheaper than grouping them by a string. The key is XxHash3 of the identity, which is stable across
/// processes and releases. At a million distinct users the chance that any two share a key is about
/// 3 in 100 million, which an analytics count can carry.
/// </summary>
public static class UserIdentity
{
    public static ulong KeyOf(string? userId, string? anonymousId)
    {
        // A known user and an anonymous visitor with the same identifier string are different people, so the
        // namespace is part of what is hashed.
        (byte prefix, string id) = !string.IsNullOrEmpty(userId)
            ? ((byte)'u', userId)
            : ((byte)'a', anonymousId ?? throw new ArgumentException("An event needs a userId or an anonymousId."));

        if (id.Length > Names.MaxUserIdLength)
        {
            throw new ArgumentException($"Identifiers are limited to {Names.MaxUserIdLength} characters.", nameof(userId));
        }

        // At most 256 UTF-16 characters, so at most 768 UTF-8 bytes: always fits on the stack.
        Span<byte> buffer = stackalloc byte[(Names.MaxUserIdLength * 3) + 1];
        buffer[0] = prefix;
        int written = Encoding.UTF8.GetBytes(id, buffer[1..]);

        return XxHash3.HashToUInt64(buffer[..(written + 1)]);
    }
}
