using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

// One variant = the header-block sizes (bytes on the wire) that one encoding produces for a workload.
// Sizes come from measure.py (safe mode, acks 10 late, 16 KB table): see README.
public sealed record Variant(string Name, int RequestHeaderBytes, int ResponseHeaderBytes);

public sealed record Result(string Scenario, string Variant, int RequestBytes, int ResponseBytes, double MedianMs, double P90Ms);

public sealed class Scenarios(int port, Version version, bool quick)
{
    private const int RequestBodyBytes = 100;
    private static readonly byte[] RequestBody = new byte[RequestBodyBytes];
    private string Authority => $"127.0.0.1:{port}";

    // Responses stay unpadded (Kestrel's own ~74 bytes) in the API scenarios: gRPC responses are tiny anyway.
    public static readonly Variant[] Api =
    [
        new(".NET today", 1502, 0),
        new("dynamic, auth not indexed", 1306, 0),
        new("dynamic, auth indexed", 119, 0),
    ];

    public static readonly Variant[] Page =
    [
        new(".NET today", 511, 745),
        new("dynamic table", 203, 213),
    ];

    // One request at a time on a warm connection (a unary call in a chatty client).
    public async Task<List<Result>> SequentialAsync(string scenario, Variant[] variants)
    {
        var requests = quick ? 20 : 100;
        var clients = variants.Select(_ => CreateClient()).ToArray();
        for (var v = 0; v < variants.Length; v++)
        {
            for (var i = 0; i < 10; i++)
            {
                await SendAsync(clients[v], variants[v]); // warm-up: handshake, congestion window
            }
        }

        var timings = variants.Select(_ => new List<double>()).ToArray();
        for (var i = 0; i < requests; i++)
        {
            for (var v = 0; v < variants.Length; v++) // interleaved so drift hits every variant equally
            {
                var started = Stopwatch.GetTimestamp();
                await SendAsync(clients[v], variants[v]);
                timings[v].Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }

        return Summarize(scenario, variants, timings);
    }

    // A fresh connection, then `concurrency` requests at once (page load, or a fan-out of RPCs).
    public async Task<List<Result>> BurstAsync(string scenario, Variant[] variants, int concurrency)
    {
        var repetitions = quick ? 3 : 10;
        var timings = variants.Select(_ => new List<double>()).ToArray();
        for (var r = 0; r < repetitions; r++)
        {
            for (var v = 0; v < variants.Length; v++)
            {
                using var client = CreateClient();
                var started = Stopwatch.GetTimestamp();
                await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => SendAsync(client, variants[v])));
                timings[v].Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        return Summarize(scenario, variants, timings);
    }

    private async Task SendAsync(HttpClient client, Variant variant)
    {
        var path = $"/r?s={variant.ResponseHeaderBytes}";
        // DefaultRequestVersion only applies to HttpClient's helper methods, not to SendAsync with our own message.
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(RequestBody),
            Version = version,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation(HeaderSizer.PadName, new string('a', RequestPad(path, variant.RequestHeaderBytes)));
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        await response.Content.CopyToAsync(Stream.Null);
    }

    // What HttpClient puts in the header block for this request (Http3RequestStream.BufferHeaders).
    public List<(string, string)> RequestHeaders(string path) =>
    [
        (":method", "POST"), (":scheme", "https"), (":authority", Authority), (":path", path),
        ("alt-used", Authority), ("content-type", "application/grpc"), ("content-length", RequestBodyBytes.ToString()),
    ];

    private int RequestPad(string path, int targetBytes) => targetBytes == 0 ? 0 : HeaderSizer.PadFor(RequestHeaders(path), targetBytes);

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }, // throwaway test certificate
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{Authority}"),
            DefaultRequestVersion = version,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    private List<Result> Summarize(string scenario, Variant[] variants, List<double>[] timings)
    {
        var results = new List<Result>();
        for (var v = 0; v < variants.Length; v++)
        {
            var sorted = timings[v].Order().ToArray();
            var path = $"/r?s={variants[v].ResponseHeaderBytes}";
            var requestBytes = variants[v].RequestHeaderBytes == 0
                ? HeaderSizer.Encoded(RequestHeaders(path))
                : HeaderSizer.Encoded([.. RequestHeaders(path), (HeaderSizer.PadName, new string('a', RequestPad(path, variants[v].RequestHeaderBytes)))]);
            results.Add(new Result(scenario, variants[v].Name, requestBytes, variants[v].ResponseHeaderBytes,
                Percentile(sorted, 0.5), Percentile(sorted, 0.9)));
        }

        return results;
    }

    private static double Percentile(double[] sorted, double p) => sorted[(int)Math.Min(sorted.Length - 1, Math.Round(p * (sorted.Length - 1)))];
}
