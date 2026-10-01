using System.Security.Cryptography;
using System.Text;
using SubtitleStudio.Models;

namespace SubtitleStudio.Services;

/// <summary>
/// DPAPI (CurrentUser scope) protection for API keys. The stored form is "dpapi:&lt;base64&gt;".
/// Blobs only decrypt for the same Windows account on the same PC, so moving the portable
/// folder elsewhere means re-entering keys (the UI says so).
/// </summary>
public sealed class SecureKeyService
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SubtitleStudio.ApiKeys.v1");

    public string Protect(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    public bool TryUnprotect(string? stored, out string plainText)
    {
        plainText = string.Empty;
        if (string.IsNullOrWhiteSpace(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        try
        {
            var protectedBytes = Convert.FromBase64String(stored[Prefix.Length..]);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            plainText = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>"sk-••••••••a1B2" style mask. Short keys are fully masked.</summary>
    public static string Mask(string key)
    {
        const string dots = "••••••••";
        if (string.IsNullOrEmpty(key)) return string.Empty;
        return key.Length <= 10 ? dots : key[..3] + dots + key[^4..];
    }
}

public enum ApiKeyState
{
    NotSet,
    Saved,
    Unreadable,
}

/// <summary>
/// The one place engines get keys from. Keys are decrypted on demand and never cached in plain text.
/// </summary>
public sealed class ApiKeyStore
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly SecureKeyService _crypto;

    public ApiKeyStore(AppSettings settings, SettingsService settingsService, SecureKeyService crypto)
    {
        _settings = settings;
        _settingsService = settingsService;
        _crypto = crypto;
    }

    public ApiKeyState GetState(string providerId, out string? plainKey)
    {
        plainKey = null;
        if (!_settings.ApiKeys.TryGetValue(Normalize(providerId), out var stored))
            return ApiKeyState.NotSet;

        if (_crypto.TryUnprotect(stored, out var key))
        {
            plainKey = key;
            return ApiKeyState.Saved;
        }
        return ApiKeyState.Unreadable;
    }

    /// <summary>For engines: the decrypted key, or null when missing/unreadable.</summary>
    public string? GetKey(string providerId) => GetState(providerId, out var key) == ApiKeyState.Saved ? key : null;

    public void Set(string providerId, string plainKey)
    {
        _settings.ApiKeys[Normalize(providerId)] = _crypto.Protect(plainKey.Trim());
        _settingsService.ScheduleSave(_settings);
    }

    public void Remove(string providerId)
    {
        if (_settings.ApiKeys.Remove(Normalize(providerId)))
            _settingsService.ScheduleSave(_settings);
    }

    private static string Normalize(string providerId) => providerId.Trim().ToLowerInvariant();
}
