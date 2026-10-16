using System.Net.Http.HPack;
using System.Net.Http.QPack;

// Encodes header lists the way .NET does today (see Program.cs for the rules). With huffman: true,
// every literal string is Huffman-coded when that is shorter: the only change a Huffman PR would make.
public sealed class DotnetStyleEncoder
{
    private readonly bool _huffman;
    private readonly StaticLookup _static = StaticLookup.Instance;
    private readonly byte[] _buffer = new byte[1 << 20];

    public DotnetStyleEncoder(bool huffman = false)
    {
        _huffman = huffman;
    }

    public byte[] Encode(List<(string Name, string Value)> headers) => _buffer[..EncodeInto(headers)];

    // Allocation-free variant for benchmarks; the block is in Buffer[..returned length].
    public ReadOnlySpan<byte> Buffer => _buffer;

    public int EncodeInto(List<(string Name, string Value)> headers)
    {
        var isResponse = headers.Exists(h => h.Name == ":status");
        _buffer[0] = 0x00; // required insert count
        _buffer[1] = 0x00; // sign + delta base
        var length = 2;

        foreach (var (name, value) in headers)
        {
            length += isResponse ? EncodeKestrel(name, value, length) : EncodeHttpClient(name, value, length);
        }

        return length;
    }

    private int EncodeHttpClient(string name, string value, int offset)
    {
        switch (name)
        {
            case ":method":
            case ":scheme":
                // HttpMethod.Http3EncodedBytes / SchemeHttps: indexed when the exact value is in the table.
                return _static.TryGetExact(name, value, out var exact)
                    ? Indexed(exact, offset)
                    : NameReference(StaticNameIndex(name), value, offset);
            case ":path":
                return value == "/" ? Indexed(H3StaticTable.PathSlash, offset) : NameReference(H3StaticTable.PathSlash, value, offset);
            case ":authority":
                return NameReference(H3StaticTable.Authority, value, offset);
            case "te":
                return Literal(name, value, offset);
        }

        // Known headers carry a pre-encoded static name reference; the value is always a literal.
        return !name.StartsWith(':') && _static.TryGetName(name, out var nameIndex)
            ? NameReference(nameIndex, value, offset)
            : Literal(name, value, offset);
    }

    private int EncodeKestrel(string name, string value, int offset)
    {
        if (name == ":status")
        {
            return H3StaticTable.TryGetStatusIndex(int.Parse(value), out var statusIndex)
                ? Indexed(statusIndex, offset)
                : NameReference(H3StaticTable.Status200, value, offset);
        }

        if (_static.TryGetExact(name, value, out var exact))
        {
            return Indexed(exact, offset);
        }

        return _static.TryGetName(name, out var nameIndex)
            ? NameReference(nameIndex, value, offset)
            : Literal(name, value, offset);
    }

    private int Indexed(int index, int offset)
    {
        Check(QPackEncoder.EncodeStaticIndexedHeaderField(index, _buffer.AsSpan(offset), out var written));
        return written;
    }

    private int NameReference(int index, string value, int offset)
    {
        if (_huffman)
        {
            // 01NT + 4-bit static index, then the value string.
            _buffer[offset] = 0b0101_0000;
            Check(IntegerEncoder.Encode(index, 4, _buffer.AsSpan(offset), out var prefix));
            return prefix + WriteString(value, offset + prefix, prefixBits: 7, flags: 0, huffmanFlag: 0x80);
        }

        Check(QPackEncoder.EncodeLiteralHeaderFieldWithStaticNameReference(index, value, _buffer.AsSpan(offset), out var written));
        return written;
    }

    private int Literal(string name, string value, int offset)
    {
        if (_huffman)
        {
            // 001N H + 3-bit name length (N=1 like .NET's EncodeNameString), then the value string.
            var nameLength = WriteString(name, offset, prefixBits: 3, flags: 0x30, huffmanFlag: 0x08);
            return nameLength + WriteString(value, offset + nameLength, prefixBits: 7, flags: 0, huffmanFlag: 0x80);
        }

        Check(QPackEncoder.EncodeLiteralHeaderFieldWithoutNameReference(name, value, _buffer.AsSpan(offset), out var written));
        return written;
    }

    // String literal with an H flag (RFC 9204 4.1.2): Huffman when shorter, raw otherwise.
    private int WriteString(string s, int offset, int prefixBits, byte flags, byte huffmanFlag)
    {
        var huffmanLength = HuffmanString.GetEncodedLength(s);
        var useHuffman = huffmanLength < s.Length;
        var length = useHuffman ? huffmanLength : s.Length;

        _buffer[offset] = (byte)(flags | (useHuffman ? huffmanFlag : 0));
        Check(IntegerEncoder.Encode(length, prefixBits, _buffer.AsSpan(offset), out var prefix));
        var body = _buffer.AsSpan(offset + prefix);

        if (useHuffman)
        {
            HuffmanString.Encode(s, body);
        }
        else
        {
            for (var i = 0; i < s.Length; i++)
            {
                body[i] = (byte)s[i];
            }
        }

        return prefix + length;
    }

    private int StaticNameIndex(string name) => _static.TryGetName(name, out var index) ? index : throw new InvalidOperationException(name);

    private static void Check(bool ok)
    {
        if (!ok)
        {
            throw new InvalidOperationException("Header block exceeded the encode buffer.");
        }
    }
}
