using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;

// Context for the Huffman numbers: every header byte sent over HTTP/3 is also encrypted
// (QUIC packet protection, usually AES-128-GCM). This is the CPU cost of encrypting N bytes,
// i.e. what each saved byte stops costing. Run separately so it cannot disturb the main benchmark:
//   dotnet run -c Release -- --filter '*Encryption*'
public class EncryptionContextBenchmarks
{
    private AesGcm _aes = null!;
    private byte[] _plaintext = null!;
    private byte[] _ciphertext = null!;
    private readonly byte[] _nonce = new byte[12];
    private readonly byte[] _tag = new byte[16];

    // 1,502 / 1,182 = average gRPC request headers today / with Huffman (see README).
    [Params(1182, 1502)]
    public int Bytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _aes = new AesGcm(RandomNumberGenerator.GetBytes(16), tagSizeInBytes: 16);
        _plaintext = RandomNumberGenerator.GetBytes(Bytes);
        _ciphertext = new byte[Bytes];
    }

    [Benchmark]
    public void Aes128Gcm() => _aes.Encrypt(_nonce, _plaintext, _ciphertext, _tag);

    [GlobalCleanup]
    public void Cleanup() => _aes.Dispose();
}
