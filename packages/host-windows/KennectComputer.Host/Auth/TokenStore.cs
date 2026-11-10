using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Auth;

/// <summary>
/// Owns <c>session.token</c> and <c>session.json</c> in the host state directory.
/// The token is 32 random bytes encoded as unpadded base64url and is readable only
/// by the current user.
/// </summary>
public sealed class TokenStore
{
    private const string TokenFileName = "session.token";
    private const string SessionFileName = "session.json";
    private const int TokenByteLength = 32;

    private readonly object _gate = new();

    public TokenStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        StateDirectory = stateDirectory;
        Directory.CreateDirectory(stateDirectory);
        TokenPath = Path.Combine(stateDirectory, TokenFileName);
        SessionInfoPath = Path.Combine(stateDirectory, SessionFileName);
    }

    public string StateDirectory { get; }

    public string TokenPath { get; }

    public string SessionInfoPath { get; }

    /// <summary>Creates a store rooted at <c>%LOCALAPPDATA%\KennectComputer</c>.</summary>
    public static TokenStore CreateDefault()
        => new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KennectComputer"));

    /// <summary>Creates a store at an explicit directory, used by tests.</summary>
    public static TokenStore CreateForTests(string stateDirectory) => new(stateDirectory);

    /// <summary>Replaces the on-disk token with fresh 32-byte randomness and returns it.</summary>
    public string Rotate()
    {
        lock (_gate)
        {
            var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(TokenByteLength));
            WritePrivateFile(TokenPath, token);
            return token;
        }
    }

    /// <summary>Constant-time check of a candidate token against the stored token.</summary>
    public bool Validate(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        string stored;
        lock (_gate)
        {
            if (!File.Exists(TokenPath))
            {
                return false;
            }

            stored = File.ReadAllText(TokenPath, Encoding.UTF8).Trim();
        }

        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        byte[] expected;
        byte[] candidate;
        try
        {
            expected = Base64Url.Decode(stored);
            candidate = Base64Url.Decode(token);
        }
        catch (FormatException)
        {
            return false;
        }

        return expected.Length == candidate.Length
            && CryptographicOperations.FixedTimeEquals(expected, candidate);
    }

    /// <summary>Writes <c>session.json</c> so clients can discover the pipe.</summary>
    public void WriteSessionInfo(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var session = new SessionInfo
        {
            PipeName = pipeName,
            Pid = Environment.ProcessId,
            ProtocolVersion = HostInfo.ProtocolVersion,
            StartedAt = DateTimeOffset.UtcNow,
        };

        lock (_gate)
        {
            WritePrivateFile(SessionInfoPath, JsonSerializer.Serialize(session, ProtocolJson.Options));
        }
    }

    public void DeleteSessionInfo()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(SessionInfoPath);
            }
            catch (IOException)
            {
                // A stale discovery file is harmless; the next run overwrites it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Writes through a temporary file so readers never observe a partial token,
    /// then applies an ACL granting full control to the current user only.
    /// </summary>
    private static void WritePrivateFile(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        RestrictToCurrentUser(temporaryPath);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current Windows identity has no SID.");

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.SetOwner(user);

        new FileInfo(path).SetAccessControl(security);
    }
}
