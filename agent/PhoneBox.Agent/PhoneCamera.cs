using System.Security.Cryptography;

namespace PhoneBox.Agent;

/// <summary>One explicitly approved camera session; never persists frames or consent.</summary>
public sealed class PhoneCamera : IDisposable
{
    public const int MaxFrameBytes = 1536 * 1024; // a 1920 × 1080 JPEG with headroom
    private readonly object gate = new();
    private readonly Func<long> now;
    private readonly System.Threading.Timer? timer;
    private string? request, device, secret, owner;
    private long deadline;
    private byte[]? frame;
    private int rotation;
    private long sequence;

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

    /// <summary>A request is waiting for the phone, or video is live.</summary>
    public bool Active { get { lock (gate) { Expire(); return request != null; } } }

    /// <summary>
    /// The phone to ask for video: the one most recently heard from over the USB cable (video never goes over Wi-Fi),
    /// or null when no phone was on USB in the last 15 seconds.
    /// </summary>
    public static string? UsbPhone(IEnumerable<PhoneSeen> phones, long nowMs) =>
        phones.Where(p => p.Via == "USB" && nowMs - p.LastSeen < 15_000).MaxBy(p => p.LastSeen)?.Id;

    public static bool IsUsbPhone(IEnumerable<PhoneSeen> phones, string id, long nowMs) =>
        phones.Any(p => p.Id == id && p.Via == "USB" && nowMs - p.LastSeen < 15_000);

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
            sequence++;
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

    /// <summary>The current frame. Sequence changes with every accepted upload, so a reader can skip an unchanged one.</summary>
    public (byte[]? Jpeg, int Rotation, long Sequence) Frame()
    {
        lock (gate) { Expire(); return (frame, rotation, frame == null ? 0 : sequence); }
    }

    public void Dispose() { timer?.Dispose(); Stop(); }
}
