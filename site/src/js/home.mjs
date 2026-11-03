// Wires the home page: the hero point cloud, the "Check a link" field, the publisher button, the price
// table and the snippet's copy button. The page's prose is plain HTML; this adds what needs data or script.
import { HERO_ASPECT, HERO_LINK, PUBLISHER_URL } from './config.mjs';
import { initCheckLink } from './check-link.mjs';
import { initCopyButton } from './copy-button.mjs';
import { CHECK_LINK_STRINGS, COPY_STRINGS, PRICE_STRINGS, cloudStrings } from './home-strings.mjs';
import { activeLang, pageLang } from './languages.mjs';
import { mountCloud } from './cloud.mjs';
import { hasSeenMosaic, startHeroRead } from './hero-read.mjs';
import { formatDate, formatXrp, priceRows, reserveSummary } from './price.mjs';
import { pathFromLink } from './route.mjs';

const PRICE_URL = '/data/price.json';

function langOf(document) {
  return activeLang(pageLang(document.documentElement.lang));
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
  mountCloud(
    canvas,
    {
      link: HERO_LINK,
      aspect: HERO_ASPECT,
      // A visitor who has already seen the hero in this session gets the quick replay.
      fast: hasSeenMosaic(),
      caption,
      note: document.getElementById('hero-note'),
      callouts: { file: document.getElementById('co-file'), link: document.getElementById('co-link') },
      card: document.getElementById('hero-card'),
      strings: cloudStrings(lang, network),
    },
    { startRead: startHeroRead },
  );
}

/** @param {string|null} [url] the release URL; injectable so a test need not edit config.mjs */
export function initPublisher(document, url = PUBLISHER_URL) {
  const button = document.getElementById('publisher');
  if (!button || url === null) return;
  // The button is disabled in the HTML; a configured release URL turns it into a plain link.
  const link = document.createElement('a');
  link.id = button.id;
  link.className = button.className;
  link.href = url;
  link.textContent = button.querySelector('[data-label]')?.textContent ?? button.textContent;
  button.replaceWith(link);
}

/** The "read from the ledger on <date>" line, with the date as a <time> element wherever the translation put it. */
export function priceSource(document, sentence, date, takenAt) {
  const source = element(document, 'p', undefined, 'price-source');
  const at = sentence.indexOf(date);
  // A translation that rephrases the date away must still show the sentence whole.
  if (at < 0) {
    source.textContent = sentence;
    return source;
  }
  const time = element(document, 'time', date);
  time.dateTime = takenAt;
  source.append(sentence.slice(0, at), time, sentence.slice(at + date.length));
  return source;
}

function renderPrice(document, container, json, lang) {
  const strings = PRICE_STRINGS[lang];
  const table = element(document, 'table', undefined, 'price-table__grid');
  table.append(element(document, 'caption', strings.caption, 'visually-hidden'));

  const head = element(document, 'tr');
  for (const key of ['size', 'fee']) {
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
    tr.append(element(document, 'td', row.fee, 'num'));
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
  const date = formatDate(json.takenAt, lang);
  nodes.push(priceSource(document, strings.source({ network: json.network, date }), date, json.takenAt));
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

  const copy = document.getElementById('snippet-copy');
  if (copy) {
    initCopyButton({
      button: copy,
      source: document.getElementById('embed-snippet'),
      status: document.getElementById('snippet-status'),
      strings: COPY_STRINGS[lang],
    });
  }

  return initPrice(document, lang, fetchJson);
}

if (typeof document !== 'undefined') initHome(document);
