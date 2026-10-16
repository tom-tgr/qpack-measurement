using System.Net.Http.HPack;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

// Per header list: what .NET does today vs. the same encoding with Huffman-coded literals.
// Decode = Huffman-decoding the coded values with .NET's existing decoder, which a .NET server
// already runs today for every browser request (browsers Huffman-code their headers).
[MemoryDiagnoser]
public class HeaderEncodingBenchmarks
{
    private const int Lists = 100;

    private List<List<(string Name, string Value)>> _lists = null!;
    private DotnetStyleEncoder _today = null!;
    private DotnetStyleEncoder _huffman = null!;
    private byte[][] _huffmanValues = null!;
    private byte[] _decodeBuffer = new byte[16 * 1024];

    [Params("grpc-req.qif", "rest-req.qif", "fb-req.qif", "fb-resp.qif")]
    public string Workload { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _lists = Qif.Read(Path.Combine(CorpusDirectory(), Workload)).Take(Lists).ToList();
        _today = new DotnetStyleEncoder(huffman: false);
        _huffman = new DotnetStyleEncoder(huffman: true);

        var scratch = new byte[16 * 1024];
        _huffmanValues = _lists
            .SelectMany(list => list)
            .Where(h => HuffmanString.GetEncodedLength(h.Value) < h.Value.Length)
            .Select(h => scratch[..HuffmanString.Encode(h.Value, scratch)])
            .ToArray();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Lists)]
    public int Today() => EncodeAll(_today);

    [Benchmark(OperationsPerInvoke = Lists)]
    public int Huffman() => EncodeAll(_huffman);

    [Benchmark(OperationsPerInvoke = Lists)]
    public int Decode()
    {
        var total = 0;
        foreach (var value in _huffmanValues)
        {
            total += System.Net.Http.HPack.Huffman.Decode(value, ref _decodeBuffer);
        }

        return total;
    }

    private int EncodeAll(DotnetStyleEncoder encoder)
    {
        var total = 0;
        foreach (var list in _lists)
        {
            total += encoder.EncodeInto(list);
        }

        return total;
    }

    private static string CorpusDirectory([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "corpus");
}
