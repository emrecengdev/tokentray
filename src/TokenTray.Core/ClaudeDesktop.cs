using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TokenTray.Core;

/// <summary>
/// The Claude desktop app's own sign-in, which it keeps fresh while it runs. It sits in the app's
/// config.json encrypted the Chromium way: an AES-256-GCM key in "Local State", wrapped with
/// Windows DPAPI for the current user, and values stored as "v10" + nonce + ciphertext + tag.
/// Read-only: nothing here writes to the app's files.
/// </summary>
public static class ClaudeDesktop
{
    static readonly string[] CacheKeys = ["oauth:tokenCacheV2", "oauth:tokenCache"];

    public sealed record Token(string AccessToken, DateTimeOffset? ExpiresAt, string? AccountId, string? Plan = null);

    /// <summary>Regular install first, then Microsoft Store packages.</summary>
    public static IEnumerable<string> ConfigPaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "config.json");
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(packages, "Claude_*").OrderBy(d => !d.EndsWith("Claude_pzs8sxrjxfjjc")).ThenBy(d => d))
            yield return Path.Combine(dir, "LocalCache", "Roaming", "Claude", "config.json");
    }

    /// <summary>The best token that passes <paramref name="accept"/>, across every install and cache.</summary>
    public static Token? Read(Func<Token, bool> accept)
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var config in ConfigPaths().Where(File.Exists))
        {
            try
            {
                var key = OsCryptKey(Path.Combine(Path.GetDirectoryName(config)!, "Local State"));
                if (key is null) continue;
                using var doc = JsonDocument.Parse(ReadAllShared(config));
                foreach (var name in CacheKeys)
                {
                    if (Http.Str(doc.RootElement, name) is not { Length: > 0 } value) continue;
                    if (Decrypt(value, key) is { } plain && Select(Encoding.UTF8.GetString(plain), accept) is { } token) return token;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or FormatException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        }
        return null;
    }

    /// <summary>
    /// Entries are keyed "accountId:orgId:baseUrl:scope scope…". Prefer a token that can read usage
    /// (user:inference and user:profile), then the latest expiry.
    /// </summary>
    public static Token? Select(string plaintext, Func<Token, bool>? accept = null)
    {
        using var doc = JsonDocument.Parse(plaintext);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        return doc.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Object)
            .Select(p => (p.Name, p.Value, token: Http.Str(p.Value, "token"), expires: Http.Number(p.Value, "expiresAt")))
            .Where(e => !string.IsNullOrWhiteSpace(e.token))
            .Select(e =>
            {
                var inference = e.Name.Contains("user:inference");
                var profile = e.Name.Contains("user:profile");
                // "acct:<account uuid>|<org uuid>:…"
                var m = System.Text.RegularExpressions.Regex.Match(e.Name, @"^acct:([0-9a-fA-F-]{36})");
                DateTimeOffset? exp = e.expires is { } ms ? Http.Unix(ms) : null;
                var plan = ClaudeProvider.PlanName(Http.Str(e.Value, "subscriptionType"), Http.Str(e.Value, "rateLimitTier"));
                return (rank: (inference && profile ? 2 : inference ? 1 : 0), token: new Token(e.token!, exp, m.Success ? m.Groups[1].Value : null, plan));
            })
            .Where(x => x.rank > 0 && (accept is null || accept(x.token)))
            .OrderByDescending(x => x.rank)
            .ThenByDescending(x => x.token.ExpiresAt ?? DateTimeOffset.MinValue)
            .Select(x => x.token)
            .FirstOrDefault();
    }

    static byte[]? OsCryptKey(string localState)
    {
        if (!File.Exists(localState)) return null;
        using var doc = JsonDocument.Parse(ReadAllShared(localState));
        if (!doc.RootElement.TryGetProperty("os_crypt", out var os) || Http.Str(os, "encrypted_key") is not { } encoded) return null;
        var wrapped = Convert.FromBase64String(encoded);
        if (wrapped.Length < 6 || Encoding.ASCII.GetString(wrapped, 0, 5) != "DPAPI") return null;
        return Unprotect(wrapped[5..]);
    }

    static byte[]? Decrypt(string value, byte[] key)
    {
        var blob = Convert.FromBase64String(value);
        if (blob.Length < 3 + 12 + 16 || Encoding.ASCII.GetString(blob, 0, 3) != "v10") return null;
        var nonce = blob.AsSpan(3, 12);
        var tag = blob.AsSpan(blob.Length - 16);
        var cipher = blob.AsSpan(15, blob.Length - 15 - 16);
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    static string ReadAllShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32", SetLastError = true)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("kernel32")] static extern IntPtr LocalFree(IntPtr mem);

    static byte[]? Unprotect(byte[] data)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1 /* UI_FORBIDDEN */, ref output)) return null;
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}
