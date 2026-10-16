// Picks the length of a padding header ("x-h") so that a header list encodes to an exact number of bytes
// with .NET's current QPACK encoding (the validated model in ../dotnet/DotnetStyleEncoder.cs).
// This lets the latency test send exactly the header-block sizes measured for each compression variant.
public static class HeaderSizer
{
    public const string PadName = "x-h";

    // The encoder reuses one buffer, so calls are serialized; results are cached because every
    // request of a variant needs the same pad (burst tests ask for it concurrently).
    private static readonly DotnetStyleEncoder Encoder = new(huffman: false);
    private static readonly Dictionary<string, int> Cache = new();
    private static readonly Lock Gate = new();

    // Returns the pad length that makes `headers` + x-h encode to `targetBytes` (or the closest below it).
    public static int PadFor(List<(string Name, string Value)> headers, int targetBytes)
    {
        var key = string.Join('\n', headers.Select(h => h.Name + ":" + h.Value)) + "#" + targetBytes;
        lock (Gate)
        {
            if (!Cache.TryGetValue(key, out var pad))
            {
                Cache[key] = pad = ComputePad(headers, targetBytes);
            }

            return pad;
        }
    }

    public static int Encoded(List<(string Name, string Value)> headers)
    {
        lock (Gate)
        {
            return Encoder.EncodeInto(headers);
        }
    }

    private static int ComputePad(List<(string Name, string Value)> headers, int targetBytes)
    {
        var withPad = new List<(string, string)>(headers) { (PadName, "") };
        var best = 0;
        for (var pad = 0; pad <= targetBytes; pad++)
        {
            withPad[^1] = (PadName, new string('a', pad));
            var size = Encoder.EncodeInto(withPad);
            if (size > targetBytes)
            {
                break;
            }

            best = pad;
            if (size == targetBytes)
            {
                break;
            }
        }

        return best;
    }
}
