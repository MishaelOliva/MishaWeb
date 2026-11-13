namespace MishaWeb;

internal static class ReaderModeScript
{
    // This is intentionally app-owned and injected only after the user requests
    // reader mode. It never runs at document start and never makes a request.
    public const string Toggle = """
(() => {
  const id = '__misha_reader_overlay';
  const priorOverflow = document.documentElement?.dataset?.mishaReaderOverflow;
  const existing = document.getElementById(id);
  if (existing) {
    if (typeof existing.__mishaCleanup === 'function') existing.__mishaCleanup();
    existing.remove();
    if (document.documentElement) {
      document.documentElement.style.overflow = priorOverflow ?? '';
      delete document.documentElement.dataset.mishaReaderOverflow;
    }
    return true;
  }
  if (!document.body || !document.documentElement) return false;
  const selectors = ['article', 'main', '[role="main"]', '[itemprop="articleBody"]', '.article-body', '.post-content', '.entry-content', '.story-body', '#content', '#main'];
  const candidates = [];
  const seen = new Set();
  for (const selector of selectors) {
    for (const element of document.querySelectorAll(selector)) {
      if (!seen.has(element)) { seen.add(element); candidates.push(element); }
    }
  }
  if (!candidates.length) candidates.push(document.body);
  // The incumbent's score is carried in a local rather than recomputed. The
  // previous form called score(winner) inside the comparison, so N candidates
  // cost about 2N scores, and each score is four descendant walks plus a read of
  // innerText on every anchor, which forces a synchronous layout. Reader mode
  // candidates nest, so the same paragraph subtree was re-walked repeatedly.
  // Nothing in the loop mutates the document and candidates are deduplicated,
  // so every candidate is now scored exactly once and the ranking is unchanged.
  const score = (element) => {
    const text = (element.innerText || '').trim();
    let linkCharacters = 0;
    for (const anchor of element.querySelectorAll('a')) {
      linkCharacters += (anchor.innerText || '').length;
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
  if (((winner.innerText || '').trim()).length < 320) return false;
  const clone = winner.cloneNode(true);
  clone.querySelectorAll('script,style,form,button,input,select,textarea,nav,aside,header,footer,dialog,iframe,video,audio,canvas,[role="navigation"],[role="dialog"]').forEach(node => node.remove());
  clone.querySelectorAll('[id]').forEach(node => node.removeAttribute('id'));
  clone.querySelectorAll('a[href]').forEach(a => { try { a.href = new URL(a.getAttribute('href'), location.href).href; } catch (_) {} });
  clone.querySelectorAll('img[src]').forEach(img => { try { img.src = new URL(img.getAttribute('src'), location.href).href; } catch (_) {} });
  const text = (clone.innerText || '').trim();
  if (text.length < 240) return false;

  const prefKey = '__misha_reader_prefs';
  const fontSteps = [0.94, 1.04, 1.15, 1.28, 1.42];
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
  let prefs = { theme: 'night', fontIndex: 2, width: 'compact' };
  try {
    if (typeof localStorage !== 'undefined') {
      const saved = JSON.parse(localStorage.getItem(prefKey) || 'null');
      if (saved && themes[saved.theme]) prefs.theme = saved.theme;
      if (saved && Number.isInteger(saved.fontIndex) && saved.fontIndex >= 0 && saved.fontIndex < fontSteps.length) prefs.fontIndex = saved.fontIndex;
      if (saved && (saved.width === 'compact' || saved.width === 'wide')) prefs.width = saved.width;
    }
  } catch (_) {}
  const savePrefs = () => {
    try {
      if (typeof localStorage !== 'undefined') localStorage.setItem(prefKey, JSON.stringify(prefs));
    } catch (_) {}
  };

  const wordCount = text.split(/\s+/).filter(Boolean).length;
  const readMinutes = Math.max(1, Math.round(wordCount / 220));

  const overlay = document.createElement('div');
  overlay.id = id;
  overlay.tabIndex = -1;
  overlay.setAttribute('role', 'document');

  const styleEl = document.createElement('style');
  const bar = document.createElement('div');
  bar.setAttribute('role', 'toolbar');
  bar.setAttribute('aria-label', 'Reader mode controls');

  const content = document.createElement('div');
  content.id = '__misha_reader_content';
  content.appendChild(clone);
  content.querySelectorAll('h1,h2,h3,h4').forEach(h => { h.style.lineHeight = '1.25'; });
  content.querySelectorAll('img').forEach(img => { img.style.maxWidth = '100%'; img.style.height = 'auto'; img.style.borderRadius = '10px'; });

  const closeReader = () => {
    if (typeof overlay.__mishaCleanup === 'function') overlay.__mishaCleanup();
    overlay.remove();
    if (document.documentElement) {
      document.documentElement.style.overflow = priorOverflow ?? '';
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
  const sep2 = document.createElement('span');
  sep2.className = 'misha-rm-sep';
  const sep3 = document.createElement('span');
  sep3.className = 'misha-rm-sep';

  const fontDownBtn = makeButton('A-', 'Decrease font size', () => {
    if (prefs.fontIndex > 0) { prefs.fontIndex--; savePrefs(); applyState(); }
  });
  const fontUpBtn = makeButton('A+', 'Increase font size', () => {
    if (prefs.fontIndex < fontSteps.length - 1) { prefs.fontIndex++; savePrefs(); applyState(); }
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

  [timeBadge, sep1, fontDownBtn, fontUpBtn, sep2, compactBtn, wideBtn, sep3, nightBtn, sepiaBtn, lightBtn, closeBtn]
    .forEach(node => bar.appendChild(node));

  const applyState = () => {
    const t = themes[prefs.theme] || themes.night;
    const maxW = prefs.width === 'wide' ? '920px' : '680px';
    const fontEm = fontSteps[prefs.fontIndex] || 1.15;
    overlay.style.cssText = `position:fixed;inset:0;z-index:2147483646;overflow:auto;background:${t.bg};color:${t.fg};padding:18px 20px 72px;box-sizing:border-box;font:1em/1.68 system-ui,-apple-system,"Segoe UI",sans-serif;color-scheme:${t.scheme};`;
    bar.style.cssText = `position:sticky;top:12px;z-index:2;max-width:${maxW};margin:0 auto 22px;padding:7px 10px;border-radius:999px;display:flex;flex-wrap:wrap;align-items:center;justify-content:center;gap:6px;background:${t.barBg};border:1px solid ${t.barBorder};backdrop-filter:blur(16px);-webkit-backdrop-filter:blur(16px);box-shadow:0 10px 28px rgba(0,0,0,0.24);font-size:13px;line-height:1.2;box-sizing:border-box;`;
    content.style.cssText = `max-width:${maxW};margin:0 auto;font-size:${fontEm}em;overflow-wrap:break-word;`;
    styleEl.textContent = `#${id} a{color:${t.link};text-decoration-thickness:1px;text-underline-offset:2px;}#${id} blockquote{border-left:3px solid ${t.quoteBorder};margin:1.1em 0;padding:0.2em 0 0.2em 1em;color:${t.muted};}#${id} pre,#${id} code{background:${t.codeBg};border-radius:6px;}#${id} pre{padding:12px 14px;overflow:auto;}#${id} .misha-rm-badge{color:${t.muted};font-weight:600;padding:4px 10px;border-radius:999px;background:${t.pillBg};white-space:nowrap;}#${id} .misha-rm-sep{width:1px;height:16px;background:${t.barBorder};margin:0 2px;}#${id} .misha-rm-btn{appearance:none;border:1px solid transparent;background:${t.pillBg};color:${t.fg};border-radius:999px;padding:5px 11px;font:600 12.5px/1.2 system-ui,-apple-system,"Segoe UI",sans-serif;cursor:pointer;transition:background .14s ease,border-color .14s ease,opacity .14s ease;}#${id} .misha-rm-btn:hover{background:${t.pillHover};}#${id} .misha-rm-btn[data-active="true"]{background:${t.activeBg};border-color:${t.activeBorder};color:${t.activeFg};}#${id} .misha-rm-btn[disabled]{opacity:0.42;cursor:default;}`;

    fontDownBtn.disabled = prefs.fontIndex <= 0;
    fontUpBtn.disabled = prefs.fontIndex >= fontSteps.length - 1;
    compactBtn.setAttribute('data-active', prefs.width === 'compact' ? 'true' : 'false');
    wideBtn.setAttribute('data-active', prefs.width === 'wide' ? 'true' : 'false');
    nightBtn.setAttribute('data-active', prefs.theme === 'night' ? 'true' : 'false');
    sepiaBtn.setAttribute('data-active', prefs.theme === 'sepia' ? 'true' : 'false');
    lightBtn.setAttribute('data-active', prefs.theme === 'light' ? 'true' : 'false');
  };

  applyState();

  const onKeyDown = (event) => {
    if (event && event.key === 'Escape') {
      if (typeof event.preventDefault === 'function') event.preventDefault();
      if (typeof event.stopPropagation === 'function') event.stopPropagation();
      closeReader();
    }
  };
  if (typeof document.addEventListener === 'function') {
    document.addEventListener('keydown', onKeyDown, true);
  }
  overlay.__mishaCleanup = () => {
    if (typeof document.removeEventListener === 'function') {
      document.removeEventListener('keydown', onKeyDown, true);
    }
  };

  overlay.appendChild(styleEl);
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
