/* Step players and benchmark chart for index.html. No libraries; data comes from bench-data.js. */
(() => {
  "use strict";
  const $ = id => document.getElementById(id);
  const escape = value => String(value).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const code = value => `<code>${escape(value)}</code>`;
  const copyText = (title, body) => `<h4>${title}</h4><p>${body}</p>`;
  const cell = (value, label, cls = "") => `<div class="cell ${cls}"><small>${escape(label)}</small>${escape(value)}</div>`;
  const pills = values => values.length ? values.map(x => `<span class="result-pill">${escape(x)}</span>`).join(" ") : '<span class="empty-value">[] · no hits</span>';
  const metric = text => `<span class="metric">${text}</span>`;

  // The same small collection everywhere; Ids are what a caller would use to find the entity.
  const items = ["FR77", "GB19", "US12", "US55", "US99", "ZA88"];
  const idOf = key => items.indexOf(key) + 1;
  const matchers = { prefix: (k, q) => k.startsWith(q), suffix: (k, q) => k.endsWith(q), contains: (k, q) => k.includes(q) };
  const method = { prefix: "StartsWith", suffix: "EndsWith", contains: "Contains" };
  const queries = { linear: { prefix: "US", suffix: "9", contains: "7" }, rocks: { prefix: "US", suffix: "9", contains: "S9" } };

  // Same controls as the walkthrough page: reset, back, play/pause, next.
  const players = new Map();
  function player(name, frames, render) {
    const controls = document.querySelector(`[data-player="${name}"]`);
    const state = { frames, index: 0, timer: null };
    const playButton = controls.querySelector('[data-action="play"]');
    function stop() { clearInterval(state.timer); state.timer = null; playButton.textContent = "Play"; }
    function draw() {
      render(state.frames[state.index], state.index);
      controls.querySelector(".step-count").textContent = `${state.index + 1} / ${state.frames.length}`;
      controls.querySelector('[data-action="prev"]').disabled = state.index === 0;
      controls.querySelector('[data-action="next"]').disabled = state.index === state.frames.length - 1;
    }
    Object.assign(state, { stop, draw, replace: newFrames => { stop(); state.frames = newFrames; state.index = 0; draw(); } });
    controls.addEventListener("click", event => {
      const action = event.target.closest("button")?.dataset.action;
      if (!action) return;
      if (action === "play") {
        if (state.timer) { stop(); return; }
        if (state.index === state.frames.length - 1) state.index = 0;
        playButton.textContent = "Pause";
        draw();
        state.timer = setInterval(() => {
          state.index = Math.min(state.index + 1, state.frames.length - 1);
          draw();
          if (state.index === state.frames.length - 1) stop();
        }, 1700);
      } else {
        stop();
        state.index = action === "reset" ? 0 : Math.max(0, Math.min(state.frames.length - 1, state.index + (action === "next" ? 1 : -1)));
        draw();
      }
    });
    players.set(name, state); draw(); return state;
  }

  // 01 · LinearScan: one frame per item checked.
  function linearFrames(kind) {
    const q = queries.linear[kind];
    return [{ at: -1 }, ...items.map((_, at) => ({ at })), { at: items.length }].map(f => ({ ...f, kind, q }));
  }
  function renderLinear({ at, kind, q }) {
    const checked = Math.max(0, Math.min(at + 1, items.length));
    const hits = items.slice(0, checked).filter(k => matchers[kind](k, q));
    $("linear-canvas").innerHTML = `<div class="array">${items.map((k, i) => {
      const cls = i === at ? "probe" : i < at || at === items.length ? (matchers[kind](k, q) ? "hit" : "muted") : "";
      return cell(k, `#${i + 1}`, cls);
    }).join("")}</div><div class="metric-row">${metric(`checks so far = ${checked}`)}${metric(`hits = ${hits.length}`)}</div><div class="metric-row">${pills(hits)}</div>`;
    let title, body;
    if (at < 0) { title = "No index: the only plan is to look at everything"; body = `The query is ${code(`${method[kind]}("${q}")`)}. ${code("FindAll")} takes the read lock, then walks the whole set.`; }
    else if (at < items.length) {
      const k = items[at], ok = matchers[kind](k, q);
      title = escape(`Check #${at + 1}: "${k}".${kind === "contains" ? "Contains" : method[kind]}("${q}") → ${ok}`);
      body = ok ? `A hit: the entry goes into the result list.` : `No match, but we still paid for the check. Nothing about ${code(k)} tells us whether the next item matches.`;
    } else { title = `Done: ${items.length} checks for ${hits.length} ${hits.length === 1 ? "hit" : "hits"}`; body = `The number of checks never depends on the answer. With 100,000 ISINs this is 100,000 string comparisons per query, even for a query with no hits.`; }
    $("linear-copy").innerHTML = copyText(title, body);
  }

  // 02 · lock vs lock-free, the same four moments on both sides.
  const lanes = [
    [["Reader A", "running", "holds the read lock"], ["Writer", "idle", "—"], ["Reader B", "idle", "—"]],
    [["Reader A", "running", "holds the read lock"], ["Writer", "waiting", "needs the lock exclusively"], ["Reader B", "idle", "—"]],
    [["Reader A", "running", "holds the read lock"], ["Writer", "waiting", "first in line"], ["Reader B", "waiting", "queued behind the writer"]],
    [["Reader A", "done", "returned its result"], ["Writer", "done", "Remove(US12) applied"], ["Reader B", "running", "sees the write"]]
  ];
  const freeLanes = [
    [["Reader A", "running", "enumerating, no lock"], ["Writer", "idle", "—"], ["Reader B", "idle", "—"]],
    [["Reader A", "running", "may or may not see the removal"], ["Writer", "done", "Remove(US12) applied at once"], ["Reader B", "idle", "—"]],
    [["Reader A", "running", "still enumerating"], ["Writer", "done", "—"], ["Reader B", "running", "starts at once, sees the write"]],
    [["Reader A", "done", "result may still contain US12"], ["Writer", "done", "—"], ["Reader B", "done", "returned"]]
  ];
  const concurrentCopy = [
    ["Reader A starts a scan", "LinearScan takes a shared read lock; any number of searches can hold it together. ConcurrentLinearScan takes nothing."],
    ["A write arrives during the scan", "With the lock, Remove must wait until every reader has left. Without it, the dictionary applies the removal immediately, and Reader A, part-way through, may or may not pass the removed item."],
    ["A second search arrives", "ReaderWriterLockSlim gives a waiting writer priority, so Reader B queues too: one slow search delays both the write and everyone behind it. Lock-free, Reader B simply starts."],
    ["Everyone finishes", "Both are correct under the contract: a search that starts after a write returned sees it. The lock-free version trades a fuzzy view during writes for never waiting. Neither touches fewer items."]
  ];
  function lane(rows) {
    return rows.map(([who, status, note]) => `<div class="lane-row"><span class="lane-who">${who}</span><span class="badge badge-${status}">${status}</span><span class="lane-note">${escape(note)}</span></div>`).join("");
  }
  function renderConcurrent(step) {
    $("concurrent-canvas").innerHTML = `<div class="timeline-grid"><div class="timeline-card"><h4>LinearScan · ReaderWriterLockSlim</h4>${lane(lanes[step])}</div><div class="timeline-card newer"><h4>ConcurrentLinearScan · no lock</h4>${lane(freeLanes[step])}</div></div>`;
    $("concurrent-copy").innerHTML = copyText(...concurrentCopy[step]);
  }

  // 03 · NaiveRocksDbIndex: a seek for prefixes, a full scan otherwise.
  const rocksKey = k => `${k}·#${idOf(k)}`;
  function rocksFrames(kind) {
    const q = queries.rocks[kind], frames = [{ type: "intro" }];
    if (kind === "prefix") {
      let pos = items.findIndex(k => k >= q);
      frames.push({ type: "seek", pos, read: [] });
      const read = [];
      for (; pos < items.length; pos++) {
        read.push(pos);
        const ok = items[pos].startsWith(q);
        frames.push({ type: ok ? "hit" : "stop", pos, read: [...read] });
        if (!ok) break;
      }
      if (pos === items.length) frames.push({ type: "end", pos, read: [...read] });
    } else {
      frames.push({ type: "first", pos: 0, read: [] });
      items.forEach((k, pos) => frames.push({ type: matchers[kind](k, q) ? "hit" : "miss", pos, read: items.slice(0, pos + 1).map((_, i) => i) }));
      frames.push({ type: "end", pos: items.length, read: items.map((_, i) => i) });
    }
    return frames.map(f => ({ ...f, kind, q }));
  }
  function renderRocks(f) {
    const read = new Set(f.read || []);
    const hits = [...read].filter(i => matchers[f.kind](items[i], f.q)).map(i => items[i]);
    $("rocks-canvas").innerHTML = `<p class="view-label">Default column family · keys in byte order</p><div class="array">${items.map((k, i) => {
      let cls = f.type === "intro" ? "" : i === f.pos ? "probe" : read.has(i) ? (matchers[f.kind](k, f.q) ? "hit" : "") : "muted";
      return cell(rocksKey(k), `key ${i}`, cls);
    }).join("")}</div><div class="metric-row">${metric(`keys read = ${read.size} of ${items.length}`)}${metric(`hits = ${hits.length}`)}</div><div class="metric-row">${pills(hits)}</div>`;
    const k = items[f.pos], titles = {
      intro: [`${method[f.kind]}("${f.q}")`, f.kind === "prefix" ? "Every key starting with the query sits in one run of the sorted keys. We only need to find where the run begins." : `Byte order groups keys by how they <em>start</em>. Keys ${f.kind === "suffix" ? "ending" : "containing"} "${escape(f.q)}" can be anywhere, so the naive version cannot seek.`],
      seek: [`Seek("${f.q}") lands on ${k}`, "RocksDB binary-searches its index blocks to the first key ≥ the query. No key before it is read."],
      first: ["SeekToFirst: start at the very first key", "There is no smarter starting point. Every key will cross into .NET, be copied into a new byte[] and decoded into a string."],
      hit: [`Next: ${k} matches`, f.kind === "prefix" ? "Still inside the run: decode the Key and Id and add the hit." : "Decoded, checked in C#, kept."],
      miss: [`Next: ${k} does not match`, "Decoded and checked anyway: that is the cost of reading everything."],
      stop: [`${k} no longer starts with "${f.q}": stop`, `${read.size} keys read for ${hits.length} hits. With 100,000 keys, a prefix search still reads only its hits plus one.`],
      end: ["Iterator exhausted", f.kind === "prefix" ? "The run reached the last key." : `${items.length} keys read for ${hits.length} ${hits.length === 1 ? "hit" : "hits"}: a linear scan, plus a native call and two copies per key.`]
    }[f.type];
    $("rocks-copy").innerHTML = copyText(escape(titles[0]), titles[1]);
  }

  // 04 · NaiveLuceneIndex: Filter("S9") as WildcardQuery("*S9*").
  const luceneFrames = [{ type: "intro" }, { type: "reader" }, ...items.map((_, at) => ({ type: "term", at })), { type: "collect" }, { type: "stored" }, { type: "done" }];
  function renderLucene(f) {
    const hitAt = items.indexOf("US99");
    const visited = f.type === "term" ? f.at : ["collect", "stored", "done"].includes(f.type) ? items.length : -1;
    const terms = items.map((k, i) => {
      const cls = f.type === "term" && i === f.at ? "probe" : i < visited || visited === items.length ? (k.includes("S9") ? "hit" : "muted") : "";
      return `<div class="cell ${cls}"><small>term</small>${k}<span class="item-label">→ doc ${i}</span></div>`;
    }).join("");
    const box = (title, body, on) => `<div class="pipe-box ${on ? "on" : ""}"><span class="view-label">${title}</span>${body}</div>`;
    $("lucene-canvas").innerHTML = `<p class="view-label">Field "value" · term dictionary (sorted) → postings</p><div class="array">${terms}</div>
      <div class="pipe">${box("1 · READER", "DirectoryReader.Open(writer)<br><small>new for this search</small>", f.type === "reader")}${box("2 · AUTOMATON", "*S9* tested against<br>every term", f.type === "term")}${box("3 · TOP DOCS", ["collect", "stored", "done"].includes(f.type) ? "doc 4, score 1.0" : "empty", f.type === "collect")}${box("4 · STORED FIELDS", ["stored", "done"].includes(f.type) ? "doc 4 → value US99, id 5" : "not loaded", f.type === "stored")}</div>
      <div class="metric-row">${f.type === "done" ? pills([`US99 · #5`]) : ""}</div>`;
    const copy = {
      intro: ["Filter(\"S9\") becomes a wildcard pattern", `The naive index escapes the query and wraps it: ${code("*S9*")}. Each entry is one document; its whole Key is one term in field ${code("value")}.`],
      reader: ["Open a fresh reader", "So the search sees recent writes, each call opens a near-real-time reader from the writer, which first flushes any buffered adds and deletes into a new segment. With writes arriving, every query pays this."],
      term: [`Test term "${items[f.at]}" against *S9*`, f.at === undefined ? "" : items[f.at].includes("S9") ? "Accepted: its postings (doc " + f.at + ") are collected." : "Rejected. A leading * means there is no region of the dictionary to skip to, so every term is tested."],
      collect: ["Rank the hits", `${code("Search(query, int.MaxValue)")} feeds every hit through a scoring top-N queue as large as the index, built for "best ten results", although all hits are wanted and none needs a score.`],
      stored: ["Load the stored document per hit", `${code("searcher.Doc(4)")} reads and decompresses the stored fields to get the Key and Id back. One stored-document read per hit.`],
      done: ["One hit, four layers of work", `Scan every term, collect, rank, load. For a 100,000-entry index and a short fragment that is 100,000 terms tested, then thousands of ranked hits and stored reads.`]
    }[f.type];
    $("lucene-copy").innerHTML = copyText(escape(copy[0]), copy[1]);
  }

  // 05 · LuceneIndex: the n-gram fields for US99.
  const grams = (s, min, max) => { const out = []; for (let n = min; n <= max; n++) for (let i = 0; i + n <= s.length; i++) out.push(s.slice(i, i + n)); return out; };
  const edges = (s, max) => Array.from({ length: Math.min(max, s.length) }, (_, i) => s.slice(0, i + 1));
  const tokenRow = (label, tokens, hit) => `<p class="view-label">${label}</p><div class="token-row">${tokens.map(t => `<span class="token ${t === hit ? "hit" : ""}">${escape(t)}</span>`).join("")}</div>`;
  const ngramFrames = [0, 1, 2, 3, 4, 5];
  function renderNgram(step) {
    const s = "US99", rev = "99SU";
    const rows = [
      tokenRow('Naive · field "value"', ["US99"]),
      step >= 1 ? tokenRow('field "value-prefix" · edges 1–6', edges(s, 6), step === 5 ? "US" : null) : "",
      step >= 2 ? tokenRow('field "value-suffix" · edges of the reversed key "99SU"', edges(rev, 6), step === 5 ? "9" : null) : "",
      step >= 3 ? tokenRow('field "value-ngram" · every substring of length 1–6', grams(s, 1, 6), step >= 4 ? "S9" : null) : ""
    ].join("");
    const lookup = step === 4 ? `<div class="pipe">${["TermQuery(value-ngram:\"S9\")", "postings → doc 4", "ids[4] → US99 · #5"].map(t => `<div class="pipe-box on">${escape(t)}</div>`).join("")}</div>` : "";
    $("ngram-canvas").innerHTML = rows + lookup;
    $("ngram-copy").innerHTML = copyText(...[
      ["Naive: one term per entry", "The whole Key is one term, so only a pattern can find anything inside it, and a pattern with a leading * visits every term."],
      ["Every prefix becomes a term", `StartsWith("US") is now an exact lookup of term ${code("US")} in ${code("value-prefix")}: one seek in the term dictionary, then its postings list is the answer.`],
      ["Every suffix, through the reversed key", `EndsWith("9") reverses the query and looks up ${code("9")} in ${code("value-suffix")}. Edges stop at 6 characters: longer ones are almost unique per item, so a longer query looks up its first 6 and verifies the rest.`],
      ["Every substring up to 6 characters", `Contains becomes an exact lookup too. US99 yields ${grams(s, 1, 6).length} n-grams; a 12-character ISIN yields 57, plus 12 edges.`],
      ["Filter(\"S9\") is one term lookup", "No automaton, no scan: find the term, walk its postings, and turn each doc number into an Entry through a per-segment table. No scoring, no stored-field reads."],
      ["The price: a much bigger index", "Search work moved into indexing. The build tokenizes every item three ways and writes all those terms; the index grows accordingly, and every write must refresh the reader before the next search can see it."]
    ][step]);
  }

  // 06 · RocksDbIndex: key families for (US99, #5) and a Filter over buckets.
  const SEP = "￿"; // stands in for byte 0xFF, which sorts after every UTF-8 byte
  const repeatAt = (s, o) => { let r = 0; for (let j = 0; j < o; j++) { let n = 0; while (o + n < s.length && s[j + n] === s[o + n]) n++; r = Math.max(r, n); } return r; };
  const rotations = k => [...k].map((_, o) => ({ k, repeat: repeatAt(k, o), text: k.slice(o) + SEP + k.slice(0, o) }));
  const show = r => `[${r.repeat}] ${r.text.replace(SEP, "·")}#${idOf(r.k)}`;
  const family = ["GB19", "US99"].flatMap(rotations).sort((a, b) => a.repeat - b.repeat || (a.text < b.text ? -1 : a.text > b.text ? 1 : 0));
  const rocksKeyFrames = [0, 1, 2, 3, 4, 5, 6];
  function renderRocksKeys(step) {
    const keyCells = (rows, hitFn, dimFn) => `<div class="array">${rows.map(r => cell(show(r), `Repeat ${r.repeat}`, hitFn?.(r) ? "hit" : dimFn?.(r) ? "muted" : "")).join("")}</div>`;
    let canvas = "";
    if (step <= 2) {
      canvas += `<p class="view-label">default family · StartsWith</p><div class="array">${cell("US99·#5", "Key, separator, Id", step === 0 ? "hit" : "")}</div>`;
      if (step >= 1) canvas += `<p class="view-label">"suffix" family · EndsWith</p><div class="array">${cell("99SU·#5", "reversed bytes", step === 1 ? "hit" : "")}</div>`;
      if (step >= 2) canvas += `<p class="view-label">"substring" family · Filter · one rotation per offset</p>${keyCells(rotations("US99"), r => r.repeat > 0)}`;
    } else {
      const fragment = step === 4 ? "9" : step === 5 ? "99" : null;
      canvas += `<p class="view-label">"substring" family for GB19 (#2) and US99 (#5) · sorted by bytes, Repeat first</p>`;
      canvas += keyCells(family, fragment ? r => r.repeat < fragment.length && r.text.startsWith(fragment) : null, fragment ? r => r.repeat >= fragment.length : null);
      if (fragment) {
        const hits = family.filter(r => r.repeat < fragment.length && r.text.startsWith(fragment)).map(r => `${r.k} · #${idOf(r.k)}`);
        canvas += `<div class="metric-row">${metric(fragment.length === 1 ? "bucket read: 0" : `buckets read: 0…${fragment.length - 1}`)}${metric(`keys read = ${hits.length}`)}</div><div class="metric-row">${pills(hits)}</div>`;
      }
      if (step === 6) canvas += `<div class="pipe"><div class="pipe-box on">WriteBatch</div><div class="pipe-box">1 default key</div><div class="pipe-box">1 suffix key</div><div class="pipe-box">4 rotation keys</div><div class="pipe-box on">one atomic write</div></div>`;
    }
    $("rockskeys-canvas").innerHTML = canvas;
    $("rockskeys-copy").innerHTML = copyText(...[
      ["Key 1: the Key itself", "Unchanged from the naive version: StartsWith seeks to the prefix. New: an upper bound (the prefix with its last byte + 1) makes the iterator stop by itself at the end of the run."],
      ["Key 2: the reversed bytes", `EndsWith("9") becomes "starts with 9" in this family. Bytes are reversed, not characters, so multi-byte characters stay intact.`],
      ["Keys 3–6: one rotation per offset", `Each rotation starts at one offset and carries the rest of the key after the separator, so the Key can be rebuilt from the key alone: no value read. The leading ${code("[Repeat]")} counts how much of this text already started at an earlier offset: offset 3's "9" repeats offset 2's, so it gets Repeat 1.`],
      ["Repeat sorts first, so it splits the family into buckets", "Bucket 0 holds the first occurrence of everything. Higher buckets hold only repeats."],
      ["Filter(\"9\"): read only bucket 0", "An entry is a key's first match exactly when its Repeat is shorter than the fragment. So only buckets below the fragment's length are seeked, and every key they hold for this prefix is a distinct hit. US99's second 9 sits in bucket 1 and is never read."],
      ["Filter(\"99\"): buckets 0 and 1", "Two buckets, one seek each, both read from one snapshot so a write between them can't produce a mixed answer. Only one key matches; no de-duplication pass is needed."],
      ["Every write is one atomic batch", "Add and Remove stage all keys of an entry (here 6, for an ISIN 14) into one WriteBatch across the three families. No search can see an entry in one family and not another, and no lock is needed."]
    ][step]);
  }

  // 07 · benchmark chart.
  const families = { SortedArrayIndex: "sorted", LuceneIndex: "tuned", RocksDbIndex: "tuned", NaiveLuceneIndex: "naive", NaiveRocksDbIndex: "naive", LinearScan: "scan", ConcurrentLinearScan: "scan" };
  const time = ns => ns < 1e3 ? `${ns.toPrecision(3)} ns` : ns < 1e6 ? `${(ns / 1e3).toPrecision(3)} μs` : ns < 1e9 ? `${(ns / 1e6).toPrecision(3)} ms` : `${(ns / 1e9).toPrecision(3)} s`;
  const bytes = b => b < 1024 ? `${Math.round(b)} B` : b < 1048576 ? `${(b / 1024).toPrecision(3)} KB` : b < 1073741824 ? `${(b / 1048576).toPrecision(3)} MB` : `${(b / 1073741824).toPrecision(3)} GB`;
  const tickTime = ns => ns < 1e3 ? `${ns} ns` : ns < 1e6 ? `${ns / 1e3} μs` : ns < 1e9 ? `${ns / 1e6} ms` : `${ns / 1e9} s`;
  const tickBytes = b => bytes(b).replace(/\.0+ /, " ");
  function scenarios(B) {
    const search = (writes, size = "Medium") => q => B.search.filter(r => r.method === q && r.writes === writes && r.size === size);
    return [
      { id: "single", label: "Single query", queries: ["StartsWith", "EndsWith", "Filter"], rows: search(0), title: q => `One ${q} query`, sub: "Mean time per query, no writes running.", unit: "time" },
      { id: "writes", label: "Under writes", queries: ["StartsWith", "EndsWith", "Filter"], rows: search(1000), title: q => `One ${q} query, 1,000 writes/s running`, sub: "Mean time per query while a background writer swaps entries (Remove + Add).", unit: "time" },
      { id: "typeahead", label: "Typeahead", queries: [...new Set(B.typeahead.map(r => r.method))], rows: q => B.typeahead.filter(r => r.method === q), title: q => `Typeahead, ${q}`, sub: "One search per keystroke of a 3–6 character query, first 10 hits read. Time per typed query.", unit: "time" },
      { id: "build", label: "Build", queries: ["Time", "Allocated"], rows: q => B.build.map(r => ({ ...r, mean: q === "Time" ? r.mean : r.allocated })), title: q => q === "Time" ? "Building the index from 100,000 entries" : "Bytes allocated while building", sub: "The constructor, start to finish.", unit: q => q === "Time" ? "time" : "bytes" },
      { id: "writes1", label: "Add / Remove", queries: [...new Set(B.mutation.map(r => r.method))], rows: q => B.mutation.filter(r => r.method === q), title: q => `One ${q}`, sub: "Single-threaded cost of one write. For Lucene, part of the cost moves to the next search's refresh.", unit: "time" },
      { id: "memory", label: "Memory", queries: ["Heap", "Disk"], // Footprint reports whole MB: a 0 heap is under 1 MB, drawn at 0.5 MB; a 0 disk means no files.
        rows: q => B.footprint.map(r => q === "Heap" && !r.heap ? { algorithm: r.algorithm, mean: 524288, label: "< 1 MB" } : { algorithm: r.algorithm, mean: r[q.toLowerCase()] }).filter(r => r.mean > 0), title: q => ({ Heap: "Managed heap retained after build", Disk: "Files on disk" })[q], sub: "100,000 entries, one fresh process per index. Heap is managed memory only (RocksDB's native cache is not in it); indexes with nothing on disk show a dash.", unit: "bytes" }
    ];
  }
  function chart() {
    const B = window.BENCH;
    if (!B) { $("chart").innerHTML = '<p class="empty-value">Benchmark data did not load.</p>'; return; }
    const list = scenarios(B);
    let current = list[0], query = current.queries[0];
    const tip = $("chart-tip");
    function tabs(el, values, active, onPick, label = x => x) {
      el.innerHTML = values.map(v => `<button type="button" role="tab" aria-selected="${v === active}" data-v="${escape(v)}">${escape(label(v))}</button>`).join("");
      el.onclick = e => { const v = e.target.closest("button")?.dataset.v; if (v) onPick(v); };
    }
    function draw() {
      tabs($("chart-scenario"), list.map(s => s.id), current.id, id => { current = list.find(s => s.id === id); query = current.queries.includes(query) ? query : current.queries[0]; draw(); }, id => list.find(s => s.id === id).label);
      tabs($("chart-query"), current.queries, query, q => { query = q; draw(); });
      const unit = typeof current.unit === "function" ? current.unit(query) : current.unit;
      const fmt = unit === "time" ? time : bytes, tick = unit === "time" ? tickTime : tickBytes;
      const rows = current.rows(query).filter(r => r.mean > 0).sort((a, b) => a.mean - b.mean);
      const missing = Object.keys(families).filter(a => !rows.some(r => r.algorithm === a));
      $("chart-title").textContent = current.title(query);
      $("chart-sub").textContent = current.sub;
      if (!rows.length) { $("chart-bars").innerHTML = '<p class="empty-value">No results for this view.</p>'; $("chart-axis").innerHTML = ""; return; }
      // Gridlines at 1, 10, 100 of each unit: ns-based powers of ten for time, B/KB/MB/GB (1024-based) for bytes.
      const ticks = unit === "time" ? [...Array(13)].map((_, k) => 10 ** k) : [...Array(12)].map((_, i) => 1024 ** Math.floor(i / 3) * 10 ** (i % 3));
      const min = rows[0].mean, max = rows[rows.length - 1].mean;
      const lo = [...ticks].reverse().find(t => t <= min) ?? ticks[0], hi = ticks.find(t => t >= max) ?? ticks[ticks.length - 1];
      const pos = v => (Math.log10(v) - Math.log10(lo)) / (Math.log10(hi) - Math.log10(lo)) * 100;
      const decades = ticks.filter(t => t >= lo && t <= hi);
      const every = $("chart-bars").clientWidth < 500 ? 2 : 1; // narrow screens: label every other gridline
      const grid = decades.map(v => `<i style="left:${pos(v)}%"></i>`).join("");
      $("chart-bars").setAttribute("aria-label", `${current.title(query)}: ` + rows.map(r => `${r.algorithm} ${fmt(r.mean)}`).join(", "));
      $("chart-bars").innerHTML = rows.map(r => `<div class="bar-row" data-a="${r.algorithm}" tabindex="0"><span class="bar-name">${r.algorithm}</span><span class="bar-track"><span class="bar-grid">${grid}</span><span class="bar fam-${families[r.algorithm]}" style="width:${Math.max(pos(r.mean), 1.5)}%"></span><span class="bar-value" style="left:${Math.max(pos(r.mean), 1.5)}%">${r.label ?? fmt(r.mean)}</span></span></div>`).join("")
        + missing.map(a => `<div class="bar-row missing"><span class="bar-name">${a}</span><span class="bar-track"><span class="bar-value" style="left:0">—</span></span></div>`).join("");
      $("chart-axis").innerHTML = `<span></span><span class="axis-track">${decades.filter((_, i) => i % every === 0).map(v => `<span style="left:${pos(v)}%">${tick(v)}</span>`).join("")}</span>`;
      const byName = Object.fromEntries(rows.map(r => [r.algorithm, r]));
      const fastest = rows[0].mean;
      $("chart-bars").querySelectorAll(".bar-row[data-a]").forEach(row => {
        const r = byName[row.dataset.a];
        const show = () => {
          const extra = r.allocated !== undefined && unit === "time" ? `<br>allocated ${bytes(r.allocated)}` : "";
          tip.innerHTML = `<strong>${r.algorithm}</strong><br>${r.label ?? fmt(r.mean)}${r.mean > fastest ? ` · ${(r.mean / fastest).toFixed(r.mean / fastest < 10 ? 1 : 0)}× the best` : " · best"}${extra}`;
          tip.hidden = false;
          const box = row.getBoundingClientRect(), host = $("chart").getBoundingClientRect();
          tip.style.top = `${box.top - host.top - tip.offsetHeight - 6}px`;
          tip.style.left = `${Math.min(Math.max(box.left - host.left + box.width / 2 - tip.offsetWidth / 2, 8), host.width - tip.offsetWidth - 8)}px`;
        };
        row.addEventListener("mouseenter", show); row.addEventListener("focus", show);
        row.addEventListener("mouseleave", () => { tip.hidden = true; }); row.addEventListener("blur", () => { tip.hidden = true; });
      });
    }
    draw();

    // Full table for readers who want the numbers, and as the non-visual view.
    const names = Object.keys(families);
    const cols = list.flatMap(s => s.queries.map(q => ({ s, q })));
    $("bench-table").innerHTML = `<caption>Medium (100,000 ISINs). Times are means; memory from a fresh process.</caption><thead><tr><th>Scenario</th>${names.map(a => `<th>${a}</th>`).join("")}</tr></thead><tbody>${cols.map(({ s, q }) => {
      const unit = typeof s.unit === "function" ? s.unit(q) : s.unit, fmt = unit === "time" ? time : bytes;
      return `<tr><td>${escape(s.label)} · ${escape(q)}</td>${names.map(a => { const r = s.rows(q).find(r => r.algorithm === a); return `<td>${r ? r.label ?? fmt(r.mean) : "—"}</td>`; }).join("")}</tr>`;
    }).join("")}</tbody>`;

    // Headline ratios in chapters 05 and 06, from the same data.
    const filter = Object.fromEntries(B.search.filter(r => r.method === "Filter" && r.writes === 0 && r.size === "Medium").map(r => [r.algorithm, r.mean]));
    const gain = (naive, tuned) => filter[naive] && filter[tuned] ? `${Math.round(filter[naive] / filter[tuned]).toLocaleString("en-US")}×` : "—";
    $("lucene-gain").textContent = gain("NaiveLuceneIndex", "LuceneIndex");
    $("rocks-gain").textContent = gain("NaiveRocksDbIndex", "RocksDbIndex");
    if (B.meta) {
      $("machine-note").textContent = B.meta.machine;
      $("bench-provenance").textContent = B.meta.provenance;
    }
  }

  const linear = player("linear", linearFrames("prefix"), renderLinear);
  $("linear-kind").addEventListener("input", e => linear.replace(linearFrames(e.target.value)));
  player("concurrent", [0, 1, 2, 3], renderConcurrent);
  const rocks = player("rocks", rocksFrames("prefix"), renderRocks);
  $("rocks-kind").addEventListener("input", e => rocks.replace(rocksFrames(e.target.value)));
  player("lucene", luceneFrames, renderLucene);
  player("ngram", ngramFrames, renderNgram);
  player("rockskeys", rocksKeyFrames, renderRocksKeys);
  chart();

  if ("IntersectionObserver" in window) {
    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) if (entry.isIntersecting) document.querySelectorAll(".sidebar nav a").forEach(a => {
        const active = a.hash === `#${entry.target.id}`;
        a.classList.toggle("active", active);
        if (active) a.setAttribute("aria-current", "location"); else a.removeAttribute("aria-current");
      });
    }, { rootMargin: "-10% 0px -65% 0px", threshold: 0 });
    document.querySelectorAll(".chapter").forEach(section => observer.observe(section));
  }
  document.addEventListener("visibilitychange", () => { if (document.hidden) players.forEach(p => p.stop()); });
  window.addEventListener("beforeprint", () => players.forEach(p => { p.stop(); p.index = p.frames.length - 1; p.draw(); }));
  $("print-page").addEventListener("click", () => window.print());
})();
