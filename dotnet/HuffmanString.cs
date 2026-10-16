using System.Net.Http.HPack;

// Huffman-encodes an ASCII/Latin-1 header string (RFC 7541 Appendix B) with .NET's own code table
// (Huffman.Encode in the shared HPack source). Same loop shape as dotnet/runtime's test-only
// HuffmanEncoder: a 64-bit bit buffer that flushes whole bytes as it fills.
public static class HuffmanString
{
    public static int GetEncodedLength(string s)
    {
        var bits = 0;
        foreach (var c in s)
        {
            bits += Huffman.Encode(c).bitLength;
        }

        return (bits + 7) >> 3;
    }

    public static int Encode(string s, Span<byte> destination)
    {
        ulong buffer = 0;
        var bufferLength = 0;
        var written = 0;

        foreach (var c in s)
        {
            var (code, codeLength) = Huffman.Encode(c);
            buffer = (buffer << codeLength) | (code >> (32 - codeLength));
            bufferLength += codeLength;

            while (bufferLength >= 8)
            {
                bufferLength -= 8;
                destination[written++] = (byte)(buffer >> bufferLength);
            }
        }

        if (bufferLength != 0)
        {
            // Pad with the most significant bits of EOS (all ones).
            destination[written++] = (byte)((buffer << (8 - bufferLength)) | (0xFFu >> bufferLength));
        }

        return written;
    }
}
