// Isle: keeps the list of tabs that are playing and talks to Isle on Windows through native messaging
// (Isle.exe started by the browser, which hands everything to the running Isle over a local pipe).
const HOST = 'com.isle.browser';

const brands = (navigator.userAgentData && navigator.userAgentData.brands || []).map(b => b.brand);
const BROWSER = brands.find(b => /Edge|Brave|Opera/.test(b)) || 'Chrome';

const media = new Map();      // "tabId:frameId" -> { tabId, frameId, seq, state, icon }
const images = new Map();     // url -> base64 already sent (small cache, so a reconnect can resend)
let port = null, retry = 0, seq = 0, sendTimer = null;

// ---------- connection to Isle ----------
function connect() {
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch (e) { port = null; later(); return; }
  port.onMessage.addListener(fromIsle);
  port.onDisconnect.addListener(() => { void chrome.runtime.lastError; port = null; later(); });
}

function later() {
  retry = Math.min(retry + 1, 6);
  setTimeout(connect, [1, 2, 5, 10, 30, 60][retry - 1] * 1000);
}

function post(msg) {
  if (!port) return;
  try { port.postMessage(msg); } catch (e) { }
}

function fromIsle(msg) {
  if (!msg) return;
  if (msg.type === 'hello') {         // Isle (re)connected: send everything
    retry = 0;
    sendList();
    for (const [url, data] of images) if (data) post({ type: 'image', url, data });
    // this worker may have been restarted and lost its list: every page reports again
    chrome.tabs.query({}, tabs => {
      for (const t of tabs || [])
        chrome.tabs.sendMessage(t.id, { type: 'cmd', cmd: 'report' }, () => void chrome.runtime.lastError);
    });
    return;
  }
  if (msg.type !== 'cmd') return;
  const e = media.get(msg.key);
  if (!e) return;
  if (msg.cmd === 'focus') {
    chrome.tabs.update(e.tabId, { active: true }, t => {
      if (chrome.runtime.lastError || !t) return;
      chrome.windows.update(t.windowId, { focused: true });
    });
    return;
  }
  chrome.tabs.sendMessage(e.tabId, { type: 'cmd', cmd: msg.cmd, value: msg.value }, { frameId: e.frameId }, () => void chrome.runtime.lastError);
}

// ---------- tab list ----------
function sendList() {
  clearTimeout(sendTimer);
  sendTimer = null;
  const tabs = [];
  for (const [key, e] of media) {
    if (!e.state) continue;
    tabs.push(Object.assign({ key, seq: e.seq, icon: e.icon || '' }, e.state));
  }
  post({ type: 'tabs', browser: BROWSER, tabs });
}

function sendSoon() {
  if (!sendTimer) sendTimer = setTimeout(sendList, 120);
}

// pictures (album art, site icons) are fetched here, with the browser's own access, and handed over once
async function sendImage(url) {
  if (!url || images.has(url) || !port) return;
  images.set(url, null);
  try {
    const r = await fetch(url, { credentials: 'omit' });
    const buf = await r.arrayBuffer();
    if (buf.byteLength > 700 * 1024) return;
    let s = '';
    const bytes = new Uint8Array(buf);
    for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    const data = btoa(s);
    images.set(url, data);
    if (images.size > 40) images.delete(images.keys().next().value);
    post({ type: 'image', url, data });
  } catch (e) { images.delete(url); }
}

chrome.runtime.onMessage.addListener((msg, sender) => {
  if (!msg || msg.type !== 'state' || !sender.tab) return;
  if (!port) connect();
  const key = sender.tab.id + ':' + (sender.frameId || 0);
  if (!msg.state) {
    if (media.delete(key)) sendSoon();
    return;
  }
  let e = media.get(key);
  if (!e) { e = { tabId: sender.tab.id, frameId: sender.frameId || 0, seq: ++seq }; media.set(key, e); }
  e.state = msg.state;
  e.icon = sender.tab.favIconUrl || e.icon || '';
  sendImage(msg.state.art);
  sendImage(e.icon);
  sendSoon();
});

chrome.tabs.onRemoved.addListener(tabId => {
  let changed = false;
  for (const [key, e] of media) if (e.tabId === tabId) { media.delete(key); changed = true; }
  if (changed) sendSoon();
});

chrome.tabs.onUpdated.addListener((tabId, info) => {
  if (info.status === 'loading' && info.url) {          // navigated away: the page reports again if it plays
    let changed = false;
    for (const [key, e] of media) if (e.tabId === tabId) { media.delete(key); changed = true; }
    if (changed) sendSoon();
  }
  if (info.favIconUrl) {
    for (const e of media.values()) if (e.tabId === tabId) { e.icon = info.favIconUrl; sendImage(e.icon); sendSoon(); }
  }
});

// tabs that were already open when the extension was installed or reloaded get the page script now
async function injectExisting() {
  const tabs = await chrome.tabs.query({ url: ['http://*/*', 'https://*/*'] });
  for (const t of tabs) {
    if (t.discarded) continue;
    chrome.scripting.executeScript({ target: { tabId: t.id, allFrames: true }, files: ['content.js'] }).catch(() => { });
  }
}

chrome.runtime.onInstalled.addListener(injectExisting);
chrome.runtime.onStartup.addListener(connect);
connect();
