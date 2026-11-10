namespace KennectComputer.Host.Auth;

/// <summary>Unpadded base64url (RFC 4648 section 5), used for session tokens.</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static byte[] Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - (base64.Length % 4)) % 4);
        return Convert.FromBase64String(base64);
    }
}
