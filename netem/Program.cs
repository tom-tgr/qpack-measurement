using System.Net;
using System.Net.Quic;

// Usage: NetemLatency --profile <label> [--out results.md] [--smoke] [--quick]
// The network shaping itself is done outside (tc netem on lo, see run.sh); --profile only labels the output.
// --smoke runs everything over HTTP/1.1 to check the code on platforms without QUIC (macOS has no HTTP/3 and
// no server ALPN for HTTP/2); its numbers mean nothing for HTTP/3.
var profile = Arg("--profile") ?? "unlabelled";
var output = Arg("--out");
var smoke = args.Contains("--smoke");
var quick = args.Contains("--quick");

if (!smoke && !QuicConnection.IsSupported)
{
    Console.Error.WriteLine("QUIC is not supported here (needs Linux with libmsquic, or Windows). Use --smoke for a smoke test.");
    return 1;
}

const int Port = 5443;
await using var server = await TestServer.StartAsync(Port, http3: !smoke);
var scenarios = new Scenarios(Port, smoke ? HttpVersion.Version11 : HttpVersion.Version30, quick);

var results = new List<Result>();
results.AddRange(await scenarios.SequentialAsync("gRPC unary, sequential, warm connection", Scenarios.ApiWarm));
results.AddRange(await scenarios.WarmBurstAsync("gRPC fan-out, 50 concurrent, warm connection", Scenarios.ApiWarm, 50));
results.AddRange(await scenarios.BurstAsync("gRPC fan-out, 50 concurrent, new connection", Scenarios.ApiCold, 50));
results.AddRange(await scenarios.BurstAsync("page load, 100 concurrent, new connection", Scenarios.PageCold, 100));

var lines = new List<string> { $"### {profile}{(smoke ? " (HTTP/1.1 smoke test, not HTTP/3)" : "")}", "",
    "| Scenario | Variant | Request header bytes | Response header bytes | Median ms | p90 ms | Median vs today |",
    "|---|---|---:|---:|---:|---:|---:|" };
foreach (var group in results.GroupBy(r => r.Scenario))
{
    var today = group.First().MedianMs;
    foreach (var r in group)
    {
        var delta = r.MedianMs - today;
        var deltaText = r == group.First() ? "" : $"{delta:+0.00;-0.00;0.00} ms ({delta / today:+0.0%;-0.0%;0.0%})";
        var response = r.ResponseBytes == 0 ? "~74" : r.ResponseBytes.ToString();
        lines.Add($"| {r.Scenario} | {r.Variant} | {r.RequestBytes} | {response} | {r.MedianMs:0.00} | {r.P90Ms:0.00} | {deltaText} |");
    }
}

lines.Add("");
foreach (var line in lines)
{
    Console.WriteLine(line);
}

if (output is not null)
{
    await File.AppendAllLinesAsync(output, lines);
}

return 0;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
