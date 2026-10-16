using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

// Per header list on a long-lived connection: .NET today vs. Huffman vs. a dynamic table (no Huffman).
// Every method walks the whole workload in order, 100 lists per invocation, wrapping around, so the
// dynamic encoder stays warm the way a real connection does after its first requests. Unique values
// (traceparent, ids) fall out of its 256-entry "seen" history before they come round again.
[MemoryDiagnoser]
public class DynamicTableBenchmarks
{
    private const int Lists = 100;

    private List<(string Name, string Value)>[] _lists = null!;
    private DotnetStyleEncoder _today = null!;
    private DotnetStyleEncoder _huffman = null!;
    private DynamicTableEncoder _dynamic = null!;
    private DynamicTableEncoder _dynamicIndexAll = null!;
    private int _cursor;

    [Params("grpc-req.qif", "rest-req.qif", "fb-req.qif", "fb-resp.qif")]
    public string Workload { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _lists = Qif.Read(Path.Combine(CorpusDirectory(), Workload)).ToArray();
        _today = new DotnetStyleEncoder(huffman: false);
        _huffman = new DotnetStyleEncoder(huffman: true);
        // Safe mode, acks 10 requests late. Default never indexes authorization/short cookies.
        _dynamic = new DynamicTableEncoder(capacity: 16384, ackDelay: 10);
        _dynamicIndexAll = new DynamicTableEncoder(capacity: 16384, ackDelay: 10, indexSensitive: true);
        for (var i = 0; i < _lists.Length; i++)
        {
            _dynamic.Encode(_lists[i]); // warm the tables: one full pass over the connection
            _dynamicIndexAll.Encode(_lists[i]);
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Lists)]
    public int Today()
    {
        var total = 0;
        for (var i = 0; i < Lists; i++)
        {
            total += _today.EncodeInto(Next());
        }

        return total;
    }

    [Benchmark(OperationsPerInvoke = Lists)]
    public int Huffman()
    {
        var total = 0;
        for (var i = 0; i < Lists; i++)
        {
            total += _huffman.EncodeInto(Next());
        }

        return total;
    }

    [Benchmark(OperationsPerInvoke = Lists)]
    public int DynamicTable()
    {
        var total = 0;
        for (var i = 0; i < Lists; i++)
        {
            var (encoderLength, blockLength) = _dynamic.Encode(Next());
            total += encoderLength + blockLength;
        }

        return total;
    }

    [Benchmark(OperationsPerInvoke = Lists)]
    public int DynamicTableIndexAll()
    {
        var total = 0;
        for (var i = 0; i < Lists; i++)
        {
            var (encoderLength, blockLength) = _dynamicIndexAll.Encode(Next());
            total += encoderLength + blockLength;
        }

        return total;
    }

    private List<(string Name, string Value)> Next()
    {
        var list = _lists[_cursor];
        _cursor = _cursor + 1 == _lists.Length ? 0 : _cursor + 1;
        return list;
    }

    private static string CorpusDirectory([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "corpus");
}
