import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  ACTIVE_LANGS, LANGS, LANGUAGE_NAMES, homePath, htmlLang, isActive, langFromTag, localeOf, pageLang, pathPrefix,
} from '../src/js/languages.mjs';
import { STRINGS } from '../src/js/chrome-strings.mjs';
import { TABLES } from '../src/js/strings.mjs';
import {
  CHECK_LINK_STRINGS, COPY_STRINGS, NETWORK_NAMES, NETWORK_SHORT, PRICE_STRINGS, cloudStrings, fileCloudStrings,
} from '../src/js/home-strings.mjs';
import { SIZE_UNITS } from '../src/js/price.mjs';

/** The structure of a value without its words: keys of objects, "fn" for functions, "text" for strings. */
function shape(value) {
  if (typeof value === 'function') return 'fn';
  if (typeof value === 'string') return 'text';
  return Object.fromEntries(Object.keys(value).sort().map((key) => [key, shape(value[key])]));
}

const placeholders = (text) => [...text.matchAll(/\{(\w+)\}/g)].map((match) => match[1]).sort();

test('the six languages have a name in their own language, and English is the default home', () => {
  assert.deepEqual(Object.keys(LANGUAGE_NAMES), LANGS);
  assert.deepEqual(Object.values(LANGUAGE_NAMES), ['English', 'Русский', 'Deutsch', 'Español', 'Português', '中文']);
  assert.equal(homePath('en'), '/');
  assert.equal(homePath('zh'), '/zh/');
  assert.equal(pathPrefix('pt'), '/pt');
});

test('the Chinese pages are tagged with their script, every other language with its own code', () => {
  assert.equal(htmlLang('zh'), 'zh-Hans');
  for (const lang of LANGS.filter((code) => code !== 'zh')) assert.equal(htmlLang(lang), lang);
  for (const lang of LANGS) assert.equal(langFromTag(htmlLang(lang)), lang, 'the tag leads back to the language');
  assert.equal(pageLang('zh-Hans'), 'zh');
  assert.equal(pageLang('fr'), 'en');
  assert.equal(pageLang(undefined), 'en');
});

test('every language maps to a locale Intl really has, and the locale speaks that language', () => {
  for (const lang of LANGS) {
    const locale = localeOf(lang);
    assert.deepEqual(Intl.NumberFormat.supportedLocalesOf(locale), [locale], `${lang}: ${locale} is not supported`);
    assert.equal(new Intl.Locale(locale).language, lang);
  }
  assert.equal(localeOf('xx'), localeOf('en'));
  assert.equal(new Intl.Locale(localeOf('pt')).region, 'BR');
  assert.equal(new Intl.Locale(localeOf('zh')).script, 'Hans');
});

test('only a known language can be active', () => {
  assert.ok(ACTIVE_LANGS.every((lang) => LANGS.includes(lang)));
  assert.equal(isActive('en'), true);
  assert.equal(isActive('klingon'), false);
});

test('every string table has the same keys in every active language', () => {
  const tables = {
    'chrome strings': STRINGS,
    'file and 404 strings': TABLES,
    'check-link strings': CHECK_LINK_STRINGS,
    'copy strings': COPY_STRINGS,
    'price strings': PRICE_STRINGS,
    'network names': NETWORK_NAMES,
    'size units': SIZE_UNITS,
  };
  for (const [name, table] of Object.entries(tables)) {
    for (const lang of ACTIVE_LANGS) assert.ok(table[lang], `${name}: no ${lang} table`);
    const [first, ...rest] = ACTIVE_LANGS;
    for (const lang of rest) assert.deepEqual(shape(table[lang]), shape(table[first]), `${name}: ${lang} differs from ${first}`);
  }
  for (const lang of ACTIVE_LANGS) assert.equal(typeof NETWORK_SHORT[lang], 'object', `network short names: no ${lang} table`);
});

test('the cloud strings have the same shape in every active language, and every function answers with text', () => {
  const [first, ...rest] = ACTIVE_LANGS;
  for (const lang of rest) {
    assert.deepEqual(shape(cloudStrings(lang, 'testnet')), shape(cloudStrings(first, 'testnet')), lang);
  }
  for (const lang of ACTIVE_LANGS) {
    const strings = cloudStrings(lang, 'testnet');
    for (const phase of ['reading', 'developing', 'done', 'failed']) {
      for (const chunks of [null, 1, 123]) {
        const label = strings.label(phase, { chunks });
        assert.ok(typeof label === 'string' && label.length > 0, `${lang} label ${phase}`);
      }
    }
    for (const code of ['NodeUnreachable', 'Stalled', 'BlockMismatch', 'DecodeFailed', 'NodeLacksHistory', 'ChunkMissing', 'Other', undefined]) {
      assert.ok(strings.failed(code).length > 0, `${lang} failed ${code}`);
    }
    assert.ok(strings.caption({ chunks: 123, firstLedger: 5, lastLedger: 7 }).length > 0);
    assert.ok(strings.caption({ chunks: 1, firstLedger: 5, lastLedger: 5 }).length > 0);
    for (const callout of [strings.fileCallout(123), strings.fileCallout(1), strings.linkCallout(84), strings.linkCallout(1)]) {
      assert.deepEqual(Object.keys(callout).sort(), ['caption', 'unit', 'value']);
      assert.ok(callout.caption && callout.value && callout.unit, `${lang} callout ${JSON.stringify(callout)}`);
    }
  }
});

test("the file page's cloud strings have the same shape in every active language, and every function answers with text", () => {
  const [first, ...rest] = ACTIVE_LANGS;
  for (const lang of rest) {
    assert.deepEqual(shape(fileCloudStrings(lang, 'testnet')), shape(fileCloudStrings(first, 'testnet')), lang);
  }
  for (const lang of ACTIVE_LANGS) {
    const strings = fileCloudStrings(lang, 'testnet');
    assert.deepEqual(Object.keys(strings).sort(), ['caption', 'failed', 'label', 'note', 'reading']);
    for (const phase of ['reading', 'done', 'failed']) {
      for (const chunks of [null, 1, 123]) {
        for (const perCube of [1, 84]) {
          const label = strings.label(phase, { chunks, perCube });
          assert.ok(typeof label === 'string' && label.length > 0, `${lang} label ${phase}`);
          assert.ok(!label.includes('undefined'), `${lang} label ${phase}`);
        }
      }
    }
    assert.ok(strings.label('reading', { chunks: 123, perCube: 84 }).includes('84'), `${lang} label names the chunks to a cube`);
    assert.ok(!strings.label('reading', { chunks: 123, perCube: 1 }).includes('84'));
    for (const perCube of [1, 84]) {
      for (const [firstLedger, lastLedger] of [[5, 7], [5, 5]]) {
        const reading = strings.reading({ chunks: 123, perCube, firstLedger, lastLedger });
        assert.ok(reading.includes('123') && reading.includes('5'), `${lang} reading`);
        assert.ok(!reading.includes('undefined'), `${lang} reading`);
      }
      for (const capped of [false, true]) {
        const note = strings.note({ ledgers: 40, ledgersPerPad: 3, capped, perCube });
        assert.ok(typeof note === 'string' && note.length > 0, `${lang} note`);
        assert.ok(!note.includes('undefined'), `${lang} note`);
        assert.equal(note.includes('84'), perCube > 1, `${lang} note names the chunks to a cube only when there are several`);
        assert.equal(note.includes('40'), capped, `${lang} note names the span only when the ground is capped`);
      }
    }
    assert.equal(strings.note({ ledgers: null, ledgersPerPad: null, capped: false, perCube: 1 }), '', `${lang} note before the manifest`);
    for (const code of ['NodeUnreachable', 'Stalled', 'BlockMismatch', 'DecodeFailed', 'NodeLacksHistory', 'ManifestNotFound', 'ChunkMissing', 'Other', undefined]) {
      assert.ok(strings.failed(code).length > 0, `${lang} failed ${code}`);
    }
    assert.equal(strings.caption(), '', `${lang}: the file replaces the cloud's end line`);
  }
});

test("the file page's cloud strings take any input without throwing, and an unknown language reads English", () => {
  for (const lang of ACTIVE_LANGS) {
    const strings = fileCloudStrings(lang, 'devnet');
    assert.doesNotThrow(() => strings.label('unknown', {}), lang);
    assert.doesNotThrow(() => strings.label('reading'), lang);
    assert.doesNotThrow(() => strings.reading({}), lang);
    assert.doesNotThrow(() => strings.reading(), lang);
    assert.doesNotThrow(() => strings.note({}), lang);
    assert.doesNotThrow(() => strings.note(), lang);
    assert.ok(strings.reading({ chunks: 2, perCube: 1, firstLedger: 5, lastLedger: 5 }).includes('devnet'), `${lang} names a network without a short form as it is`);
  }
  assert.deepEqual(shape(fileCloudStrings('xx', 'testnet')), shape(fileCloudStrings('en', 'testnet')));
  const args = { chunks: 1, perCube: 1 };
  assert.equal(fileCloudStrings('xx', 'testnet').label('done', args), fileCloudStrings('en', 'testnet').label('done', args));
});

test('a string with placeholders asks for the same values in every language', () => {
  for (const key of Object.keys(TABLES.en)) {
    for (const lang of ACTIVE_LANGS) assert.deepEqual(placeholders(TABLES[lang][key]), placeholders(TABLES.en[key]), `${lang}.${key}`);
  }
});

test('the price sentences name every figure they are given, in every language', () => {
  for (const lang of ACTIVE_LANGS) {
    const reserve = PRICE_STRINGS[lang].reserve({ held: 'HELD', returned: 'RETURNED', kept: 'KEPT' });
    for (const figure of ['HELD', 'RETURNED', 'KEPT']) assert.ok(reserve.includes(figure), `${lang} reserve ${figure}`);
    const source = PRICE_STRINGS[lang].source({ network: 'mainnet', date: 'DATE' });
    assert.ok(source.includes('DATE'), `${lang} source date`);
    assert.ok(!source.includes('undefined'));
  }
});

test('every active language has its home page, tagged with its own <html lang>', async () => {
  for (const lang of ACTIVE_LANGS) {
    const html = await readFile(new URL(`../src/${lang === 'en' ? '' : `${lang}/`}index.html`, import.meta.url), 'utf8');
    assert.match(html, new RegExp(`<html lang="${htmlLang(lang)}">`), lang);
  }
});

// The English reserve sentence has no actor: the reserve is part of the publishing account's own balance,
// held by the ledger's rules. A translation must not make the network the one who takes or gives it.
test('the reserve sentence names no actor that holds or returns the reserve, in any language', () => {
  const actor = /bindet das Netzwerk|gibt es .* zurück|la red retiene|devuelve|a rede retém|devolve|сеть удерживает|网络.{0,3}(锁定|退还)/;
  for (const lang of ACTIVE_LANGS) {
    const reserve = PRICE_STRINGS[lang].reserve({ held: 'HELD', returned: 'RETURNED', kept: 'KEPT' });
    assert.doesNotMatch(reserve, actor, lang);
  }
});

// English says "right now" or "just now" only where the string is about a moment (a failed read, a missing
// price table) and nothing for a file that is not in the ledger; the other languages follow it string by string.
test('"save unavailable" carries no time qualifier in any language, as in English', () => {
  const qualifier = /\b(now|right now|just now)\b|сейчас|gerade|ahora|agora|暂时|目前|刚才/;
  for (const lang of ACTIVE_LANGS) assert.doesNotMatch(TABLES[lang].saveUnavailable, qualifier, lang);
});

// The one string that does speak of a moment: the price fallback says it is not available right now.
// Chinese 暂时 ("for the time being") reads as a promise that it will come back; 目前 states the moment.
test('the price fallback hint says "right now" in every language, as in English', async () => {
  const moment = { en: /right now/, ru: /сейчас/, de: /gerade/, es: /ahora mismo/, pt: /agora/, zh: /目前/ };
  for (const lang of ACTIVE_LANGS) {
    const html = await readFile(new URL(`../src/${lang === 'en' ? '' : `${lang}/`}index.html`, import.meta.url), 'utf8');
    const hint = html.match(/<div id="price-data"[^>]*><p class="hint">(.*?)<\/p>/)?.[1];
    assert.ok(hint, `${lang} price fallback hint`);
    assert.match(hint, moment[lang], lang);
  }
  const zh = await readFile(new URL('../src/zh/index.html', import.meta.url), 'utf8');
  assert.ok(!zh.includes('暂时'), 'zh: 暂时 promises a return');
});

test('the German pages define "Chunk" before they use it bare', async () => {
  const home = await readFile(new URL('../src/de/index.html', import.meta.url), 'utf8');
  const first = home.indexOf('Chunk');
  assert.ok(first > 0);
  assert.match(home.slice(first - 10, first + 12), /\(Chunks\)/, 'the first use is the explained one');
  assert.match(TABLES.de.howText, /^[^C]*\(Chunks\)/, 'the file page explains it at first use');
});

test('the Chinese home says 条目 for the ledger entry throughout, not 记录, and does not say the company went bankrupt', async () => {
  const home = await readFile(new URL('../src/zh/index.html', import.meta.url), 'utf8');
  assert.ok(home.includes('条目'));
  assert.ok(!home.includes('记录'));
  assert.ok(!home.includes('倒闭'));
});
