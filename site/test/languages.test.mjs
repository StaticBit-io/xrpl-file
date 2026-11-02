import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  ACTIVE_LANGS, LANGS, LANGUAGE_NAMES, homePath, htmlLang, isActive, langFromTag, localeOf, pageLang, pathPrefix,
} from '../src/js/languages.mjs';
import { STRINGS } from '../src/js/chrome-strings.mjs';
import { TABLES } from '../src/js/strings.mjs';
import {
  CHECK_LINK_STRINGS, COPY_STRINGS, NETWORK_NAMES, NETWORK_SHORT, PRICE_STRINGS, cloudStrings,
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
