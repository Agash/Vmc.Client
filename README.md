# Vmc.Client

.NET implementation of the [VMC protocol](https://protocol.vmc.info/english) (Virtual Motion Capture, protocol
3.1) over OSC: drive or read VSeeFace, VNyan, Warudo, VMC and any other VMC performer or marionette. Every
message the protocol defines is a typed record, generated from `spec/vmc-protocol.txt`. Native AOT
compatible, `net10.0` and `net11.0`.

## Install

```shell
dotnet add package Vmc.Client
```

## Use

```csharp
// Make a performer (an avatar app listening on 39540) smile, then blink.
using VmcSender performer = new("127.0.0.1", VmcMessages.PerformerPort);
await performer.SendFrameAsync([new BlendShapeValue("Joy", 1f), new BlendShapeApply()]);

// Read what a performer sends to a marionette (39539).
using VmcReceiver marionette = new(VmcMessages.MarionettePort);
await foreach (IVmcMessage message in marionette.ReadAllAsync())
{
    if (message is BoneTransform bone)
    {
        Console.WriteLine($"{bone.Bone}: {bone.PositionX}, {bone.PositionY}, {bone.PositionZ}");
    }
}
```

`SendFrameAsync` packs a frame into bundles that each fit one datagram. Blendshape names are VRM 0.x presets
and case sensitive; `VrmBlendShapes` converts to and from VRM 1.0 names. Messages the protocol adds in later
versions are optional trailing arguments, so older senders still read. Vendor `/VMC/Thru/` messages arrive as
`VmcThruMessage` for relaying.

OSC itself comes from [`OpenSoundControl.Core`](https://github.com/Agash/OpenSoundControl.Core). Every generated message implements its
`IOscMessage<TSelf>`, so it routes typed on a shared endpoint beside other OSC dialects:
`router.On<BlendShapeValue>(...)`. `VmcReceiver` listens on loopback unless given a `bind` address; pass
`IPAddress.Any` to receive from another machine.

## Adding a message

Add a line to `spec/vmc-protocol.txt`; the source generator in `src/Vmc.Client.Generators` emits its record,
encoding and decoding on the next build.

## License

MIT. Not affiliated with the VMC protocol's authors.
