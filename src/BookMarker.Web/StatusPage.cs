namespace BookMarker.Web;

/// <summary>
/// Read-only status page served at "/". Shows free/busy state and live processing details
/// (current job progress + recent conversions). Polls GET /api/status - no actions here.
/// </summary>
public static class StatusPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>BookMarker — status</title>
<style>
:root { color-scheme: dark; }
* { box-sizing: border-box; }
body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; background: #12141c; color: #e7e9ee; margin: 0; padding: 2rem 1rem; }
.wrap { max-width: 1080px; margin: 0 auto; }
header { display: flex; align-items: baseline; gap: 1rem; margin-bottom: 1.2rem; flex-wrap: wrap; }
h1 { font-size: 1.45rem; margin: 0; letter-spacing: 0.02em; color: #dbe3ff; }
.sub { color: #8b93a7; font-size: 0.85rem; }
.spacer { flex: 1; }
.pill { padding: 0.3rem 1rem; border-radius: 999px; font-weight: 700; font-size: 0.85rem; letter-spacing: 0.08em; }
.pill.free { background: #14301f; color: #4ade80; border: 1px solid #2c5e45; }
.pill.busy { background: #33280f; color: #fbbf24; border: 1px solid #6b5426; }
.pill.unknown { background: #262c3d; color: #8b93a7; border: 1px solid #3a4258; }
.card { background: #1a1e2b; border: 1px solid #262c3d; border-radius: 10px; padding: 1rem 1.2rem; margin-bottom: 1rem; }
.card.live { border-color: #33406b; }
.card h2 { font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.1em; color: #8b93a7; margin: 0 0 0.7rem; }
.grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 0.6rem 1.5rem; }
.kv .k { color: #8b93a7; font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.06em; }
.kv .v { font-size: 0.95rem; margin-top: 0.1rem; }
table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
th, td { text-align: left; padding: 0.35rem 0.55rem; border-bottom: 1px solid #262c3d; white-space: nowrap; }
th { color: #8b93a7; font-weight: 600; font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.06em; }
tbody tr:last-child td { border-bottom: none; }
.mono { font-family: ui-monospace, "Cascadia Code", Consolas, monospace; font-size: 0.8rem; }
.log { background: #0d1017; border: 1px solid #262c3d; border-radius: 8px; padding: 0.6rem 0.8rem; font-family: ui-monospace, Consolas, monospace; font-size: 0.75rem; line-height: 1.55; max-height: 230px; overflow-y: auto; white-space: pre-wrap; word-break: break-all; color: #9aa3b7; margin-top: 0.8rem; }
.ok { color: #4ade80; } .bad { color: #f87171; } .warn { color: #fbbf24; } .dim { color: #5d6478; }
footer { color: #5d6478; font-size: 0.75rem; margin-top: 1.2rem; }
</style>
</head>
<body>
<div class="wrap">
  <header>
    <h1>BookMarker</h1>
    <span class="sub" id="version"></span>
    <span class="spacer"></span>
    <span class="pill unknown" id="state">…</span>
  </header>

  <div class="card live" id="currentCard" hidden>
    <h2>Current conversion</h2>
    <div class="grid" id="currentGrid"></div>
    <div class="log" id="currentLog"></div>
  </div>

  <div class="card">
    <h2>Recent conversions</h2>
    <table>
      <thead><tr><th>when</th><th>file</th><th>llm</th><th>pages</th><th>wall</th><th>s/page</th><th>result</th><th>state</th><th>trace</th></tr></thead>
      <tbody id="recentBody"></tbody>
    </table>
    <div class="sub" id="recentEmpty">nothing processed yet</div>
  </div>

  <div class="card">
    <h2>Service</h2>
    <div class="grid" id="configGrid"></div>
  </div>

  <footer>read-only status page — auto-refreshes every 2s. Uploaded files are deleted when a conversion ends (success or failure); the service retains nothing.</footer>
</div>
<script>
(function () {
  "use strict";

  const MODE_LABEL = { noLlm: "nollm", llmOnly: "llmonly", llmFallback: "llmfallback" };

  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const fmtWall = (s) => s == null ? "—" : (s < 90 ? s.toFixed(1) + "s" : Math.floor(s / 60) + "m " + Math.round(s % 60) + "s");
  const fmtBytes = (b) => b == null ? "—" : (b >= 1048576 ? (b / 1048576).toFixed(1) + " MiB" : (b / 1024).toFixed(0) + " KiB");
  const fmtChars = (c) => c == null ? "—" : (c >= 1024 ? (c / 1024).toFixed(1) + "k chars" : c + " chars");
  const fmtTime = (iso) => iso ? new Date(iso).toLocaleString() : "—";
  const kv = (k, v) => '<div class="kv"><div class="k">' + esc(k) + '</div><div class="v">' + v + "</div></div>";

  function llmCell(job) {
    let out = esc(MODE_LABEL[job.llmMode] ?? job.llmMode ?? "nollm");
    if (job.llmFellBack) out += ' <span class="warn">(fell back)</span>';
    else if (job.llmUsed) out += ' <span class="dim">· llm</span>';
    return out;
  }

  function render(s) {
    document.getElementById("version").textContent = (s.service ?? "BookMarker") + " " + (s.version ?? "");

    const state = document.getElementById("state");
    const busy = s.state === "busy";
    state.textContent = busy ? "BUSY" : "FREE";
    state.className = "pill " + (busy ? "busy" : "free");

    // current job
    const card = document.getElementById("currentCard");
    const grid = document.getElementById("currentGrid");
    const logEl = document.getElementById("currentLog");
    if (s.current) {
      const j = s.current;
      card.hidden = false;
      const progress = (j.pagesDone != null && j.pagesTotal != null) ? (j.pagesDone + " / " + j.pagesTotal) : (j.pages != null && j.pages > 0 ? String(j.pages) : "—");
      grid.innerHTML =
        kv("file", esc(j.fileName)) +
        kv("llm policy", llmCell(j)) +
        kv("page range", j.pageRange ? '<span class="mono">' + esc(j.pageRange) + "</span>" : "whole document") +
        kv("pages", esc(progress)) +
        kv("elapsed", fmtWall(j.elapsedSeconds)) +
        kv("phase", esc(j.phase ?? "—")) +
        kv("started", esc(fmtTime(j.startedAt))) +
        kv("trace", '<span class="mono">' + esc(j.traceId ?? "") + "</span>");
      logEl.textContent = (j.progressLines ?? []).join("\n") || "(waiting for output…)";
      logEl.scrollTop = logEl.scrollHeight;
    } else {
      card.hidden = true;
    }

    // recent
    const body = document.getElementById("recentBody");
    const recent = s.recent ?? [];
    document.getElementById("recentEmpty").style.display = recent.length ? "none" : "";
    let rows = "";
    for (const j of recent) {
      const st = j.state === "succeeded" ? '<span class="ok">succeeded</span>' : '<span class="bad">failed</span>';
      rows += "<tr>" +
        "<td>" + esc(fmtTime(j.startedAt)) + "</td>" +
        "<td>" + esc(j.fileName) + "</td>" +
        "<td>" + llmCell(j) + "</td>" +
        "<td>" + (j.pages > 0 ? j.pages : "—") + "</td>" +
        "<td>" + fmtWall(j.elapsedSeconds) + "</td>" +
        "<td>" + (j.secPerPage != null ? j.secPerPage.toFixed(2) : "—") + "</td>" +
        "<td>" + fmtChars(j.resultChars) + "</td>" +
        "<td>" + st + "</td>" +
        '<td class="mono">' + esc((j.traceId ?? "").slice(0, 8)) + "</td>" +
        "</tr>";
    }
    body.innerHTML = rows;

    // config
    const c = s.config ?? {};
    const t = s.totals ?? {};
    document.getElementById("configGrid").innerHTML =
      kv("state", esc(s.state)) +
      kv("concurrency", '<span class="mono">' + esc(String(c.concurrency ?? 1)) + "</span> (fixed)") +
      kv("workers", esc(String(c.workers ?? "—"))) +
      kv("llm backend", c.llmConfigured ? '<span class="mono">' + esc(c.llmModel ?? "") + "</span>" : '<span class="dim">not configured</span>') +
      kv("max upload", esc(fmtBytes(c.maxUploadBytes))) +
      kv("conversion timeout", c.maxTimeMs > 0 ? esc(fmtWall(c.maxTimeMs / 1000)) : "unlimited") +
      kv("busy retry-after", esc(String(c.busyRetryAfterSeconds ?? "—")) + "s") +
      kv("uptime", esc(fmtWall(s.uptimeSeconds))) +
      kv("conversions", '<span class="ok">' + esc(String(t.completed ?? 0)) + "</span> ok / <span class=\"bad\">" + esc(String(t.failed ?? 0)) + "</span> failed") +
      kv("scratch root", '<span class="mono">' + esc(c.scratchRoot ?? "") + "</span>");
  }

  async function refresh() {
    try {
      const res = await fetch("/api/status", { cache: "no-store" });
      if (!res.ok) throw new Error("HTTP " + res.status);
      render(await res.json());
    } catch (e) {
      const state = document.getElementById("state");
      state.textContent = "OFFLINE";
      state.className = "pill unknown";
    }
  }

  refresh();
  setInterval(refresh, 2000);
})();
</script>
</body>
</html>
""";
}
