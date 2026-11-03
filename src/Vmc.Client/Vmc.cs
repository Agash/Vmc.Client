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
    /// <summary>The OSC address.</summary>
    string Address { get; }

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

/// <summary>Sends VMC messages over UDP.</summary>
public sealed class VmcSender : IDisposable
{
    // The protocol asks for bundles that fit one Ethernet frame.
    private const int MaxBundleBytes = 1500;

    private readonly UdpClient _udp;
    private readonly IPEndPoint _target;

    /// <summary>A sender to one receiver.</summary>
    /// <param name="host">The receiver's host.</param>
    /// <param name="port">Its port; a performer listens on <see cref="VmcMessages.PerformerPort"/>, a marionette on <see cref="VmcMessages.MarionettePort"/>.</param>
    public VmcSender(string host = "127.0.0.1", int port = VmcMessages.PerformerPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _target = new IPEndPoint(Dns.GetHostAddresses(host)[0], port);
        _udp = new UdpClient(_target.AddressFamily);
    }

    /// <summary>Sends one message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>When it is sent.</returns>
    public async ValueTask SendAsync(IVmcMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _ = await _udp.SendAsync(OscCodec.Encode(message.ToOsc()), _target, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Sends a frame of messages as bundles that each fit one datagram; send blendshape values with their <see cref="BlendShapeApply"/> last.</summary>
    /// <param name="messages">The frame.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>When it is sent.</returns>
    public async ValueTask SendFrameAsync(
        IEnumerable<IVmcMessage> messages,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(messages);
        List<OscMessage> bundle = [];
        int size = 16;
        foreach (IVmcMessage message in messages)
        {
            OscMessage osc = message.ToOsc();
            int length = OscCodec.Encode(osc).Length + 4;
            if (bundle.Count > 0 && size + length > MaxBundleBytes)
            {
                _ = await _udp.SendAsync(OscCodec.EncodeBundle(bundle), _target, ct)
                    .ConfigureAwait(false);
                bundle.Clear();
                size = 16;
            }

            bundle.Add(osc);
            size += length;
        }

        if (bundle.Count > 0)
        {
            _ = await _udp.SendAsync(OscCodec.EncodeBundle(bundle), _target, ct)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _udp.Dispose();
}

/// <summary>Receives VMC messages over UDP.</summary>
public sealed partial class VmcReceiver : IDisposable
{
    private readonly UdpClient _udp;
    private readonly ILogger _logger;

    /// <summary>A receiver on a local port.</summary>
    /// <param name="port">The port; a marionette listens on <see cref="VmcMessages.MarionettePort"/>.</param>
    /// <param name="logger">Where it logs.</param>
    public VmcReceiver(int port = VmcMessages.MarionettePort, ILogger<VmcReceiver>? logger = null)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The port the receiver is bound to.</summary>
    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    /// <summary>Every message received, read as VMC; messages the protocol does not define are skipped.</summary>
    /// <param name="ct">Ends the stream.</param>
    /// <returns>The messages in arrival order.</returns>
    public async IAsyncEnumerable<IVmcMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (SocketException ex)
            {
                // A previous send's ICMP error surfaces on the next receive on Windows; the socket stays usable.
                LogReceiveFailed(_logger, ex);
                continue;
            }

            foreach (OscMessage message in OscCodec.Decode(datagram.Buffer))
            {
                if (VmcMessages.Read(message) is { } vmc)
                {
                    yield return vmc;
                }
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _udp.Dispose();

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "A VMC receive failed; receiving continues."
    )]
    private static partial void LogReceiveFailed(ILogger logger, Exception exception);
}
