using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Intel GPU load on Linux from DRM fdinfo (issue #224): i915 and xe have no gpu_busy_percent, so the load is added up
/// from every client's usage counters between two samples, as nvtop does.
/// </summary>
public sealed class DrmUsageTests
{
    private const string Igpu = "0000:00:02.0";

    private static string I915(string client, ulong render, ulong video = 0, string pdev = Igpu) => $"""
        pos:	0
        flags:	02100002
        mnt_id:	26
        ino:	685
        drm-driver:	i915
        drm-client-id:	{client}
        drm-pdev:	{pdev}
        drm-total-system0:	12288 KiB
        drm-shared-system0:	0
        drm-active-system0:	0
        drm-resident-system0:	12288 KiB
        drm-purgeable-system0:	0
        drm-memory-system0:	12288 KiB
        drm-engine-render:	{render} ns
        drm-engine-copy:	0 ns
        drm-engine-video:	{video} ns
        drm-engine-capacity-video:	2
        drm-engine-video-enhance:	0 ns
        """.Replace("\r\n", "\n");

    private static string Xe(string client, ulong rcs, ulong total, ulong vcs = 0, string pdev = Igpu) => $"""
        pos:	0
        flags:	0100002
        mnt_id:	26
        ino:	1061
        drm-driver:	xe
        drm-client-id:	{client}
        drm-pdev:	{pdev}
        drm-total-system:	0
        drm-shared-system:	0
        drm-active-system:	0
        drm-resident-system:	0
        drm-purgeable-system:	0
        drm-total-gtt:	192 KiB
        drm-shared-gtt:	0
        drm-active-gtt:	0
        drm-resident-gtt:	192 KiB
        drm-cycles-rcs:	{rcs}
        drm-total-cycles-rcs:	{total}
        drm-cycles-bcs:	0
        drm-total-cycles-bcs:	{total}
        drm-cycles-vcs:	{vcs}
        drm-total-cycles-vcs:	{total}
        drm-engine-capacity-vcs:	2
        drm-cycles-vecs:	0
        drm-total-cycles-vecs:	{total}
        drm-cycles-ccs:	0
        drm-total-cycles-ccs:	{total}
        drm-engine-capacity-ccs:	4
        """.Replace("\r\n", "\n");

    private static DrmClient P(string text) => DrmUsage.ParseFdinfo(text) ?? throw new InvalidOperationException("not a DRM client");

    /// <summary>The load after two samples, <paramref name="seconds"/> apart.</summary>
    private static double? Load(IEnumerable<DrmClient> prev, IEnumerable<DrmClient> cur, double seconds = 1, string pdev = Igpu)
    {
        var meter = new DrmUsage.Meter();
        meter.Update(prev, 0, [pdev]);
        return Value(meter.Update(cur, seconds, [pdev]), pdev);
    }

    private static double? Value(Dictionary<string, double> load, string pdev = Igpu) => load.TryGetValue(pdev, out var v) ? v : null;

    [Fact]
    public void AnI915FdinfoIsParsedWithoutItsMemoryLines()
    {
        var c = P(I915("7", 25662044495, 1200));
        Assert.Equal("i915", c.Driver);
        Assert.Equal("7", c.ClientId);
        Assert.Equal(Igpu, c.Pdev);
        Assert.Equal(new[] { "copy", "render", "video", "video-enhance" }, c.Busy.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(25662044495UL, c.Busy["render"]);
        Assert.Equal(1200UL, c.Busy["video"]);
        Assert.Empty(c.TotalCycles); // i915 counts nanoseconds
        Assert.Equal(2, Assert.Single(c.Capacity).Value); // "capacity-video" is not an engine of its own
        Assert.Equal("video", c.Capacity.Keys.Single());
    }

    [Fact]
    public void AnXeFdinfoIsParsedIntoCyclesAndTotals()
    {
        var c = P(Xe("19", 28257900, 7655183225, vcs: 500));
        Assert.Equal("xe", c.Driver);
        Assert.Equal("19", c.ClientId);
        Assert.Equal(Igpu, c.Pdev);
        Assert.Equal(new[] { "bcs", "ccs", "rcs", "vcs", "vecs" }, c.Busy.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(28257900UL, c.Busy["rcs"]);
        Assert.Equal(500UL, c.Busy["vcs"]);
        Assert.Equal(7655183225UL, c.TotalCycles["rcs"]); // and not "drm-total-system" or "drm-total-gtt"
        Assert.Equal(5, c.TotalCycles.Count);
        Assert.Equal(2, c.Capacity["vcs"]);
        Assert.Equal(4, c.Capacity["ccs"]);
    }

    [Theory]
    [InlineData("pos:\t0\nflags:\t02\nmnt_id:\t15\nino:\t1033\n")] // a regular file
    [InlineData("drm-driver:\ti915\ndrm-pdev:\t0000:00:02.0\n")] // no client id
    [InlineData("")]
    public void OtherFdinfoIsNotAClient(string text) => Assert.Null(DrmUsage.ParseFdinfo(text));

    [Fact]
    public void I915LoadIsBusyNanosecondsOverTheInterval()
    {
        // render: 0.5 s busy in 1 s = 50 %; video: 0.8 s busy on 2 engines = 40 %; the busiest class wins
        var prev = new[] { P(I915("7", 1_000_000_000, 100_000_000)) };
        var cur = new[] { P(I915("7", 1_500_000_000, 900_000_000)) };
        Assert.Equal(50, Load(prev, cur));
        Assert.Equal(25, Load(prev, cur, seconds: 2));
    }

    [Fact]
    public void XeLoadIsBusyCyclesOverTotalCycles()
    {
        // rcs: 300 of 1200 cycles = 25 %; vcs: 1080 of 1200 on 2 engines = 45 %; the wall clock doesn't matter
        var prev = new[] { P(Xe("19", 1000, 50_000, vcs: 20)) };
        var cur = new[] { P(Xe("19", 1300, 51_200, vcs: 1100)) };
        Assert.Equal(45, Load(prev, cur, seconds: 2));
        Assert.Equal(45, Load(prev, cur, seconds: 7));
    }

    [Fact]
    public void ClientsAddUpAndTwoFdsOfOneClientCountOnce()
    {
        var prev = new[] { P(Xe("19", 0, 1000)), P(Xe("19", 0, 1000)), P(Xe("20", 0, 1000)) };
        var cur = new[] { P(Xe("19", 300, 2000)), P(Xe("19", 300, 2000)), P(Xe("20", 200, 2000)) };
        Assert.Equal(50, Load(prev, cur)); // 30 % + 20 %, not 30 % + 30 % + 20 %
    }

    [Fact]
    public void OnlyClientsInBothSamplesAndOnThisCardCount()
    {
        var prev = new[] { P(I915("1", 0)), P(I915("2", 0)), P(I915("3", 0, pdev: "0000:03:00.0")) };
        var cur = new[] { P(I915("1", 200_000_000)), P(I915("4", 900_000_000)), P(I915("3", 900_000_000, pdev: "0000:03:00.0")) };
        // client 2 is gone, client 4 is new (no baseline yet), client 3 is on another card
        Assert.Equal(20, Load(prev, cur));
        Assert.Equal(90, Load(prev, cur, pdev: "0000:03:00.0"));
    }

    [Fact]
    public void ACounterThatWentBackwardsIsSkipped()
    {
        // a reused client id: render went down, so only its video counter (on 2 engines) counts this time
        var prev = new[] { P(I915("1", 5_000_000_000, 0)) };
        var cur = new[] { P(I915("1", 1_000_000_000, 600_000_000)) };
        Assert.Equal(30, Load(prev, cur));
        // xe: the total going backwards too, so nothing was measured at all
        Assert.Null(Load([P(Xe("1", 500, 9000))], [P(Xe("1", 600, 8000))]));
    }

    [Fact]
    public void ADipIsIgnoredUntilTheCounterCatchesUp()
    {
        // i915, a second apart: 1000 → 800 → 1000 → 1200 (in 1e6 ns); the way back up from 800 is not load
        var meter = new DrmUsage.Meter();
        Assert.Null(Value(meter.Update([P(I915("1", 1_000_000_000))], 0, [Igpu])));
        Assert.Equal(0, Value(meter.Update([P(I915("1", 800_000_000))], 1, [Igpu])));
        Assert.Equal(0, Value(meter.Update([P(I915("1", 1_000_000_000))], 2, [Igpu])));
        Assert.Equal(20, Value(meter.Update([P(I915("1", 1_200_000_000))], 3, [Igpu])));

        // xe: the same, measured against the total cycles of the highest reading
        meter = new DrmUsage.Meter();
        meter.Update([P(Xe("1", 1000, 10_000))], 0, [Igpu]);
        Assert.Equal(0, Value(meter.Update([P(Xe("1", 800, 11_000))], 1, [Igpu])));
        Assert.Equal(0, Value(meter.Update([P(Xe("1", 1000, 12_000))], 2, [Igpu])));
        Assert.Equal(20, Value(meter.Update([P(Xe("1", 1200, 13_000))], 3, [Igpu])));
    }

    [Fact]
    public void OnlySharesOverTheSameIntervalAreAddedUp()
    {
        // A is busy from 0 to 2 s and idle after, but its 2 s read fails; B is 20 % busy from 2 to 4 s.
        // Measured from 0 s, A would add 50 % to B's 20 %; it missed a sample, so it takes a new baseline instead.
        var meter = new DrmUsage.Meter();
        meter.Update([P(I915("A", 0))], 0, [Igpu]);
        meter.Update([P(I915("A", 0))], 1, [Igpu]);
        meter.Update([P(I915("B", 0))], 2, [Igpu], unread: new HashSet<(string, string)> { (Igpu, "A") });
        Assert.Equal(20, Value(meter.Update([P(I915("A", 2_000_000_000)), P(I915("B", 400_000_000))], 4, [Igpu])));
        Assert.Equal(0, Value(meter.Update([P(I915("A", 2_000_000_000)), P(I915("B", 400_000_000))], 6, [Igpu])));
    }

    [Fact]
    public void ACardIsMeasuredOnlyWhenTimePassed()
    {
        var meter = new DrmUsage.Meter();
        meter.Update([P(I915("1", 0))], 5, [Igpu]);
        Assert.Null(Value(meter.Update([P(I915("1", 0))], 5, [Igpu]))); // two reads at the same clock value
        Assert.Equal(0, Value(meter.Update([P(I915("1", 0))], 6, [Igpu]))); // an idle second: a real 0

        meter = new DrmUsage.Meter();
        meter.Update([P(Xe("1", 100, 1000))], 0, [Igpu]);
        Assert.Null(Value(meter.Update([P(Xe("1", 100, 1000))], 2, [Igpu]))); // the GPU clock didn't move
        Assert.Equal(0, Value(meter.Update([P(Xe("1", 100, 2000))], 4, [Igpu])));
    }

    [Fact]
    public void AnXeClientWithoutCountersForAMomentIsUnknownNotIdle()
    {
        // xe prints the client without drm-cycles when it can't wake the GPU
        var noCounters = "drm-driver:\txe\ndrm-client-id:\t1\ndrm-pdev:\t0000:00:02.0\ndrm-total-gtt:\t192 KiB\n";
        var meter = new DrmUsage.Meter();
        meter.Update([P(Xe("1", 0, 1000))], 0, [Igpu]);
        Assert.Equal(25, Value(meter.Update([P(Xe("1", 300, 2200))], 2, [Igpu])));
        Assert.Null(Value(meter.Update([P(noCounters)], 4, [Igpu]))); // the card's only client: no value, not 0
        Assert.Null(Value(meter.Update([P(Xe("1", 900, 3400))], 6, [Igpu]))); // back, but it missed a sample: a new baseline
        Assert.Equal(50, Value(meter.Update([P(Xe("1", 1500, 4600))], 8, [Igpu])));

        // next to a client that was measured, the card shows that one
        meter = new DrmUsage.Meter();
        meter.Update([P(Xe("1", 0, 1000)), P(Xe("2", 0, 1000))], 0, [Igpu]);
        Assert.Equal(10, Value(meter.Update([P(noCounters), P(Xe("2", 100, 2000))], 2, [Igpu])));

        // two descriptors of one client, the first without counters: the one with counters is used, once
        meter = new DrmUsage.Meter();
        meter.Update([P(noCounters), P(Xe("1", 0, 1000))], 0, [Igpu]);
        Assert.Equal(25, Value(meter.Update([P(noCounters), P(Xe("1", 300, 2200)), P(Xe("1", 300, 2200))], 2, [Igpu])));
    }

    [Fact]
    public void ACardHasAValueOnlyOnceAClientWasMeasuredAndStaysAtZeroWhenItsClientsExit()
    {
        var meter = new DrmUsage.Meter();
        Assert.Null(Value(meter.Update([], 0, [Igpu]))); // nothing on the card yet
        Assert.Null(Value(meter.Update([P(Xe("1", 5000, 10_000))], 2, [Igpu]))); // a busy client appears: its baseline only
        Assert.Equal(25, Value(meter.Update([P(Xe("1", 5300, 11_200))], 4, [Igpu])));
        Assert.Equal(0, Value(meter.Update([], 6, [Igpu]))); // its last client exited
    }

    [Fact]
    public void TheLoadStaysWithinAHundredAndASecondReadAtTheSameTimeKeepsTheBaseline()
    {
        Assert.Equal(100, Load([P(I915("1", 0))], [P(I915("1", 3_000_000_000))]));
        var meter = new DrmUsage.Meter();
        meter.Update([P(I915("1", 0))], 0, [Igpu]);
        meter.Update([P(I915("1", 250_000_000))], 0, [Igpu]); // no time passed: nothing measured, the baseline stays
        Assert.Equal(50, Value(meter.Update([P(I915("1", 500_000_000))], 1, [Igpu])));
    }

    // ---------------------------------------------------------------- the whole path, on a fake /proc and /sys/class/drm

    private static void W(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void TheFirstSampleHasNoLoadAndAnIdleCardThenShowsZero()
    {
        var root = Directory.CreateTempSubdirectory("pb-proc-");
        try
        {
            var proc = root.FullName;
            var now = 10.0;
            var usage = new DrmUsage(() => now);
            W(Path.Combine(proc, "300", "fdinfo", "5"), I915("1", 4_000_000_000));
            Assert.Empty(usage.Sample(proc, [Igpu])); // first sample: a baseline, no value
            now = 12;
            W(Path.Combine(proc, "300", "fdinfo", "5"), I915("1", 4_100_000_000));
            Assert.Equal(5, usage.Sample(proc, [Igpu])[Igpu]);
            now = 14;
            Directory.Delete(Path.Combine(proc, "300"), true); // the only client exited
            Assert.Equal(0, usage.Sample(proc, [Igpu])[Igpu]);
            Assert.Empty(new DrmUsage().Sample(Path.Combine(proc, "missing"), [Igpu]));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void AClientThatCantBeReadForAMomentKeepsItsBaseline()
    {
        var root = Directory.CreateTempSubdirectory("pb-proc-");
        try
        {
            var proc = root.FullName;
            var fdinfo = Path.Combine(proc, "300", "fdinfo", "5");
            var now = 0.0;
            var usage = new DrmUsage(() => now, discoverySeconds: 30);
            W(fdinfo, I915("1", 0));
            usage.Sample(proc, [Igpu]);
            now = 2;
            W(fdinfo, I915("1", 200_000_000));
            Assert.Equal(10, usage.Sample(proc, [Igpu])[Igpu]);

            // the file can't be read for one sample (a directory in its place: access denied, not "not found")
            File.Delete(fdinfo);
            Directory.CreateDirectory(fdinfo);
            now = 4;
            Assert.Empty(usage.Sample(proc, [Igpu])); // its load is unknown this time, not 0

            // readable again and still known without a new pass; it missed a sample, so it takes a new baseline first
            Directory.Delete(fdinfo);
            W(fdinfo, I915("1", 1_400_000_000));
            now = 6;
            Assert.Empty(usage.Sample(proc, [Igpu]));
            W(fdinfo, I915("1", 1_800_000_000));
            now = 8;
            Assert.Equal(20, usage.Sample(proc, [Igpu])[Igpu]);
            Assert.Equal(1, usage.Discoveries);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void AClientUnreadableDuringADiscoveryPassIsKeptAndRetried()
    {
        var root = Directory.CreateTempSubdirectory("pb-proc-");
        try
        {
            var proc = root.FullName;
            var fdinfo = Path.Combine(proc, "300", "fdinfo", "5");
            var now = 0.0;
            var usage = new DrmUsage(() => now, discoverySeconds: 30);
            W(fdinfo, I915("1", 0));
            usage.Sample(proc, [Igpu]);
            now = 2;
            W(fdinfo, I915("1", 200_000_000));
            Assert.Equal(10, usage.Sample(proc, [Igpu])[Igpu]);

            // unreadable while the 30 s pass walks /proc: the pass doesn't find it, but it isn't forgotten
            File.Delete(fdinfo);
            Directory.CreateDirectory(fdinfo);
            now = 30;
            Assert.Empty(usage.Sample(proc, [Igpu])); // unknown, not 0
            Assert.Equal(2, usage.Discoveries);

            Directory.Delete(fdinfo);
            W(fdinfo, I915("1", 1_000_000_000));
            now = 32;
            Assert.Empty(usage.Sample(proc, [Igpu])); // re-read by the 2 s poll: a new baseline
            W(fdinfo, I915("1", 1_600_000_000));
            now = 34;
            Assert.Equal(30, usage.Sample(proc, [Igpu])[Igpu]);
            Assert.Equal(2, usage.Discoveries);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void BetweenDiscoveryPassesOnlyTheKnownClientsAreRead()
    {
        var root = Directory.CreateTempSubdirectory("pb-proc-");
        try
        {
            var proc = root.FullName;
            string F(string pid) => Path.Combine(proc, pid, "fdinfo", "5");
            var now = 0.0;
            var usage = new DrmUsage(() => now, discoverySeconds: 30);
            W(F("300"), I915("1", 0));
            Assert.Empty(usage.Sample(proc, [Igpu]));
            Assert.Equal(1, usage.Discoveries);

            W(F("400"), I915("2", 0)); // a new client: not seen until the next pass
            now = 2;
            W(F("300"), I915("1", 200_000_000));
            Assert.Equal(10, usage.Sample(proc, [Igpu])[Igpu]);

            now = 4;
            W(F("300"), I915("1", 400_000_000));
            W(F("400"), I915("2", 1_000_000_000)); // would add 50 % had pid 400 been walked at 2 s
            Assert.Equal(10, usage.Sample(proc, [Igpu])[Igpu]);
            Assert.Equal(1, usage.Discoveries);

            now = 30; // the next pass finds pid 400 and takes its baseline
            Assert.Equal(0, usage.Sample(proc, [Igpu])[Igpu]);
            Assert.Equal(2, usage.Discoveries);

            now = 32;
            W(F("400"), I915("2", 1_600_000_000));
            Assert.Equal(30, usage.Sample(proc, [Igpu])[Igpu]);
            Assert.Equal(2, usage.Discoveries);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void AClientWhoseFdinfoIsGoneIsDroppedWithoutAFullPass()
    {
        var root = Directory.CreateTempSubdirectory("pb-proc-");
        try
        {
            var proc = root.FullName;
            string F(string pid) => Path.Combine(proc, pid, "fdinfo", "5");
            var now = 0.0;
            var usage = new DrmUsage(() => now, discoverySeconds: 30);
            W(F("300"), I915("1", 0));
            W(F("301"), I915("2", 0));
            Assert.Empty(usage.Sample(proc, [Igpu]));

            now = 2;
            W(F("300"), I915("1", 200_000_000));
            W(F("301"), I915("2", 200_000_000));
            Assert.Equal(20, usage.Sample(proc, [Igpu])[Igpu]);

            Directory.Delete(Path.Combine(proc, "301"), true); // the process exited
            now = 4;
            W(F("300"), I915("1", 400_000_000));
            Assert.Equal(10, usage.Sample(proc, [Igpu])[Igpu]);

            // a new process under the same path is not read again before the next pass
            W(F("301"), I915("2", 0));
            for (var i = 1; i <= 2; i++)
            {
                now = 4 + 2 * i;
                W(F("300"), I915("1", 400_000_000 + 200_000_000UL * (ulong)i));
                W(F("301"), I915("2", 1_000_000_000UL * (ulong)i));
                Assert.Equal(10, usage.Sample(proc, [Igpu])[Igpu]);
            }
            Assert.Equal(1, usage.Discoveries);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void AKernelWithoutBusyCountersLeavesTheLoadEmptyNotZero()
    {
        var root = Directory.CreateTempSubdirectory("pb-proc-");
        try
        {
            var proc = root.FullName;
            // early xe: client, card and memory lines, but no drm-cycles; i915 before 5.19: no drm-* lines at all
            W(Path.Combine(proc, "300", "fdinfo", "5"), "pos:\t0\nflags:\t0100002\nmnt_id:\t26\nino:\t1061\ndrm-driver:\txe\n" +
                "drm-client-id:\t3\ndrm-pdev:\t0000:00:02.0\ndrm-total-system:\t0\ndrm-resident-gtt:\t192 KiB\n");
            W(Path.Combine(proc, "301", "fdinfo", "4"), "pos:\t0\nflags:\t02100002\nmnt_id:\t26\nino:\t685\n");
            var now = 10.0;
            var usage = new DrmUsage(() => now);
            Assert.Empty(usage.Sample(proc, [Igpu]));
            now = 12;
            Assert.Empty(usage.Sample(proc, [Igpu]));
            now = 14;
            Assert.Empty(usage.Sample(proc, [Igpu]));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void AnIntelCardOnLinuxGetsItsLoadFromFdinfo()
    {
        var root = Directory.CreateTempSubdirectory("pb-intel-");
        try
        {
            var drm = Path.Combine(root.FullName, "drm");
            var proc = Path.Combine(root.FullName, "proc");
            // card0: Intel Arc 130V (Lunar Lake) on xe; card1: an AMD card that has gpu_busy_percent and keeps it
            var intel = Path.Combine(drm, "card0", "device");
            W(Path.Combine(intel, "vendor"), "0x8086\n");
            W(Path.Combine(intel, "device"), "0x64a0\n");
            W(Path.Combine(intel, "uevent"), "DRIVER=xe\nPCI_CLASS=30000\nPCI_ID=8086:64A0\nPCI_SLOT_NAME=0000:00:02.0\nMODALIAS=pci:v00008086d000064A0\n");
            W(Path.Combine(intel, "tile0", "gt0", "freq0", "cur_freq"), "1850\n");
            var amd = Path.Combine(drm, "card1", "device");
            W(Path.Combine(amd, "vendor"), "0x1002\n");
            W(Path.Combine(amd, "device"), "0x744c\n");
            W(Path.Combine(amd, "uevent"), "DRIVER=amdgpu\nPCI_SLOT_NAME=0000:03:00.0\n");
            W(Path.Combine(amd, "gpu_busy_percent"), "64\n");
            W(Path.Combine(amd, "mem_info_vram_total"), (24L << 30).ToString());

            void Clients(ulong game, ulong desktop, ulong total)
            {
                W(Path.Combine(proc, "4242", "fdinfo", "0"), "pos:\t0\nflags:\t0100002\nmnt_id:\t26\nino:\t7\n"); // not a GPU
                W(Path.Combine(proc, "4242", "fdinfo", "17"), Xe("31", game, total));
                W(Path.Combine(proc, "4242", "fdinfo", "18"), Xe("31", game, total)); // a dup of fd 17
                W(Path.Combine(proc, "1001", "fdinfo", "9"), Xe("4", desktop, total));
                W(Path.Combine(proc, "1001", "fdinfo", "10"), I915("8", 9_000_000_000, pdev: "0000:00:04.0")); // another card
            }
            Directory.CreateDirectory(Path.Combine(proc, "self", "fdinfo"));
            Directory.CreateDirectory(Path.Combine(proc, "77")); // fdinfo unreadable or gone: skipped
            W(Path.Combine(proc, "cpuinfo"), "processor\t: 0\n");
            var ids = Path.Combine(root.FullName, "missing.ids");

            var now = 100.0;
            var usage = new DrmUsage(() => now);
            Clients(game: 1_000, desktop: 500, total: 10_000);
            var first = Gpus.ReadSysfs(drm, ids, proc, usage);
            var g = first.Single(x => x.Vendor == "intel");
            Assert.Null(g.Util); // no value yet, not 0
            Assert.Equal(1850, g.Clock);
            Assert.True(g.Integrated);
            Assert.Equal(64, first.Single(x => x.Vendor == "amd").Util);

            now = 102;
            Clients(game: 1_000 + 6_000, desktop: 500 + 1_000, total: 10_000 + 20_000);
            var second = Gpus.ReadSysfs(drm, ids, proc, usage);
            Assert.Equal(35, second.Single(x => x.Vendor == "intel").Util); // (6000 + 1000) / 20000 cycles on render
            Assert.Equal(64, second.Single(x => x.Vendor == "amd").Util);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void AnIntelCardWithoutAPciAddressHasNoLoad()
    {
        var root = Directory.CreateTempSubdirectory("pb-intel-");
        try
        {
            var dev = Path.Combine(root.FullName, "drm", "card0", "device");
            W(Path.Combine(dev, "vendor"), "0x8086\n");
            W(Path.Combine(dev, "uevent"), "DRIVER=i915\n");
            Assert.Null(Gpus.PciAddress(dev));
            var usage = new DrmUsage();
            Assert.Null(Assert.Single(Gpus.ReadSysfs(Path.Combine(root.FullName, "drm"), Path.Combine(root.FullName, "missing.ids"), root.FullName, usage)).Util);
            Assert.Null(Assert.Single(Gpus.ReadSysfs(Path.Combine(root.FullName, "drm"), Path.Combine(root.FullName, "missing.ids"), root.FullName, usage)).Util);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void FdsThatArentGpuDevicesAreNotRead()
    {
        var root = Directory.CreateTempSubdirectory("pb-proc-");
        try
        {
            var pid = Path.Combine(root.FullName, "500");
            W(Path.Combine(pid, "fdinfo", "3"), I915("1", 0));
            W(Path.Combine(pid, "fdinfo", "4"), I915("2", 0));
            Directory.CreateDirectory(Path.Combine(pid, "fd"));
            try
            {
                File.CreateSymbolicLink(Path.Combine(pid, "fd", "3"), "/dev/dri/renderD128");
                File.CreateSymbolicLink(Path.Combine(pid, "fd", "4"), "/run/user/1000/socket");
            }
            catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
            {
                return; // creating symlinks needs Developer Mode or admin rights on Windows; elsewhere a failure here fails the test
            }
            Assert.Equal("1", Assert.Single(DrmUsage.Scan(root.FullName)).ClientId);
        }
        finally { root.Delete(true); }
    }
}
