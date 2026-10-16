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
- This points to the **dynamic table** as the real opportunity (below).

## A minimal .NET dynamic-table encoder (`dotnet/DynamicTableEncoder.cs`)

About 270 lines, no Huffman, safe mode only (references acknowledged entries only, so nothing can block):
- **Insert rule:** insert a value when it repeats one of the last 16 values seen for that name.
- **Eviction:** FIFO, only when the entry's insertion is acknowledged and no unacknowledged section uses it.
- **Never indexed:** `authorization` and cookies under 20 chars (like nghttp3; RFC 9204 7.1). They are sent as literals
  with the N bit. `--index-sensitive` indexes them too (like QUICHE and ls-qpack).
- **Lookups are keyed by name.** Values are compared, never hashed.

Every block is checked by ls-qpack's decoder with `blocked_streams=0`.

**Bytes** (safe mode, 16 KB table, acknowledgement of each new entry delayed until 10 more requests were sent, about one round trip with ~10 requests in flight):

| Workload | .NET today | Dynamic, auth never indexed | Dynamic, auth indexed |
|---|---:|---:|---:|
| gRPC requests | 1,502 | 1,306 (13%) | 119 (92%) |
| REST requests | 1,395 | 1,329 (5%) | 141 (90%) |
| gRPC responses + trailers | 62 | 12 (81%) | 12 (81%) |
| REST responses | 154 | 36 (76%) | 36 (76%) |
| Facebook page: requests | 511 | 203 (60%) | 199 (61%) |
| Facebook page: responses | 745 | 213 (71%) | 213 (71%) |

**The API-request win is almost entirely the bearer token.** Without indexing `authorization`, requests save 5–13%,
which is less than Huffman alone. Responses and browser traffic don't depend on it.

**CPU** (`DynamicTableBenchmarks`, a warm long-lived connection, Apple M4, ns per header list, no allocations;
`bench/results/DynamicTableBenchmarks-v3-final-report-github.md`):

| Workload | Today | Huffman | Dynamic (auth never indexed) | Dynamic (auth indexed) |
|---|---:|---:|---:|---:|
| gRPC request | 197 | 1,927 | 254 (+56) | 263 (+66) |
| REST request | 151 | 2,025 | 268 (+118) | 283 (+133) |
| Facebook request | 182 | 788 | 402 (+220) | 394 (+212) |
| Facebook response | 222 | 1,138 | 600 (+378) | 592 (+370) |

**Two implementation mistakes cost more than QPACK itself.** Both are kept in the history as lessons:
1. **v1 hashed every `(name, value)` pair** with .NET's string hash: 1,404 ns for gRPC, 7× today
   (`bench/results/DynamicTableBenchmarks-v1-pair-hashing-report-github.md`). Hashing a 1 KB token on every request
   dominated. Keying by name and comparing values brought it to ~280 ns.
2. **Copying literals char by char** instead of with the vectorized `Ascii.FromUtf16` that `QPackEncoder` uses added
   ~300 ns per 1 KB literal.

## Does it make requests faster? (`netem/`)

Kestrel and HttpClient over real HTTP/3 (msquic) on GitHub Actions Ubuntu 24.04, with the loopback shaped by
`tc netem` (`netem/run.sh`, workflow `netem-latency`). Each variant sends header blocks of exactly the size that
encoding produces, padded with an `x-h` header and sized with the validated ".NET today" model. So this measures the
time effect of header *size* on the real stack; compression CPU is negligible (see above).

- **Cold bursts** use the first 50/100 header lists of a connection, all sent before any acknowledgement can return.
  - In **safe mode** nothing can be referenced yet, so it gains nothing (`.NET today` 443/799 B vs safe 437/809 B per
    page request/response).
  - With **blocking allowed** (ls-qpack, 100 blocked streams, as Chrome permits; Firefox allows 20) the entries inserted
    earlier in the burst are referenced at once: 124/175 B. These ls-qpack sizes include Huffman, so they are slightly
    optimistic for a no-Huffman .NET encoder.

Median of 10 bursts or 100 sequential calls ([run 38878023353](../../actions/runs/38878023353); raw tables in
`netem/results-2026-10-16-run-38878023353.md`):

| Scenario | Variant | Wi-Fi 100 Mbit, 20 ms RTT | Mobile 10 Mbit, 50 ms | Poor 1 Mbit, 100 ms |
|---|---|---:|---:|---:|
| gRPC unary, sequential, warm | dynamic, auth not indexed | −0.4% | −0.5% | −2.2% |
| gRPC unary, sequential, warm | dynamic, auth indexed | −0.7% | −2.2% | −10.2% |
| gRPC fan-out ×50, warm | dynamic, auth not indexed | −3.3% | −6.0% | −10.8% |
| gRPC fan-out ×50, warm | dynamic, auth indexed | −17.7% (28.6 → 23.6 ms) | −45.6% (125 → 68 ms) | −67.1% (879 → 289 ms) |
| gRPC fan-out ×50, new connection | dynamic, safe mode | +1.0% | +0.3% | +1.6% |
| gRPC fan-out ×50, new connection | dynamic, blocking allowed | −44.7% (94.5 → 52.2 ms) | −35.8% (251 → 161 ms) | −56.3% (1,109 → 484 ms) |
| Page load ×100, new connection | dynamic, safe mode | −0.2% | +0.8% | +0.4% |
| Page load ×100, new connection | dynamic, blocking allowed | −29.6% (128 → 90 ms) | −32.6% (336 → 226 ms) | −51.4% (1,607 → 780 ms) |

**Reading:**
- **Single requests barely change.** One round trip dominates and 1.4 KB is a few packets at most. That's the 0.5–10%
  in the first rows.
- **Bursts change a lot.** The gains are larger than the bytes alone explain: on Wi-Fi, 70 KB fewer is ~6 ms of
  transmission, yet the gRPC cold burst is 42 ms faster. QUIC's congestion window starts at ~12 KB and grows per round
  trip, so fewer header bytes means fewer round trips before everything is sent.
- **Safe mode is useless at connection start.** The win on new connections needs a blocking-capable encoder: allow
  references to entries inserted earlier in the same burst, within the peer's advertised blocked-streams budget.
  Chrome advertises 100 and Firefox 20, so Kestrel encoding responses for browsers can use it. On warm connections
  safe mode works.
- **Without indexing `authorization`, API bursts gain only 3–11%.** The token policy question again.
- **Caveats:**
  - netem on loopback shares one rate limit across both directions, which is harsher than a real asymmetric link but
    the same for every variant.
  - Header sizes are emulated by padding rather than produced by a real dynamic-table encoder in Kestrel/HttpClient.
  - One run on one machine type.

## How other QPACK implementations do it

Read from source on 2026-10-16, default branches:

| Implementation (used by) | Encoder inserts? | Lookup | Indexes `authorization` | Huffman | Advertised as decoder |
|---|---|---|---|---|---|
| ls-qpack (LiteSpeed, lsquic) | Yes, after a value is seen twice (hash history) | XXH32 of name, and of value seeded by the name hash; callers can pass cached hashes | Yes | If shorter (2-bytes-at-a-time table) | lsquic: 4 KB / 100 blocked |
| nghttp3 (curl) | Yes, by allowlist; `:path`, `etag`, `set-cookie`, … stay literal | **Name only** (precomputed per known name, FNV-1a otherwise); values compared | **Never** (nor cookies < 20 B) | If shorter | **0** (curl accepts nothing) |
| QUICHE (Chrome) | Everything that fits; cookies split into crumbs | absl hash of `(name, value)` | Yes | If shorter (on by default) | **64 KB / 100 blocked** |
| neqo (Firefox) | Only headers whose name matches no table entry | Linear scan, name first, no hashing | No (its name is in the static table) | Always | **64 KB / 20 blocked** |
| nginx | No (always static) | — | — | If shorter | 4 KB / 128 blocked |
| h2o | Code exists, production passes it off | Linear scan, interned names (pointer compare) | No (`likely_to_repeat=0`) | If shorter; never for cookies | Server: 16 KB / 100 blocked |
| quic-go, Cloudflare quiche | No dynamic table at all | — | — | quic-go always; quiche if not longer | 0 |

## Who would benefit

- **Kestrel responses to browsers:** Chrome and Firefox both accept a 64 KB table, so responses could shrink by
  71–88% with no security trade-off. This is the clearest win.
- **Browser requests to Kestrel:** if Kestrel advertised a table (decoder side), Chrome would compress requests into
  it. It indexes everything, including cookies and auth. Here .NET implements the decoder and makes no indexing
  decision itself.
- **HttpClient requests:** only nginx (4 KB) and h2o (16 KB) accept a table. quic-go and Cloudflare quiche don't. The
  big saving needs `authorization` indexed, which curl, Firefox and h2o refuse to do. That makes it a security policy
  question for the .NET team, not a measurement one.

## What this does and doesn't show

- **Shown:**
  - Huffman alone saves 21–26% of header bytes for 1–2 µs of encode CPU plus 1–3.5 µs at the decoder.
  - A dynamic table saves 60–90% for +56 to +378 ns of encode CPU (+56 to +133 ns on API traffic).
  - For API requests, the dynamic table's saving depends on indexing the bearer token.
- **Not shown yet:**
  - **Decoder CPU for the dynamic table** (Kestrel accepting browser compression).
  - **x64 numbers.** Everything ran on an Apple M4.
  - **Real HttpClient/Kestrel integration**, as opposed to this standalone encoder (the netem test emulates sizes).
  - **Share of total traffic.** Small gRPC messages are dominated by headers; downloads are not.
- **Approximations:**
  - Any header whose name is in the static table gets a name reference in the ".NET today" model. Real HttpClient and
    Kestrel only do this for their known headers, so the baseline is slightly *better* than real .NET.
  - HttpClient's extra `alt-used` header is left out of every variant.
  - The Facebook captures are from 2017 (Chrome 63). Each file is treated as one connection.
  - Acknowledgements are modelled with in-order delivery.

## Run it

```bash
python3 -m venv .venv && .venv/bin/pip install pylsqpack==0.3.22
dotnet build dotnet -o build/dotnet
.venv/bin/python gen_synthetic.py
.venv/bin/python measure.py --blocked-streams 0 --ack-delay 10
dotnet run -c Release --project bench -- --filter '*HeaderEncoding*'
dotnet run -c Release --project bench -- --filter '*DynamicTable*'
netem/run.sh   # Linux with libmsquic and sudo; or run the netem-latency workflow
```
