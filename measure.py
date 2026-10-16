"""Measures HTTP/3 header bytes for .NET's current QPACK encoding vs. what a dynamic table would save.

Every QIF file is treated as one connection, in order. Variants:
  dotnet         .NET today: static table only, no Huffman (real .NET encoder, see dotnet/Program.cs)
  dotnet+huff    the same .NET encoding with Huffman-coded literals (dotnet/DotnetStyleEncoder.cs)
  static+huff    ls-qpack with dynamic table capacity 0: static table + Huffman only
  dyn-4k/16k/64k ls-qpack with a dynamic table of that capacity

Counted bytes = header blocks + encoder-stream instructions (same direction). Decoder-stream bytes
(acknowledgements flowing back) are reported separately. HEADERS frame envelopes are excluded for all
variants. Every variant is decoded again and checked against the input, so the bytes are valid QPACK.
"""

import argparse
import json
import os
import subprocess
from collections import deque

import pylsqpack

from qpack_wire import count_inserts, insert_count_increment

ROOT = os.path.dirname(os.path.abspath(__file__))
DOTNET_DLL = os.path.join(ROOT, "build", "dotnet", "DotnetBaseline.dll")
WORKLOADS = [
    ("Facebook page load: requests", "fb-req.qif"),
    ("Facebook page load: responses", "fb-resp.qif"),
    ("netbsd.org page load (mixed)", "netbsd.qif"),
    ("gRPC unary calls: requests", "grpc-req.qif"),
    ("gRPC unary calls: responses+trailers", "grpc-resp.qif"),
    ("REST/JSON API: requests", "rest-req.qif"),
    ("REST/JSON API: responses", "rest-resp.qif"),
]
CAPACITIES = {"static+huff": 0, "dyn-4k": 4096, "dyn-16k": 16384, "dyn-64k": 65536}


def read_qif(path):
    blocks, current = [], []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if line.startswith("#"):
                continue
            if not line:
                if current:
                    blocks.append(current)
                    current = []
                continue
            name, _, value = line.partition("\t")
            current.append((name.encode(), value.encode()))
    if current:
        blocks.append(current)
    return blocks


def stream_ids(count):
    # Client-initiated bidirectional QUIC streams: 0, 4, 8, ...
    return [4 * i for i in range(count)]


def measure_dotnet(path, blocks, huffman=False):
    command = ["dotnet", DOTNET_DLL] + (["--huffman"] if huffman else []) + [path]
    out = subprocess.run(command, check=True, capture_output=True, text=True).stdout.split()
    assert len(out) == len(blocks), "baseline produced a different number of header blocks"
    decoder = pylsqpack.Decoder(0, 0)
    total = 0
    for sid, hex_block, expected in zip(stream_ids(len(blocks)), out, blocks):
        data = bytes.fromhex(hex_block)
        _, decoded = decoder.feed_header(sid, data)
        assert decoded == expected, "baseline bytes do not decode back to the input"
        total += len(data)
    return {"bytes": total, "decoder_stream": 0}


def measure_lsqpack(blocks, capacity, blocked_streams, ack_delay):
    """ack_delay = how many later requests are sent before a decoder acknowledgement reaches the encoder
    (0 = instant, i.e. the best case for a dynamic table)."""
    encoder = pylsqpack.Encoder()
    decoder = pylsqpack.Decoder(capacity, blocked_streams)
    encoder_stream = encoder.apply_settings(capacity, blocked_streams)
    decoder.feed_encoder(encoder_stream)
    sent = len(encoder_stream)
    decoder_stream_bytes = 0
    pending_acks = deque()

    for sid, headers in zip(stream_ids(len(blocks)), blocks):
        enc_data, block = encoder.encode(sid, headers)
        sent += len(enc_data) + len(block)

        # Decoder side: the encoder stream is delivered before the request stream (no reordering).
        for unblocked in decoder.feed_encoder(enc_data):
            raise AssertionError(f"stream {unblocked} blocked although instructions arrived first")
        ack, decoded = decoder.feed_header(sid, block)
        assert decoded == headers, "ls-qpack round trip mismatch"
        # pylsqpack omits Insert Count Increment; add it so acknowledged-only encoding works (qpack_wire.py).
        # With blocked_streams=0 a section only references already-acknowledged entries, so its Section
        # Acknowledgment never covers this round's inserts and the increment cannot double-count them.
        if blocked_streams == 0:
            ack += insert_count_increment(count_inserts(enc_data))

        decoder_stream_bytes += len(ack)
        pending_acks.append(ack)
        while len(pending_acks) > ack_delay:
            encoder.feed_decoder(pending_acks.popleft())

    return {"bytes": sent, "decoder_stream": decoder_stream_bytes}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--blocked-streams", type=int, default=0,
                        help="SETTINGS_QPACK_BLOCKED_STREAMS advertised by the decoder "
                             "(0 = encoder only references acknowledged entries, so no request can ever block)")
    parser.add_argument("--ack-delay", type=int, default=0,
                        help="requests sent before each acknowledgement arrives (models concurrency/latency)")
    parser.add_argument("--json", help="also write raw results to this file")
    args = parser.parse_args()

    results = []
    header = f"{'workload':<38}{'lists':>6}{'.NET today':>12}{'.NET+huff':>14}" + "".join(f"{k:>14}" for k in CAPACITIES)
    print(f"blocked_streams={args.blocked_streams} ack_delay={args.ack_delay}  (bytes per header list, % saved vs .NET today)")
    print(header)
    print("-" * len(header))
    for label, file in WORKLOADS:
        path = os.path.join(ROOT, "corpus", file)
        blocks = read_qif(path)
        row = {"workload": label, "file": file, "lists": len(blocks), "dotnet": measure_dotnet(path, blocks),
               "dotnet+huff": measure_dotnet(path, blocks, huffman=True)}
        for name, capacity in CAPACITIES.items():
            row[name] = measure_lsqpack(blocks, capacity, args.blocked_streams, args.ack_delay)
        results.append(row)

        base = row["dotnet"]["bytes"]
        cells = "".join(
            f"{row[k]['bytes'] / len(blocks):>7.0f} ({1 - row[k]['bytes'] / base:>4.0%})" for k in CAPACITIES)
        huff = row["dotnet+huff"]["bytes"]
        huff_cell = f"{huff / len(blocks):>7.0f} ({1 - huff / base:>4.0%})"
        print(f"{label:<38}{len(blocks):>6}{base / len(blocks):>12.0f}{huff_cell}{cells}")

    if args.json:
        with open(args.json, "w") as f:
            json.dump({"settings": vars(args), "results": results}, f, indent=2)


if __name__ == "__main__":
    main()
