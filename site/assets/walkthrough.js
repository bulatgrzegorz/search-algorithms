/* Browser-only illustrations of SortedArrayIndex.cs; no libraries or network requests. */
(() => {
  "use strict";
  const example = ["GB19", "US12", "US99"];
  // JS's relational string comparison, like .NET ordinal comparison, uses UTF-16 units.
  const ordinal = (a, b) => a < b ? -1 : a > b ? 1 : 0;
  const prefixQuery = (item, query) => ordinal(item.slice(0, query.length), query);
  function suffixOrder(a, b) {
    for (let i = 1; i <= Math.min(a.length, b.length); i++) {
      const c = a.charCodeAt(a.length - i) - b.charCodeAt(b.length - i);
      if (c) return c;
    }
    return a.length - b.length;
  }
  function suffixQuery(item, query) {
    for (let i = 1; i <= query.length; i++) {
      if (i > item.length) return -1;
      const c = item.charCodeAt(item.length - i) - query.charCodeAt(query.length - i);
      if (c) return c;
    }
    return 0;
  }
  function repeatAt(item, offset) {
    let repeat = 0;
    for (let j = 0; j < offset; j++) {
      let n = 0;
      while (offset + n < item.length && item[j + n] === item[offset + n]) n++;
      repeat = Math.max(repeat, n);
    }
    return repeat;
  }
  const substringOrder = (a, b) => ordinal(a.item.slice(a.offset), b.item.slice(b.offset)) || ordinal(a.item, b.item);
  function substringsOf(item) {
    return Array.from({ length: item.length }, (_, offset) => ({ item, offset, repeat: repeatAt(item, offset) }));
  }
  function entriesFor(items, kind) {
    if (kind === "contains") return items.flatMap(substringsOf).sort(substringOrder);
    return [...items].sort(kind === "suffix" ? suffixOrder : ordinal);
  }
  const queryComparer = kind => kind === "suffix" ? suffixQuery : kind === "contains" ? (entry, q) => prefixQuery(entry.item.slice(entry.offset), q) : prefixQuery;

  // Inclusive high matches the middle-position rule of Span.BinarySearch.
  // The second boundary searches the suffix starting at the first boundary.
  function findBlock(sorted, query, compare, frames) {
    function boundary(offset, past) {
      let low = offset, high = sorted.length - 1;
      while (low <= high) {
        const mid = low + Math.floor((high - low) / 2);
        const c = compare(sorted[mid], query);
        const moveRight = c < 0 || (past && c === 0);
        if (frames) frames.push({ type: "probe", past, low, high, mid, c, moveRight, start: offset });
        if (moveRight) low = mid + 1;
        else high = mid - 1;
      }
      return low;
    }
    const start = boundary(0, false);
    if (frames) frames.push({ type: "start", start });
    const end = boundary(start, true);
    return { start, end };
  }

  function searchTrace(items, kind, query) {
    const effectiveKind = kind === "contains" && !query.length ? "prefix" : kind;
    const entries = entriesFor(items, effectiveKind);
    const frames = [{ type: "intro" }];
    const block = findBlock(entries, query, queryComparer(effectiveKind), frames);
    frames.push({ type: "block", ...block });
    const hits = entries.slice(block.start, block.end)
      .filter(entry => effectiveKind !== "contains" || query.length > entry.repeat)
      .map(entry => effectiveKind === "contains" ? entry.item : entry);
    frames.push({ type: "result", ...block, hits });
    return { entries, frames, block, hits, kind: effectiveKind };
  }

  function binaryPosition(sorted, value, compare) {
    let low = 0, high = sorted.length - 1;
    while (low <= high) {
      const mid = low + Math.floor((high - low) / 2), c = compare(value, sorted[mid]);
      if (c === 0) return mid;
      if (c > 0) low = mid + 1;
      else high = mid - 1;
    }
    return ~low;
  }
  function mergePlan(sorted, removed, added, compare) {
    const removedAt = removed.map(item => binaryPosition(sorted, item, compare)).sort((a, b) => a - b);
    const addedAt = added.map(item => ({ at: ~binaryPosition(sorted, item, compare), item }));
    return { removedAt, addedAt, count: sorted.length - removed.length + added.length };
  }
  function copyMerged(sorted, plan) {
    const result = [], events = [];
    let from = 0, r = 0, a = 0;
    while (r < plan.removedAt.length || a < plan.addedAt.length) {
      const insert = a < plan.addedAt.length && (r === plan.removedAt.length || plan.addedAt[a].at <= plan.removedAt[r]);
      const at = insert ? plan.addedAt[a].at : plan.removedAt[r];
      const stretch = sorted.slice(from, at);
      result.push(...stretch);
      if (stretch.length) events.push({ type: "copy", from, at, result: [...result] });
      from = at;
      if (insert) {
        const item = plan.addedAt[a++].item;
        result.push(item);
        events.push({ type: "insert", at, item, result: [...result] });
      } else {
        from++; r++;
        events.push({ type: "remove", at, item: sorted[at], result: [...result] });
      }
    }
    result.push(...sorted.slice(from));
    if (from < sorted.length) events.push({ type: "tail", from, result: [...result] });
    return { result, events };
  }

  function newState(items = example) {
    return { base: [...new Set(items)].sort(ordinal), added: [], removed: [], items: new Set(items) };
  }
  function writeState(state, action, item) {
    const present = state.items.has(item);
    if ((action === "add") === present) return { state, reason: "noop" };
    const items = new Set(state.items), added = new Set(state.added), removed = new Set(state.removed);
    let reason;
    if (action === "add") {
      items.add(item);
      if (removed.delete(item)) reason = "cancel-remove";
      else { added.add(item); reason = "added"; }
    } else {
      items.delete(item);
      if (added.delete(item)) reason = "cancel-add";
      else { removed.add(item); reason = "removed"; }
    }
    const threshold = Math.max(256, Math.floor(items.size / 128));
    if (added.size + removed.size >= threshold) return { state: newState([...items]), reason: "merged" };
    return { state: { base: state.base, added: [...added].sort(ordinal), removed: [...removed].sort(ordinal), items }, reason };
  }

  function searchState(state, kind, query) {
    if (kind === "contains" && query.length) {
      const entries = entriesFor(state.base, "contains");
      const { start, end } = findBlock(entries, query, queryComparer("contains"));
      const block = entries.slice(start, end);
      const removedAt = state.removed.map(item => {
        const offset = item.indexOf(query);
        return offset < 0 ? -1 : binaryPosition(block, { item, offset }, substringOrder);
      }).filter(at => at >= 0).sort((a, b) => a - b);
      let r = 0;
      const hits = [];
      block.forEach((entry, i) => {
        if (r < removedAt.length && removedAt[r] === i) r++;
        else if (query.length > entry.repeat) hits.push(entry.item);
      });
      hits.push(...state.added.filter(item => item.includes(query)));
      return { hits, baseBlock: block, removedAt, addedBlock: state.added.filter(item => item.includes(query)), materialized: true };
    }
    const effectiveKind = kind === "contains" ? "prefix" : kind;
    const compare = effectiveKind === "suffix" ? suffixOrder : ordinal;
    const blockOf = items => {
      const sorted = entriesFor(items, effectiveKind);
      const { start, end } = findBlock(sorted, query, queryComparer(effectiveKind));
      return sorted.slice(start, end);
    };
    const baseBlock = blockOf(state.base), removedBlock = blockOf(state.removed), addedBlock = blockOf(state.added);
    const plan = mergePlan(baseBlock, removedBlock, addedBlock, compare);
    const hits = copyMerged(baseBlock, plan).result;
    return { hits, baseBlock, removedBlock, addedBlock, plan, materialized: false, direct: !removedBlock.length && !addedBlock.length };
  }

  // Expose the illustration logic to the dependency-free verification script in Node.
  if (typeof module !== "undefined" && module.exports) module.exports = { ordinal, suffixOrder, suffixQuery, prefixQuery, repeatAt, substringOrder, substringsOf, entriesFor, findBlock, searchTrace, mergePlan, copyMerged, newState, writeState, searchState };
  if (typeof document === "undefined") return;

  const $ = id => document.getElementById(id);
  const escape = value => String(value).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const text = value => escape(value === "" ? '""' : value);
  const code = value => `<code>${text(value)}</code>`;
  const quoted = value => code(JSON.stringify(value));
  const list = values => code(`[${values.map(x => x === "" ? '""' : x).join(", ")}]`);
  const pills = values => values.length ? values.map(x => `<span class="result-pill">${text(x)}</span>`).join(" ") : '<span class="empty-value">[] · no matching strings</span>';
  const row = (label, values, cls = "") => `<p class="view-label">${escape(label)}</p><div class="array">${values.length ? values.map((x, i) => cell(x, i, cls)).join("") : '<span class="empty-value">[]</span>'}</div>`;
  function cell(value, index, cls = "") {
    const isEntry = typeof value === "object";
    return `<div class="cell ${cls}"><small>${index}</small>${text(isEntry ? value.item.slice(value.offset) : value)}${isEntry ? `<span class="item-label">${text(value.item)} @ ${value.offset}</span><span class="item-label">Repeat ${value.repeat}</span>` : ""}</div>`;
  }
  function copyText(title, body) { return `<h4>${title}</h4><p>${body}</p>`; }

  const players = new Map();
  function player(name, frames, render) {
    const controls = document.querySelector(`[data-player="${name}"]`);
    const state = { frames, index: 0, timer: null, draw: null };
    function stop() {
      clearInterval(state.timer); state.timer = null;
      controls.querySelector('[data-action="play"]').textContent = "Play";
    }
    function draw() {
      render(state.frames[state.index]);
      controls.querySelector(".step-count").textContent = `${state.index + 1} / ${state.frames.length}`;
      controls.querySelector('[data-action="prev"]').disabled = state.index === 0;
      controls.querySelector('[data-action="next"]').disabled = state.index === state.frames.length - 1;
    }
    state.draw = draw;
    state.stop = stop;
    state.replace = newFrames => { stop(); state.frames = newFrames; state.index = 0; draw(); };
    controls.addEventListener("click", event => {
      const action = event.target.closest("button")?.dataset.action;
      if (!action) return;
      if (action === "play") {
        if (state.timer) { stop(); return; }
        if (state.index === state.frames.length - 1) state.index = 0;
        controls.querySelector('[data-action="play"]').textContent = "Pause";
        draw();
        state.timer = setInterval(() => {
          state.index = Math.min(state.index + 1, state.frames.length - 1);
          draw();
          if (state.index === state.frames.length - 1) stop();
        }, 1900);
      } else {
        stop();
        state.index = action === "reset" ? 0 : Math.max(0, Math.min(state.frames.length - 1, state.index + (action === "next" ? 1 : -1)));
        draw();
      }
    });
    players.set(name, state); draw(); return state;
  }

  const buildEntries = example.flatMap(substringsOf);
  const sortedEntries = entriesFor(example, "contains");
  const bucketChars = [...new Set(buildEntries.map(x => x.item[x.offset]))].sort(ordinal);
  function buckets(sorted) {
    let start = 0;
    return '<div class="bucket-grid">' + bucketChars.map(c => {
      const items = buildEntries.filter(e => e.item[e.offset] === c);
      if (sorted) items.sort(substringOrder);
      const at = start; start += items.length;
      return `<div class="bucket"><strong>'${text(c)}' <span class="origin">[${at}, ${start})</span></strong>${items.map(e => `<p>${text(e.item.slice(e.offset))} <span class="origin">${text(e.item)} @ ${e.offset}</span></p>`).join("")}</div>`;
    }).join("") + "</div>";
  }
  player("build", [0, 1, 2, 3, 4, 5], step => {
    const canvas = $("build-canvas"), copy = $("build-copy");
    const content = [
      [row("Unique original strings", example), "Start with three original strings", "The constructor has removed duplicates. Build copies the three references into a prefix array and a suffix array. Independently, SortedSubstrings will describe all starting positions."],
      [example.map(item => row(`Starting positions inside ${item}`, substringsOf(item))).join(""), "Describe the text starting at every character", `${code("GB19")} contributes ${code('"GB19", "B19", "19", "9"')}. ${code("US12")} and ${code("US99")} contribute four entries each too. Twelve entries total; their text is shown here for reading, but the code stores the item’s number and an offset: once US99 is number 2 in ByPrefix, its entry from offset 2 is ${code("(2, 2)")}. Repeat is calculated later.`],
      [buckets(false), "Count first characters and reserve bucket ranges", `There are two entries beginning with '1', one with '2', three with '9', and six beginning with letters. Cumulative counts reserve ${code("[0, 2)")} for '1', ${code("[2, 3)")} for '2', and ${code("[3, 6)")} for '9'. Each bucket now has exactly enough slots.`],
      [buckets(false), "Place each entry into its reserved bucket", `A separate ${code("next")} cursor starts at each bucket’s beginning. The first '9' entry goes into slot 3, then the next into 4, then 5. Entries in a bucket are not yet ordered by their full text; "99" can precede "9" at this stage.`],
      [buckets(true), "Sort inside buckets, in parallel", `Inside the '9' bucket, ${code('"9" from GB19')} comes first, then ${code('"9" from US99')}, then ${code('"99" from US99')}. The original item breaks equal-text ties. Bucket '1' becomes "12", "19". Buckets with one entry need no sorting.`],
      [row("Final sorted entries, including calculated Repeat", sortedEntries), "Fill Repeat, then publish the built views", `RepeatAt finds that ${code("US99 @ 3")} repeats one character from an earlier position, so its Repeat is 1. The other eleven entries have Repeat 0. Each entry’s item number is then set to its string’s place in the sorted ByPrefix (GB19 0, US12 1, US99 2), and Build returns the three arrays together as one Snapshot; the initial Added and Removed are empty.`]
    ][step];
    canvas.innerHTML = content[0]; copy.innerHTML = copyText(content[1], content[2]);
    if (step === 1) canvas.querySelectorAll(".item-label:last-child").forEach(el => { el.textContent = "Repeat: not filled yet"; });
  });

  const datasets = { source: example, larger: ["CA10", "DE44", "FR77", "GB19", "GB90", "JP22", "US12", "US55", "US99", "ZA88"], repeated: ["", "A", "AAA", "AAAA", "BAAA", "US99"], empty: [] };
  let currentSearch;
  function characterComparison(entry, query, kind) {
    const item = kind === "contains" ? entry.item : entry;
    const offset = kind === "contains" ? entry.offset : 0;
    const steps = [];
    const unitLabel = unit => unit === undefined ? "end" : /[\ud800-\udfff]/.test(unit) ? `\\u${unit.charCodeAt(0).toString(16).toUpperCase()}` : unit;
    for (let i = 0; i < query.length; i++) {
      const at = kind === "suffix" ? item.length - 1 - i : offset + i;
      const qAt = kind === "suffix" ? query.length - 1 - i : i;
      const a = item[at], b = query[qAt], same = a === b;
      steps.push(`<div class="compare-step ${same ? "same" : "different"}"><small>check ${i + 1}</small><div><strong>${text(unitLabel(a))}</strong><span>${a === undefined ? "&lt;" : same ? "=" : ordinal(a, b) < 0 ? "&lt;" : "&gt;"}</span><strong>${text(unitLabel(b))}</strong></div><small>${a === undefined ? "item ended" : `item @ ${at}`} · query @ ${qAt}</small></div>`);
      if (!same) break;
    }
    return `<div class="comparison-panel"><p class="view-label">${kind === "suffix" ? "Read from the right: last character first" : kind === "contains" ? `Read original ${quoted(item)} from offset ${offset}` : "Read from the left: first character first"}</p><div class="comparison-steps">${steps.join("") || '<span class="empty-value">Empty query: no character checks needed.</span>'}</div><p class="caption">${steps.length && queryComparer(kind)(entry, query) !== 0 ? "Stop at the first difference, or when the item runs out. Later characters cannot change this comparison." : "The whole query matched. Extra characters in the item do not affect whether it matches."}</p></div>`;
  }
  function renderSearch(frame) {
    const { entries, kind } = currentSearch, query = $("search-query").value;
    let title, body;
    let canvas = `<p class="phase-label">${frame.type === "probe" ? (frame.past ? "SEARCH 2 · END BOUNDARY" : "SEARCH 1 · START BOUNDARY") : frame.type === "result" ? "FINAL RESULT" : "THE SORTED VIEW"}</p>`;
    canvas += `<div class="array">${entries.length ? entries.map((entry, i) => {
      let cls = "";
      if (frame.type === "probe") cls = i === frame.mid ? "probe" : i < frame.low || i > frame.high ? "muted" : "";
      if (["block", "result"].includes(frame.type)) cls = i >= frame.start && i < frame.end ? "hit" : "muted";
      if (frame.type === "result" && kind === "contains" && i >= frame.start && i < frame.end && query.length <= entry.repeat) cls = "removed";
      return cell(entry, i, cls);
    }).join("") : '<span class="empty-value">[] · the view has no entries</span>'}</div>`;
    if (frame.type === "intro") {
      title = "Begin with the arrangement for this question";
      body = kind === "contains" ? `We are searching for ${quoted(query)} inside the original strings. Each cell shows the text from one starting position. We will find the entries whose text starts with ${quoted(query)}, then select one occurrence per string.` : `We are searching for ${quoted(query)}. ${kind === "suffix" ? "This view reads characters from the right; all strings ending with the query are neighbors." : "This view uses ordinary ordinal order; all strings starting with the query are neighbors."} First locate the gap before the matching group, then the gap after it.`;
      if ($("search-kind").value === "contains" && !query.length) body = `${code('Filter("")')} delegates to ${code('GetByStartsWith("")')}. All strings match, including an empty string. We search the original strings instead of starting-position entries.`;
      if ($("search-dataset").value === "empty") body += " This demonstrates the boundary helper on an empty view. The current constructor throws if its initial input has no characters; see the limitation in chapter 02.";
    } else if (frame.type === "probe") {
      const entry = entries[frame.mid], key = kind === "contains" ? entry.item.slice(entry.offset) : entry;
      const relation = frame.c < 0 ? "comes before the matching group" : frame.c > 0 ? "comes after the matching group" : "matches the query";
      title = `Check middle position ${frame.mid}: ${JSON.stringify(key)}`;
      // Titles also include editable input, so escape before injecting below.
      title = escape(title);
      body = `Current possible positions are ${code(`${frame.low}…${frame.high}`)}. ${quoted(key)} ${relation}. ${frame.moveRight ? `The ${frame.past ? "end" : "start"} boundary belongs to its right, so continue at position ${frame.mid + 1} or later.` : `The ${frame.past ? "end" : "start"} boundary belongs at or before this item, so continue to its left.`} ${frame.c === 0 ? (frame.past ? "For the end, a match sends us right: more matches may follow." : "For the start, a match sends us left: earlier matches may exist.") : "A whole half of the current range is now eliminated."}`;
      canvas += `<div class="metric-row"><span class="metric">low = ${frame.low}</span><span class="metric">high = ${frame.high}</span><span class="metric">middle = ${frame.mid}</span><span class="metric">item vs query: ${frame.c === 0 ? "0 (match)" : frame.c < 0 ? "&lt; 0" : "&gt; 0"}</span></div>`;
      canvas += characterComparison(entry, query, kind);
    } else if (frame.type === "start") {
      title = `The start boundary is gap ${frame.start}`;
      body = `No candidates remain in the first binary search. Its insertion position is ${frame.start}; ${code(`BinarySearch → ${~frame.start}`)} and ${code(`~(${~frame.start}) → ${frame.start}`)}. Now search only the slice starting at ${frame.start} for the end boundary. A start is still meaningful when there are no matches.`;
      canvas += `<p class="result-pill">start = ${frame.start}</p>`;
    } else if (frame.type === "block") {
      title = `The matching block is [${frame.start}, ${frame.end})`;
      body = `The end search finds local insertion position ${frame.end - frame.start} in the slice starting at ${frame.start}. Add them: ${code(`${frame.start} + ${frame.end - frame.start} = ${frame.end}`)}. The block contains ${frame.end - frame.start} ${kind === "contains" ? "starting-position entries" : "strings"}. ${frame.start === frame.end ? "The boundaries meet, so the result is empty." : "The end is excluded."}`;
      canvas += `<div class="metric-row"><span class="metric">start = ${frame.start}</span><span class="metric">end = ${frame.end}</span><span class="metric">count = ${frame.end - frame.start}</span></div>`;
    } else {
      title = `${frame.hits.length} unique ${frame.hits.length === 1 ? "string" : "strings"} returned`;
      body = kind === "contains" ? `Walk the block and keep entries where ${code(`${query.length} > Repeat`)}. A struck-through entry matches but repeats an earlier occurrence in its own string. Return each eligible original string once. Filter itself returns before this walk; it runs as the caller reads hits. There are no pending writes in this demo.` : `With no matching pending changes, return an ArraySegment covering this block. Its Count is ${frame.hits.length}; returning the window does not copy its entries.`;
      canvas += `<div class="metric-row">${pills(frame.hits)}</div>`;
    }
    $("search-canvas").innerHTML = canvas;
    $("search-copy").innerHTML = copyText(title, body);
  }
  function updateSearch() {
    currentSearch = searchTrace(datasets[$("search-dataset").value], $("search-kind").value, $("search-query").value);
    players.get("search")?.replace(currentSearch.frames);
  }
  updateSearch(); player("search", currentSearch.frames, renderSearch);
  ["search-kind", "search-query", "search-dataset"].forEach(id => $(id).addEventListener("input", updateSearch));
  document.querySelectorAll("[data-search-preset]").forEach(button => button.addEventListener("click", () => {
    const [kind, query] = button.dataset.searchPreset.split(":");
    $("search-kind").value = kind; $("search-query").value = query; updateSearch();
  }));

  function renderRepeat() {
    const item = $("repeat-item").value, query = $("repeat-query").value;
    if (!query.length) {
      $("repeat-canvas").innerHTML = `<p>${code('Filter("")')} takes the prefix path, returning the original string ${quoted(item)} once, even if it is empty. No Repeat test is needed.</p>`;
      return;
    }
    const entries = substringsOf(item);
    $("repeat-canvas").innerHTML = `<p class="caption">Offsets below are in the original string’s order so you can follow earlier occurrences. The actual search walks the matching entries in sorted text order.</p><div class="table-scroll"><table class="repeat-table"><caption>Original ${quoted(item)} · fragment ${quoted(query)} · length ${query.length}</caption><thead><tr><th>Offset</th><th>Text from here</th><th>Repeat calculation</th><th>Decision for ${quoted(query)}</th></tr></thead><tbody>${entries.map(entry => {
      const suffix = item.slice(entry.offset), matches = suffix.startsWith(query), keep = matches && query.length > entry.repeat;
      const comparisons = [];
      for (let j = 0; j < entry.offset; j++) {
        let n = 0;
        while (entry.offset + n < item.length && item[j + n] === item[entry.offset + n]) n++;
        comparisons.push(`${quoted(item.slice(j))} @ ${j}: shares ${quoted(item.slice(entry.offset, entry.offset + n))} (${n})`);
      }
      return `<tr class="${keep ? "match-row" : ""}"><td>${entry.offset}</td><td>${quoted(suffix)}</td><td>${comparisons.length ? comparisons.join("<br>") : "No earlier position"}<br><strong>Repeat = ${entry.repeat}</strong></td><td>${!matches ? "Does not start with fragment" : keep ? `${query.length} &gt; ${entry.repeat} → <strong>keep this occurrence</strong>` : `${query.length} ≤ ${entry.repeat} → duplicate; skip`}</td></tr>`;
    }).join("") || '<tr><td colspan="4">An empty string has no entries and cannot contain a nonempty fragment.</td></tr>'}</tbody></table></div><p>${item.includes(query) ? `The result includes ${quoted(item)} <strong>once</strong>, represented by offset ${item.indexOf(query)}.` : `${quoted(item)} does not contain ${quoted(query)}, so there is no result.`}</p>`;
  }
  ["repeat-item", "repeat-query"].forEach(id => $(id).addEventListener("input", renderRepeat));
  document.querySelectorAll("[data-repeat-preset]").forEach(button => button.addEventListener("click", () => {
    const [item, query] = button.dataset.repeatPreset.split(":"); $("repeat-item").value = item; $("repeat-query").value = query; renderRepeat();
  })); renderRepeat();

  let mutation = newState();
  function renderMutation() {
    const threshold = Math.max(256, Math.floor(mutation.items.size / 128));
    const pending = mutation.added.length + mutation.removed.length;
    $("mutation-state").innerHTML = `<div class="state-diagram"><div><span class="view-label">BASE · unchanged arrays</span><strong>${list(mutation.base)}</strong></div><span>+</span><div class="added-box"><span class="view-label">ADDED</span><strong>${list(mutation.added)}</strong></div><span>−</span><div class="removed-box"><span class="view-label">REMOVED</span><strong>${list(mutation.removed)}</strong></div></div><div class="metric-row"><span class="metric">_items.Count = ${mutation.items.size}</span><span class="metric">pending = ${pending}</span><span class="metric">merge threshold = ${threshold}</span></div><div class="progress-track" aria-label="${pending} of ${threshold} pending items before merge"><span style="width:${100 * pending / threshold}%"></span></div>`;
    $("mutation-orders").innerHTML = `<p>Added.ByPrefix: ${list(mutation.added)}<br>Added.BySuffix: ${list([...mutation.added].sort(suffixOrder))}</p><p>Removed.ByPrefix: ${list(mutation.removed)}<br>Removed.BySuffix: ${list([...mutation.removed].sort(suffixOrder))}</p>`;
    renderMutationSearch();
  }
  function renderMutationSearch() {
    const kind = $("mutation-kind").value, query = $("mutation-query").value;
    const result = searchState(mutation, kind, query);
    let explanation;
    if (result.materialized) {
      explanation = `<div>Base block: ${result.baseBlock.length ? result.baseBlock.map(e => `${quoted(e.item.slice(e.offset))} from ${code(`${e.item} @ ${e.offset}`)} (Repeat ${e.repeat})`).join("; ") : "[]"}.</div><div>Removed representatives at block-relative positions: ${list(result.removedAt)}. Skip those and entries where ${query.length} ≤ Repeat.</div><div>Added strings matching Contains(${quoted(query)}): ${list(result.addedBlock)}.</div><div>Count = ${result.baseBlock.filter(e => query.length > e.repeat).length} kept − ${result.removedAt.length} removed + ${result.addedBlock.length} added = ${result.hits.length}, from Repeat values alone. Filter walks the block only when a hit is read; this display reads them all.</div>`;
    } else {
      explanation = `<div>Base block: ${list(result.baseBlock)}.</div><div>Removed matching block: ${list(result.removedBlock)}. Added matching block: ${list(result.addedBlock)}.</div><div>${result.direct ? "Neither delta affects this query. Return the Base ArraySegment directly." : `MergeSorted records removed positions ${list(result.plan.removedAt)} and added positions ${list(result.plan.addedAt.map(e => `${e.item} before ${e.at}`))}, all relative to this Base block. Count = ${result.baseBlock.length} − ${result.removedBlock.length} + ${result.addedBlock.length} = ${result.hits.length}. This display reads the values, so it materializes the result.`}</div>`;
      if (kind === "contains") explanation = `<div>Empty fragment: delegate to GetByStartsWith("").</div>${explanation}`;
    }
    $("mutation-results").innerHTML = `<div class="mutation-query-result"><p class="view-label">${escape(kind === "prefix" ? "StartsWith" : kind === "suffix" ? "EndsWith" : "Filter")}(${quoted(query)})</p><div class="inline-steps">${explanation}</div><div class="metric-row">${pills(result.hits)}</div></div>`;
  }
  function mutate(action, item) {
    const outcome = writeState(mutation, action, item);
    if (outcome.state.added.length + outcome.state.removed.length > 32) {
      $("mutation-message").textContent = "This diagram is limited to 32 pending strings. Reset the example to try a new sequence. The C# implementation uses its normal merge threshold."; return;
    }
    mutation = outcome.state;
    const messages = {
      noop: `${quoted(item)} is ${action === "add" ? "already present" : "already absent"}. The _items set makes this a no-op; nothing is published.`,
      "cancel-remove": `Cancel the pending removal of ${quoted(item)}. Base already has it, so remove it from Removed.`,
      "cancel-add": `Cancel the pending addition of ${quoted(item)}. Base never had it, so remove it from Added.`,
      added: `Put ${quoted(item)} in Added, in both sorted orders. Publish a new State. Base stays unchanged.`,
      removed: `Put ${quoted(item)} in Removed. Base still has it, but the newly published State hides it from searches.`,
      merged: "The pending count reached the threshold. Merge into new Base arrays, clear both deltas, and publish."
    };
    $("mutation-message").innerHTML = messages[outcome.reason]; renderMutation();
  }
  $("mutation-add").addEventListener("click", () => mutate("add", $("mutation-item").value));
  $("mutation-remove").addEventListener("click", () => mutate("remove", $("mutation-item").value));
  $("mutation-reset").addEventListener("click", () => { mutation = newState(); $("mutation-message").textContent = "Reset: three base strings, no pending changes."; renderMutation(); });
  document.querySelectorAll("[data-write]").forEach(button => button.addEventListener("click", () => {
    const [action, item] = button.dataset.write.split(":"); $("mutation-item").value = item; mutate(action, item);
  }));
  ["mutation-kind", "mutation-query"].forEach(id => $(id).addEventListener("input", renderMutationSearch));
  $("mutation-message").textContent = "Start with the original three strings. Try adding FR77, then removing it again."; renderMutation();

  const mergeBase = example, mergePositions = mergePlan(example, ["US12"], ["US55"], ordinal);
  const mergeCopy = copyMerged(mergeBase, mergePositions);
  const mergeFrames = [{ type: "plan", result: [] }, { type: "count", result: [] }, ...mergeCopy.events, { type: "done", result: mergeCopy.result }];
  player("merge", mergeFrames, frame => {
    let canvas = `<p class="view-label">Original Base positions · these never shift</p><div class="array">${mergeBase.map((item, i) => cell(item, i, i === 1 ? "removed" : "")).join("")}</div><div class="metric-row"><span class="metric">remove US12 at 1</span><span class="metric">insert US55 before position 2</span></div>`;
    canvas += row("New result array", frame.result, "hit");
    const descriptions = {
      plan: ["Binary-search the changes, keep a plan", `Locate ${code("US12")} at position 1. Search for ${code("US55")}: it is absent and would be inserted at gap 2. Store ${code("removedAt = [1]")} and ${code("addedAt = [(2, US55)]")}. No combined array is allocated yet.`],
      count: ["Count is already known: 3 − 1 + 1 = 3", "A caller reading only Count stops here. To show the values, the next step calls ToArray, allocates a three-slot array, and starts Copy. For a query-time merge, all positions would be relative to the matching Base window."],
      copy: ["Copy the unchanged stretch before the next event", `The first event is removal at 1. Copy original positions ${code("[0, 1)")} — just GB19 — into output position 0. Now from = 1 and to = 1.`],
      remove: ["Skip US12 at original position 1", "Advance from from 1 to 2 without writing an output value. The next unread Base item is US99. The input array has not been edited; US55’s original insertion gap is still 2."],
      insert: ["Insert US55 before original position 2", "There are no unchanged entries between from = 2 and insertion gap 2. Write US55 into output position 1. Keep from = 2 so US99 will still be copied."],
      tail: ["Copy the remaining tail: US99", "There are no more change events. Copy the original array from position 2 onward into the remaining output slot. The result is [GB19, US55, US99]."],
      done: ["The new array is complete; the old one still exists", "For a query result, Merged caches this array for subsequent reads. During a full Base merge, Merge does this for prefix, suffix, and substring arrays, then publishes a new State with empty deltas. The full merge is separate from the threshold decision that triggers it."]
    };
    $("merge-canvas").innerHTML = canvas; $("merge-copy").innerHTML = copyText(...descriptions[frame.type]);
  });

  player("reader", [0, 1, 2, 3, 4], step => {
    const current = step >= 2 ? "State B" : "State A";
    let canvas = `<p class="phase-label">_state → ${current}</p><div class="timeline-grid"><div class="timeline-card older"><h4>State A · earlier view</h4><p>Base: ${list(example)}</p><p>Added: [] · Removed: []</p><p><strong>Reader A:</strong> ${step >= 1 ? 'holds this State → [US12, US99]' : 'has not started yet'}</p></div><div class="timeline-card ${step >= 2 ? "newer" : "older"}"><h4>State B · ${step >= 2 ? "published" : "not yet published"}</h4><p>Base: ${list(example)}</p><p>Added: [] · Removed: ${step >= 2 ? "[US12]" : "[]"}</p><p><strong>Reader B:</strong> ${step >= 3 ? 'reads this State → [US99]' : 'has not started yet'}</p></div></div>`;
    if (step === 4) canvas += '<p class="caption">A later threshold merge can replace Base with [GB19, US99]. Reader A’s original result still references the old array.</p>';
    const descriptions = [
      ["Begin with one published State", "_state points at State A. Base contains GB19, US12, US99. No pending changes exist."],
      ["Reader A searches before the removal", 'GetByStartsWith("US") takes a single Volatile.Read of State A. It returns a window containing US12 and US99. Reader A may enumerate it later.'],
      ["The writer removes US12 and publishes State B", 'Inside _writeLock, Remove changes _items, creates a new Removed delta [US12], and publishes a whole new State reference through Volatile.Write. Remove then returns. State A stays unchanged.'],
      ["Reader B searches after Remove has returned", 'Its Volatile.Read gets State B. The same Base block is now overlaid with Removed [US12]. Reader B gets [US99]. Reader A still has [US12, US99]. Both results describe their respective search views.'],
      ["Readers may finish at different times", "Reader A can finish after Reader B and still return its earlier result. A later Base merge does not invalidate that result either: published arrays are never edited by the implementation. Writers wait for each other; searches do not take the writer lock."]
    ];
    $("reader-canvas").innerHTML = canvas; $("reader-copy").innerHTML = copyText(...descriptions[step]);
  });

  if ("IntersectionObserver" in window) {
    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) if (entry.isIntersecting) {
        document.querySelectorAll(".sidebar nav a").forEach(a => {
          const active = a.hash === `#${entry.target.id}`;
          a.classList.toggle("active", active);
          if (active) a.setAttribute("aria-current", "location"); else a.removeAttribute("aria-current");
        });
      }
    }, { rootMargin: "-10% 0px -65% 0px", threshold: 0 });
    document.querySelectorAll(".chapter").forEach(section => observer.observe(section));
  }
  document.addEventListener("visibilitychange", () => { if (document.hidden) players.forEach(p => p.stop()); });
  const printPositions = new Map(), printDetails = new Map();
  window.addEventListener("beforeprint", () => {
    players.forEach((p, name) => { p.stop(); printPositions.set(name, p.index); p.index = p.frames.length - 1; p.draw(); });
    document.querySelectorAll("details").forEach(d => { printDetails.set(d, d.open); d.open = true; });
  });
  window.addEventListener("afterprint", () => {
    players.forEach((p, name) => { p.index = printPositions.get(name) ?? 0; p.draw(); });
    printDetails.forEach((open, d) => { d.open = open; });
  });
  $("print-page").addEventListener("click", () => window.print());
})();
