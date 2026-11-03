namespace Vmc.Client.Tests;

[TestClass]
public sealed class VmcTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Encode_SpecExample_MatchesOscOneByteForByte()
    {
        // The example from the OSC 1.0 specification: "/oscillator/4/frequency" with the float 440.0.
        byte[] expected =
        [
            0x2f,
            0x6f,
            0x73,
            0x63,
            0x69,
            0x6c,
            0x6c,
            0x61,
            0x74,
            0x6f,
            0x72,
            0x2f,
            0x34,
            0x2f,
            0x66,
            0x72,
            0x65,
            0x71,
            0x75,
            0x65,
            0x6e,
            0x63,
            0x79,
            0x00,
            0x2c,
            0x66,
            0x00,
            0x00,
            0x43,
            0xdc,
            0x00,
            0x00,
        ];

        byte[] actual = OscCodec.Encode(
            new OscMessage("/oscillator/4/frequency", [OscArgument.FromFloat32(440f)])
        );

        Assert.AreSequenceEqual(expected, actual);
    }

    [TestMethod]
    public void BlendShapeValue_RoundTrips()
    {
        BlendShapeValue sent = new("Joy", 0.75f);

        IVmcMessage? read = VmcMessages.Read(
            OscCodec.Decode(OscCodec.Encode(sent.ToOsc())).Single()
        );

        Assert.AreEqual(sent, read);
    }

    [TestMethod]
    public void Available_FromAnOlderSender_ReadsWithoutTheLaterArguments()
    {
        OscMessage v20 = new(Available.AddressPattern, [OscArgument.FromInt32(1)]);

        Assert.IsTrue(Available.TryRead(v20, out Available? read));
        Assert.AreEqual(1, read.Loaded);
        Assert.IsNull(read.CalibrationState);
        Assert.HasCount(1, read.ToOsc().Arguments);
    }

    [TestMethod]
    public void TryRead_TooFewArguments_IsIgnored()
    {
        OscMessage truncated = new(BoneTransform.AddressPattern, [OscArgument.FromString("Head")]);

        Assert.IsNull(VmcMessages.Read(truncated));
    }

    [TestMethod]
    public void Decode_Bundle_FlattensItsMessages()
    {
        byte[] bundle = OscCodec.EncodeBundle([
            new BlendShapeValue("A", 1f).ToOsc(),
            new BlendShapeApply().ToOsc(),
        ]);

        IVmcMessage?[] read = [.. OscCodec.Decode(bundle).Select(VmcMessages.Read)];

        Assert.HasCount(2, read);
        Assert.IsInstanceOfType<BlendShapeValue>(read[0]);
        Assert.IsInstanceOfType<BlendShapeApply>(read[1]);
    }

    [TestMethod]
    public void Read_VendorMessage_IsKeptForRelay()
    {
        OscMessage vendor = new(
            "/VMC/Thru/Example/Hello",
            [OscArgument.FromString("hi"), OscArgument.FromFloat32(2f)]
        );

        Assert.IsInstanceOfType<VmcThruMessage>(VmcMessages.Read(vendor));
    }

    [TestMethod]
    public void VrmBlendShapes_MapBothWays()
    {
        Assert.AreEqual("happy", VrmBlendShapes.ToVrm1("Joy"));
        Assert.AreEqual("Blink_L", VrmBlendShapes.ToVrm0("blinkLeft"));
        Assert.AreEqual("Custom", VrmBlendShapes.ToVrm1("Custom"));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task SendFrameAsync_ArrivesAtTheReceiver()
    {
        using VmcReceiver receiver = new(port: 0);
        using VmcSender sender = new("127.0.0.1", receiver.Port);

        await sender.SendFrameAsync(
            [new BlendShapeValue("A", 0.5f), new BlendShapeApply()],
            TestContext.CancellationToken
        );

        List<IVmcMessage> received = [];
        await foreach (IVmcMessage message in receiver.ReadAllAsync(TestContext.CancellationToken))
        {
            received.Add(message);
            if (received.Count == 2)
            {
                break;
            }
        }

        Assert.AreEqual(new BlendShapeValue("A", 0.5f), received[0]);
        Assert.IsInstanceOfType<BlendShapeApply>(received[1]);
    }
}
