using System.Collections.Concurrent;
using System.Reflection;
using PhoneBox.Agent;
using Xunit;

public sealed class PhoneRegistryConcurrencyTests
{
    [Fact]
    public void ConcurrentRequestsReturnAnUnchangedWarningOnlyOnce()
    {
        for (var wave = 0; wave < 8; wave++)
        {
            var id = "audit" + Guid.NewGuid().ToString("N")[..16];
            try { Assert.Equal(1, PhoneRegistryConcurrencyFixture.Wave(Enumerable.Repeat(id, 32).ToArray(), old: true)); }
            finally { PhoneRegistryConcurrencyFixture.Forget([id]); }
        }
    }
}

public static class PhoneRegistryConcurrencyFixture
{
    public const string Old = "com.google.android.webview/83.0.4103.106";
    public const string Modern = "com.google.android.webview/134.0.0.0";

    public static int Wave(string[] ids, bool old)
    {
        using var ready = new CountdownEvent(ids.Length);
        using var release = new ManualResetEventSlim(false);
        var warnings = new string?[ids.Length];
        var failures = new Exception?[ids.Length];
        var threads = ids.Select((id, index) => new Thread(() =>
        {
            try
            {
                ready.Signal();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Start gate not released");
                warnings[index] = PhoneRegistry.Touch(id, "Audit phone", "USB", old ? Old : Modern);
            }
            catch (Exception ex) { failures[index] = ex; }
        }) { IsBackground = true }).ToArray();
        try
        {
            foreach (var thread in threads) thread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Workers not ready");
            release.Set();
            foreach (var thread in threads)
                if (!thread.Join(TimeSpan.FromSeconds(10))) throw new Exception("Worker did not finish");
            if (failures.FirstOrDefault(e => e is not null) is { } failure) throw failure;
            return warnings.Count(w => w is not null);
        }
        finally
        {
            release.Set();
            foreach (var thread in threads) if (thread.IsAlive) thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    public static void Forget(IEnumerable<string> ids)
    {
        // This process-only registry has no public reset. Remove only fixture-owned random IDs after all joins.
        var fields = BindingFlags.NonPublic | BindingFlags.Static;
        var seen = (ConcurrentDictionary<string, PhoneSeen>)typeof(PhoneRegistry).GetField("Seen", fields)!.GetValue(null)!;
        var warned = (ConcurrentDictionary<string, string?>)typeof(PhoneRegistry).GetField("Warned", fields)!.GetValue(null)!;
        foreach (var id in ids) { seen.TryRemove(id, out _); warned.TryRemove(id, out _); }
    }
}
