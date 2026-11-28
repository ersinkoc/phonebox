using PhoneBox.Agent;
using Xunit;

public class BatteryTests
{
    [Fact]
    public void ActiveBatteryRefreshBalancesResponsivenessAndReceiverWakeups()
        => Assert.InRange(BatteryMonitor.ActiveRefreshIntervalMs, 15_000, 20_000);

    [Fact]
    public void MacBatteriesStayAtOnceAMinuteBecauseSystemProfilerIsSlow()
    {
        Assert.Equal(60_000, BatteryMonitor.RefreshIntervalMs(macOS: true));
        Assert.Equal(BatteryMonitor.ActiveRefreshIntervalMs, BatteryMonitor.RefreshIntervalMs(macOS: false));
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    public void DisconnectedWindowsDevicesAreExcluded(bool hasProperty, byte value, bool expected)
        => Assert.Equal(expected, BatteryMonitor.IncludeWindowsBatteryDevice(hasProperty, [value]));

    [Fact]
    public void ClassicAudioBatteryNodesUseTheConnectionOfTheirOwnBluetoothDevice()
    {
        var devices = BatteryMonitor.WindowsBluetoothBatteries([
            new(@"BTHENUM\DEV_78C11D112233\A&BLUETOOTHDEVICE_78C11D112233", true),
            new(@"BTHENUM\{0000111E-0000-1000-8000-00805f9b34fb}\DEV&78c11d112233_C00000000", false, "Hands-Free AG", 92),
            new(@"BTHLE\DEV_78C11D112233\one", false, "Galaxy Buds4 Pro", 93),
            new(@"BTHLE\DEV_78C11D112234\two", false, "Disconnected Buds", 42),
            new(@"BTHLE\DEV_123456AABBCC\three", true, "M720 Triathlon", 100),
            new(@"BTHLE\DEV_123456AABBDD\four", null, "Unknown connection mouse", 80),
        ]);
        Assert.Equal([new("Galaxy Buds4 Pro", 93, "headphones", null),
            new("M720 Triathlon", 100, "mouse", null), new("Unknown connection mouse", 80, "mouse", null)], devices);
    }

    [Fact]
    public void WindowsInstanceIdStopsAtTheFirstNulInTheReusedBuffer()
        => Assert.Equal(@"BTHLE\DEV_123456AABBCC", BatteryMonitor.WindowsInstanceId("BTHLE\\DEV_123456AABBCC\0LEFTOVER\0"));

    [Fact]
    public void ClassicServiceSuffixMatchesTheAddressAndDistinguishesIdenticalModels()
    {
        var devices = BatteryMonitor.WindowsBluetoothBatteries([
            new(@"BTHENUM\DEV_123456ABCDEF\parent", true),
            new(@"BTHENUM\service\branch&123456ABCDEF_C00000000", false, "Bose headphones", 85),
            new(@"BTHENUM\DEV_123456ABCDEC\parent", true),
            new(@"BTHENUM\service\branch&123456ABCDEC_C00000000", false, "Bose headphones", 75),
            new(@"BTHENUM\service\branch&123456ABCDED_C00000000", false, "Bose headphones", 40),
        ]);
        Assert.Equal(["Bose headphones (CDEF)", "Bose headphones (CDEC)"], devices.Select(d => d.Name));
    }

    [Theory]
    [InlineData("WH-1000XM5", "headphones")]
    [InlineData("Galaxy Buds2 Pro", "headphones")]
    [InlineData("AirPods Pro", "headphones")]
    [InlineData("Razer BlackShark V2 Pro 2.4", "headphones")]
    [InlineData("Razer Barracuda X", "headphones")]
    [InlineData("MX Master 3S", "mouse")]
    [InlineData("Logitech Pebble M350", "mouse")]
    [InlineData("Razer Basilisk V3 Pro", "mouse")]
    [InlineData("Razer Viper V3 Pro", "mouse")]
    [InlineData("Razer Lancehead Wireless", "mouse")]
    [InlineData("MX Keys", "keyboard")]
    [InlineData("Slim Solar+", "keyboard")]
    [InlineData("Razer BlackWidow V4 Pro", "keyboard")]
    [InlineData("Xbox Wireless Controller", "controller")]
    [InlineData("DualSense Wireless Controller", "controller")]
    [InlineData("Some Speaker", "other")]
    public void KindFromName(string name, string kind) => Assert.Equal(kind, BatteryMonitor.KindOf(name));

    [Fact]
    public void BuildRazerReport_ConstructsValidBatteryAndChargingRequests()
    {
        var batteryReq = BatteryMonitor.BuildRazerReport(0x1F, BatteryMonitor.RazerCmdBattery);
        Assert.Equal(BatteryMonitor.RazerReportLength, batteryReq.Length);
        Assert.Equal(0x00, batteryReq[0]);
        Assert.Equal(BatteryMonitor.RazerStatusNew, batteryReq[1]);
        Assert.Equal(0x1F, batteryReq[2]);
        Assert.Equal(0x02, batteryReq[6]);
        Assert.Equal(BatteryMonitor.RazerClassPower, batteryReq[7]);
        Assert.Equal(BatteryMonitor.RazerCmdBattery, batteryReq[8]);
        // CRC is XOR of Razer frame bytes [2..87] (HID buffer indices [3..88]) = 0x02 ^ 0x07 ^ 0x80 = 0x85.
        Assert.Equal(0x85, batteryReq[89]);

        var chargingReq = BatteryMonitor.BuildRazerReport(0x3F, BatteryMonitor.RazerCmdCharging);
        Assert.Equal(BatteryMonitor.RazerReportLength, chargingReq.Length);
        Assert.Equal(0x3F, chargingReq[2]);
        Assert.Equal(0x02, chargingReq[6]);
        Assert.Equal(BatteryMonitor.RazerClassPower, chargingReq[7]);
        Assert.Equal(BatteryMonitor.RazerCmdCharging, chargingReq[8]);
        // CRC = 0x02 ^ 0x07 ^ 0x84 = 0x81.
        Assert.Equal(0x81, chargingReq[89]);
    }

    [Fact]
    public void ParseRazerBattery_And_Charging_FromCapturedDeviceResponses()
    {
        // Real 91-byte HidD_GetFeature responses captured from Razer Basilisk V3 Pro (PID 0x00AB, wireless).
        byte[] capturedBattery = Convert.FromHexString("00021F0000000207800039" + new string('0', 78 * 2) + "BC00");
        byte[] capturedCharging = Convert.FromHexString("00021F0000000207840000" + new string('0', 78 * 2) + "8100");

        Assert.Equal(22, BatteryMonitor.ParseRazerBattery(capturedBattery));
        Assert.False(BatteryMonitor.ParseRazerCharging(capturedCharging));

        // Real 91-byte battery response shape from an earlier wireless session (0x78 = 47%) and full-charge (0xFF = 100%).
        byte[] midBattery = Convert.FromHexString("00021F0000000207800078" + new string('0', 78 * 2) + "FD00");
        byte[] fullBatteryTx3F = Convert.FromHexString("00023F00000002078000FF" + new string('0', 78 * 2) + "7A00");
        byte[] activeCharging = Convert.FromHexString("00023F0000000207840001" + new string('0', 78 * 2) + "8000");

        Assert.Equal(47, BatteryMonitor.ParseRazerBattery(midBattery));
        Assert.Equal(100, BatteryMonitor.ParseRazerBattery(fullBatteryTx3F));
        Assert.True(BatteryMonitor.ParseRazerCharging(activeCharging));

        // Edge cases: sleeping/off (raw=0), busy (status=0x01), foreign command class (0x0F), short buffer.
        byte[] sleeping = Convert.FromHexString("00021F0000000207800000" + new string('0', 78 * 2) + "8500");
        byte[] busy = Convert.FromHexString("00011F0000000207800078" + new string('0', 78 * 2) + "FD00");
        byte[] foreign = Convert.FromHexString("00021F0000002C0F030000" + new string('0', 78 * 2) + "0000");

        Assert.Null(BatteryMonitor.ParseRazerBattery(sleeping));
        Assert.Null(BatteryMonitor.ParseRazerBattery(busy));
        Assert.Null(BatteryMonitor.ParseRazerBattery(foreign));
        Assert.Null(BatteryMonitor.ParseRazerCharging(foreign));
        Assert.Null(BatteryMonitor.ParseRazerBattery(new byte[16]));
        Assert.Null(BatteryMonitor.ParseRazerCharging(new byte[16]));
    }

    [Fact]
    public void RazerPaProtocol_BuildsFramesAndParsesCapturedHeadsetResponses()
    {
        var remoteOn = BatteryMonitor.BuildRazerPaRemoteFrame(true);
        Assert.Equal(BatteryMonitor.RazerPaReportLength, remoteOn.Length);
        Assert.Equal(BatteryMonitor.RazerPaReportId, remoteOn[0]);
        Assert.Equal((byte)'P', remoteOn[5]);
        Assert.Equal((byte)'A', remoteOn[6]);
        Assert.Equal(0xE1, remoteOn[10]);
        Assert.Equal(0x01, remoteOn[11]);

        var remoteOff = BatteryMonitor.BuildRazerPaRemoteFrame(false);
        Assert.Equal(0x00, remoteOff[11]);

        var query = BatteryMonitor.BuildRazerPaQueryFrame(BatteryMonitor.RazerPaCmdBattery);
        Assert.Equal(BatteryMonitor.RazerPaReportLength, query.Length);
        Assert.Equal(BatteryMonitor.RazerPaReportId, query[0]);
        Assert.Equal(BatteryMonitor.RazerPaCmdBattery, query[10]);

        // Real 64-byte PA replies captured from Razer BlackShark V2 Pro 2.4 (PID 0x0555, wireless).
        byte[] capturedPaBattery = Convert.FromHexString(
            "020E504908C8ED6B510004002101011D" +
            "504908C683FB4C0004002A0201000002" +
            "060B0002060B" +
            new string('0', 26 * 2));
        byte[] capturedPaCharging = Convert.FromHexString(
            "020E504908C9126C510004002A010100" +
            "504908C683FB4C0004002A0201000002" +
            "060B0002060B" +
            new string('0', 26 * 2));

        Assert.Equal((byte)29, BatteryMonitor.ParseRazerPaReply(capturedPaBattery, BatteryMonitor.RazerPaCmdBattery));
        Assert.Null(BatteryMonitor.ParseRazerPaReply(capturedPaBattery, BatteryMonitor.RazerPaCmdCharging));
        Assert.Equal((byte)0, BatteryMonitor.ParseRazerPaReply(capturedPaCharging, BatteryMonitor.RazerPaCmdCharging));
        Assert.Null(BatteryMonitor.ParseRazerPaReply(new byte[10], BatteryMonitor.RazerPaCmdBattery));
    }

    [Fact]
    public void BarracudaPaProtocol_UsesItsOwnOffsetsAndParsesRepliesWithOrWithoutReportId()
    {
        var remote = BatteryMonitor.BuildRazerBarracudaRemoteFrame(true);
        Assert.Equal(BatteryMonitor.RazerBarracudaReportId, remote[0]);
        Assert.Equal((byte)'P', remote[3]);
        Assert.Equal((byte)'A', remote[4]);
        Assert.Equal(0xE1, remote[8]);
        Assert.Equal(1, remote[9]);

        var query = BatteryMonitor.BuildRazerBarracudaQueryFrame(BatteryMonitor.RazerPaCmdBattery);
        Assert.Equal(BatteryMonitor.RazerPaCmdBattery, query[8]);

        var reply = new byte[BatteryMonitor.RazerPaReportLength];
        reply[0] = BatteryMonitor.RazerBarracudaReportId;
        reply[3] = (byte)'P'; reply[4] = (byte)'I';
        reply[13] = BatteryMonitor.RazerPaCmdBattery;
        reply[14] = 0x01; reply[15] = 0x01; reply[16] = 73;
        Assert.Equal((byte)73, BatteryMonitor.ParseRazerBarracudaReply(reply, BatteryMonitor.RazerPaCmdBattery));
        Assert.Equal((byte)73, BatteryMonitor.ParseRazerBarracudaReply(reply.AsSpan(1), BatteryMonitor.RazerPaCmdBattery));
        Assert.Null(BatteryMonitor.ParseRazerBarracudaReply(reply, BatteryMonitor.RazerPaCmdCharging));
    }

    [Theory]
    [InlineData(0x00AB, "mouse")]
    [InlineData(0x00C1, "mouse")]
    [InlineData(0x0555, "headphones")]
    [InlineData(0x053A, "headphones")]
    [InlineData(0x025C, "keyboard")]
    [InlineData(0xFFFF, null)]
    public void RazerKindFromPid_MapsKnownDevicesAndFallsBackToName(int pid, string? expectedKind)
        => Assert.Equal(expectedKind, BatteryMonitor.RazerKindFromPid(pid));

    [Fact]
    public void MergeRazerIntoBluetooth_PreservesBluetoothDuplicatesAndCollapsesCableWithDongle()
    {
        var bluetooth = new[]
        {
            new DeviceBattery("Office Mouse", 60, "mouse", null),
            new DeviceBattery("office mouse", 40, "mouse", null),
            new DeviceBattery("Razer Basilisk V3 Pro", 45, "mouse", null),
        };
        var hid = new[]
        {
            new DeviceBattery("Razer Basilisk V3 Pro", 46, "mouse", false), // wireless dongle
            new DeviceBattery("Razer Basilisk V3 Pro", 47, "mouse", true),  // USB cable
            new DeviceBattery("Razer BlackWidow V3 Pro", 80, "keyboard", false),
        };

        var merged = BatteryMonitor.MergeRazerIntoBluetooth(bluetooth, hid);
        Assert.Equal(4, merged.Count);
        Assert.Equal(2, merged.Count(d => string.Equals(d.Name, "Office Mouse", StringComparison.OrdinalIgnoreCase)));
        var mouse = Assert.Single(merged, d => d.Name == "Razer Basilisk V3 Pro");
        Assert.True(mouse.Charging);
        Assert.Equal(47, mouse.Level);
    }

    [Fact]
    public void QueryRazerFeatureDevice_EverySentFrameHasReadBitSet()
    {
        var sentFrames = new List<byte[]>();

        foreach (var pid in BatteryMonitor.RazerFeaturePids)
        {
            // Path 1: device responds NotSupported across all transaction IDs.
            BatteryMonitor.QueryRazerFeatureDevice(pid, null, req =>
            {
                sentFrames.Add((byte[])req.Clone());
                var resp = (byte[])req.Clone();
                resp[1] = BatteryMonitor.RazerStatusNotSupported;
                return resp;
            });

            // Path 2: device responds StatusOk on battery and charging queries.
            BatteryMonitor.QueryRazerFeatureDevice(pid, null, req =>
            {
                sentFrames.Add((byte[])req.Clone());
                var resp = (byte[])req.Clone();
                resp[1] = BatteryMonitor.RazerStatusOk;
                resp[10] = req[8] == BatteryMonitor.RazerCmdBattery ? (byte)0x80 : (byte)0x01;
                return resp;
            });
        }

        Assert.NotEmpty(sentFrames);
        foreach (var frame in sentFrames)
        {
            Assert.Equal(BatteryMonitor.RazerClassPower, frame[7]);
            Assert.True((frame[8] & 0x80) != 0, $"Sent command 0x{frame[8]:X2} must have the read bit (bit 7) set.");
        }
    }

    [Fact]
    public void QueryRazerFeatureDevice_NeverSendsToUnknownOrHeadsetOnlyPids()
    {
        var sent = new List<byte[]>();
        byte[]? Spy(byte[] req) { sent.Add(req); return null; }

        Assert.Equal((null, null, null), BatteryMonitor.QueryRazerFeatureDevice(0x9999, null, Spy));
        Assert.Equal((null, null, null), BatteryMonitor.QueryRazerFeatureDevice(0x0555, null, Spy));
        Assert.Empty(sent);
    }

    [Fact]
    public void QueryRazerFeatureDevice_StopsOnOkAndUsesPreferredTxFirst()
    {
        var sentTx = new List<byte>();
        byte[]? SleepingOnFirstTx(byte[] req)
        {
            sentTx.Add(req[2]);
            var resp = (byte[])req.Clone();
            resp[1] = BatteryMonitor.RazerStatusOk;
            resp[10] = 0x00; // sleeping
            return resp;
        }

        var (level, charging, workingTx) = BatteryMonitor.QueryRazerFeatureDevice(0x00AB, null, SleepingOnFirstTx);
        Assert.Null(level);
        Assert.Null(charging);
        Assert.Equal((byte)0x1F, workingTx);
        Assert.Equal([(byte)0x1F], sentTx);

        sentTx.Clear();
        byte[]? RespondOnPreferred(byte[] req)
        {
            sentTx.Add(req[2]);
            var resp = (byte[])req.Clone();
            resp[1] = BatteryMonitor.RazerStatusOk;
            resp[10] = req[8] == BatteryMonitor.RazerCmdBattery ? (byte)0x78 : (byte)0x01;
            return resp;
        }

        var res = BatteryMonitor.QueryRazerFeatureDevice(0x00AB, 0x3F, RespondOnPreferred);
        Assert.Equal(47, res.Level);
        Assert.True(res.Charging);
        Assert.Equal((byte)0x3F, res.WorkingTxId);
        Assert.Equal([(byte)0x3F, (byte)0x3F], sentTx);
    }

    // The accepted replies are the start of input reports captured from an HS80 RGB Wireless (0x0A69) and an Ironclaw
    // RGB Wireless (0x1B4C), behind their receiver (0x0A6B) and on a USB cable; the rest of each report is zeroed here.
    // Replies that must be refused or ignored, and bit fields other than "both connected", are made up from them.
    private static byte[] CorsairReport(string head)
        => Convert.FromHexString(head + new string('0', 64 * 2 - head.Length));

    private const byte Level = BatteryMonitor.CorsairPropBatteryLevel;
    private const byte State = BatteryMonitor.CorsairPropBatteryState;
    private const byte Pid = BatteryMonitor.CorsairPropProductId;
    private const byte Connected = BatteryMonitor.CorsairPropConnectedSlots;

    // What the receiver answers about itself: the headset is on slot 0x09, the mouse on slot 0x0A.
    private static readonly (byte, byte, string) CorsairBothConnected = (0x08, Connected, "010002000600");
    private static readonly (byte, byte, string) CorsairMouseConnected = (0x08, Connected, "010002000400");

    private static readonly (byte, byte, string)[] CorsairHeadsetSlot =
        [(0x09, Level, "010102008403"), (0x09, State, "010102000200"), (0x09, Pid, "01010200690A")];
    private static readonly (byte, byte, string)[] CorsairMouseSlot =
        [(0x0A, Level, "01020200AC03"), (0x0A, State, "010202000200"), (0x0A, Pid, "010202004C1B")];

    /// <summary>Answers each read with the listed reports in order, repeating the last; unlisted reads get silence.</summary>
    private static Func<byte, byte, BatteryMonitor.CorsairReply?> CorsairReplies(
        List<(byte Target, byte Property)> sent, params (byte Target, byte Property, string Report)[] replies)
    {
        var queues = replies.GroupBy(r => (r.Target, r.Property))
            .ToDictionary(g => g.Key, g => new Queue<string>(g.Select(r => r.Report)));
        return (target, property) =>
        {
            sent.Add((target, property));
            if (!queues.TryGetValue((target, property), out var queue)) return null;
            var report = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return BatteryMonitor.ParseCorsairReply(CorsairReport(report), target);
        };
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public void BuildCorsairRequest_OnlyEverReadsOneProperty(int length)
    {
        var request = BatteryMonitor.BuildCorsairRequest(length, 0x02, 0x09, Level);

        Assert.Equal(length, request.Length);
        Assert.Equal(new byte[] { 0x02, 0x09, BatteryMonitor.CorsairCmdGet, 0x0F }, request[..4]);
        Assert.All(request[4..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void ParseCorsairReply_AcceptsOnlyTheAnswerForTheAskedSlot()
    {
        BatteryMonitor.CorsairReply? Parse(string head, byte target)
            => BatteryMonitor.ParseCorsairReply(CorsairReport(head), target);

        Assert.Equal(new BatteryMonitor.CorsairReply(0, 900), Parse("010102008403000000392A1C", 0x09));
        Assert.Equal(new BatteryMonitor.CorsairReply(0, 940), Parse("01020200AC03", 0x0A));
        Assert.Equal(new BatteryMonitor.CorsairReply(0, 1000), Parse("00000200E803", 0x08));
        // A refusal keeps its status: here a receiver asked for a battery of its own (5, not supported).
        Assert.Equal(5, Parse("0100020528420020", 0x08)!.Value.Status);
        // Seen while iCUE was starting: a notification, then replies for other slots.
        Assert.Null(Parse("010206000000", 0x0A));
        Assert.Null(Parse("010102000200", 0x0B));
        Assert.Null(Parse("010002001C1B", 0x09));
        Assert.Null(BatteryMonitor.ParseCorsairReply(new byte[4], 0x08));
    }

    [Fact]
    public void QueryCorsairBattery_ReadsLevelStateThenLevelAgain()
    {
        var sent = new List<(byte, byte)>();
        var cabledHeadset = CorsairReplies(sent, (0x08, Level, "010002008403"), (0x08, State, "010002000100"));

        var reading = BatteryMonitor.QueryCorsairBattery(0x08, cabledHeadset);

        Assert.Equal(90, reading.Level);
        Assert.True(reading.Charging);
        Assert.Equal([(0x08, Level), (0x08, State), (0x08, Level)], sent);

        // The cabled mouse says "full": its level is reported as it comes, shown as charging.
        var full = BatteryMonitor.QueryCorsairBattery(0x08,
            CorsairReplies([], (0x08, Level, "00000200E803"), (0x08, State, "000002000300")));
        Assert.Equal(100, full.Level);
        Assert.True(full.Charging);

        var onBattery = BatteryMonitor.QueryCorsairBattery(0x09,
            CorsairReplies([], (0x09, Level, "010102008403"), (0x09, State, "010102000200")));
        Assert.False(onBattery.Charging);
    }

    [Theory]
    [InlineData("010102008403", "010102008403", "010102008403")] // a late level reply taken for the state
    [InlineData("010102008403", "010102000200", "010102000200")] // the two levels differ
    [InlineData("010102000200", "010102000200", "010102000200")] // iCUE's "mode 2" reply: 0.2% is not a level
    [InlineData("010102001C1B", "010102000200", "010102001C1B")] // iCUE's vendor id reply
    [InlineData("010102008403", "010102050000", "010102008403")] // state not supported
    [InlineData("010102050000", "010102000200", "010102050000")] // level not supported
    public void QueryCorsairBattery_RefusesDoubtfulReadings(string first, string state, string second)
    {
        var reading = BatteryMonitor.QueryCorsairBattery(0x09,
            CorsairReplies([], (0x09, Level, first), (0x09, State, state), (0x09, Level, second)));

        Assert.Null(reading.Level);
    }

    [Fact]
    public void QueryCorsairBattery_AsksNothingMoreOfASilentDevice()
    {
        var sent = new List<(byte, byte)>();

        Assert.Null(BatteryMonitor.QueryCorsairBattery(0x09, CorsairReplies(sent)).Level);
        Assert.Single(sent);
    }

    [Theory]
    [InlineData("01010200690A", "01010200690A", 0x0A69)]
    [InlineData("01010200690A", "010102005203", null)] // the two product ids differ
    [InlineData("010102001C1B", "010102001C1B", null)] // a vendor id reply taken for the product id
    [InlineData("010102000000", "010102000000", null)] // no product
    [InlineData("010102050000", "010102050000", null)] // not supported
    public void QueryCorsairProductId_NeedsTwoEqualPlausibleReads(string first, string second, int? expected)
        => Assert.Equal(expected, BatteryMonitor.QueryCorsairProductId(0x09,
            CorsairReplies([], (0x09, Pid, first), (0x09, Pid, second))));

    [Theory]
    [InlineData("010002000600", "010002000600", new byte[] { 0x09, 0x0A })]
    [InlineData("010002000400", "010002000400", new byte[] { 0x0A })]
    [InlineData("010002000000", "010002000000", new byte[0])]
    [InlineData("010002000600", "010002000400", null)] // the two reads differ
    [InlineData("010002001C1B", "010002001C1B", null)] // a vendor id reply taken for the bit field
    [InlineData("010002000100", "010002000100", null)] // bit 0 would be the receiver itself
    [InlineData("010002004600", "010002004600", null)] // a bit above the slots
    [InlineData("010002000602", "010002000602", null)] // a bit in the high byte
    [InlineData("010002050000", "010002050000", null)] // not supported
    public void QueryCorsairConnectedTargets_NeedsTwoEqualPlausibleReads(string first, string second, byte[]? expected)
        => Assert.Equal(expected, BatteryMonitor.QueryCorsairConnectedTargets(
            CorsairReplies([], (0x08, Connected, first), (0x08, Connected, second))));

    private static readonly (byte, byte, string)[] CorsairBothAnswering =
        [CorsairBothConnected, .. CorsairHeadsetSlot, .. CorsairMouseSlot];
    // The headset's bit is set, but it answers nothing.
    private static readonly (byte, byte, string)[] CorsairSilentHeadset = [CorsairBothConnected, .. CorsairMouseSlot];

    /// <summary>A receiver read time after time, as the agent does.</summary>
    private sealed class CorsairReceiver
    {
        public readonly BatteryMonitor.CorsairInterfaceState State = new();
        public readonly List<(byte Target, byte Property)> Sent = [];
        public long Now = 1_000;

        public List<(int ProductId, int Level, bool Charging)> Read(params (byte, byte, string)[] replies)
        {
            Sent.Clear();
            return BatteryMonitor.QueryCorsairInterface(0x0A6B, State, CorsairReplies(Sent, replies), clock: () => Now);
        }

        public int SentTo(byte target) => Sent.Count(s => s.Target == target);
    }

    [Fact]
    public void QueryCorsairInterface_AsksASlotOnceTwoReadsReportedItConnected()
    {
        var receiver = new CorsairReceiver();

        Assert.Empty(receiver.Read(CorsairBothAnswering));
        Assert.All(receiver.Sent, s => Assert.Equal((0x08, Connected), s));

        Assert.Equal([(0x0A69, 90, false), (0x1B4C, 94, false)], receiver.Read(CorsairBothAnswering));

        // A bit set for the first time waits for the next read.
        receiver = new CorsairReceiver();
        (byte, byte, string)[] mouseOnly = [CorsairMouseConnected, .. CorsairHeadsetSlot, .. CorsairMouseSlot];
        receiver.Read(mouseOnly);
        Assert.Single(receiver.Read(CorsairBothAnswering));
        Assert.Equal(0, receiver.SentTo(0x09));
        Assert.Equal(2, receiver.Read(CorsairBothAnswering).Count);
    }

    [Fact]
    public void QueryCorsairInterface_AsksConnectedDevicesAndIdentifiesThemOnceInAWhile()
    {
        var receiver = new CorsairReceiver();
        receiver.Read(CorsairBothAnswering);

        Assert.Equal(2, receiver.Read(CorsairBothAnswering).Count);
        // Nothing but the list of connected devices is asked of the receiver, and nothing of the other slots.
        Assert.All(receiver.Sent.Where(s => s.Target is not (0x09 or 0x0A)), s => Assert.Equal((0x08, Connected), s));

        receiver.Now += BatteryMonitor.CorsairReidentifyMs - 1;
        Assert.Equal(2, receiver.Read(CorsairBothAnswering).Count);
        Assert.DoesNotContain(receiver.Sent, s => s.Property == Pid);

        receiver.Now += 1;
        Assert.Equal(2, receiver.Read(CorsairBothAnswering).Count);
        Assert.Contains(receiver.Sent, s => s.Property == Pid);
    }

    [Fact]
    public void QueryCorsairInterface_SendsNothingToADeviceThatIsAway()
    {
        var receiver = new CorsairReceiver();
        receiver.Read(CorsairBothAnswering);
        Assert.Equal(2, receiver.Read(CorsairBothAnswering).Count);

        // The headset leaves: the receiver would keep anything sent to it.
        (byte, byte, string)[] mouseOnly = [CorsairMouseConnected, .. CorsairHeadsetSlot, .. CorsairMouseSlot];
        Assert.Equal([(0x1B4C, 94, false)], receiver.Read(mouseOnly));
        Assert.Equal(0, receiver.SentTo(0x09));

        Assert.Empty(receiver.Read((0x08, Connected, "010002000000")));
        Assert.All(receiver.Sent, s => Assert.Equal(0x08, s.Target));

        // Back again: it is identified anew.
        receiver.Read(CorsairBothAnswering);
        Assert.Equal(2, receiver.Read(CorsairBothAnswering).Count);
        Assert.Contains(receiver.Sent, s => s == (0x09, Pid));
    }

    [Fact]
    public void QueryCorsairInterface_StopsAskingASilentSlotUntilItLeavesAndComesBack()
    {
        var receiver = new CorsairReceiver();
        receiver.Read(CorsairSilentHeadset);

        for (var read = 0; read < BatteryMonitor.CorsairMaxMisses; read++)
        {
            Assert.Single(receiver.Read(CorsairSilentHeadset));
            Assert.Equal(1, receiver.SentTo(0x09));
        }
        for (var read = 0; read < 3; read++)
        {
            receiver.Now += BatteryMonitor.CorsairReidentifyMs;
            Assert.Single(receiver.Read(CorsairSilentHeadset));
            Assert.Equal(0, receiver.SentTo(0x09));
        }

        receiver.Read([CorsairMouseConnected, .. CorsairMouseSlot]);
        receiver.Read(CorsairBothAnswering);
        Assert.Equal(2, receiver.Read(CorsairBothAnswering).Count);
    }

    [Fact]
    public void QueryCorsairInterface_AnOldReadingDoesNotCountAsTheLastOne()
    {
        var receiver = new CorsairReceiver();
        receiver.Read(CorsairBothAnswering);
        receiver.Now += BatteryMonitor.CorsairStaleMs + 1; // the widget was closed for a while
        Assert.Empty(receiver.Read(CorsairBothAnswering)); // one reading is not enough again
        Assert.Equal(0, receiver.SentTo(0x09));
        Assert.Equal(2, receiver.Read(CorsairBothAnswering).Count); // the second one in a row is
    }

    [Fact]
    public void QueryCorsairInterface_ForgetsAMissOnceTheSlotAnswers()
    {
        var receiver = new CorsairReceiver();
        receiver.Read(CorsairSilentHeadset);

        for (var round = 0; round <= BatteryMonitor.CorsairMaxMisses; round++)
        {
            receiver.Read(CorsairSilentHeadset);
            Assert.Equal(1, receiver.SentTo(0x09));
            Assert.Equal(2, receiver.Read(CorsairBothAnswering).Count);
        }
    }

    [Fact]
    public void QueryCorsairInterface_ForgetsAProductItCannotIdentifyAgain()
    {
        var receiver = new CorsairReceiver();
        receiver.Read(CorsairBothAnswering);
        Assert.Equal(2, receiver.Read(CorsairBothAnswering).Count);

        // The next identification of the headset's slot fails: another device may be there.
        receiver.Now += BatteryMonitor.CorsairReidentifyMs;
        var found = receiver.Read(
            [CorsairBothConnected, .. CorsairHeadsetSlot, (0x09, Pid, "010102005203"), .. CorsairMouseSlot]);
        Assert.Equal([(0x1B4C, 94, false)], found);
        Assert.All(receiver.Sent.Where(s => s.Target == 0x09), s => Assert.Equal(Pid, s.Property));

        Assert.Equal(2, receiver.Read(CorsairBothAnswering).Count);
        Assert.Contains(receiver.Sent, s => s == (0x09, Pid));
    }

    [Fact]
    public void QueryCorsairInterface_ReadsTwoSlowDevicesWithinItsBudget()
    {
        var state = new BatteryMonitor.CorsairInterfaceState { Connected = [0x09, 0x0A], ConnectedAt = 0 };
        var replies = CorsairReplies([], CorsairBothAnswering);
        long now = 0;

        var found = BatteryMonitor.QueryCorsairInterface(0x0A6B, state, (target, property) =>
        {
            now += target == 0x08 ? 5 : 290;
            return replies(target, property);
        }, deadlineMs: BatteryMonitor.CorsairTotalBudgetMs, clock: () => now);

        Assert.Equal(2, found.Count);
    }

    [Theory]
    [InlineData(BatteryMonitor.CorsairReplyTimeoutMs - 1, 0)]
    [InlineData(BatteryMonitor.CorsairReplyTimeoutMs, 3)]
    public void QueryCorsairInterface_SendsNothingItHasNoTimeToHearFrom(long budget, int requests)
    {
        var sent = new List<(byte Target, byte Property)>();

        BatteryMonitor.QueryCorsairInterface(0x1B4C, new(),
            CorsairReplies(sent, (0x08, Level, "00000200E803"), (0x08, State, "000002000300")),
            deadlineMs: budget, clock: () => 0);

        Assert.Equal(requests, sent.Count);
    }

    [Fact]
    public void QueryCorsairInterface_AsksNoSlotWithoutTheListOfConnectedDevices()
    {
        var receiver = new CorsairReceiver();

        for (var read = 0; read < 2; read++)
        {
            Assert.Empty(receiver.Read([.. CorsairHeadsetSlot, .. CorsairMouseSlot]));
            Assert.All(receiver.Sent, s => Assert.Equal(0x08, s.Target));
        }
    }

    [Theory]
    [InlineData(null)]           // it does not say which product it is
    [InlineData("01010200491B")] // a product that is not listed (a wired keyboard's id)
    public void QueryCorsairInterface_ReadsNoBatteryOfAnUnknownDeviceOnASlot(string? productId)
    {
        (byte, byte, string)[] identity = productId is null ? [] : [(0x09, Pid, productId)];
        (byte, byte, string)[] replies =
            [CorsairBothConnected, .. CorsairHeadsetSlot[..2], .. identity, .. CorsairMouseSlot];
        var receiver = new CorsairReceiver();
        receiver.Read(replies);

        Assert.Equal([(0x1B4C, 94, false)], receiver.Read(replies));
        Assert.All(receiver.Sent.Where(s => s.Target == 0x09), s => Assert.Equal(Pid, s.Property));
    }

    [Theory]
    [InlineData(0x1B49)] // a Corsair keyboard that is not listed
    [InlineData(0x1BF0)] // iCUE's virtual device
    public void QueryCorsairInterface_AsksNothingOfAnUnknownProduct(int productId)
    {
        var sent = new List<(byte Target, byte Property)>();

        var found = BatteryMonitor.QueryCorsairInterface(productId, new(), CorsairReplies(sent, CorsairBothAnswering));

        Assert.Empty(found);
        Assert.Empty(sent);
    }

    [Fact]
    public void QueryCorsairInterface_DoesAnInterruptedIdentificationAgain()
    {
        var state = new BatteryMonitor.CorsairInterfaceState { Connected = [0x09, 0x0A], ConnectedAt = 0 };
        var sent = new List<(byte Target, byte Property)>();
        var replies = CorsairReplies(sent, CorsairBothAnswering);
        long now = 0;

        // The time runs out once the list of connected devices has been read.
        BatteryMonitor.QueryCorsairInterface(0x0A6B, state, (target, property) =>
        {
            now += 100;
            return replies(target, property);
        }, deadlineMs: 450, clock: () => now);

        Assert.Equal(2, sent.Count);
        Assert.Null(state.IdentifiedAt);
    }

    [Fact]
    public void QueryCorsairInterface_ReadsACabledDeviceWithoutAskingForSlots()
    {
        var sent = new List<(byte Target, byte Property)>();

        var found = BatteryMonitor.QueryCorsairInterface(0x1B4C, new(),
            CorsairReplies(sent, (0x08, Level, "00000200E803"), (0x08, State, "000002000300")));

        Assert.Equal([(0x1B4C, 100, true)], found);
        Assert.All(sent, s => Assert.Equal(0x08, s.Target));
    }

    [Fact]
    public void CorsairReportIds_AreBothTriedOnlyUntilOneWorks()
    {
        Assert.Equal(new byte[] { 0x02, 0x00 }, BatteryMonitor.CorsairReportIdsFor(null));
        Assert.Equal(new byte[] { 0x00 }, BatteryMonitor.CorsairReportIdsFor(0x00));
    }

    [Fact]
    public void CorsairEnabled_IsOffWithoutTheSetting()
        => Assert.False(new BatteryMonitor(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BatteryMonitor>.Instance).CorsairEnabled);

    [Theory]
    [InlineData(0x0A69, "headphones")]
    [InlineData(0x1B4C, "mouse")]
    [InlineData(0x1B6E, "keyboard")]
    [InlineData(0x2B10, "keyboard")]
    [InlineData(0x0A6B, null)] // a receiver is not a device
    [InlineData(0x2B07, null)]
    public void CorsairDeviceInfo_KnowsPairedDevices(int pid, string? expectedKind)
        => Assert.Equal(expectedKind, BatteryMonitor.CorsairDeviceInfo(pid).Kind);

    [Fact]
    public void CorsairDeviceBattery_NamesFromTheTable()
    {
        var onReceiver = BatteryMonitor.CorsairDeviceBattery(0x0A69, 90, false);
        Assert.Equal(new DeviceBattery("Corsair HS80 RGB Wireless", 90, "headphones", false), onReceiver);

        // The cable and the receiver slot of one device end up as one entry.
        var cabled = BatteryMonitor.CorsairDeviceBattery(0x0A69, 91, true);
        Assert.Equal([cabled], BatteryMonitor.MergeRazerIntoBluetooth([], [onReceiver, cabled]));
    }
}
