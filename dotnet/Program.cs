using System.Net.Http.QPack;

// Encodes every header list of a QIF file the way .NET does today: static table only, no Huffman,
// required-insert-count/base prefix always 0. Prints one hex header block per line.
//
//   request blocks  (no :status) -> HttpClient rules: Http3RequestStream.BufferHeaders + KnownHeader.Http3EncodedName
//   response blocks (:status)    -> Kestrel rules: QPackHeaderWriter (exact static match indexed, else name reference)
//
// Approximation (favours .NET slightly): any header whose name is in the static table gets a static
// name reference, although HttpClient/Kestrel only do that for headers in their known-header lists.

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: DotnetBaseline <file.qif>");
    return 1;
}

var encoder = new DotnetStyleEncoder();
foreach (var headers in Qif.Read(args[0]))
{
    Console.WriteLine(Convert.ToHexString(encoder.Encode(headers)));
}

return 0;

internal static class Qif
{
    // QIF: "name<TAB>value" lines, blank line between header lists, '#' comments.
    public static IEnumerable<List<(string Name, string Value)>> Read(string path)
    {
        var current = new List<(string, string)>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            if (line.Length == 0)
            {
                if (current.Count > 0)
                {
                    yield return current;
                    current = new List<(string, string)>();
                }

                continue;
            }

            var tab = line.IndexOf('\t');
            current.Add(tab < 0 ? (line, "") : (line[..tab], line[(tab + 1)..]));
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }
}

internal sealed class DotnetStyleEncoder
{
    private readonly Dictionary<string, int> _firstNameIndex = new();
    private readonly Dictionary<(string, string), int> _exactIndex = new();
    private readonly byte[] _buffer = new byte[1 << 20];

    public DotnetStyleEncoder()
    {
        for (var i = 0; i < H3StaticTable.Count; i++)
        {
            var field = H3StaticTable.Get(i);
            var name = System.Text.Encoding.ASCII.GetString(field.Name);
            var value = System.Text.Encoding.ASCII.GetString(field.Value);
            _firstNameIndex.TryAdd(name, i);
            _exactIndex.TryAdd((name, value), i);
        }
    }

    public byte[] Encode(List<(string Name, string Value)> headers)
    {
        var isResponse = headers.Exists(h => h.Name == ":status");
        _buffer[0] = 0x00; // required insert count
        _buffer[1] = 0x00; // sign + delta base
        var length = 2;

        foreach (var (name, value) in headers)
        {
            length += isResponse ? EncodeKestrel(name, value, length) : EncodeHttpClient(name, value, length);
        }

        return _buffer[..length];
    }

    private int EncodeHttpClient(string name, string value, int offset)
    {
        switch (name)
        {
            case ":method":
            case ":scheme":
                // HttpMethod.Http3EncodedBytes / SchemeHttps: indexed when the exact value is in the table.
                return _exactIndex.TryGetValue((name, value), out var exact)
                    ? Indexed(exact, offset)
                    : NameReference(_firstNameIndex[name], value, offset);
            case ":path":
                return value == "/" ? Indexed(H3StaticTable.PathSlash, offset) : NameReference(H3StaticTable.PathSlash, value, offset);
            case ":authority":
                return NameReference(H3StaticTable.Authority, value, offset);
            case "te":
                return Literal(name, value, offset);
        }

        // Known headers carry a pre-encoded static name reference; the value is always a literal.
        return !name.StartsWith(':') && _firstNameIndex.TryGetValue(name, out var nameIndex)
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

        if (_exactIndex.TryGetValue((name, value), out var exact))
        {
            return Indexed(exact, offset);
        }

        return _firstNameIndex.TryGetValue(name, out var nameIndex)
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
        Check(QPackEncoder.EncodeLiteralHeaderFieldWithStaticNameReference(index, value, _buffer.AsSpan(offset), out var written));
        return written;
    }

    private int Literal(string name, string value, int offset)
    {
        Check(QPackEncoder.EncodeLiteralHeaderFieldWithoutNameReference(name, value, _buffer.AsSpan(offset), out var written));
        return written;
    }

    private static void Check(bool ok)
    {
        if (!ok)
        {
            throw new InvalidOperationException("Header block exceeded the encode buffer.");
        }
    }
}
