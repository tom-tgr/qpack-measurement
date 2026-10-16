#!/usr/bin/env bash
# Runs the latency test under several network profiles with tc netem on the loopback interface.
# Linux only (HTTP/3 needs libmsquic), needs sudo. Usage: netem/run.sh [results.md]
#
# netem on lo delays every packet once per direction, so RTT = 2 x delay. The rate limit is shared by both
# directions (one pipe), which is harsher than a real asymmetric link but the same for every variant.
set -euo pipefail
OUT=$(realpath -m "${1:-results-netem.md}")
cd "$(dirname "$0")"

dotnet build -c Release -o ../build/netem
sudo modprobe sch_netem 2>/dev/null || true
sudo ip link set dev lo mtu 1500 # loopback defaults to 64 KB; real paths carry ~1500-byte packets
trap 'sudo tc qdisc del dev lo root 2>/dev/null || true' EXIT

run() {
  local label=$1 rate=$2 delay=$3
  sudo tc qdisc del dev lo root 2>/dev/null || true
  if [ "$rate" != "-" ]; then
    sudo tc qdisc add dev lo root netem rate "$rate" delay "$delay" limit 100000
  fi
  dotnet ../build/netem/NetemLatency.dll --profile "$label" --out "$OUT"
}

: > "$OUT"
run "no shaping (loopback)" - -
run "Wi-Fi: 100 Mbit/s, 20 ms RTT" 100mbit 10ms
run "mobile: 10 Mbit/s, 50 ms RTT" 10mbit 25ms
run "poor mobile: 1 Mbit/s, 100 ms RTT" 1mbit 50ms
