#!/usr/bin/env node

// Executes the reader-mode script that MishaWeb actually injects, against a
// small DOM stub. The script is extracted from the C# source rather than
// restated here, so a change to the scoring has to survive extraction and
// execution to pass: this asserts behaviour, not the presence of a substring.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, '..', 'desktop', 'ReaderModeScript.cs'), 'utf8');

function extractToggle(text) {
  const marker = 'Toggle = """';
  const open = text.indexOf(marker);
  assert.ok(open >= 0, 'ReaderModeScript.cs has no Toggle raw string literal');
  const start = open + marker.length;
  const end = text.indexOf('"""', start);
  assert.ok(end > start, 'the Toggle raw string literal is unterminated');
  return text.slice(start, end);
}

const toggle = extractToggle(source);

// Each element counts its own descendant walks. Scoring is expected to walk a
// candidate exactly four times, once per selector it consults.
function makeElement(spec) {
  const element = {
    name: spec.name,
    walks: 0,
    children: [],
    innerText: spec.innerText ?? '',
    querySelectorAll(selector) {
      element.walks += 1;
      if (selector === 'a') return spec.anchors ?? [];
      if (selector === 'p') return spec.paragraphs ?? [];
      if (selector === 'h1,h2,h3') return spec.headings ?? [];
      if (selector.includes('nav')) return spec.bad ?? [];
      return [];
    },
    getAttribute: (name) => spec.attributes?.[name] ?? null,
    setAttribute() {},
    removeAttribute() {},
    remove() {},
    appendChild(node) {
      element.children.push(node);
      return node;
    },
    style: {},
    dataset: {},
    cloneNode() {
      // A clone carries the text but none of the selectors the scorer walks, so
      // counting walks measures scoring only.
      return makeElement({
        name: `${spec.name}-clone`,
        innerText: spec.innerText,
        attributes: {}
      });
    }
  };
  return element;
}

const filler = (count) => Array.from({ length: count }, () => ({}));

function runToggle(candidates, bySelector, bodyText = 'E'.repeat(600)) {
  const appended = [];
  const documentElement = { dataset: {}, style: { overflow: 'auto' } };
  const body = makeElement({ name: 'body', innerText: bodyText });
  body.appendChild = (node) => {
    appended.push(node);
    return node;
  };
  const document = {
    documentElement,
    body,
    getElementById: () => null,
    querySelectorAll: (selector) => bySelector[selector] ?? [],
    createElement: (tag) => makeElement({ name: tag })
  };
  const context = {
    document,
    location: { href: 'https://publisher.invalid/article' },
    URL,
    Set
  };
  const run = new Function(...Object.keys(context), `return (${toggle});`);
  return { applied: run(...Object.values(context)), appended };
}

const article = makeElement({
  name: 'article',
  innerText: 'A'.repeat(400),
  paragraphs: filler(6),
  headings: filler(2)
});
const nav = makeElement({
  name: 'nav',
  innerText: 'B'.repeat(60),
  anchors: [{ innerText: 'B'.repeat(30) }, { innerText: 'B'.repeat(30) }],
  bad: filler(3)
});
const aside = makeElement({
  name: 'aside',
  innerText: 'C'.repeat(80),
  bad: filler(2)
});
const footer = makeElement({
  name: 'footer',
  innerText: 'D'.repeat(40),
  bad: filler(1)
});

const candidates = [article, nav, aside, footer];
const page = runToggle(
  candidates,
  { article: [article], '#content': [nav, article, aside, footer] }
);

assert.equal(page.applied, true, 'reader mode should activate on a page with a strong article');

const overlay = page.appended.at(-1);
assert.ok(overlay, 'the overlay must be appended to the document body');
const content = overlay.children.at(-1);
assert.ok(content, 'the content wrapper must be appended to the overlay');
const shown = content.children.at(-1);

// The winner's clone is what gets shown, so the overlay holds a clone of the
// highest scoring candidate rather than of the first or the largest element.
assert.ok(shown, 'the winning candidate clone must be inserted into the content wrapper');
assert.equal(shown.name, 'article-clone');

// The scoring must walk each candidate's subtree exactly once. Recomputing the
// incumbent's score inside the comparison loop doubled that, which is the
// regression this guards: reader mode runs only on user request, so nothing else
// in the product would notice the wasted work.
for (const element of candidates) {
  assert.equal(
    element.walks,
    4,
    `${element.name} was walked ${element.walks} times, expected exactly 4 (one per selector)`
  );
}
const totalWalks = candidates.reduce((sum, element) => sum + element.walks, 0);
assert.equal(totalWalks, 4 * candidates.length);

// A page with no substantial content block must be left alone rather than being
// replaced by an overlay holding a navigation menu. The script falls back to
// body when no selector matches, so the body has to be thin too.
const stub = makeElement({ name: 'nav', innerText: 'B'.repeat(60), bad: filler(3) });
const thin = runToggle([stub], { '#main': [stub] }, 'B'.repeat(60));
assert.equal(thin.applied, false, 'a page without a substantial content block must not activate');
assert.equal(thin.appended.length, 0, 'nothing may be appended when reader mode declines to run');
assert.equal(stub.walks, 4, 'the declined path must still score its candidate exactly once');

console.log(JSON.stringify({
  status: 'PASS',
  contract: 'reader mode scores each candidate subtree once and shows the highest scoring one'
}));
