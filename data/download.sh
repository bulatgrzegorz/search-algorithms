#!/usr/bin/env bash
# Real-world data: GLEIF's daily ISIN->LEI mapping (public, no auth, ~33 MB zip, ~9.4M unique ISINs).
# Writes one ISIN per line to data/isin.txt.
set -euo pipefail
cd "$(dirname "$0")"
curl -fL -o isin-lei.zip https://mapping.gleif.org/api/v2/isin-lei/latest/download
unzip -p isin-lei.zip | tail -n +2 | cut -d, -f2 | tr -d '\r' > isin.txt
rm isin-lei.zip
wc -l isin.txt
