using System.Buffers;
using System.Net.Http.HPack;
using System.Text;

// RFC 9204 primitives without Huffman: a prefixed integer under some flag bits, and a raw string literal.
public static class QPackWire
{
    public static int WriteInt(Span<byte> destination, byte flags, int prefixBits, int value)
    {
        destination[0] = flags;
        if (!IntegerEncoder.Encode(value, prefixBits, destination, out var written))
        {
            throw new InvalidOperationException("Buffer too small.");
        }

        return written;
    }

    // H = 0: the length prefix sits under `flags`, followed by the raw (already lower-case) bytes.
    // Same vectorized copy as QPackEncoder.EncodeValueStringPart, so literals cost the same as today.
    public static int WriteString(Span<byte> destination, byte flags, int prefixBits, string s)
    {
        var prefix = WriteInt(destination, flags, prefixBits, s.Length);
        if (Ascii.FromUtf16(s, destination.Slice(prefix, s.Length), out _) != OperationStatus.Done)
        {
            throw new InvalidOperationException("Header strings must be ASCII.");
        }

        return prefix + s.Length;
    }
}
