using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DigiChat.Infrastructure.Twitch;

public sealed record StoredTokens(string AccessToken, string RefreshToken, DateTime ObtainedUtc);

/// <summary>
/// Persists OAuth tokens locally, encrypted with Windows DPAPI (current user).
/// The file is gitignored; tokens are never logged. No client secret exists —
/// the Twitch app is registered as a Public client using the device code flow.
/// </summary>
[SupportedOSPlatform("windows")] // DPAPI — this is a Windows-only application
public class TwitchTokenStore(ILogger<TwitchTokenStore> logger)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DigiChat.TwitchTokens.v1");

    public StoredTokens? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var raw = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredTokens>(raw);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stored Twitch tokens could not be read; re-authorization will be required");
            return null;
        }
    }

    public void Save(string path, StoredTokens tokens)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(tokens);
        var protectedBytes = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temporary = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory replacement avoids a partially written token file
            // if the process dies while persisting a rotated refresh token.
            if (File.Exists(fullPath))
                File.Replace(temporary, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        logger.LogInformation("Twitch tokens saved (DPAPI-protected) to {Path}", fullPath);
    }

    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
