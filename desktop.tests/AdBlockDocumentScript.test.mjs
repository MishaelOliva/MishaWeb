#!/usr/bin/env node

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, '..', 'desktop', 'AdBlockDocumentScript.cs'), 'utf8');

function extractRawLiteral(text, name) {
  const regex = new RegExp(`(?:public|private) const string ${name}\\s*=\\s*"""\\r?\\n([\\s\\S]*?)\\r?\\n\\s*""";`);
  const match = text.match(regex);
  assert.ok(match, `AdBlockDocumentScript.cs is missing ${name} raw string literal`);
  const lines = match[1].split(/\r?\n/);
  const minIndent = lines
    .filter((l) => l.trim().length > 0)
    .reduce((min, l) => Math.min(min, l.match(/^[ \t]*/)[0].length), Infinity);
  return lines.map((l) => l.slice(Number.isFinite(minIndent) ? minIndent : 0)).join('\n');
}

function extractYouTubeHeaderSnippet(text) {
  const regex = /public const string YouTubeRuntimeSource\s*=\s*"\(\(\) => \{\\n"\s*\+\s*SharedYouTubeBootstrapPrefix\s*\+\s*"\\n"\s*\+\s*"""\r?\n([\s\S]*?)\r?\n\s*"""/;
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

function runYouTubeSandbox({ pathname = '/watch', hidden = false } = {}) {
  let nextTimerId = 0;
  const timers = new Map();
  const windowListeners = new Map();
  const docListeners = new Map();
  let fakeNow = 0;

  class MockHTMLElement {}
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
  const player = {
    getPlayerResponse: () => undefined,
    getProgressState: () => ({ duration: 120, loaded: 10, current: 0 }),
    getPlayerState: () => -1,
    getPlayerStateObject: () => ({ isBuffering: false }),
    getStatsForNerds: () => ({ debug_info: '' }),
    playVideo: () => {},
    classList: { contains: () => false, remove: () => {} }
  };
  const video = new MockHTMLVideoElement();
  Object.assign(video, { readyState: 1, duration: 120, currentTime: 0, paused: true, muted: false });

  const sandbox = {
    URL,
    URLSearchParams,
    queueMicrotask,
    Request: class MockRequest {
      constructor(url) {
        this.url = url;
        this.method = 'GET';
        this.headers = new Map();
      }
      clone() {
        return { text: async () => '' };
      }
    },
    Headers: Map,
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
    fetch: () => Promise.resolve({ ok: true, status: 200 }),
    __mishaAdBlockEnabled: true
  };
  sandbox.window = sandbox;
  sandbox.globalThis = sandbox;
  sandbox.Object = Object;
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
    querySelectorAll: () => [],
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
    emitDoc(name) {
      for (const cb of docListeners.get(name) ?? []) cb();
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

console.log(JSON.stringify({ status: 'PASS', contract: 'AdBlockDocumentScript universal/youtube split, timer gating, WeakSet dedupe, and XHR global hygiene verified' }));
