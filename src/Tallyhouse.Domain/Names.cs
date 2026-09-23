namespace Tallyhouse.Domain;

/// <summary>
/// What an identifier may look like. These run for every event on the ingest path, so they are plain loops
/// rather than regular expressions, and they bound every length a client controls.
/// </summary>
public static class Names
{
    public const int MaxEventNameLength = 128;
    public const int MaxPropertyNameLength = 64;
    public const int MaxMessageIdLength = 128;
    public const int MaxUserIdLength = 256;

    /// <summary>Starts with a letter; then letters, digits, <c>_ . : -</c>. Matches what SQL and URLs carry unescaped.</summary>
    public static bool IsValidEventName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || name.Length > MaxEventNameLength || !char.IsAsciiLetter(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or ':' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidPropertyName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || name.Length > MaxPropertyNameLength || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Printable ASCII without spaces. UUIDs, ULIDs and KSUIDs all fit; anything that would need escaping in a
    /// Redis key or a log line does not.
    /// </summary>
    public static bool IsValidMessageId(ReadOnlySpan<char> id)
    {
        if (id.IsEmpty || id.Length > MaxMessageIdLength)
        {
            return false;
        }

        foreach (char c in id)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>User identifiers are opaque, so only length and control characters are policed.</summary>
    public static bool IsValidUserId(ReadOnlySpan<char> id)
    {
        if (id.IsEmpty || id.Length > MaxUserIdLength)
        {
            return false;
        }

        foreach (char c in id)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }
}
