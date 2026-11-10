#!/usr/bin/env bash
# Real-world data from GLEIF (public, no auth), one item per line. Benchmarks read the one the DATASET env var names (default isin).
#   data/download.sh [isin]  daily ISIN->LEI mapping, ~33 MB zip, ~9.4M unique ISINs -> data/isin.txt
#   data/download.sh names   LEI golden copy, ~485 MB zip, ~3.5M entities' unique legal names -> data/names.txt
set -euo pipefail
cd "$(dirname "$0")"
dataset="${1:-isin}"
case "$dataset" in
  isin)
    curl -fL -o isin-lei.zip https://mapping.gleif.org/api/v2/isin-lei/latest/download
    unzip -p isin-lei.zip | tail -n +2 | cut -d, -f2 | tr -d '\r' > isin.txt
    rm isin-lei.zip ;;
  names)
    curl -fL -o lei2.zip https://goldencopy.gleif.org/api/v2/golden-copies/publishes/lei2/latest.csv
    # Quoted CSV with commas and line breaks inside names, so a real CSV parser. Entity.LegalName is the 2nd column.
    unzip -p lei2.zip | python3 -c '
import csv, io, sys
rows = csv.reader(io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8", newline=""))
next(rows)
for row in rows:
    name = " ".join(row[1].split())
    if name: print(name)
' | LC_ALL=C sort -u > names.txt
    rm lei2.zip ;;
  *) echo "usage: $0 [isin|names]" >&2; exit 1 ;;
esac
wc -l "$dataset.txt"
