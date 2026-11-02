// Wires the home page: the hero point cloud, the "Check a link" field, the publisher button, the price
// table and the snippet's copy button. The page's prose is plain HTML; this adds what needs data or script.
import { HERO_ASPECT, HERO_LINK, PUBLISHER_URL } from './config.mjs';
import { initCheckLink } from './check-link.mjs';
import { initCopyButton } from './copy-button.mjs';
import { CHECK_LINK_STRINGS, COPY_STRINGS, PRICE_STRINGS, cloudStrings } from './home-strings.mjs';
import { mountCloud } from './cloud.mjs';
import { formatDate, formatXrp, priceRows, reserveSummary } from './price.mjs';
import { pathFromLink } from './route.mjs';

const PRICE_URL = '/data/price.json';

function langOf(document) {
  return document.documentElement.lang === 'ru' ? 'ru' : 'en';
}

function element(document, tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}

function initHero(document, lang) {
  const canvas = document.getElementById('hero-cloud');
  const caption = document.getElementById('hero-caption');
  if (!canvas || !caption) return;
  // The caption names the network the file was read from; it is derived from the same constant the
  // cloud reads, never written twice.
  const network = pathFromLink(HERO_LINK)?.split('/')[1] ?? '';
  mountCloud(canvas, {
    link: HERO_LINK,
    aspect: HERO_ASPECT,
    caption,
    note: document.getElementById('hero-note'),
    callouts: { file: document.getElementById('co-file'), link: document.getElementById('co-link') },
    card: document.getElementById('hero-card'),
    strings: cloudStrings(lang, network),
  });
}

function initPublisher(document) {
  const button = document.getElementById('publisher');
  if (!button || PUBLISHER_URL === null) return;
  // The button is disabled in the HTML; a configured release URL turns it into a plain link.
  const link = document.createElement('a');
  link.id = button.id;
  link.className = button.className;
  link.href = PUBLISHER_URL;
  link.textContent = button.querySelector('[data-label]')?.textContent ?? button.textContent;
  button.replaceWith(link);
}

function renderPrice(document, container, json, lang) {
  const strings = PRICE_STRINGS[lang];
  const table = element(document, 'table', undefined, 'price-table__grid');
  table.append(element(document, 'caption', strings.caption, 'visually-hidden'));

  const head = element(document, 'tr');
  for (const key of ['size', 'fee', 'held', 'returned']) {
    const label = key === 'size' ? strings[key] : `${strings[key]} (${strings.unit})`;
    const cell = element(document, 'th', label, key === 'size' ? undefined : 'num');
    cell.scope = 'col';
    head.append(cell);
  }
  const thead = element(document, 'thead');
  thead.append(head);
  table.append(thead);

  const body = element(document, 'tbody');
  for (const row of priceRows(json, lang)) {
    const tr = element(document, 'tr');
    const size = element(document, 'th', row.size);
    size.scope = 'row';
    tr.append(size);
    for (const key of ['fee', 'held', 'returned']) tr.append(element(document, 'td', row[key], 'num'));
    body.append(tr);
  }
  table.append(body);

  const scroller = element(document, 'div', undefined, 'price-table__scroll');
  scroller.tabIndex = 0;
  scroller.setAttribute('role', 'region');
  scroller.setAttribute('aria-label', strings.caption);
  scroller.append(table);

  const nodes = [scroller];
  const reserve = reserveSummary(json);
  if (reserve) {
    nodes.push(
      element(
        document,
        'p',
        strings.reserve({
          held: formatXrp(reserve.heldDrops, lang),
          returned: formatXrp(reserve.returnedDrops, lang),
          kept: formatXrp(reserve.keptDrops, lang),
        }),
      ),
    );
  }
  const source = element(document, 'p', undefined, 'price-source');
  const date = formatDate(json.takenAt, lang);
  const time = element(document, 'time', date);
  time.dateTime = json.takenAt;
  const sentence = strings.source({ network: json.network, date });
  const at = sentence.indexOf(date);
  source.append(sentence.slice(0, at), time, sentence.slice(at + date.length));
  nodes.push(source);
  container.replaceChildren(...nodes);
}

async function initPrice(document, lang, fetchJson) {
  const container = document.getElementById('price-data');
  if (!container) return;
  try {
    const json = await fetchJson(PRICE_URL);
    renderPrice(document, container, json, lang);
  } catch {
    // The static fallback sentence stays; a missing table must not break the rest of the page.
  }
}

async function defaultFetchJson(url) {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${url}: ${response.status}`);
  return response.json();
}

/** @param {Document} document */
export function initHome(document, fetchJson = defaultFetchJson) {
  const lang = langOf(document);
  initHero(document, lang);
  initPublisher(document);

  const form = document.getElementById('check-link');
  if (form) {
    initCheckLink({
      form,
      input: document.getElementById('check-link-input'),
      message: document.getElementById('check-link-error'),
      strings: CHECK_LINK_STRINGS[lang],
    });
  }

  for (const suffix of ['', '-alt']) {
    const button = document.getElementById(`snippet-copy${suffix}`);
    if (!button) continue;
    initCopyButton({
      button,
      source: document.getElementById(`embed-snippet${suffix}`),
      status: document.getElementById(`snippet-status${suffix}`),
      strings: COPY_STRINGS[lang],
    });
  }

  return initPrice(document, lang, fetchJson);
}

if (typeof document !== 'undefined') initHome(document);
