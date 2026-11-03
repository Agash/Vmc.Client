using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace Vmc.Client;

/// <summary>The OSC argument types VMC uses.</summary>
public enum OscType
{
    /// <summary><c>i</c>: 32-bit integer.</summary>
    IntegerNumber,

    /// <summary><c>f</c>: 32-bit float.</summary>
    FloatNumber,

    /// <summary><c>s</c>: string.</summary>
    Text,
}

/// <summary>One OSC argument.</summary>
public readonly record struct OscArgument
{
    private readonly int _int;
    private readonly float _float;
    private readonly string? _string;

    private OscArgument(OscType type, int i, float f, string? s)
    {
        Type = type;
        _int = i;
        _float = f;
        _string = s;
    }

    /// <summary>The argument's type.</summary>
    public OscType Type { get; }

    /// <summary>An integer argument.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The argument.</returns>
    public static OscArgument FromInt32(int value) => new(OscType.IntegerNumber, value, 0, null);

    /// <summary>A float argument.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The argument.</returns>
    public static OscArgument FromFloat32(float value) => new(OscType.FloatNumber, 0, value, null);

    /// <summary>A string argument.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The argument.</returns>
    public static OscArgument FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(OscType.Text, 0, 0, value);
    }

    /// <summary>The integer, when this is one.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGetInt32(out int value)
    {
        value = _int;
        return Type is OscType.IntegerNumber;
    }

    /// <summary>The float, when this is one; an integer reads as a float, since some senders send whole numbers as <c>i</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGetFloat32(out float value)
    {
        value = Type is OscType.IntegerNumber ? _int : _float;
        return Type is OscType.FloatNumber or OscType.IntegerNumber;
    }

    /// <summary>The string, when this is one.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGetString(out string value)
    {
        value = _string ?? string.Empty;
        return Type is OscType.Text;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        Type switch
        {
            OscType.IntegerNumber => _int.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            ),
            OscType.FloatNumber => _float.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            ),
            _ => _string ?? string.Empty,
        };
}

/// <summary>An OSC message: an address and its arguments.</summary>
/// <param name="Address">The OSC address.</param>
/// <param name="Arguments">The arguments in order.</param>
public sealed record OscMessage(string Address, ImmutableArray<OscArgument> Arguments);

/// <summary>OSC 1.0 encoding: messages, and bundles of them.</summary>
public static class OscCodec
{
    private static readonly byte[] _bundleTag = "#bundle\0"u8.ToArray();

    /// <summary>The OSC time tag that means "immediately".</summary>
    public const ulong Immediately = 1;

    /// <summary>Encodes a message.</summary>
    /// <param name="message">The message.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Encode(OscMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using MemoryStream stream = new();
        Write(stream, message);
        return stream.ToArray();
    }

    /// <summary>Encodes messages as one bundle.</summary>
    /// <param name="messages">The messages.</param>
    /// <param name="timeTag">When they apply; <see cref="Immediately"/> by default.</param>
    /// <returns>The bundle's bytes.</returns>
    public static byte[] EncodeBundle(IEnumerable<OscMessage> messages, ulong timeTag = Immediately)
    {
        ArgumentNullException.ThrowIfNull(messages);
        using MemoryStream stream = new();
        stream.Write(_bundleTag);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(number, timeTag);
        stream.Write(number);
        foreach (OscMessage message in messages)
        {
            byte[] element = Encode(message);
            BinaryPrimitives.WriteInt32BigEndian(number, element.Length);
            stream.Write(number[..4]);
            stream.Write(element);
        }

        return stream.ToArray();
    }

    /// <summary>Decodes a packet: one message, or a bundle whose messages (and nested bundles) are flattened.</summary>
    /// <param name="packet">The packet.</param>
    /// <returns>Its messages, or none when it is not valid OSC.</returns>
    public static ImmutableArray<OscMessage> Decode(ReadOnlySpan<byte> packet)
    {
        ImmutableArray<OscMessage>.Builder messages = ImmutableArray.CreateBuilder<OscMessage>();
        _ = TryDecode(packet, messages, depth: 0);
        return messages.ToImmutable();
    }

    private static bool TryDecode(
        ReadOnlySpan<byte> packet,
        ImmutableArray<OscMessage>.Builder messages,
        int depth
    )
    {
        if (depth > 8)
        {
            return false;
        }

        if (packet.StartsWith(_bundleTag))
        {
            int at = 16;
            while (at + 4 <= packet.Length)
            {
                int size = BinaryPrimitives.ReadInt32BigEndian(packet[at..]);
                at += 4;
                if (
                    size <= 0
                    || at + size > packet.Length
                    || !TryDecode(packet.Slice(at, size), messages, depth + 1)
                )
                {
                    return false;
                }

                at += size;
            }

            return true;
        }

        int offset = 0;
        if (!TryReadString(packet, ref offset, out string address) || !address.StartsWith('/'))
        {
            return false;
        }

        if (offset >= packet.Length)
        {
            messages.Add(new OscMessage(address, []));
            return true;
        }

        if (!TryReadString(packet, ref offset, out string tags) || !tags.StartsWith(','))
        {
            return false;
        }

        ImmutableArray<OscArgument>.Builder arguments = ImmutableArray.CreateBuilder<OscArgument>(
            tags.Length - 1
        );
        foreach (char tag in tags.AsSpan(1))
        {
            switch (tag)
            {
                case 'i' when offset + 4 <= packet.Length:
                    arguments.Add(
                        OscArgument.FromInt32(BinaryPrimitives.ReadInt32BigEndian(packet[offset..]))
                    );
                    offset += 4;
                    break;
                case 'f' when offset + 4 <= packet.Length:
                    arguments.Add(
                        OscArgument.FromFloat32(
                            BinaryPrimitives.ReadSingleBigEndian(packet[offset..])
                        )
                    );
                    offset += 4;
                    break;
                case 's' when TryReadString(packet, ref offset, out string text):
                    arguments.Add(OscArgument.FromString(text));
                    break;
                case 'T':
                    arguments.Add(OscArgument.FromInt32(1));
                    break;
                case 'F':
                    arguments.Add(OscArgument.FromInt32(0));
                    break;
                default:
                    // A type VMC does not use; the message is skipped, as receivers ignore what they cannot read.
                    return false;
            }
        }

        messages.Add(new OscMessage(address, arguments.MoveToImmutable()));
        return true;
    }

    private static void Write(MemoryStream stream, OscMessage message)
    {
        WriteString(stream, message.Address);
        StringBuilder tags = new(",", message.Arguments.Length + 1);
        foreach (OscArgument argument in message.Arguments)
        {
            _ = tags.Append(
                argument.Type switch
                {
                    OscType.IntegerNumber => 'i',
                    OscType.FloatNumber => 'f',
                    _ => 's',
                }
            );
        }

        WriteString(stream, tags.ToString());
        Span<byte> number = stackalloc byte[4];
        foreach (OscArgument argument in message.Arguments)
        {
            if (argument.TryGetString(out string text))
            {
                WriteString(stream, text);
            }
            else if (argument.Type is OscType.IntegerNumber && argument.TryGetInt32(out int i))
            {
                BinaryPrimitives.WriteInt32BigEndian(number, i);
                stream.Write(number);
            }
            else if (argument.TryGetFloat32(out float f))
            {
                BinaryPrimitives.WriteSingleBigEndian(number, f);
                stream.Write(number);
            }
        }
    }

    // OSC strings are UTF-8, terminated by a null and padded with nulls to a multiple of four bytes.
    private static void WriteString(MemoryStream stream, string text)
    {
        int length = Encoding.UTF8.GetByteCount(text);
        int padded = (length + 4) & ~3;
        Span<byte> bytes = padded <= 256 ? stackalloc byte[padded] : new byte[padded];
        bytes.Clear();
        _ = Encoding.UTF8.GetBytes(text, bytes);
        stream.Write(bytes);
    }

    private static bool TryReadString(ReadOnlySpan<byte> packet, ref int offset, out string text)
    {
        int end = packet[offset..].IndexOf((byte)0);
        if (end < 0)
        {
            text = string.Empty;
            return false;
        }

        text = Encoding.UTF8.GetString(packet.Slice(offset, end));
        offset += (end + 4) & ~3;
        return offset <= packet.Length;
    }
}
