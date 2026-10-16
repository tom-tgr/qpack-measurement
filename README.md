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

## What this does and doesn't show

- **Shown:** header bytes drop by 21–26% from Huffman alone, which needs no state, no new streams, and no blocking.
  A dynamic table cuts them by 57–96%, even in safe mode. The biggest wins are API traffic with repeated tokens.
- **Not shown yet:**
  - **CPU cost.** This was the team's concern for HPACK. It needs a benchmark of a .NET implementation.
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
```
