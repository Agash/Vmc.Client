using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vmc.Client;

/// <summary>Who sends a VMC message, in the protocol's terms.</summary>
public enum VmcDirection
{
    /// <summary>A performer (the avatar app) sends it to a marionette.</summary>
    ToMarionette,

    /// <summary>A marionette or assistant sends it to a performer.</summary>
    ToPerformer,

    /// <summary>Either side.</summary>
    Both,
}

/// <summary>A VMC message; every one the protocol defines is generated from <c>spec/vmc-protocol.txt</c>.</summary>
public interface IVmcMessage
{
    /// <summary>Who sends it.</summary>
    VmcDirection Direction { get; }

    /// <summary>The message as OSC.</summary>
    /// <returns>The OSC message.</returns>
    OscMessage ToOsc();
}

/// <summary>
/// A vendor message under <c>/VMC/Thru/</c>, which a performer relays to its marionette unchanged
/// (protocol 2.6).
/// </summary>
/// <param name="Address">The full address.</param>
/// <param name="Arguments">A string, optionally followed by a float or an integer.</param>
public sealed record VmcThruMessage(string Address, ImmutableArray<OscArgument> Arguments)
    : IVmcMessage
{
    /// <summary>The prefix every vendor message has.</summary>
    public const string Prefix = "/VMC/Thru/";

    /// <inheritdoc/>
    public VmcDirection Direction => VmcDirection.Both;

    /// <inheritdoc/>
    public OscMessage ToOsc() => new(Address, Arguments);
}

public static partial class VmcMessages
{
    /// <summary>The port a marionette usually listens on.</summary>
    public const int MarionettePort = 39539;

    /// <summary>The port a performer usually listens on for assistants.</summary>
    public const int PerformerPort = 39540;

    /// <summary>Reads an OSC message as a VMC message, a vendor <c>/VMC/Thru/</c> message included.</summary>
    /// <param name="message">The OSC message.</param>
    /// <returns>The VMC message, or null when the protocol does not define it.</returns>
    public static IVmcMessage? Read(OscMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return TryRead(message, out IVmcMessage? known) ? known
            : message.Address.StartsWith(VmcThruMessage.Prefix, StringComparison.Ordinal)
                ? new VmcThruMessage(message.Address, message.Arguments)
            : null;
    }
}

/// <summary>
/// Blendshape names. The protocol sends VRM 0.x preset names, which are case sensitive; a receiver on VRM 1.0
/// converts them (protocol.vmc.info, VRM0/VRM1).
/// </summary>
public static class VrmBlendShapes
{
    private static readonly FrozenDictionary<string, string> _toVrm1 = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        ["Joy"] = "happy",
        ["Angry"] = "angry",
        ["Sorrow"] = "sad",
        ["Fun"] = "relaxed",
        ["A"] = "aa",
        ["I"] = "ih",
        ["U"] = "ou",
        ["E"] = "ee",
        ["O"] = "oh",
        ["Blink_L"] = "blinkLeft",
        ["Blink_R"] = "blinkRight",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> _toVrm0 = _toVrm1.ToFrozenDictionary(
        static p => p.Value,
        static p => p.Key,
        StringComparer.Ordinal
    );

    /// <summary>The VRM 1.0 name for a VRM 0.x preset, or the name unchanged when it is not a preset.</summary>
    /// <param name="vrm0Name">A VRM 0.x name.</param>
    /// <returns>The VRM 1.0 name.</returns>
    public static string ToVrm1(string vrm0Name) => _toVrm1.GetValueOrDefault(vrm0Name, vrm0Name);

    /// <summary>The VRM 0.x name for a VRM 1.0 preset, or the name unchanged when it is not a preset.</summary>
    /// <param name="vrm1Name">A VRM 1.0 name.</param>
    /// <returns>The VRM 0.x name, which the protocol sends.</returns>
    public static string ToVrm0(string vrm1Name) => _toVrm0.GetValueOrDefault(vrm1Name, vrm1Name);
}

/// <summary>Sends VMC messages to a Marionette (39539) or Performer (39540), bundling a frame into datagrams that fit one Ethernet frame.</summary>
public sealed class VmcSender : IDisposable
{
    private readonly OscUdpSender _udp;

    /// <summary>A sender.</summary>
    /// <param name="host">The receiving app's host.</param>
    /// <param name="port">Its VMC port.</param>
    public VmcSender(string host = "127.0.0.1", int port = VmcMessages.PerformerPort) =>
        _udp = OscUdpSender.Create(host, port);

    /// <summary>Sends one message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>When it is sent.</returns>
    public ValueTask SendAsync(IVmcMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _udp.SendAsync(message.ToOsc(), ct);
    }

    /// <summary>Sends a frame of messages as bundles, the way VMC senders do.</summary>
    /// <param name="messages">The frame.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>When it is sent.</returns>
    public ValueTask SendFrameAsync(
        IEnumerable<IVmcMessage> messages,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(messages);
        return _udp.SendBundleAsync(messages.Select(static m => m.ToOsc()), ct: ct);
    }

    /// <inheritdoc/>
    public void Dispose() => _udp.Dispose();
}

/// <summary>Receives VMC messages on a port, as a Marionette or Performer does.</summary>
public sealed class VmcReceiver : IDisposable
{
    private readonly OscUdpReceiver _udp;

    /// <summary>A receiver.</summary>
    /// <param name="port">The port; VMC Marionettes listen on 39539.</param>
    /// <param name="bind">The local address; loopback unless given.</param>
    /// <param name="logger">Where receive faults are logged.</param>
    public VmcReceiver(
        int port = VmcMessages.MarionettePort,
        System.Net.IPAddress? bind = null,
        Microsoft.Extensions.Logging.ILogger<OscUdpReceiver>? logger = null
    ) => _udp = new OscUdpReceiver(port, bind, logger);

    /// <summary>The port it listens on.</summary>
    public int Port => _udp.Port;

    /// <summary>Every VMC message received, known ones typed and vendor ones as <see cref="VmcThruMessage"/>.</summary>
    /// <param name="ct">Ends the reading.</param>
    /// <returns>The messages.</returns>
    public async IAsyncEnumerable<IVmcMessage> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await foreach (OscReceived received in _udp.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (VmcMessages.Read(received.Message) is { } vmc)
            {
                yield return vmc;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _udp.Dispose();
}
