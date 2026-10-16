using System.Net.Http.QPack;

// Encodes every header list of a QIF file the way .NET does today: static table only, no Huffman,
// required-insert-count/base prefix always 0. Prints one hex header block per line.
// --huffman: same rules, but literal strings are Huffman-coded when shorter.
//
//   request blocks  (no :status) -> HttpClient rules: Http3RequestStream.BufferHeaders + KnownHeader.Http3EncodedName
//   response blocks (:status)    -> Kestrel rules: QPackHeaderWriter (exact static match indexed, else name reference)
//
// Approximation (favours .NET slightly): any header whose name is in the static table gets a static
// name reference, although HttpClient/Kestrel only do that for headers in their known-header lists.

var huffman = args.Length == 2 && args[0] == "--huffman";
if (args.Length != (huffman ? 2 : 1))
{
    Console.Error.WriteLine("usage: DotnetBaseline [--huffman] <file.qif>");
    return 1;
}

var encoder = new DotnetStyleEncoder(huffman);
foreach (var headers in Qif.Read(args[^1]))
{
    Console.WriteLine(Convert.ToHexString(encoder.Encode(headers)));
}

return 0;
