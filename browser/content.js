// Isle: runs in every page. Finds the audio/video you're actually listening to, reports it to the
// extension's background script, and carries out play / pause / skip / seek / like for Isle.
(() => {
  // already running here, unless that copy belongs to an extension that was reloaded (it can't send any more)
  if (window.__isleAlive && window.__isleAlive()) return;
  window.__isleAlive = () => { try { return !!chrome.runtime.id; } catch (e) { return false; } };

  const host = location.hostname;
  const site = host === 'music.youtube.com' ? 'ytmusic' : /(^|\.)youtube\.com$/.test(host) ? 'youtube' : 'web';
  const siteName = site === 'ytmusic' ? 'YouTube Music' : site === 'youtube' ? 'YouTube' : host.replace(/^www\./, '');

  // only media that has been heard: muted autoplay previews (YouTube's home page) never count
  const heard = new WeakSet();
  let lastKey = '', poll = null, gone = false;

  const audible = m => !m.muted && m.volume > 0;
  const mediaEls = () => Array.from(document.querySelectorAll('video, audio'));

  function pick() {
    const els = mediaEls().filter(m => heard.has(m) && m.isConnected && !(m.duration > 0 && m.duration < 1.5));
    if (!els.length) return null;
    return els.find(m => !m.paused) || els.sort((a, b) => (b.currentTime || 0) - (a.currentTime || 0))[0];
  }

  // ---------- site buttons ----------
  const visible = el => !!el && el.offsetParent !== null && !el.disabled;
  const q = sel => document.querySelector(sel);

  // YouTube Music has two players: the older "player bar" and the newer "miniplayer" (wiz controls)
  const YTM_PLAYERS = 'ytmusic-miniplayer, ytmusic-player-bar, ytmusic-player-page';
  function ytmButton(rx) {
    for (const root of document.querySelectorAll(YTM_PLAYERS))
      for (const b of root.querySelectorAll('button, tp-yt-paper-icon-button'))
        if (rx.test(b.getAttribute('aria-label') || b.getAttribute('title') || '') && visible(b)) return b;
    return null;
  }

  function nextButton() {
    if (site === 'ytmusic') return q('ytmusic-player-bar .next-button') || ytmButton(/^next\b/i);
    if (site === 'youtube') return q('.html5-video-player .ytp-next-button');
    return null;
  }
  function prevButton() {
    if (site === 'ytmusic') return q('ytmusic-player-bar .previous-button') || ytmButton(/^previous\b/i);
    if (site === 'youtube') return q('.html5-video-player .ytp-prev-button');
    return null;
  }

  function likeRenderer() {
    return site === 'ytmusic' ? q('ytmusic-player-bar ytmusic-like-button-renderer') : null;
  }
  function likeButton() {
    if (site === 'ytmusic') {
      // newer player: the same like button YouTube uses, inside the miniplayer / player page (a visible copy first)
      const all = [];
      for (const root of document.querySelectorAll(YTM_PLAYERS))
        all.push(...root.querySelectorAll('like-button-view-model button'));
      const shown = all.find(visible);
      if (shown || all.length) return shown || all[0];
      // older player bar
      const r = likeRenderer();
      if (!r) return null;
      return r.querySelector('#button-shape-like button') ||
             Array.from(r.querySelectorAll('button')).find(b => /^like/i.test(b.getAttribute('aria-label') || ''));
    }
    if (site === 'youtube') {
      if (!/^\/(watch|shorts\/)/.test(location.pathname)) return null;
      // the same Like shows in several places (under the video, in the player); any copy will do, a visible one first
      const all = Array.from(document.querySelectorAll('like-button-view-model button, #segmented-like-button button'));
      if (!all.length)
        all.push(...Array.from(document.querySelectorAll('button[aria-label]'))
          .filter(b => /like this video/i.test(b.getAttribute('aria-label')) && !/dislike/i.test(b.getAttribute('aria-label'))));
      return all.find(visible) || all[0] || null;
    }
    return null;
  }
  function isLiked(b) {
    if (b.closest('like-button-view-model')) return b.getAttribute('aria-pressed') === 'true';
    const r = likeRenderer();
    const status = r && r.getAttribute('like-status');
    if (status) return status === 'LIKE';
    return b.getAttribute('aria-pressed') === 'true';
  }

  // ---------- what's playing ----------
  function biggestArt(list) {
    let best = null, area = -1;
    for (const a of list || []) {
      const m = /(\d+)x(\d+)/.exec(a.sizes || '');
      const s = m ? m[1] * m[2] : 0;
      if (s > area) { area = s; best = a.src; }
    }
    return best || '';
  }

  function describe(m) {
    let title = '', artist = '', album = '', art = '';
    try {
      const md = navigator.mediaSession && navigator.mediaSession.metadata;
      if (md) { title = md.title || ''; artist = md.artist || ''; album = md.album || ''; art = biggestArt(md.artwork); }
    } catch (e) { }
    if (!title) title = document.title.replace(/\s*[-|]\s*YouTube( Music)?\s*$/i, '').replace(/^\(\d+\)\s*/, '');
    if (!art) {
      const og = q('meta[property="og:image"]');
      if (og && og.content) art = og.content;
    }
    if (!artist && site === 'web' && window === window.top) artist = siteName;
    const like = likeButton();
    const nb = nextButton(), pb = prevButton();
    const finite = isFinite(m.duration) && m.duration > 0;
    return {
      title, artist, album, art, site, siteName,
      playing: !m.paused && !m.ended,
      dur: finite ? m.duration : 0,
      rate: m.playbackRate || 1,
      canSeek: finite,
      canNext: visible(nb),
      canPrev: visible(pb) || finite,   // without a site button, "previous" restarts the track
      canLike: !!like,
      liked: like ? isLiked(like) : false
    };
  }

  function report(force) {
    if (gone) return;
    const m = pick();
    const st = m ? describe(m) : null;
    const key = JSON.stringify(st);
    if (!force && key === lastKey) return;
    lastKey = key;
    if (st) { st.pos = m.currentTime || 0; st.at = Date.now(); }
    try { chrome.runtime.sendMessage({ type: 'state', state: st }).catch(() => { }); } catch (e) { stop(); }
    if (st && !poll) poll = setInterval(() => report(false), 1000);   // catches track / like changes
    if (!st && poll) { clearInterval(poll); poll = null; }
  }

  function stop() {
    gone = true;
    if (poll) clearInterval(poll);
    poll = null;
  }

  const onMedia = e => {
    const m = e.target;
    if (!(m instanceof HTMLMediaElement)) return;
    if (audible(m) && (!m.paused || e.type === 'volumechange')) heard.add(m);
    if (heard.has(m)) report(true);
  };
  for (const t of ['play', 'playing', 'pause', 'ended', 'seeked', 'ratechange', 'durationchange', 'loadedmetadata', 'emptied', 'volumechange'])
    document.addEventListener(t, onMedia, true);
  // media that was already playing when the script arrived (extension installed / reloaded)
  for (const m of mediaEls()) if (!m.paused && audible(m)) heard.add(m);
  report(true);

  addEventListener('pagehide', () => { try { chrome.runtime.sendMessage({ type: 'state', state: null }).catch(() => { }); } catch (e) { } stop(); });

  // ---------- commands from Isle ----------
  chrome.runtime.onMessage.addListener((msg, sender, reply) => {
    if (!msg || msg.type !== 'cmd') return;
    const m = pick();
    try {
      switch (msg.cmd) {
        case 'toggle':
          if (m) { if (m.paused || m.ended) m.play().catch(() => { }); else m.pause(); }
          break;
        case 'next': {
          const b = nextButton();
          if (visible(b)) b.click();
          break;
        }
        case 'prev': {
          const b = prevButton();
          if (visible(b) && (!m || m.currentTime < 4)) b.click();
          else if (m) m.currentTime = 0;
          break;
        }
        case 'seek':
          if (m && isFinite(m.duration)) m.currentTime = Math.max(0, Math.min(m.duration, +msg.value || 0));
          break;
        case 'like': {
          const b = likeButton();
          if (b) b.click();
          setTimeout(() => report(true), 400);
          break;
        }
      }
    } catch (e) { }
    setTimeout(() => report(true), 150);
    reply({ ok: true });
  });
})();
