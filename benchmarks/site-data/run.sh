#!/bin/bash
# One consistent pass over all 7 algorithms (ISINs, Medium; updates also at Huge and on names) for the GitHub Pages charts. Resumable: a label with a .ok file is skipped.
# Every algorithm must be registered in Algorithms.All. Build first, then:
#   benchmarks/site-data/run.sh <results dir> && benchmarks/site-data/extract.py <results dir> site/assets/bench-data.js
set -uo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
out=$1; mkdir -p "$out"
dotnet=${DOTNET:-/opt/homebrew/bin/dotnet}
project="$root/benchmarks/SearchAlgorithms.Benchmarks/SearchAlgorithms.Benchmarks.csproj"

run() {
  local label=$1; shift
  if [[ -f "$out/$label.ok" ]]; then echo "SKIP $label"; return; fi
  echo "START $label $(date +%T)"
  if ALGORITHM=$algorithm DATASET=${dataset:-isin} $dotnet run --project "$project" -c Release --no-build --no-restore -- "$@" > "$out/$label.log" 2>&1 \
     && ! grep -qE 'Benchmarks with issues:|Unhandled exception|There are not any results runs' "$out/$label.log"; then
    touch "$out/$label.ok"
  else
    echo "FAILED $label"
  fi
}

bdn=(--inProcess --job short --warmupCount 3 --iterationCount 5 --iterationTime 200 --exporters json)
for algorithm in SortedArrayIndex RocksDbIndex LuceneIndex LinearScan ConcurrentLinearScan NaiveRocksDbIndex NaiveLuceneIndex; do
  run "searches-$algorithm" "${bdn[@]}" --artifacts "$out/searches-$algorithm" --filter \
    '*SearchBenchmarks*(Requests:?1,?WritesPerSecond:?0,*Size:?Small)' \
    '*SearchBenchmarks*(Requests:?1,?WritesPerSecond:?0,*Size:?Medium)' \
    '*SearchBenchmarks*(Requests:?1,?WritesPerSecond:?1000,*Size:?Medium)'
  run "typeahead-$algorithm" "${bdn[@]}" --artifacts "$out/typeahead-$algorithm" --filter '*TypeaheadBenchmarks*Size:?Medium)'
  run "build-mutation-$algorithm" "${bdn[@]}" --artifacts "$out/build-mutation-$algorithm" --filter '*BuildBenchmarks*Size:?Medium)' '*MutationBenchmarks*Size:?Medium)'
  for dataset in isin names; do
    run "updates-$dataset-$algorithm" "${bdn[@]}" --artifacts "$out/updates-$dataset-$algorithm" --filter '*UpdateBenchmarks*Size:?Medium)' '*UpdateBenchmarks*Size:?Huge)'
  done
  unset dataset
  run "footprint-$algorithm" footprint 100000
done
echo "ALL COMPLETE $(date +%T)"
