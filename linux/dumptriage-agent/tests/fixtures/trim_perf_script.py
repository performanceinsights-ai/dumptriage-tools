"""Trim a real `perf script --header` capture to a fixture small enough to commit (#2000).

Keeps the header except the `bpf_prog_info` lines (this host's BPF programs, 40+ lines of noise), then every
STRIDE-th sample, unchanged, so shares stay close to the full capture's. Nothing else is edited: the hostname and
command line stay in, because the digest's redaction is what the tests check.

    python3 trim.py perf-script-full.txt STRIDE > perf-<class>.txt
"""
import sys

src, stride = sys.argv[1], int(sys.argv[2])
header, samples, cur = [], [], []
for line in open(src, encoding="utf-8", errors="replace"):
    line = line.rstrip("\n")
    if not samples and not cur and (line.startswith("#") or not line):
        if not line.startswith("# bpf_prog_info"):
            header.append(line)
        continue
    if line == "":
        if cur:
            samples.append(cur)
        cur = []
    else:
        cur.append(line)
if cur:
    samples.append(cur)
out = header + [""] if header and header[-1] else header
for i, sample in enumerate(samples):
    if i % stride == 0:
        out.extend(sample + [""])
print("\n".join(out))
