#!/usr/bin/env node

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, '..', 'desktop', 'AdBlockDocumentScript.cs'), 'utf8');

function extractRawLiteral(text, name) {
  const regex = new RegExp(`(?:public|private) (?:const|static readonly) string ${name}\\s*=\\s*"""\\r?\\n([\\s\\S]*?)\\r?\\n\\s*""";`);
  const match = text.match(regex);
  assert.ok(match, `AdBlockDocumentScript.cs is missing ${name} raw string literal`);
  const lines = match[1].split(/\r?\n/);
  const minIndent = lines
    .filter((l) => l.trim().length > 0)
    .reduce((min, l) => Math.min(min, l.match(/^[ \t]*/)[0].length), Infinity);
  return lines.map((l) => l.slice(Number.isFinite(minIndent) ? minIndent : 0)).join('\n');
}

function extractYouTubeHeaderSnippet(text) {
  const regex = /public (?:const|static readonly) string YouTubeRuntimeSource\s*=\s*"\(\(\) => \{\\n"\s*\+\s*SharedYouTubeBootstrapPrefix\s*\+\s*"\\n"\s*\+\s*"""\r?\n([\s\S]*?)\r?\n\s*"""/;
  const match = text.match(regex);
  assert.ok(match, 'AdBlockDocumentScript.cs is missing YouTubeRuntimeSource header snippet');
  return match[1];
}

const universalBootstrap = extractRawLiteral(source, 'UniversalBootstrapSource');
assert.ok(
  Buffer.byteLength(universalBootstrap, 'utf8') <= 3_000,
  `UniversalBootstrapSource exceeded 3 KB budget (${Buffer.byteLength(universalBootstrap, 'utf8')} bytes)`
);
assert.ok(!universalBootstrap.includes('ytd-ad-slot-renderer'), 'Universal bootstrap must not bundle YouTube selectors');
assert.ok(!universalBootstrap.includes('[id*="-ad-"]'), 'Universal bootstrap must not include wildcard DOM selectors');
assert.ok(!universalBootstrap.includes('[class*="advert"]'), 'Universal bootstrap must not include wildcard class selectors');

const youTubeRuntime =
  '(() => {\n' +
  extractRawLiteral(source, 'SharedYouTubeBootstrapPrefix') +
  '\n' +
  extractYouTubeHeaderSnippet(source) +
  '\n' +
  extractRawLiteral(source, 'SharedYouTubeBody') +
  '\n' +
  extractRawLiteral(source, 'SharedYouTubeTrailer');

function runYouTubeSandbox({ pathname = '/watch', hidden = false, initialFetch = null } = {}) {
  let nextTimerId = 0;
  const timers = new Map();
  const windowListeners = new Map();
  const docListeners = new Map();
  let fakeNow = 0;
  let fetchDelegate = initialFetch ?? (() => Promise.resolve({ ok: true, status: 200 }));

  class MockHTMLElement {
    constructor() {
      this.disabled = false;
      this.hidden = false;
      this.style = {};
      this.attributes = new Map();
    }
    getAttribute(name) {
      return this.attributes.has(name) ? this.attributes.get(name) : null;
    }
    setAttribute(name, value) {
      this.attributes.set(name, String(value));
    }
    closest() {
      return null;
    }
    click() {
      this.clicks = (this.clicks || 0) + 1;
    }
  }
  class MockHTMLVideoElement extends MockHTMLElement {}
  class MockNode {}
  class MockHTMLIFrameElement extends MockHTMLElement {}
  class MockMutationObserver {
    observe() {}
    disconnect() {}
  }

  function MockXMLHttpRequest() {}
  MockXMLHttpRequest.prototype = {
    open() {},
    send() {},
    response: '{"ok":true}',
    responseText: '{"ok":true}',
    addEventListener(name, cb) {
      if (!this._events) this._events = new Map();
      const list = this._events.get(name) ?? [];
      list.push(cb);
      this._events.set(name, list);
    },
    _trigger(name) {
      for (const cb of this._events?.get(name) ?? []) cb();
    }
  };

  let ytcfgStore = { data_: { INNERTUBE_CONTEXT: { client: { userAgent: 'UA' } }, EXPERIMENT_FLAGS: {} } };
  let adShowing = false;
  let skipButtons = [];
  let onPlayerMute = () => {
    video.muted = true;
  };
  const player = {
    getPlayerResponse: () => undefined,
    getProgressState: () => ({ duration: 120, loaded: 10, current: 0 }),
    getPlayerState: () => -1,
    getPlayerStateObject: () => ({ isBuffering: false }),
    getStatsForNerds: () => ({ debug_info: '' }),
    mute: () => onPlayerMute(),
    playVideo: () => {},
    classList: { contains: (name) => adShowing && name === 'ad-showing', remove: () => {} }
  };
  const video = new MockHTMLVideoElement();
  Object.assign(video, { readyState: 1, duration: 120, currentTime: 0, paused: true, muted: false, playbackRate: 1 });

  const sandbox = {
    URL,
    URLSearchParams,
    queueMicrotask,
    Response,
    ReadableStream,
    TextDecoder,
    TextEncoder,
    Request: class MockRequest {
      constructor(url, init = {}) {
        this.url = url;
        this.method = init.method ?? 'GET';
        this.headers = new Headers(init.headers);
        this._body = init.body ?? '';
      }
      clone() {
        const bodyText = typeof this._body === 'string' ? this._body : '';
        return { text: async () => bodyText };
      }
    },
    Headers,
    performance: { now: () => fakeNow },
    location: {
      hostname: 'www.youtube.com',
      pathname,
      search: pathname === '/watch' ? '?v=abc123def45' : '',
      href: `https://www.youtube.com${pathname}${pathname === '/watch' ? '?v=abc123def45' : ''}`
    },
    HTMLElement: MockHTMLElement,
    HTMLVideoElement: MockHTMLVideoElement,
    Node: MockNode,
    HTMLIFrameElement: MockHTMLIFrameElement,
    XMLHttpRequest: MockXMLHttpRequest,
    MutationObserver: MockMutationObserver,
    console,
    fetch: (...args) => fetchDelegate(...args),
    __mishaAdBlockEnabled: true
  };
  sandbox.window = sandbox;
  sandbox.globalThis = sandbox;
  sandbox.Object = Object;
  sandbox.ArrayBuffer = ArrayBuffer;
  sandbox.Uint8Array = Uint8Array;
  sandbox.setTimeout = (cb, delay = 0) => {
    const id = ++nextTimerId;
    timers.set(id, { cb, delay });
    return id;
  };
  sandbox.clearTimeout = (id) => {
    timers.delete(id);
  };
  sandbox.addEventListener = (name, cb) => {
    const list = windowListeners.get(name) ?? [];
    list.push(cb);
    windowListeners.set(name, list);
  };
  sandbox.removeEventListener = (name, cb) => {
    const list = windowListeners.get(name);
    if (list) windowListeners.set(name, list.filter((x) => x !== cb));
  };
  Object.defineProperty(sandbox, 'ytcfg', {
    configurable: true,
    get: () => ytcfgStore,
    set: (v) => {
      ytcfgStore = v;
    }
  });
  Object.defineProperty(MockHTMLIFrameElement.prototype, 'contentWindow', {
    configurable: true,
    get: () => null
  });
  sandbox.document = {
    hidden,
    readyState: 'complete',
    documentElement: {
      appendChild: () => {},
      setAttribute: () => {},
      removeAttribute: () => {}
    },
    createElement: () => ({ id: '', textContent: '', remove: () => {} }),
    getElementById: (id) => (id === 'movie_player' ? player : null),
    querySelector: (sel) => (sel.startsWith('video') ? video : null),
    querySelectorAll: (sel) => {
      if (typeof sel === 'string' && sel.includes('ytp-ad')) return skipButtons;
      if (typeof sel === 'string' && sel.startsWith('video')) return [video];
      return [];
    },
    addEventListener: (name, cb) => {
      const list = docListeners.get(name) ?? [];
      list.push(cb);
      docListeners.set(name, list);
    },
    removeEventListener: (name, cb) => {
      const list = docListeners.get(name);
      if (list) docListeners.set(name, list.filter((x) => x !== cb));
    }
  };

  vm.runInNewContext(youTubeRuntime, sandbox, { timeout: 2000 });

  return {
    sandbox,
    timers,
    docListeners,
    video,
    player,
    MockHTMLElement,
    setAdShowing(value) {
      adShowing = value;
    },
    setSkipButtons(buttons) {
      skipButtons = buttons;
    },
    setFetch(fn) {
      fetchDelegate = fn;
    },
    advanceNow(ms) {
      fakeNow += ms;
    },
    fireTimer(delay) {
      const entry = [...timers.entries()].find(([, v]) => v.delay === delay);
      if (!entry) return false;
      timers.delete(entry[0]);
      entry[1].cb();
      return true;
    },
    emitDoc(name, eventObj) {
      for (const cb of docListeners.get(name) ?? []) cb(eventObj);
    }
  };
}

// 1. XHR load handler must not leak undeclared global `_`
{
  const env = runYouTubeSandbox({ pathname: '/watch', hidden: false });
  const xhr = new env.sandbox.XMLHttpRequest();
  xhr.open('GET', 'https://www.youtube.com/youtubei/v1/player');
  xhr.send(null);
  xhr._trigger('load');
  assert.equal('_' in env.sandbox, false, 'XHR load handler must not create an undeclared global `_`');
}

// 2. scheduleInstantPlayCheck halts cleanly once hidden or autoplayAttempts >= 20
{
  const env = runYouTubeSandbox({ pathname: '/watch', hidden: false });
  let steps = 0;
  while (env.timers.size > 0 && steps < 40) {
    env.advanceNow(250);
    if (!env.fireTimer(200)) break;
    steps += 1;
  }
  assert.ok(steps <= 21, `instantPlayTimer must cap at <= 20 attempts instead of spinning (${steps})`);
  assert.equal([...env.timers.values()].some((t) => t.delay === 200), false);
}

// 3. scheduleFallback skips timers completely off /watch and when hidden, resuming on visibilitychange
{
  const homeEnv = runYouTubeSandbox({ pathname: '/', hidden: false });
  assert.equal(homeEnv.timers.size, 0, 'Non-watch YouTube pages must not arm fallback/instantPlay timers');

  homeEnv.sandbox.location.pathname = '/watch';
  homeEnv.sandbox.location.search = '?v=xyz987';
  homeEnv.sandbox.document.hidden = true;
  homeEnv.emitDoc('yt-navigate-finish');
  assert.equal(homeEnv.timers.size, 0, 'Hidden tabs must not arm fallback/instantPlay timers');

  homeEnv.sandbox.document.hidden = false;
  homeEnv.emitDoc('visibilitychange');
  assert.ok(homeEnv.timers.size > 0, 'Visible /watch tab should resume scheduling on visibilitychange');
}

// 4. patchYtcfgData deduplicates identical data objects via WeakSet
{
  const env = runYouTubeSandbox({ pathname: '/watch', hidden: false });
  const cfgData = { EXPERIMENT_FLAGS: {} };
  env.sandbox.ytcfg.data_ = cfgData;
  const firstDesc = Object.getOwnPropertyDescriptor(cfgData, 'EXPERIMENT_FLAGS');
  env.sandbox.ytcfg.data_ = cfgData;
  const secondDesc = Object.getOwnPropertyDescriptor(cfgData, 'EXPERIMENT_FLAGS');
  assert.strictEqual(firstDesc.get, secondDesc.get, 'Repeated ytcfg data object must not be re-wrapped');
}

// 5. YouTube runtime must not expose window.__mishaStopYouTubeRuntime and honors controlChannel teardown
{
  const substituted = youTubeRuntime.replace('__MISHA_ADBLOCK_CONTROL_CHANNEL__', 'misha_test_channel');
  const listeners = new Map();
  const timers = new Map();
  let nextTimer = 0;
  const sandbox = {
    URL,
    URLSearchParams,
    queueMicrotask,
    Request: class { constructor(url) { this.url = url; this.method = 'GET'; this.headers = new Map(); } clone() { return { text: async () => '' }; } },
    Headers: Map,
    performance: { now: () => 0 },
    location: { hostname: 'www.youtube.com', pathname: '/watch', search: '?v=abc123def45', href: 'https://www.youtube.com/watch?v=abc123def45' },
    HTMLElement: class {},
    HTMLVideoElement: class {},
    Node: class {},
    HTMLIFrameElement: class {},
    XMLHttpRequest: function MockXhr() {},
    MutationObserver: class { observe() {} disconnect() {} },
    console,
    fetch: () => Promise.resolve({ ok: true, status: 200 }),
    __mishaAdBlockEnabled: true,
    setTimeout: (cb, delay = 0) => { const id = ++nextTimer; timers.set(id, { cb, delay }); return id; },
    clearTimeout: (id) => timers.delete(id),
    addEventListener: (name, cb) => { const list = listeners.get(name) ?? []; list.push(cb); listeners.set(name, list); },
    removeEventListener: (name, cb) => { const list = listeners.get(name); if (list) listeners.set(name, list.filter((x) => x !== cb)); },
    document: {
      hidden: false,
      readyState: 'complete',
      documentElement: { appendChild: () => {}, setAttribute: () => {}, removeAttribute: () => {} },
      createElement: () => ({ id: '', textContent: '', remove: () => {} }),
      getElementById: () => null,
      querySelector: () => null,
      querySelectorAll: () => [],
      addEventListener: () => {},
      removeEventListener: () => {}
    }
  };
  sandbox.XMLHttpRequest.prototype = { open() {}, send() {}, addEventListener() {} };
  sandbox.window = sandbox;
  sandbox.globalThis = sandbox;
  sandbox.Object = Object;
  vm.runInNewContext(substituted, sandbox, { timeout: 2000 });
  assert.equal('__mishaStopYouTubeRuntime' in sandbox, false, 'window.__mishaStopYouTubeRuntime must not be exposed');
  assert.ok(listeners.has('misha_test_channel'), 'Substituted controlChannel listener must be registered');
  for (const cb of listeners.get('misha_test_channel')) cb();
}

// 6. get_watch multipart/mixed ReadableStream yields sanitized part 1 immediately, updates Content-Length, and rewrites nested playerRequest
{
  const env = runYouTubeSandbox({ pathname: '/', hidden: false });
  const enc = new TextEncoder();
  const dec = new TextDecoder();
  const part1Payload = JSON.stringify({
    videoDetails: { videoId: 'vid123' },
    adPlacements: [{ ad: true }],
    playerAds: [1],
    adSlots: [2],
    adBreakHeartbeatParams: 'ad'
  });
  const part2Payload = JSON.stringify({
    response: {
      results: [
        { adSlotRenderer: { slotId: 'ad1' } },
        { videoRenderer: { videoId: 'keep1' } }
      ]
    }
  });
  let streamController = null;
  let capturedInit = null;
  env.setFetch(async (_url, init) => {
    capturedInit = init;
    const stream = new ReadableStream({
      start(controller) {
        streamController = controller;
        const part1Chunk =
          `-----misha\r\nContent-Type: application/json\r\nContent-Length: ${Buffer.byteLength(part1Payload, 'utf8')}\r\n\r\n` +
          `${part1Payload}\r\n-----misha\r\nContent-Type: application/json\r\n\r\n`;
        controller.enqueue(enc.encode(part1Chunk));
      }
    });
    return new Response(stream, {
      status: 200,
      headers: { 'content-type': 'multipart/mixed; boundary="---misha"' }
    });
  });

  const res = await env.sandbox.fetch('https://www.youtube.com/youtubei/v1/get_watch?prettyPrint=false', {
    method: 'POST',
    body: JSON.stringify({
      context: { client: { userAgent: 'UA' } },
      playerRequest: { playbackContext: { contentPlaybackContext: {} } }
    })
  });
  const sentBody = JSON.parse(capturedInit.body);
  assert.ok(
    sentBody.playerRequest.playbackContext.contentPlaybackContext.lactMilliseconds,
    'get_watch rewritePlayerRequest must stamp nested playerRequest.playbackContext.contentPlaybackContext.lactMilliseconds'
  );

  const reader = res.body.getReader();
  const firstRead = await reader.read();
  assert.equal(firstRead.done, false, 'Part 1 must yield immediately before Part 2 arrives');
  const part1Text = dec.decode(firstRead.value);
  const splitIdx = part1Text.indexOf('\r\n\r\n');
  assert.ok(splitIdx !== -1, 'Part 1 must keep multipart header separator');
  const part1Headers = part1Text.slice(0, splitIdx);
  const part1BodyCore = part1Text.slice(splitIdx + 4).replace(/\r\n$/, '');
  const part1Json = JSON.parse(part1BodyCore);
  assert.equal(part1Json.videoDetails.videoId, 'vid123');
  assert.equal('adPlacements' in part1Json, false);
  assert.equal('playerAds' in part1Json, false);
  assert.equal('adSlots' in part1Json, false);
  assert.equal('adBreakHeartbeatParams' in part1Json, false);
  const clMatch = /^Content-Length:\s*(\d+)/im.exec(part1Headers);
  assert.ok(clMatch, 'Part 1 must preserve Content-Length header');
  assert.equal(
    Number(clMatch[1]),
    Buffer.byteLength(part1BodyCore, 'utf8'),
    'Part 1 Content-Length must match exact sanitized UTF-8 byte length'
  );

  streamController.enqueue(enc.encode(`${part2Payload}\r\n-----misha--\r\n`));
  streamController.close();

  let remaining = '';
  while (true) {
    const next = await reader.read();
    if (next.done) break;
    remaining += dec.decode(next.value, { stream: true });
  }
  remaining += dec.decode();
  const part2BodyStart = remaining.indexOf('\r\n\r\n') + 4;
  const part2CloseIdx = remaining.indexOf('\r\n-----misha--');
  assert.ok(part2CloseIdx !== -1, 'Closing multipart delimiter must be preserved');
  const part2Json = JSON.parse(remaining.slice(part2BodyStart, part2CloseIdx));
  assert.equal(part2Json.response.results.length, 1);
  assert.equal(part2Json.response.results[0].videoRenderer.videoId, 'keep1');
}

// 7. XSSI-prefixed /youtubei/v1/player response.arrayBuffer() strips ads while preserving XSSI prefix
{
  const env = runYouTubeSandbox({ pathname: '/watch', hidden: false });
  const xssiPayload =
    ")]}'\n" +
    JSON.stringify({
      videoDetails: { videoId: 'vid456' },
      adPlacements: [{ ad: true }],
      playerAds: [{ ad: true }]
    });
  env.setFetch(async () => new Response(xssiPayload, {
    status: 200,
    headers: { 'content-type': 'application/json; charset=UTF-8' }
  }));

  const res = await env.sandbox.fetch('https://www.youtube.com/youtubei/v1/player?prettyPrint=false');
  const buf = await res.arrayBuffer();
  const decoded = new TextDecoder().decode(buf);
  assert.ok(decoded.startsWith(")]}'\n"), 'XSSI prefix must remain intact');
  const parsed = JSON.parse(decoded.slice(5));
  assert.equal(parsed.videoDetails.videoId, 'vid456');
  assert.equal('adPlacements' in parsed, false);
  assert.equal('playerAds' in parsed, false);
}

// 8. Capture-phase yt-page-data-fetched strips ad placements from SPA navigation envelopes
{
  const env = runYouTubeSandbox({ pathname: '/watch', hidden: false });
  const eventDetail = {
    pageData: {
      playerResponse: {
        videoDetails: { videoId: 'vid789' },
        adPlacements: [{ ad: true }],
        playerAds: [1]
      },
      response: {
        contents: [
          { adSlotRenderer: { slotId: 'infeed' } },
          { videoRenderer: { videoId: 'vid789' } }
        ]
      }
    }
  };
  env.emitDoc('yt-page-data-fetched', { detail: eventDetail });
  assert.equal('adPlacements' in eventDetail.pageData.playerResponse, false);
  assert.equal('playerAds' in eventDetail.pageData.playerResponse, false);
  assert.equal(eventDetail.pageData.playerResponse.videoDetails.videoId, 'vid789');
  assert.equal(eventDetail.pageData.response.contents.length, 1);
  assert.equal(eventDetail.pageData.response.contents[0].videoRenderer.videoId, 'vid789');
}

// 9. cleanPlayerAds seeks non-SSAP client ads, captures mute before player.mute(), and avoids burning hidden/disabled skip buttons
{
  const env = runYouTubeSandbox({ pathname: '/watch', hidden: false });
  const flushCleanPass = () => {
    env.emitDoc('yt-player-updated');
    while (env.fireTimer(150)) {}
  };

  env.video.muted = false;
  env.video.duration = 45;
  env.video.currentTime = 2;

  const skipButtonHidden = new env.MockHTMLElement();
  skipButtonHidden.style.display = 'none';
  const skipButtonDisabled = new env.MockHTMLElement();
  skipButtonDisabled.disabled = true;
  env.setSkipButtons([skipButtonHidden, skipButtonDisabled]);

  env.setAdShowing(true);
  flushCleanPass();

  assert.equal(env.video.currentTime, 45, 'Client ad must seek video.currentTime to video.duration');
  assert.equal(env.video.muted, true, 'Video must be muted while client ad is active');
  assert.equal(skipButtonHidden.clicks ?? 0, 0, 'Hidden skip button must not be clicked or burned');
  assert.equal(skipButtonDisabled.clicks ?? 0, 0, 'Disabled skip button must not be clicked or burned');

  // Make skip controls active and visible; a subsequent pass must now click both once
  skipButtonHidden.style.display = '';
  skipButtonDisabled.disabled = false;
  flushCleanPass();
  assert.equal(skipButtonHidden.clicks, 1, 'Previously hidden skip button must click once visible');
  assert.equal(skipButtonDisabled.clicks, 1, 'Previously disabled skip button must click once enabled');

  // Exiting ad must restore pre-ad unmuted state even though player.mute() synchronously mutated video.muted
  env.setAdShowing(false);
  flushCleanPass();
  assert.equal(env.video.muted, false, 'Unmuted state before ad must be restored when ad ends');
}

console.log(JSON.stringify({ status: 'PASS', contract: 'AdBlockDocumentScript universal/youtube split, timer gating, WeakSet dedupe, XHR/stream sanitization, and SPA ad-skip hygiene verified' }));
