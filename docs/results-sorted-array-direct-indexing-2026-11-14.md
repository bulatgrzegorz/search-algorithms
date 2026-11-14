# Direct merged indexing: applied change — 2026-11-14

Implemented only the direct merged-indexing recommendation in SortedArrayIndex.Merged. First 64 indexed reads walk immutable merge events, larger indexed reads and enumeration materialize the existing bulk-copy result. Volatile.Read and Interlocked.CompareExchange publish a completed cache safely. Count and snapshot publication retain their behavior. Invalid indexes are rejected before allocating a result.

TypeaheadBenchmarks now includes WritesPerSecond=0/1000, using the existing Writer.Run. The old no-write-only case did not exercise this change.

## Validation

Five tests passed: the existing three SortedArrayIndex algorithm tests and two new prefix/suffix cases. The new cases check index order and bounds before materialization, access across the 64-hit boundary, concurrent cache creation/enumeration, and stable results while writes cross merge thresholds.

## Before/after method

- Before: exact source from HEAD (3c7ea4a), copied as Baseline; after: linked current production SortedArrayIndex.cs. No Filter changes, merge threshold changes, empty-input fixes or read-only wrapper changes were included.
- macOS Arm64, .NET 11.0.0, Release. Real ISIN and name datasets; original shuffle/query seeds.
- Linked the actual Benchmarks.cs classes. Matrix: 10k and 100k items, three searches, 1/100/500 requests, 0/100/1000 updates/s, typeahead 0/1000, Add/Remove, Build and full consumption controls.
- 296 scenario/variant combinations, 888 measured samples. Medians below; each timed sample completes whole cycles of all 1,000 seeded queries. All no-write result checksums matched.
- Each variant/dataset/size ran in a separate process. Dataset loading is cached outside timing with only the requested sample plus churn items retained.
- Matrix allocation uses process-wide counters and includes writer allocation during writes. Typeahead time is per complete typed query, parallel search time per batch. One update is a Remove+Add pair.
- BenchmarkDotNet 0.15.8 confirmation uses the updated TypeaheadBenchmarks.StartsWith for both datasets at 10k/100k/1M, with 0/1000 updates/s: 24 cases. InProcessEmitToolchain is required because this bundled version's default subprocess toolchain does not recognize .NET 11. Separate outer process per variant/dataset; 3 warmups, 5 measured iterations, 200 ms. Tail-heavy cases have wide error intervals; large speedups are directional, not precise production guarantees.
- Build and mutation controls can vary substantially between separate runs despite unchanged work. They do not establish a build/write optimization.

## Typeahead
| Data | Items | Scenario | Requests | Updates/s | Before us | After us | Speedup | Before B | After B |
|---|---|---|---|---|---|---|---|---|---|
| isin | 10,000 | Typeahead.Filter | 1 | 0 | 47.143 | 45.992 | 1.03x | 101,573 | 101,571 |
| isin | 10,000 | Typeahead.Filter | 1 | 1000 | 64.695 | 65.674 | 0.99x | 106,138 | 106,195 |
| isin | 10,000 | Typeahead.StartsWith | 1 | 0 | 0.478 | 0.476 | 1.00x | 245 | 245 |
| isin | 10,000 | Typeahead.StartsWith | 1 | 1000 | 20.911 | 8.348 | 2.50x | 90,544 | 1,613 |
| isin | 100,000 | Typeahead.Filter | 1 | 0 | 718.109 | 624.843 | 1.15x | 1,036,826 | 1,036,766 |
| isin | 100,000 | Typeahead.Filter | 1 | 1000 | 713.793 | 699.120 | 1.02x | 1,127,081 | 1,128,626 |
| isin | 100,000 | Typeahead.StartsWith | 1 | 0 | 0.599 | 0.632 | 0.95x | 245 | 245 |
| isin | 100,000 | Typeahead.StartsWith | 1 | 1000 | 501.498 | 15.978 | 31.39x | 963,897 | 4,178 |
| names | 10,000 | Typeahead.Filter | 1 | 0 | 57.970 | 48.691 | 1.19x | 81,122 | 81,125 |
| names | 10,000 | Typeahead.Filter | 1 | 1000 | 67.976 | 68.831 | 0.99x | 86,225 | 86,333 |
| names | 10,000 | Typeahead.StartsWith | 1 | 0 | 0.731 | 0.741 | 0.99x | 244 | 244 |
| names | 10,000 | Typeahead.StartsWith | 1 | 1000 | 2.302 | 1.524 | 1.51x | 4,157 | 449 |
| names | 100,000 | Typeahead.Filter | 1 | 0 | 472.274 | 719.604 | 0.66x | 796,115 | 796,267 |
| names | 100,000 | Typeahead.Filter | 1 | 1000 | 692.271 | 901.644 | 0.77x | 979,812 | 1,024,995 |
| names | 100,000 | Typeahead.StartsWith | 1 | 0 | 0.976 | 1.117 | 0.87x | 244 | 244 |
| names | 100,000 | Typeahead.StartsWith | 1 | 1000 | 6.523 | 2.793 | 2.34x | 42,157 | 1,463 |

## Search latency and concurrent batches

These read Count rather than result indexes, so this change is not expected to improve them systematically.
| Data | Items | Scenario | Requests | Updates/s | Before us | After us | Speedup | Before B | After B |
|---|---|---|---|---|---|---|---|---|---|
| isin | 10,000 | Search.EndsWith | 1 | 0 | 0.175 | 0.178 | 0.98x | 88 | 88 |
| isin | 10,000 | Search.EndsWith | 1 | 100 | 0.289 | 0.269 | 1.08x | 90 | 90 |
| isin | 10,000 | Search.EndsWith | 1 | 1000 | 0.251 | 0.254 | 0.99x | 94 | 94 |
| isin | 10,000 | Search.EndsWith | 100 | 0 | 14.648 | 22.014 | 0.67x | 6,331 | 6,343 |
| isin | 10,000 | Search.EndsWith | 100 | 100 | 16.922 | 20.910 | 0.81x | 6,482 | 6,399 |
| isin | 10,000 | Search.EndsWith | 100 | 1000 | 16.800 | 16.351 | 1.03x | 6,588 | 6,592 |
| isin | 10,000 | Search.EndsWith | 500 | 0 | 46.960 | 56.309 | 0.83x | 19,221 | 19,122 |
| isin | 10,000 | Search.EndsWith | 500 | 100 | 54.079 | 54.023 | 1.00x | 20,115 | 20,147 |
| isin | 10,000 | Search.EndsWith | 500 | 1000 | 52.660 | 53.309 | 0.99x | 20,839 | 20,852 |
| isin | 10,000 | Search.Filter | 1 | 0 | 1.157 | 1.210 | 0.96x | 3,694 | 3,694 |
| isin | 10,000 | Search.Filter | 1 | 100 | 2.266 | 3.207 | 0.71x | 4,092 | 3,982 |
| isin | 10,000 | Search.Filter | 1 | 1000 | 2.483 | 2.488 | 1.00x | 4,248 | 4,248 |
| isin | 10,000 | Search.Filter | 100 | 0 | 64.060 | 68.863 | 0.93x | 366,789 | 366,786 |
| isin | 10,000 | Search.Filter | 100 | 100 | 91.299 | 103.981 | 0.88x | 381,308 | 381,834 |
| isin | 10,000 | Search.Filter | 100 | 1000 | 132.633 | 112.549 | 1.18x | 421,237 | 421,300 |
| isin | 10,000 | Search.Filter | 500 | 0 | 234.767 | 239.307 | 0.98x | 1,822,292 | 1,822,296 |
| isin | 10,000 | Search.Filter | 500 | 100 | 366.803 | 359.096 | 1.02x | 1,967,399 | 1,970,426 |
| isin | 10,000 | Search.Filter | 500 | 1000 | 455.155 | 435.975 | 1.04x | 2,098,559 | 2,099,382 |
| isin | 10,000 | Search.StartsWith | 1 | 0 | 0.118 | 0.146 | 0.81x | 88 | 88 |
| isin | 10,000 | Search.StartsWith | 1 | 100 | 1.411 | 1.537 | 0.92x | 260 | 261 |
| isin | 10,000 | Search.StartsWith | 1 | 1000 | 1.330 | 1.289 | 1.03x | 317 | 315 |
| isin | 10,000 | Search.StartsWith | 100 | 0 | 19.072 | 22.380 | 0.85x | 6,280 | 6,167 |
| isin | 10,000 | Search.StartsWith | 100 | 100 | 65.049 | 64.617 | 1.01x | 31,897 | 32,230 |
| isin | 10,000 | Search.StartsWith | 100 | 1000 | 67.200 | 55.149 | 1.22x | 27,817 | 28,447 |
| isin | 10,000 | Search.StartsWith | 500 | 0 | 43.294 | 48.316 | 0.90x | 19,227 | 19,149 |
| isin | 10,000 | Search.StartsWith | 500 | 100 | 284.718 | 290.859 | 0.98x | 179,048 | 181,144 |
| isin | 10,000 | Search.StartsWith | 500 | 1000 | 191.004 | 190.880 | 1.00x | 116,482 | 121,518 |
| isin | 100,000 | Search.EndsWith | 1 | 0 | 0.316 | 0.264 | 1.20x | 88 | 88 |
| isin | 100,000 | Search.EndsWith | 1 | 100 | 0.359 | 0.395 | 0.91x | 88 | 88 |
| isin | 100,000 | Search.EndsWith | 1 | 1000 | 0.459 | 0.383 | 1.20x | 160 | 158 |
| isin | 100,000 | Search.EndsWith | 100 | 0 | 23.335 | 20.084 | 1.16x | 6,117 | 6,287 |
| isin | 100,000 | Search.EndsWith | 100 | 100 | 19.615 | 18.564 | 1.06x | 6,325 | 6,350 |
| isin | 100,000 | Search.EndsWith | 100 | 1000 | 21.510 | 20.952 | 1.03x | 10,109 | 9,948 |
| isin | 100,000 | Search.EndsWith | 500 | 0 | 79.276 | 63.091 | 1.26x | 19,080 | 19,139 |
| isin | 100,000 | Search.EndsWith | 500 | 100 | 75.450 | 63.970 | 1.18x | 20,101 | 20,244 |
| isin | 100,000 | Search.EndsWith | 500 | 1000 | 72.856 | 63.937 | 1.14x | 32,471 | 31,289 |
| isin | 100,000 | Search.Filter | 1 | 0 | 28.474 | 27.644 | 1.03x | 38,916 | 38,921 |
| isin | 100,000 | Search.Filter | 1 | 100 | 25.782 | 23.041 | 1.12x | 40,047 | 40,050 |
| isin | 100,000 | Search.Filter | 1 | 1000 | 31.174 | 26.356 | 1.18x | 45,115 | 44,122 |
| isin | 100,000 | Search.Filter | 100 | 0 | 2548.062 | 2291.727 | 1.11x | 3,905,325 | 3,906,216 |
| isin | 100,000 | Search.Filter | 100 | 100 | 1305.663 | 1217.955 | 1.07x | 3,966,586 | 3,964,839 |
| isin | 100,000 | Search.Filter | 100 | 1000 | 2181.008 | 1384.313 | 1.58x | 4,338,653 | 4,181,898 |
| isin | 100,000 | Search.Filter | 500 | 0 | 12729.950 | 12174.200 | 1.05x | 19,566,516 | 19,538,939 |
| isin | 100,000 | Search.Filter | 500 | 100 | 8193.381 | 5524.918 | 1.48x | 19,655,771 | 19,601,772 |
| isin | 100,000 | Search.Filter | 500 | 1000 | 10051.550 | 7569.944 | 1.33x | 21,585,849 | 21,212,606 |
| isin | 100,000 | Search.StartsWith | 1 | 0 | 0.860 | 0.167 | 5.15x | 88 | 88 |
| isin | 100,000 | Search.StartsWith | 1 | 100 | 2.291 | 1.725 | 1.33x | 278 | 277 |
| isin | 100,000 | Search.StartsWith | 1 | 1000 | 3.465 | 5.543 | 0.63x | 609 | 678 |
| isin | 100,000 | Search.StartsWith | 100 | 0 | 122.825 | 20.864 | 5.89x | 6,134 | 6,306 |
| isin | 100,000 | Search.StartsWith | 100 | 100 | 259.642 | 253.410 | 1.02x | 75,716 | 75,732 |
| isin | 100,000 | Search.StartsWith | 100 | 1000 | 103.486 | 121.282 | 0.85x | 51,371 | 57,704 |
| isin | 100,000 | Search.StartsWith | 500 | 0 | 61.927 | 46.055 | 1.34x | 19,085 | 19,171 |
| isin | 100,000 | Search.StartsWith | 500 | 100 | 1481.213 | 721.139 | 2.05x | 328,898 | 324,254 |
| isin | 100,000 | Search.StartsWith | 500 | 1000 | 561.374 | 356.161 | 1.58x | 243,615 | 195,505 |
| names | 10,000 | Search.EndsWith | 1 | 0 | 0.218 | 0.180 | 1.21x | 88 | 88 |
| names | 10,000 | Search.EndsWith | 1 | 100 | 0.950 | 0.927 | 1.03x | 154 | 153 |
| names | 10,000 | Search.EndsWith | 1 | 1000 | 0.569 | 0.554 | 1.03x | 156 | 157 |
| names | 10,000 | Search.EndsWith | 100 | 0 | 15.347 | 15.129 | 1.01x | 6,351 | 6,302 |
| names | 10,000 | Search.EndsWith | 100 | 100 | 37.832 | 33.776 | 1.12x | 13,177 | 13,213 |
| names | 10,000 | Search.EndsWith | 100 | 1000 | 30.170 | 26.080 | 1.16x | 12,107 | 11,994 |
| names | 10,000 | Search.EndsWith | 500 | 0 | 46.277 | 47.048 | 0.98x | 19,240 | 19,192 |
| names | 10,000 | Search.EndsWith | 500 | 100 | 117.165 | 123.555 | 0.95x | 55,371 | 55,160 |
| names | 10,000 | Search.EndsWith | 500 | 1000 | 92.952 | 101.615 | 0.91x | 48,471 | 49,801 |
| names | 10,000 | Search.Filter | 1 | 0 | 0.505 | 0.544 | 0.93x | 700 | 700 |
| names | 10,000 | Search.Filter | 1 | 100 | 1.189 | 1.206 | 0.99x | 954 | 974 |
| names | 10,000 | Search.Filter | 1 | 1000 | 1.618 | 1.651 | 0.98x | 1,175 | 1,169 |
| names | 10,000 | Search.Filter | 100 | 0 | 32.211 | 29.619 | 1.09x | 67,555 | 67,569 |
| names | 10,000 | Search.Filter | 100 | 100 | 40.983 | 39.761 | 1.03x | 81,089 | 80,802 |
| names | 10,000 | Search.Filter | 100 | 1000 | 67.921 | 64.488 | 1.05x | 113,323 | 112,948 |
| names | 10,000 | Search.Filter | 500 | 0 | 107.645 | 97.346 | 1.11x | 325,273 | 325,314 |
| names | 10,000 | Search.Filter | 500 | 100 | 195.606 | 191.768 | 1.02x | 460,430 | 460,192 |
| names | 10,000 | Search.Filter | 500 | 1000 | 259.206 | 264.314 | 0.98x | 564,293 | 576,456 |
| names | 10,000 | Search.StartsWith | 1 | 0 | 0.196 | 0.706 | 0.28x | 88 | 88 |
| names | 10,000 | Search.StartsWith | 1 | 100 | 0.315 | 0.270 | 1.17x | 91 | 91 |
| names | 10,000 | Search.StartsWith | 1 | 1000 | 0.274 | 0.269 | 1.02x | 102 | 102 |
| names | 10,000 | Search.StartsWith | 100 | 0 | 20.990 | 22.904 | 0.92x | 6,273 | 6,179 |
| names | 10,000 | Search.StartsWith | 100 | 100 | 16.739 | 21.534 | 0.78x | 6,470 | 6,380 |
| names | 10,000 | Search.StartsWith | 100 | 1000 | 17.052 | 16.720 | 1.02x | 7,153 | 7,160 |
| names | 10,000 | Search.StartsWith | 500 | 0 | 48.706 | 47.996 | 1.01x | 19,239 | 19,183 |
| names | 10,000 | Search.StartsWith | 500 | 100 | 54.677 | 57.572 | 0.95x | 20,909 | 20,873 |
| names | 10,000 | Search.StartsWith | 500 | 1000 | 59.181 | 55.852 | 1.06x | 22,992 | 22,891 |
| names | 100,000 | Search.EndsWith | 1 | 0 | 0.326 | 0.249 | 1.31x | 88 | 88 |
| names | 100,000 | Search.EndsWith | 1 | 100 | 1.273 | 1.293 | 0.98x | 150 | 150 |
| names | 100,000 | Search.EndsWith | 1 | 1000 | 1.892 | 1.439 | 1.31x | 175 | 170 |
| names | 100,000 | Search.EndsWith | 100 | 0 | 17.619 | 18.395 | 0.96x | 6,350 | 6,300 |
| names | 100,000 | Search.EndsWith | 100 | 100 | 25.873 | 25.448 | 1.02x | 9,960 | 10,009 |
| names | 100,000 | Search.EndsWith | 100 | 1000 | 64.727 | 65.096 | 0.99x | 35,803 | 32,611 |
| names | 100,000 | Search.EndsWith | 500 | 0 | 68.662 | 53.817 | 1.28x | 19,074 | 19,155 |
| names | 100,000 | Search.EndsWith | 500 | 100 | 287.361 | 294.059 | 0.98x | 73,991 | 74,113 |
| names | 100,000 | Search.EndsWith | 500 | 1000 | 211.210 | 221.447 | 0.95x | 110,900 | 111,206 |
| names | 100,000 | Search.Filter | 1 | 0 | 2.866 | 2.127 | 1.35x | 5,572 | 5,572 |
| names | 100,000 | Search.Filter | 1 | 100 | 6.304 | 6.400 | 0.99x | 6,613 | 6,614 |
| names | 100,000 | Search.Filter | 1 | 1000 | 6.812 | 8.141 | 0.84x | 8,808 | 9,447 |
| names | 100,000 | Search.Filter | 100 | 0 | 122.191 | 118.327 | 1.03x | 554,699 | 554,674 |
| names | 100,000 | Search.Filter | 100 | 100 | 172.696 | 182.299 | 0.95x | 618,004 | 618,431 |
| names | 100,000 | Search.Filter | 100 | 1000 | 348.377 | 386.111 | 0.90x | 778,447 | 791,688 |
| names | 100,000 | Search.Filter | 500 | 0 | 592.217 | 469.348 | 1.26x | 2,761,334 | 2,761,568 |
| names | 100,000 | Search.Filter | 500 | 100 | 724.871 | 612.868 | 1.18x | 2,878,600 | 2,882,805 |
| names | 100,000 | Search.Filter | 500 | 1000 | 1396.500 | 1631.465 | 0.86x | 3,780,966 | 3,887,947 |
| names | 100,000 | Search.StartsWith | 1 | 0 | 0.865 | 0.881 | 0.98x | 88 | 88 |
| names | 100,000 | Search.StartsWith | 1 | 100 | 0.361 | 0.344 | 1.05x | 89 | 89 |
| names | 100,000 | Search.StartsWith | 1 | 1000 | 0.478 | 0.454 | 1.05x | 267 | 260 |
| names | 100,000 | Search.StartsWith | 100 | 0 | 92.729 | 70.192 | 1.32x | 6,259 | 6,451 |
| names | 100,000 | Search.StartsWith | 100 | 100 | 20.995 | 21.550 | 0.97x | 6,672 | 6,704 |
| names | 100,000 | Search.StartsWith | 100 | 1000 | 24.224 | 31.359 | 0.77x | 7,242 | 7,132 |
| names | 100,000 | Search.StartsWith | 500 | 0 | 57.449 | 53.934 | 1.07x | 19,166 | 19,145 |
| names | 100,000 | Search.StartsWith | 500 | 100 | 78.205 | 68.301 | 1.15x | 21,346 | 21,605 |
| names | 100,000 | Search.StartsWith | 500 | 1000 | 77.788 | 93.970 | 0.83x | 48,728 | 55,237 |

## Build, mutations and complete result reads
| Data | Items | Scenario | Requests | Updates/s | Before us | After us | Speedup | Before B | After B |
|---|---|---|---|---|---|---|---|---|---|
| isin | 10,000 | Build | 1 | 0 | 6360.100 | 5922.200 | 1.07x | 2,825,568 | 2,825,840 |
| isin | 10,000 | Enumerate.Filter | 1 | 0 | 13.925 | 14.142 | 0.98x | 40,096 | 40,096 |
| isin | 10,000 | Enumerate.Prefix | 1 | 0 | 12.945 | 12.423 | 1.04x | 80,152 | 80,152 |
| isin | 10,000 | IndexAll.Filter | 1 | 0 | 14.021 | 13.706 | 1.02x | 40,096 | 40,096 |
| isin | 10,000 | Mutation.Add | 1 | 0 | 8.369 | 8.448 | 0.99x | 11,660 | 11,660 |
| isin | 10,000 | Mutation.Remove | 1 | 0 | 5.920 | 5.856 | 1.01x | 10,499 | 10,498 |
| isin | 100,000 | Build | 1 | 0 | 100415.300 | 97640.200 | 1.03x | 23,519,488 | 23,516,008 |
| isin | 100,000 | Enumerate.Filter | 1 | 0 | 1454.982 | 1602.208 | 0.91x | 1,815,005 | 1,815,278 |
| isin | 100,000 | Enumerate.Prefix | 1 | 0 | 477.451 | 514.091 | 0.93x | 800,419 | 800,469 |
| isin | 100,000 | IndexAll.Filter | 1 | 0 | 1399.759 | 1467.884 | 0.95x | 1,815,107 | 1,815,278 |
| isin | 100,000 | Mutation.Add | 1 | 0 | 26.629 | 18.967 | 1.40x | 45,542 | 45,479 |
| isin | 100,000 | Mutation.Remove | 1 | 0 | 21.220 | 20.481 | 1.04x | 44,794 | 44,796 |
| names | 10,000 | Build | 1 | 0 | 14853.400 | 19455.500 | 0.76x | 5,194,144 | 5,196,376 |
| names | 10,000 | Enumerate.Filter | 1 | 0 | 10.388 | 10.842 | 0.96x | 25,256 | 25,256 |
| names | 10,000 | Enumerate.Prefix | 1 | 0 | 13.073 | 12.469 | 1.05x | 80,152 | 80,152 |
| names | 10,000 | IndexAll.Filter | 1 | 0 | 11.527 | 10.478 | 1.10x | 25,256 | 25,256 |
| names | 10,000 | Mutation.Add | 1 | 0 | 22.097 | 19.503 | 1.13x | 22,382 | 22,382 |
| names | 10,000 | Mutation.Remove | 1 | 0 | 20.614 | 20.566 | 1.00x | 19,946 | 19,946 |
| names | 100,000 | Build | 1 | 0 | 321057.500 | 183547.700 | 1.75x | 46,439,528 | 46,438,120 |
| names | 100,000 | Enumerate.Filter | 1 | 0 | 1.350 | 1.305 | 1.03x | 3,728 | 3,728 |
| names | 100,000 | Enumerate.Prefix | 1 | 0 | 502.214 | 499.486 | 1.01x | 800,339 | 800,339 |
| names | 100,000 | IndexAll.Filter | 1 | 0 | 1.389 | 1.352 | 1.03x | 3,728 | 3,728 |
| names | 100,000 | Mutation.Add | 1 | 0 | 65.513 | 42.251 | 1.55x | 92,042 | 92,036 |
| names | 100,000 | Mutation.Remove | 1 | 0 | 85.663 | 56.428 | 1.52x | 90,614 | 90,613 |

## BenchmarkDotNet confirmation

Error is the interval reported by BenchmarkDotNet. Some under-write cases are noisy. Consult the raw reports in the archive for GC statistics and writer achievement.
| Data | Variant | Size | Updates/s | Mean | Error | Allocated |
|---|---|---|---|---|---|---|
| isin | Baseline | Small | 0 | 486.4 ns | 9.63 ns | 245 B |
| isin | Baseline | Medium | 0 | 607.5 ns | 9.66 ns | 245 B |
| isin | Baseline | Huge | 0 | 815.4 ns | 82.48 ns | 245 B |
| isin | Baseline | Small | 1000 | 22,337.2 ns | 13,998.14 ns | 91283 B |
| isin | Baseline | Medium | 1000 | 3,254,474.6 ns | 4,926,732.34 ns | 955911 B |
| isin | Baseline | Huge | 1000 | 10,896,773.3 ns | 10,388,385.02 ns | 9086093 B |
| isin | Changed | Small | 0 | 530.7 ns | 64.77 ns | 245 B |
| isin | Changed | Medium | 0 | 626.8 ns | 60.66 ns | 245 B |
| isin | Changed | Huge | 0 | 763.1 ns | 47.73 ns | 245 B |
| isin | Changed | Small | 1000 | 9,594.3 ns | 5,404.44 ns | 1853 B |
| isin | Changed | Medium | 1000 | 26,309.7 ns | 19,218.57 ns | 8289 B |
| isin | Changed | Huge | 1000 | 369,349.7 ns | 817,942.42 ns | 1898 B |
| names | Baseline | Small | 0 | 800.2 ns | 88.43 ns | 244 B |
| names | Baseline | Medium | 0 | 1,139.1 ns | 204.17 ns | 244 B |
| names | Baseline | Huge | 0 | 1,792.1 ns | 485.00 ns | 244 B |
| names | Baseline | Small | 1000 | 2,082.8 ns | 135.32 ns | 4074 B |
| names | Baseline | Medium | 1000 | 13,925.3 ns | 16,434.94 ns | 44959 B |
| names | Baseline | Huge | 1000 | 428,449.2 ns | 227,369.36 ns | 444601 B |
| names | Changed | Small | 0 | 806.9 ns | 12.28 ns | 244 B |
| names | Changed | Medium | 0 | 1,349.9 ns | 719.07 ns | 244 B |
| names | Changed | Huge | 0 | 1,871.9 ns | 899.61 ns | 244 B |
| names | Changed | Small | 1000 | 1,819.5 ns | 297.83 ns | 471 B |
| names | Changed | Medium | 1000 | 3,996.1 ns | 1,792.67 ns | 1332 B |
| names | Changed | Huge | 1000 | 17,439.9 ns | 15,069.31 ns | 2083 B |

## Artifacts and reproduction

Companion archive: sorted-array-direct-indexing-2026-11-14.tar.gz. Contains baseline source, current-source snapshot, linked-source harness, raw JSONL, summary.csv, benchmark logs and BenchmarkDotNet reports. Working directory: /private/tmp/sorted-array-direct.pzUfE6.

The harness uses the current checkout's benchmark/test files and local data. Build Experiments.csproj in Release, then run run.py for the recorded matrix/BenchmarkDotNet sequence. Baseline.cs preserves the pre-change implementation. changed-source.cs.txt preserves the measured implementation.

To run the new benchmark scenario directly from this repository, set ALGORITHM=SortedArrayIndex and DATASET=isin or names, then run:
    
    dotnet run --project benchmarks/SearchAlgorithms.Benchmarks -c Release -- --inProcess --job short --filter '*TypeaheadBenchmarks.StartsWith*'

Focused tests:
    
    dotnet test tests/SearchAlgorithms.Tests --filter 'FullyQualifiedName~SortedArrayIndexTests|DisplayName~SortedArrayIndex'

Direct indexing is O(matching pending edits) per access for the first page; the cutoff avoids repeatedly walking edits when reading later positions. Cache initialization can still allocate duplicate copies under a race, but all published cache arrays are complete and equivalent.
