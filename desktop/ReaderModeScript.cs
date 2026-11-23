namespace MishaWeb;

internal static class ReaderModeScript
{
    // This is intentionally app-owned and injected only after the user requests
    // reader mode. It never runs at document start and never makes a request.
    public static readonly string Toggle = """
(() => {
  const id = '__misha_reader_overlay';
  const existing = document.getElementById(id);
  if (existing) {
    const savedOverflow = document.documentElement?.dataset?.mishaReaderOverflow;
    if (typeof existing.__mishaCleanup === 'function') existing.__mishaCleanup();
    existing.remove();
    if (document.documentElement) {
      document.documentElement.style.overflow = savedOverflow ?? '';
      delete document.documentElement.dataset.mishaReaderOverflow;
    }
    return true;
  }
  if (!document.body || !document.documentElement) return false;
  const getText = (node) => (node?.textContent ?? node?.innerText ?? '');
  const selectors = ['article', 'main', '[role="main"]', '[itemprop="articleBody"]', '.article-body', '.post-content', '.entry-content', '.story-body', '#content', '#main'];
  const candidates = [];
  const seen = new Set();
  for (const selector of selectors) {
    for (const element of document.querySelectorAll(selector)) {
      if (!seen.has(element)) { seen.add(element); candidates.push(element); }
    }
  }
  if (!candidates.length) candidates.push(document.body);
  // The incumbent's score is carried in a local rather than recomputed, and
  // textContent avoids forcing synchronous layout on every candidate and anchor.
  const score = (element) => {
    const text = getText(element).trim();
    let linkCharacters = 0;
    for (const anchor of element.querySelectorAll('a')) {
      linkCharacters += getText(anchor).length;
    }
    const paragraphs = element.querySelectorAll('p').length;
    const headings = element.querySelectorAll('h1,h2,h3').length;
    const bad = element.querySelectorAll('nav,form,aside,footer,header, .sidebar, .nav, .comments, [role="navigation"]').length;
    const density = text.length ? linkCharacters / text.length : 1;
    return text.length + paragraphs * 180 + headings * 80 - density * 600 - bad * 320;
  };
  let winner = candidates[0];
  let bestScore = score(winner);
  for (let index = 1; index < candidates.length; index++) {
    const candidateScore = score(candidates[index]);
    if (candidateScore > bestScore) {
      winner = candidates[index];
      bestScore = candidateScore;
    }
  }
  if (getText(winner).trim().length < 320) return false;

  const clone = winner.cloneNode(true);
  const blockedTags = new Set([
    'SCRIPT', 'STYLE', 'FORM', 'BUTTON', 'INPUT', 'SELECT', 'TEXTAREA',
    'NAV', 'ASIDE', 'HEADER', 'FOOTER', 'DIALOG', 'IFRAME', 'VIDEO', 'AUDIO',
    'CANVAS', 'OBJECT', 'EMBED', 'BASE', 'META', 'LINK', 'NOSCRIPT'
  ]);
  const lazySrcAttrs = ['data-src', 'data-lazy-src', 'data-original', 'data-actualsrc'];
  const stripInlineEvents = (el) => {
    if (!el?.attributes) return;
    for (let i = el.attributes.length - 1; i >= 0; i--) {
      const attr = el.attributes[i];
      if (attr && /^on/i.test(attr.name)) el.removeAttribute(attr.name);
    }
  };
  const shouldDropNode = (el) => {
    const tag = (el.tagName || '').toUpperCase();
    if (blockedTags.has(tag)) return true;
    const role = (el.getAttribute?.('role') || '').toLowerCase();
    return role === 'navigation' || role === 'dialog';
  };

  let hasLeadingH1 = false;
  const tocHeadings = [];
  const tablesToWrap = [];

  const sanitizeElement = (el) => {
    stripInlineEvents(el);
    if (el.getAttribute?.('id') !== null && el.getAttribute?.('id') !== undefined) {
      el.removeAttribute('id');
    }
    const tag = (el.tagName || '').toUpperCase();
    if (tag === 'A') {
      const rawHref = el.getAttribute?.('href');
      if (rawHref) {
        try {
          const resolved = new URL(rawHref, location.href);
          if (resolved.protocol === 'http:' || resolved.protocol === 'https:') {
            el.href = resolved.href;
          } else {
            el.removeAttribute('href');
          }
        } catch (_) {
          el.removeAttribute('href');
        }
      }
    } else if (tag === 'IMG') {
      let src = el.getAttribute?.('src') || '';
      const isPlaceholder = !src || src.startsWith('data:image/') || src.startsWith('about:');
      if (isPlaceholder) {
        for (const attr of lazySrcAttrs) {
          const candidate = el.getAttribute?.(attr);
          if (candidate && !candidate.startsWith('data:image/')) {
            src = candidate;
            break;
          }
        }
      }
      if (!el.getAttribute?.('srcset')) {
        const lazySrcset = el.getAttribute?.('data-srcset') || el.getAttribute?.('data-lazy-srcset');
        if (lazySrcset) el.setAttribute?.('srcset', lazySrcset);
      }
      if (src) {
        try { el.src = new URL(src, location.href).href; } catch (_) {}
      }
      try {
        el.loading = 'lazy';
        el.decoding = 'async';
      } catch (_) {}
    } else if (tag === 'H1') {
      if (getText(el).trim().length > 0) hasLeadingH1 = true;
    } else if (tag === 'H2' || tag === 'H3') {
      const headingText = getText(el).trim();
      if (headingText.length > 0 && tocHeadings.length < 40) {
        tocHeadings.push({ el, level: tag === 'H3' ? 3 : 2, text: headingText });
      }
    } else if (tag === 'TABLE') {
      tablesToWrap.push(el);
    }
  };

  stripInlineEvents(clone);
  if (clone.getAttribute?.('id') !== null && clone.getAttribute?.('id') !== undefined) {
    clone.removeAttribute('id');
  }

  if (typeof document.createTreeWalker === 'function') {
    const toRemove = [];
    const walker = document.createTreeWalker(clone, 1);
    let node = walker.nextNode();
    while (node) {
      if (shouldDropNode(node)) {
        toRemove.push(node);
        // Skip descendants of nodes we are about to remove.
        let skipped = walker.nextSibling();
        while (!skipped) {
          const parent = walker.parentNode();
          if (!parent || parent === clone) {
            node = null;
            break;
          }
          skipped = walker.nextSibling();
        }
        if (skipped) node = skipped;
        continue;
      }
      sanitizeElement(node);
      node = walker.nextNode();
    }
    for (const removed of toRemove) removed.remove();
  } else {
    clone.querySelectorAll('script,style,form,button,input,select,textarea,nav,aside,header,footer,dialog,iframe,video,audio,canvas,object,embed,base,meta,link,noscript,[role="navigation"],[role="dialog"]').forEach(node => node.remove());
    clone.querySelectorAll('*').forEach(sanitizeElement);
  }

  for (const table of tablesToWrap) {
    if (table.parentNode && typeof table.parentNode.insertBefore === 'function') {
      const wrapper = document.createElement('div');
      wrapper.className = 'misha-rm-table-wrap';
      table.parentNode.insertBefore(wrapper, table);
      wrapper.appendChild(table);
    }
  }

  const text = getText(clone).trim();
  if (text.length < 240) return false;

  const prefKey = '__misha_reader_prefs';
  const fontSteps = [0.94, 1.04, 1.15, 1.28, 1.42];
  const fonts = {
    sans: 'system-ui,-apple-system,"Segoe UI",sans-serif',
    serif: 'Georgia,Cambria,"Times New Roman",serif',
    mono: '"Cascadia Mono",Consolas,"Courier New",monospace'
  };
  const themes = {
    night: {
      scheme: 'dark',
      bg: '#190b17',
      fg: '#f7eef3',
      muted: '#cfaab9',
      link: '#ff8fc0',
      codeBg: 'rgba(255,255,255,0.08)',
      quoteBorder: '#ff78ae',
      barBg: 'rgba(38,16,35,0.82)',
      barBorder: 'rgba(255,179,208,0.25)',
      pillBg: 'rgba(255,255,255,0.07)',
      pillHover: 'rgba(255,120,174,0.2)',
      activeBg: 'rgba(255,120,174,0.28)',
      activeBorder: 'rgba(255,120,174,0.78)',
      activeFg: '#ffeaf4'
    },
    sepia: {
      scheme: 'light',
      bg: '#f4ecd8',
      fg: '#3b2d22',
      muted: '#735c49',
      link: '#963455',
      codeBg: 'rgba(92,70,50,0.09)',
      quoteBorder: '#963455',
      barBg: 'rgba(244,236,216,0.88)',
      barBorder: 'rgba(92,70,50,0.22)',
      pillBg: 'rgba(92,70,50,0.07)',
      pillHover: 'rgba(150,52,85,0.14)',
      activeBg: 'rgba(150,52,85,0.18)',
      activeBorder: 'rgba(150,52,85,0.62)',
      activeFg: '#5a1930'
    },
    light: {
      scheme: 'light',
      bg: '#faf8fb',
      fg: '#1e1620',
      muted: '#665363',
      link: '#9e2b61',
      codeBg: 'rgba(40,20,38,0.06)',
      quoteBorder: '#c2437e',
      barBg: 'rgba(255,255,255,0.88)',
      barBorder: 'rgba(40,20,38,0.16)',
      pillBg: 'rgba(40,20,38,0.06)',
      pillHover: 'rgba(194,67,126,0.14)',
      activeBg: 'rgba(194,67,126,0.16)',
      activeBorder: 'rgba(194,67,126,0.58)',
      activeFg: '#6c163f'
    }
  };
  let prefs = { theme: 'night', fontIndex: 2, width: 'compact', fontFamily: 'sans' };
  try {
    if (typeof localStorage !== 'undefined') {
      const saved = JSON.parse(localStorage.getItem(prefKey) || 'null');
      if (saved && typeof saved.theme === 'string' && Object.hasOwn(themes, saved.theme)) prefs.theme = saved.theme;
      if (saved && Number.isInteger(saved.fontIndex) && saved.fontIndex >= 0 && saved.fontIndex < fontSteps.length) prefs.fontIndex = saved.fontIndex;
      if (saved && (saved.width === 'compact' || saved.width === 'wide')) prefs.width = saved.width;
      if (saved && typeof saved.fontFamily === 'string' && Object.hasOwn(fonts, saved.fontFamily)) prefs.fontFamily = saved.fontFamily;
    }
  } catch (_) {}
  const savePrefs = () => {
    try {
      if (typeof localStorage !== 'undefined') localStorage.setItem(prefKey, JSON.stringify(prefs));
    } catch (_) {}
  };

  let wordCount = 0;
  let inWord = false;
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    const isSpace = code <= 32
      || code === 160
      || (code >= 0x2000 && code <= 0x200B)
      || code === 0x2028
      || code === 0x2029
      || code === 0x3000;
    if (isSpace) {
      inWord = false;
    } else if (!inWord) {
      inWord = true;
      wordCount++;
    }
  }
  const readMinutes = Math.max(1, Math.round(wordCount / 220));

  const overlay = document.createElement('div');
  overlay.id = id;
  overlay.tabIndex = -1;
  overlay.setAttribute('role', 'document');

  const styleEl = document.createElement('style');
  const progressTrack = document.createElement('div');
  progressTrack.id = '__misha_reader_progress_track';
  progressTrack.setAttribute('aria-hidden', 'true');
  const progressBar = document.createElement('div');
  progressBar.id = '__misha_reader_progress';
  progressBar.setAttribute('role', 'progressbar');
  progressBar.setAttribute('aria-label', 'Reading progress');
  progressBar.setAttribute('aria-valuemin', '0');
  progressBar.setAttribute('aria-valuemax', '100');
  progressBar.setAttribute('aria-valuenow', '0');
  progressTrack.appendChild(progressBar);
  const bar = document.createElement('div');
  bar.setAttribute('role', 'toolbar');
  bar.setAttribute('aria-label', 'Reader mode controls');

  const content = document.createElement('div');
  content.id = '__misha_reader_content';

  const queryMeta = (selector) => {
    if (typeof document.querySelector !== 'function') return '';
    try {
      const node = document.querySelector(selector);
      if (!node) return '';
      return (node.getAttribute?.('content') || node.getAttribute?.('datetime') || getText(node) || '').trim();
    } catch (_) {
      return '';
    }
  };

  if (!hasLeadingH1) {
    const pageTitle = queryMeta('meta[property="og:title"]')
      || queryMeta('h1')
      || (typeof document.title === 'string' ? document.title.trim() : '');
    if (pageTitle) {
      const header = document.createElement('div');
      header.className = 'misha-rm-header';
      let domainLabel = '';
      try { domainLabel = new URL(location.href).hostname.replace(/^www\./i, ''); } catch (_) {}
      const metaRow = document.createElement('div');
      metaRow.className = 'misha-rm-meta';
      if (domainLabel) {
        const domainPill = document.createElement('span');
        domainPill.className = 'misha-rm-domain';
        domainPill.textContent = domainLabel;
        metaRow.appendChild(domainPill);
      }
      const author = queryMeta('meta[name="author"]') || queryMeta('meta[property="article:author"]') || queryMeta('[rel="author"]');
      const publishedRaw = queryMeta('meta[property="article:published_time"]') || queryMeta('time[datetime]');
      let publishedLabel = '';
      if (publishedRaw) {
        const parsedDate = Date.parse(publishedRaw);
        if (!Number.isNaN(parsedDate)) {
          try {
            publishedLabel = new Date(parsedDate).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
          } catch (_) {
            publishedLabel = publishedRaw.slice(0, 10);
          }
        }
      }
      const metaBits = [];
      if (author && author.length <= 80) metaBits.push(author);
      if (publishedLabel) metaBits.push(publishedLabel);
      metaBits.push(`${wordCount.toLocaleString()} words \u00B7 ${readMinutes} min read`);
      const metaSummary = document.createElement('span');
      metaSummary.className = 'misha-rm-meta-text';
      metaSummary.textContent = metaBits.join(' \u00B7 ');
      metaRow.appendChild(metaSummary);
      const titleHeading = document.createElement('h1');
      titleHeading.className = 'misha-rm-title';
      titleHeading.textContent = pageTitle;
      header.appendChild(metaRow);
      header.appendChild(titleHeading);
      content.appendChild(header);
    }
  }

  if (tocHeadings.length >= 3) {
    const toc = document.createElement('details');
    toc.className = 'misha-rm-toc';
    const summary = document.createElement('summary');
    summary.textContent = `Table of contents (${tocHeadings.length})`;
    toc.appendChild(summary);
    const list = document.createElement('ol');
    list.className = 'misha-rm-toc-list';
    for (let index = 0; index < tocHeadings.length; index++) {
      const entry = tocHeadings[index];
      const anchorId = `__misha_rm_h_${index}`;
      entry.el.id = anchorId;
      const item = document.createElement('li');
      item.className = entry.level === 3 ? 'misha-rm-toc-sub' : 'misha-rm-toc-item';
      const link = document.createElement('a');
      link.href = `#${anchorId}`;
      link.textContent = entry.text;
      link.onclick = (event) => {
        if (event && typeof event.preventDefault === 'function') event.preventDefault();
        if (typeof entry.el.scrollIntoView === 'function') {
          entry.el.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }
      };
      item.appendChild(link);
      list.appendChild(item);
    }
    toc.appendChild(list);
    content.appendChild(toc);
  }

  content.appendChild(clone);

  const closeReader = () => {
    const savedOverflow = document.documentElement?.dataset?.mishaReaderOverflow;
    if (typeof overlay.__mishaCleanup === 'function') overlay.__mishaCleanup();
    overlay.remove();
    if (document.documentElement) {
      document.documentElement.style.overflow = savedOverflow ?? '';
      delete document.documentElement.dataset.mishaReaderOverflow;
    }
  };

  const makeButton = (label, ariaLabel, onClick) => {
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = label;
    button.setAttribute('aria-label', ariaLabel);
    button.className = 'misha-rm-btn';
    button.onclick = onClick;
    return button;
  };

  const timeBadge = document.createElement('span');
  timeBadge.className = 'misha-rm-badge';
  timeBadge.textContent = `${readMinutes} min read`;

  const sep1 = document.createElement('span');
  sep1.className = 'misha-rm-sep';
  const sepFont = document.createElement('span');
  sepFont.className = 'misha-rm-sep';
  const sep2 = document.createElement('span');
  sep2.className = 'misha-rm-sep';
  const sep3 = document.createElement('span');
  sep3.className = 'misha-rm-sep';

  const fontDownBtn = makeButton('A-', 'Decrease font size (-)', () => {
    if (prefs.fontIndex > 0) { prefs.fontIndex--; savePrefs(); applyState(); }
  });
  const fontUpBtn = makeButton('A+', 'Increase font size (+)', () => {
    if (prefs.fontIndex < fontSteps.length - 1) { prefs.fontIndex++; savePrefs(); applyState(); }
  });
  const sansBtn = makeButton('Sans', 'Sans-serif font', () => {
    prefs.fontFamily = 'sans'; savePrefs(); applyState();
  });
  const serifBtn = makeButton('Serif', 'Serif font', () => {
    prefs.fontFamily = 'serif'; savePrefs(); applyState();
  });
  const monoBtn = makeButton('Mono', 'Monospace font', () => {
    prefs.fontFamily = 'mono'; savePrefs(); applyState();
  });
  const compactBtn = makeButton('Compact', 'Compact column width', () => {
    prefs.width = 'compact'; savePrefs(); applyState();
  });
  const wideBtn = makeButton('Wide', 'Wide column width', () => {
    prefs.width = 'wide'; savePrefs(); applyState();
  });
  const nightBtn = makeButton('Night Bunny', 'Night Bunny theme', () => {
    prefs.theme = 'night'; savePrefs(); applyState();
  });
  const sepiaBtn = makeButton('Sepia', 'Sepia theme', () => {
    prefs.theme = 'sepia'; savePrefs(); applyState();
  });
  const lightBtn = makeButton('Light', 'Light theme', () => {
    prefs.theme = 'light'; savePrefs(); applyState();
  });
  const closeBtn = makeButton('\u00D7', 'Close reader mode (Esc)', closeReader);
  closeBtn.title = 'Close reader mode (Esc)';

  [timeBadge, sep1, fontDownBtn, fontUpBtn, sepFont, sansBtn, serifBtn, monoBtn, sep2, compactBtn, wideBtn, sep3, nightBtn, sepiaBtn, lightBtn, closeBtn]
    .forEach(node => bar.appendChild(node));

  let lastPct = -1;
  let progressRafId = 0;
  const applyProgress = () => {
    progressRafId = 0;
    const maxScroll = Math.max(0, (overlay.scrollHeight || 0) - (overlay.clientHeight || 0));
    const scrollTop = Math.max(0, overlay.scrollTop || 0);
    const ratio = maxScroll > 0 ? Math.min(1, scrollTop / maxScroll) : 0;
    const pct = Math.round(ratio * 100);
    if (pct === lastPct) return;
    lastPct = pct;
    progressBar.style.transform = `scaleX(${ratio.toFixed(4)})`;
    progressBar.setAttribute('aria-valuenow', String(pct));
  };
  const scheduleProgress = () => {
    if (typeof requestAnimationFrame === 'function') {
      if (progressRafId) return;
      progressRafId = requestAnimationFrame(applyProgress);
    } else {
      applyProgress();
    }
  };

  const applyState = () => {
    const t = themes[prefs.theme] || themes.night;
    const maxW = prefs.width === 'wide' ? '920px' : '680px';
    const fontEm = fontSteps[prefs.fontIndex] || 1.15;
    const fontStack = fonts[prefs.fontFamily] || fonts.sans;
    overlay.style.cssText = `position:fixed;inset:0;z-index:2147483646;overflow:auto;background:${t.bg};color:${t.fg};padding:16px 20px 72px;box-sizing:border-box;font:1em/1.68 ${fontStack};color-scheme:${t.scheme};`;
    bar.style.cssText = `position:sticky;top:12px;z-index:2;max-width:${maxW};margin:0 auto 24px;padding:8px 12px;border-radius:999px;display:flex;flex-wrap:wrap;align-items:center;justify-content:center;gap:4px;background:${t.barBg};border:1px solid ${t.barBorder};backdrop-filter:blur(16px);-webkit-backdrop-filter:blur(16px);box-shadow:inset 0 1px 0 rgba(255,255,255,0.14),0 12px 32px rgba(0,0,0,0.28);font-size:13px;line-height:1.2;box-sizing:border-box;`;
    content.style.cssText = `max-width:${maxW};margin:0 auto;font-size:${fontEm}em;font-family:${fontStack};overflow-wrap:break-word;`;
    progressBar.style.background = t.quoteBorder;
    styleEl.textContent = `#__misha_reader_progress_track{position:fixed;top:0;left:0;right:0;height:3px;z-index:3;background:transparent;pointer-events:none;}#__misha_reader_progress{height:100%;width:100%;transform-origin:0 50%;transform:scaleX(0);will-change:transform;transition:transform .12s ease-out;}#${id} h1,#${id} h2,#${id} h3,#${id} h4{line-height:1.25;}#${id} img{max-width:100%;height:auto;border-radius:10px;}#${id} figure{margin:20px 0;text-align:center;}#${id} figcaption{margin-top:8px;font-size:0.86em;color:${t.muted};line-height:1.45;}#${id} a{color:${t.link};text-decoration-thickness:1px;text-underline-offset:2px;}#${id} blockquote{border-left:3px solid ${t.quoteBorder};margin:16px 0;padding:4px 0 4px 16px;color:${t.muted};}#${id} code{font-family:${fonts.mono};font-size:0.88em;background:${t.codeBg};padding:2px 5px;border-radius:5px;}#${id} pre{font-family:${fonts.mono};background:${t.codeBg};border:1px solid ${t.barBorder};border-radius:10px;padding:14px 16px;overflow:auto;line-height:1.5;}#${id} pre code{background:transparent;padding:0;border-radius:0;font-size:0.9em;}#${id} .misha-rm-table-wrap{overflow-x:auto;-webkit-overflow-scrolling:touch;margin:16px 0;border-radius:8px;}#${id} table{border-collapse:collapse;width:100%;margin:0;font-size:0.95em;}#${id} th,#${id} td{border:1px solid ${t.barBorder};padding:8px 12px;text-align:left;}#${id} .misha-rm-header{margin:0 0 24px;padding-bottom:18px;border-bottom:1px solid ${t.barBorder};}#${id} .misha-rm-meta{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin-bottom:10px;font-size:0.78em;color:${t.muted};}#${id} .misha-rm-domain{display:inline-flex;align-items:center;padding:2px 10px;border-radius:999px;background:${t.pillBg};border:1px solid ${t.barBorder};color:${t.fg};font-weight:600;}#${id} .misha-rm-title{margin:0;font-size:1.85em;line-height:1.2;letter-spacing:-0.015em;}#${id} .misha-rm-toc{margin:0 0 24px;padding:12px 16px;border-radius:12px;background:${t.pillBg};border:1px solid ${t.barBorder};font-size:0.86em;}#${id} .misha-rm-toc summary{cursor:pointer;font-weight:600;color:${t.fg};user-select:none;}#${id} .misha-rm-toc-list{margin:10px 0 2px;padding-left:20px;display:flex;flex-direction:column;gap:4px;}#${id} .misha-rm-toc-sub{margin-left:14px;font-size:0.95em;opacity:0.92;}#${id} .misha-rm-badge{color:${t.muted};font-weight:600;padding:4px 12px;border-radius:999px;background:${t.pillBg};white-space:nowrap;}#${id} .misha-rm-sep{width:1px;height:16px;background:${t.barBorder};margin:0 2px;}#${id} .misha-rm-btn{appearance:none;border:1px solid transparent;background:${t.pillBg};color:${t.fg};border-radius:999px;padding:4px 12px;font:600 12.5px/1.2 system-ui,-apple-system,"Segoe UI",sans-serif;cursor:pointer;transition:background .14s ease,border-color .14s ease,opacity .14s ease;}#${id} .misha-rm-btn:hover{background:${t.pillHover};}#${id} .misha-rm-btn:focus-visible{outline:2px solid ${t.quoteBorder};outline-offset:2px;}#${id} .misha-rm-btn[data-active="true"]{background:${t.activeBg};border-color:${t.activeBorder};color:${t.activeFg};}#${id} .misha-rm-btn[disabled]{opacity:0.42;cursor:default;}@media (prefers-reduced-motion:reduce){#${id} *,#${id} .misha-rm-btn{transition:none !important;}}`;

    fontDownBtn.disabled = prefs.fontIndex <= 0;
    fontUpBtn.disabled = prefs.fontIndex >= fontSteps.length - 1;
    sansBtn.setAttribute('data-active', prefs.fontFamily === 'sans' ? 'true' : 'false');
    serifBtn.setAttribute('data-active', prefs.fontFamily === 'serif' ? 'true' : 'false');
    monoBtn.setAttribute('data-active', prefs.fontFamily === 'mono' ? 'true' : 'false');
    compactBtn.setAttribute('data-active', prefs.width === 'compact' ? 'true' : 'false');
    wideBtn.setAttribute('data-active', prefs.width === 'wide' ? 'true' : 'false');
    nightBtn.setAttribute('data-active', prefs.theme === 'night' ? 'true' : 'false');
    sepiaBtn.setAttribute('data-active', prefs.theme === 'sepia' ? 'true' : 'false');
    lightBtn.setAttribute('data-active', prefs.theme === 'light' ? 'true' : 'false');
  };

  applyState();
  applyProgress();

  const onKeyDown = (event) => {
    if (!event || event.ctrlKey || event.altKey || event.metaKey) return;
    if (event.key === 'Escape') {
      if (typeof event.preventDefault === 'function') event.preventDefault();
      if (typeof event.stopPropagation === 'function') event.stopPropagation();
      closeReader();
    } else if (event.key === '+' || event.key === '=') {
      if (prefs.fontIndex < fontSteps.length - 1) {
        if (typeof event.preventDefault === 'function') event.preventDefault();
        prefs.fontIndex++; savePrefs(); applyState();
      }
    } else if (event.key === '-') {
      if (prefs.fontIndex > 0) {
        if (typeof event.preventDefault === 'function') event.preventDefault();
        prefs.fontIndex--; savePrefs(); applyState();
      }
    }
  };
  if (typeof document.addEventListener === 'function') {
    document.addEventListener('keydown', onKeyDown, true);
  }
  if (typeof overlay.addEventListener === 'function') {
    overlay.addEventListener('scroll', scheduleProgress, { passive: true });
  }
  overlay.__mishaCleanup = () => {
    if (progressRafId && typeof cancelAnimationFrame === 'function') {
      cancelAnimationFrame(progressRafId);
      progressRafId = 0;
    }
    if (typeof document.removeEventListener === 'function') {
      document.removeEventListener('keydown', onKeyDown, true);
    }
    if (typeof overlay.removeEventListener === 'function') {
      overlay.removeEventListener('scroll', scheduleProgress);
    }
  };

  overlay.appendChild(styleEl);
  overlay.appendChild(progressTrack);
  overlay.appendChild(bar);
  overlay.appendChild(content);
  document.documentElement.dataset.mishaReaderOverflow = document.documentElement.style.overflow || '';
  document.documentElement.style.overflow = 'hidden';
  document.body.appendChild(overlay);
  if (typeof overlay.focus === 'function') overlay.focus();
  return true;
})()
""";
}
