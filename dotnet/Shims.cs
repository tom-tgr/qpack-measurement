// Stand-ins for internal runtime helpers that the shared QPACK source references.
// They only affect exception metadata/messages, never the encoded bytes.

internal static class Obsoletions
{
    public const string LegacyFormatterImplMessage = "Legacy serialization support is obsolete.";
    public const string LegacyFormatterImplDiagId = "SYSLIB0051";
    public const string SharedUrlFormat = "https://aka.ms/dotnet-warnings/{0}";
}

internal static class SR
{
    public const string net_http_request_invalid_char_encoding = "Request headers must contain only ASCII characters.";
    public const string net_http_hpack_huffman_decode_failed = "Huffman-coded literal string failed to decode.";
}
