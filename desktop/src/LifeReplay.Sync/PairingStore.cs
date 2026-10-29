using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LifeReplay.Core;

namespace LifeReplay.Sync;

/// <summary>
/// Paired devices. Only a SHA-256 hash of each access token is stored; the token itself is shown once in the
/// pairing QR code and kept by the phone in its Keychain.
/// </summary>
public sealed class PairingStore
{
    public sealed class PairedDevice
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string TokenHash { get; set; } = "";
        public long CreatedUtcMs { get; set; }
        public long? LastUsedUtcMs { get; set; }
    }

    private sealed class FileModel { public List<PairedDevice> Devices { get; set; } = []; }

    private readonly string _path;
    private readonly object _gate = new();
    private FileModel _model;

    public PairingStore(string syncDir)
    {
        Directory.CreateDirectory(syncDir);
        _path = Path.Combine(syncDir, "pairing.json");
        _model = Load();
    }

    private FileModel Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), RecorderConfig.JsonOptions) ?? new() : new(); }
        catch (Exception ex) { Log.Warn("pairing.json unreadable; no devices paired", ex); return new(); }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_model, RecorderConfig.JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }

    public IReadOnlyList<PairedDevice> Devices { get { lock (_gate) return _model.Devices.ToList(); } }

    /// <summary>Creates a new 256-bit token for a device. Returns the token (shown once).</summary>
    public string CreateToken(string name)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            _model.Devices.Add(new PairedDevice { Name = name, TokenHash = Hash(token), CreatedUtcMs = TimeUtil.NowMs() });
            Save();
        }
        return token;
    }

    public bool Validate(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return false;
        var candidate = Convert.FromHexString(Hash(token));
        lock (_gate)
        {
            bool ok = false;
            foreach (var d in _model.Devices)
            {
                // Fixed-time compare against every entry so timing doesn't reveal anything.
                if (CryptographicOperations.FixedTimeEquals(candidate, Convert.FromHexString(d.TokenHash)))
                {
                    ok = true;
                    d.LastUsedUtcMs = TimeUtil.NowMs();
                }
            }
            if (ok) { try { Save(); } catch { } }
            return ok;
        }
    }

    public void RevokeAll()
    {
        lock (_gate) { _model.Devices.Clear(); Save(); }
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
