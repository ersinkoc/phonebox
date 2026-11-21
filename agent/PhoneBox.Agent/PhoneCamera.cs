using System.Security.Cryptography;

namespace PhoneBox.Agent;

/// <summary>One explicitly approved camera session; never persists frames or consent.</summary>
public sealed class PhoneCamera : IDisposable
{
    public const int MaxFrameBytes = 512 * 1024;
    private readonly object gate = new();
    private readonly Func<long> now;
    private readonly System.Threading.Timer? timer;
    private string? request, device, secret, owner;
    private long deadline;
    private byte[]? frame;
    private int rotation;

    public PhoneCamera() : this(() => Environment.TickCount64, true) { }
    internal PhoneCamera(Func<long> clock, bool automaticExpiry = false)
    {
        now = clock;
        if (automaticExpiry) timer = new System.Threading.Timer(_ => { lock (gate) Expire(); }, null, 500, 500);
    }

    private void Expire() { if (request != null && now() >= deadline) Clear(); }
    private void Clear() { request = device = secret = owner = null; frame = null; rotation = 0; }
    public void Stop() { lock (gate) Clear(); }

    public bool Request(string phone)
    {
        if (string.IsNullOrEmpty(phone) || phone.Length > 32 || !phone.All(char.IsAsciiLetterOrDigit)) return false;
        lock (gate)
        {
            Expire();
            if (request != null) return false;
            device = phone;
            request = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            deadline = now() + 30_000;
            return true;
        }
    }

    public object Status()
    {
        lock (gate) { Expire(); return new { state = request == null ? "off" : secret == null ? "waiting" : "live", device }; }
    }

    public object Poll(string phone)
    {
        lock (gate) { Expire(); return new { request = device == phone && secret == null ? request : null, pc = Environment.MachineName }; }
    }

    // The device header is a routing hint, not an identity. The extra capability is returned only once,
    // after native consent, and subsequent uploads must also use the same authenticated transport credential.
    public string? Approve(string phone, string id, string credential)
    {
        lock (gate)
        {
            Expire();
            if (request != id || device != phone || secret != null || credential.Length == 0) return null;
            owner = credential;
            secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            deadline = now() + 5_000;
            return secret;
        }
    }

    private bool Matches(string phone, string id, string key, string credential) =>
        request == id && device == phone && secret != null && owner == credential && key.Length == secret.Length &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(key), System.Text.Encoding.ASCII.GetBytes(secret));

    public bool Upload(string phone, string id, string key, string credential, byte[] jpeg, int angle)
    {
        lock (gate)
        {
            Expire();
            if (!Matches(phone, id, key, credential) || jpeg.Length is < 4 or > MaxFrameBytes ||
                jpeg[0] != 0xff || jpeg[1] != 0xd8 || jpeg[^2] != 0xff || jpeg[^1] != 0xd9 || angle is not (0 or 90 or 180 or 270)) return false;
            frame = jpeg;
            rotation = angle;
            deadline = now() + 3_000;
            return true;
        }
    }

    public bool End(string phone, string id, string key, string credential)
    {
        lock (gate)
        {
            Expire();
            if (request != id || device != phone || (secret != null && !Matches(phone, id, key, credential))) return false;
            Clear();
            return true;
        }
    }

    public (byte[]? Jpeg, int Rotation) Frame()
    {
        lock (gate) { Expire(); return (frame, rotation); }
    }

    public void Dispose() { timer?.Dispose(); Stop(); }
}
