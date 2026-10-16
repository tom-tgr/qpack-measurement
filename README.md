# How much would QPACK dynamic compression save .NET?

Measurement for [dotnet/runtime#49561](https://github.com/dotnet/runtime/issues/49561) ("HTTP/3: QPack dynamic
compression and decompression"). The team said they would revisit it *"if perf analysis demonstrates there may be
measurable value"*. This repo measures the header-byte part of that value.

## Background in five points

1. **HTTP/3 compresses headers with QPACK** (RFC 9204). It has three tools:
   - a **static table**: 99 common headers built into the spec (`:method GET`, `content-type application/json`, ...).
     A full match costs 1 byte, and a name-only match saves sending the name.
   - **Huffman coding**: a fixed bit-level code for text, about 20–30% smaller. It needs no state.
   - a **dynamic table**: a per-connection table that both sides build at runtime. After a header is sent once,
     later requests can refer to it in 1–2 bytes. This is where the big savings are, for example an auth token
     repeated on every request.
2. **The dynamic table needs two extra streams.** The encoder sends "insert this entry" instructions on an
   *encoder stream*, and the decoder answers on a *decoder stream* ("got it"). A request that refers to an entry
   the peer has not received yet must wait. This is called **blocking**, and `SETTINGS_QPACK_BLOCKED_STREAMS` limits it.
   With that setting at 0, the encoder only refers to entries the peer has already confirmed, so nothing can ever block.
3. **.NET today uses only the static table:** no dynamic table and no Huffman. HttpClient and Kestrel advertise a
   dynamic table capacity of 0, so peers can't use one when sending to .NET either. On top of that, HttpClient sends
   known headers as "static name + literal value" even when the exact header is in the static table.
4. **Every value is sent in full every time.** A 1 KB bearer token costs 1 KB on every request.
5. The source references are in `dotnet-src/SOURCE.txt`: `QPackEncoder.cs` (`H is constant 0 here, as we do not yet
   perform Huffman coding`), `Http3RequestStream.BufferHeaders`, and Kestrel's `QPackHeaderWriter`.

## What is measured

| Variant | How |
|---|---|
| **.NET today** | .NET's own `QPackEncoder.cs`, compiled from `dotnet/runtime` main (`dotnet/`), driven by HttpClient's rules for requests and Kestrel's rules for responses |
| **static+huff** | [ls-qpack](https://github.com/litespeedtech/ls-qpack) (a production QPACK library) with dynamic table capacity 0 |
| **dyn-4k / 16k / 64k** | ls-qpack with a dynamic table of that size |

- **Bytes counted:** header blocks plus encoder-stream instructions. Decoder-stream bytes (acknowledgements, a few
  bytes each) are in the JSON files. HEADERS frame envelopes are excluded for every variant.
- **Correctness:** every encoded block is decoded again and compared with the input, so all numbers are valid QPACK.
- **Corpus:**
  - real header captures from the QPACK interop project ([qpackers/qifs](https://github.com/qpackers/qifs), MIT): a Facebook page load (383 requests and responses) and netbsd.org;
  - synthetic gRPC and REST/JSON API traffic from `gen_synthetic.py`, with a ~1.1 KB bearer token, a unique `traceparent` per request, and Kestrel responses.

## Results (bytes per header list, saving vs .NET today)

**Safe mode:** `blocked_streams=0` (nothing can block), with acknowledgements arriving 10 requests late
(`measure.py --blocked-streams 0 --ack-delay 10`):

| Workload | .NET today | Huffman only | Dynamic 4 KB | Dynamic 16 KB |
|---|---:|---:|---:|---:|
| Facebook page load: requests | 511 | 381 (25%) | 165 (68%) | 155 (70%) |
| Facebook page load: responses | 745 | 548 (26%) | 321 (57%) | 166 (78%) |
| netbsd.org page load | 245 | 181 (26%) | 144 (41%) | 144 (41%) |
| gRPC unary: requests | 1,502 | 1,182 (21%) | 107 (93%) | 84 (94%) |
| gRPC unary: responses + trailers | 62 | 48 (23%) | 10 (84%) | 10 (84%) |
| REST API: requests | 1,395 | 1,098 (21%) | 120 (91%) | 99 (93%) |
| REST API: responses | 154 | 117 (24%) | 30 (80%) | 30 (80%) |

With instant acknowledgements, or with blocking allowed (`--blocked-streams 16`), the dynamic numbers are a few
points better. All runs are in `results-*.json`.

The `.NET+huff` column in `measure.py` output is .NET's own encoding with Huffman added
(`dotnet/DotnetStyleEncoder.cs`, `huffman: true`). It decodes correctly and matches ls-qpack's Huffman-only size exactly
for gRPC/REST (21–24% saved).

## CPU cost of Huffman (`bench/`)

BenchmarkDotNet, Apple M4, .NET 10.0.0, Arm64. Time per header list. The encoder uses .NET's own code table, and the
decoder is .NET's existing `Huffman.Decode`. Full reports are in `bench/results/`.

| Workload | Encode today | Encode with Huffman | Added | Bytes saved | Huffman decode at the receiver |
|---|---:|---:|---:|---:|---:|
| gRPC request | 201 ns | 1,954 ns | **+1.75 µs** | 320 B | 3.61 µs |
| REST request | 162 ns | 2,120 ns | **+1.96 µs** | 297 B | 3.48 µs |
| Facebook request | 153 ns | 705 ns | +0.55 µs | 111 B | 0.95 µs |
| Facebook response | 224 ns | 1,216 ns | +0.99 µs | 182 B | 1.73 µs |

No allocations in any variant.

**Encryption context** (`EncryptionContextBenchmarks`): encrypting 1,502 vs 1,182 bytes with `AesGcm` differs by
**~13 ns** (1.955 vs 1.942 µs). On macOS `AesGcm` has a large fixed per-call cost (CryptoKit), so only the difference
matters: the CPU saved by sending 320 fewer bytes is tiny next to the ~1.75 µs that Huffman adds.

**Reading:**
- Huffman costs CPU on both sides: about 1.7–2 µs to encode and 3.5 µs to decode a typical API request, to save
  about 300 bytes. Locally that's a net CPU loss. The only gain is fewer bytes on the wire, which helps on slow or
  metered links and when headers cross a packet boundary.
- This matches the team's HPACK experience ("too CPU intensive") and is an argument *against* doing Huffman first.
- A .NET server already pays the decode cost for browser traffic, because browsers Huffman-code their headers.
  HttpClient → Kestrel traffic currently avoids it.
- The encoder is the straightforward loop (a length pre-pass plus one table lookup per character, ~1.3 ns/byte).
  A tuned version could be faster, but it is unlikely to close a 100× gap with the encryption saving.
- This points to the **dynamic table** as the real opportunity. A repeated 1 KB token becomes a 1–2 byte index,
  which is cheaper to encode than copying the token and needs no Huffman. Its CPU cost (table lookups, insert
  bookkeeping) is the next thing to measure.

## What this does and doesn't show

- **Shown:** header bytes drop by 21–26% from Huffman alone, which needs no state, no new streams, and no blocking.
  A dynamic table cuts them by 57–96%, even in safe mode. The biggest wins are API traffic with repeated tokens.
- **Not shown yet:**
  - **CPU cost of the dynamic table.** Huffman's CPU cost is measured above; the dynamic table's isn't yet.
  - **Share of total traffic.** For a 200-byte gRPC message, 1.5 KB of headers is most of the request. For a 1 MB download it's noise.
  - **Peer support.** .NET only gains on a connection if the peer advertises a dynamic table (for requests), and .NET has to advertise one to benefit on responses.
- **Approximations:**
  - Any header whose name is in the static table gets a name reference in the ".NET today" model. Real HttpClient and Kestrel only do this for their known headers, so the baseline is slightly *better* than real .NET.
  - HttpClient's extra `alt-used` header is left out of every variant.
  - The Facebook captures are from 2017 (Chrome 63). Each file is treated as one connection.

## Run it

```bash
python3 -m venv .venv && .venv/bin/pip install pylsqpack==0.3.22
dotnet build dotnet -o build/dotnet
.venv/bin/python gen_synthetic.py
.venv/bin/python measure.py --blocked-streams 0 --ack-delay 10
dotnet run -c Release --project bench -- --filter '*HeaderEncoding*'
```
