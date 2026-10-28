// Isla Dinámica – extensión de descargas (Chrome / Edge / Brave / Opera)
// Cada ~0,5 s, mientras haya descargas en curso, manda su estado a la isla (http://127.0.0.1:47831)
// y ejecuta los comandos pause / resume / cancel que la isla devuelva en la respuesta.
// También avisa cuando una descarga termina o se interrumpe (aunque no sea en la carpeta Descargas).

const ENDPOINT = "http://127.0.0.1:47831/state";
const TICK_MS = 500;
let timer = null;
let pendingEvents = [];

async function tick() {
  let items = [];
  try {
    items = await chrome.downloads.search({ state: "in_progress" });
  } catch (e) { /* ignorar */ }

  const downloads = items.map(d => ({
    id: d.id,
    name: (d.filename || "").split(/[\\/]/).pop() || "",
    bytes: d.bytesReceived || 0,
    total: d.totalBytes > 0 ? d.totalBytes : 0,
    paused: !!d.paused
  }));

  const events = pendingEvents.splice(0);
  let commands = [];
  try {
    const r = await fetch(ENDPOINT, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ version: chrome.runtime.getManifest().version, downloads, events })
    });
    if (!r.ok) throw new Error("HTTP " + r.status);
    commands = (await r.json()).commands || [];
  } catch (e) {
    // La isla está cerrada: se reintentan los avisos recientes (los viejos se descartan)
    const fresh = events.filter(ev => Date.now() - ev.t < 30000);
    pendingEvents = fresh.concat(pendingEvents).slice(-20);
  }

  for (const c of commands) {
    try {
      if (c.cmd === "pause") await chrome.downloads.pause(c.id);
      else if (c.cmd === "resume") await chrome.downloads.resume(c.id);
      else if (c.cmd === "cancel") await chrome.downloads.cancel(c.id);
    } catch (e) { console.warn("Isla: no se pudo ejecutar", c, e); }
  }
  if (commands.length) setTimeout(tick, 150);   // refleja el cambio enseguida

  if (downloads.length === 0 && pendingEvents.length === 0) stop();
}

function start() {
  if (timer) return;
  tick();
  timer = setInterval(tick, TICK_MS);
}

function stop() {
  if (timer) { clearInterval(timer); timer = null; }
}

chrome.downloads.onCreated.addListener(start);

chrome.downloads.onChanged.addListener(async (delta) => {
  const st = delta.state && delta.state.current;
  if (st === "complete" || st === "interrupted") {
    try {
      const [d] = await chrome.downloads.search({ id: delta.id });
      pendingEvents.push({
        t: Date.now(),
        id: delta.id,
        state: st,
        path: d ? (d.filename || "") : "",
        name: d ? ((d.filename || "").split(/[\\/]/).pop() || "") : "",
        bytes: d ? (d.fileSize > 0 ? d.fileSize : (d.bytesReceived || 0)) : 0
      });
    } catch (e) { /* ignorar */ }
  }
  start();
});

chrome.runtime.onStartup.addListener(start);
chrome.runtime.onInstalled.addListener(start);

// Respaldo: si el navegador durmió el service worker con una descarga en curso, lo despierta
chrome.alarms.create("keepalive", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(start);
