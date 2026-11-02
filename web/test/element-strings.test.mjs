import { test } from 'node:test';
import assert from 'node:assert/strict';
import { XrplFileError } from '../src/link.mjs';
import { STRINGS, languageFor, stringsFor } from '../src/element/strings.mjs';
import { languageOf } from '../src/element/attributes.mjs';
import { messageFor, shortTitleFor, saveMessageFor, detailFor, elementError, NO_NODE, BAD_NODE, INSECURE, NO_LINK, TOO_LARGE, DECODE_FAILED } from '../src/element/messages.mjs';
import { formatBytes, bytesOf, progressLines, downloadLabel } from '../src/element/format.mjs';
import { captionParts, captionState } from '../src/element/host.mjs';

const LANGUAGES = ['en', 'ru', 'de', 'es', 'pt', 'zh'];

// A value that takes arguments is called with these; its result must be a finished sentence.
const SAMPLE = [7, 8, 'drawing.svg'];

test('the six languages of the site have a table', () => {
  assert.deepEqual(Object.keys(STRINGS), LANGUAGES);
});

test('every language has exactly the keys of English, and no word is empty', () => {
  const keys = Object.keys(STRINGS.en).sort();
  for (const language of LANGUAGES) {
    assert.deepEqual(Object.keys(STRINGS[language]).sort(), keys, language);
    for (const key of keys) {
      const english = STRINGS.en[key];
      const value = STRINGS[language][key];
      assert.equal(typeof value, typeof english, `${language}.${key} has the type English has`);
      if (typeof value === 'function') {
        assert.equal(value.length, english.length, `${language}.${key} takes the arguments English takes`);
        const text = value(...SAMPLE.slice(0, value.length));
        assert.ok(typeof text === 'string' && text.trim().length > 0, `${language}.${key}() is empty`);
      } else {
        assert.ok(value.length > 0 && value === value.trim(), `${language}.${key} is empty or padded`);
      }
    }
  }
});

test('a sentence that takes a block number carries it, in every language', () => {
  for (const language of LANGUAGES) {
    for (const key of ['blockMismatch', 'blockMismatchPlaying', 'chunkMissing', 'chunkMissingPlaying', 'saveBlockMismatch', 'saveChunkMissing']) {
      assert.ok(STRINGS[language][key](4711).includes('4711'), `${language}.${key}`);
    }
  }
});

test('no sentence has an exclamation mark, a "please" or a long dash', () => {
  for (const language of LANGUAGES) {
    for (const [key, value] of Object.entries(STRINGS[language])) {
      const text = typeof value === 'function' ? value(...SAMPLE.slice(0, value.length)) : value;
      assert.ok(!/[!！]/.test(text), `${language}.${key} shouts`);
      assert.ok(!text.includes('—'), `${language}.${key} has a long dash`);
    }
  }
  for (const text of Object.values(STRINGS.en)) assert.ok(typeof text === 'function' || !/please/i.test(text));
});

test('a short title fits a narrow box in every language', () => {
  for (const language of LANGUAGES) {
    for (const [key, value] of Object.entries(STRINGS[language])) {
      if (key.startsWith('short')) assert.ok(value.length <= (language === 'en' ? 12 : 16), `${language}.${key}: ${value}`);
    }
  }
});

test('the English table is the text the documentation quotes', () => {
  assert.equal(STRINGS.en.noNode, 'This network has no built-in node. Set the node attribute.');
  assert.equal(STRINGS.en.blockMismatch(12), "This file did not pass verification. Block 12 does not match the file's fingerprint. Nothing was shown.");
  assert.equal(STRINGS.en.copyLink, 'Copy link');
});

test('the language is the primary subtag of the tag, whatever its case', () => {
  const cases = {
    ru: 'ru', RU: 'ru', 'ru-RU': 'ru', 'pt-BR': 'pt', 'PT-br': 'pt', pt: 'pt', 'zh-Hans': 'zh', 'zh-CN': 'zh', zh_TW: 'zh',
    'de-AT': 'de', 'es-419': 'es', en: 'en', 'en-GB': 'en', ' de ': 'de',
  };
  for (const [tag, language] of Object.entries(cases)) assert.equal(languageFor(tag), language, tag);
});

test('a tag the element has no words for is English', () => {
  for (const tag of ['fr', 'ja-JP', 'x-klingon', '', '   ', 'constructor', '__proto__', 'toString', null, undefined, 42]) {
    assert.equal(languageFor(tag), 'en', String(tag));
  }
  assert.equal(stringsFor('fr'), STRINGS.en);
  assert.equal(stringsFor('constructor'), STRINGS.en);
  assert.equal(stringsFor(undefined), STRINGS.en);
});

// A small tree of elements with the parts of the DOM languageOf reads: closest('[lang]'), the attribute,
// the parent, and the root node a shadow root hangs from.
function node({ lang = null, parent = null, host = null } = {}) {
  const element = {
    parentElement: parent,
    getAttribute: (name) => (name === 'lang' ? lang : null),
    closest(selector) {
      assert.equal(selector, '[lang]');
      for (let at = element; at; at = at.parentElement) if (at.getAttribute('lang') !== null) return at;
      return null;
    },
    getRootNode: () => ({ host }),
  };
  return element;
}

const document = (lang) => ({ documentElement: { lang } });

test('the element\'s own lang wins over an ancestor and the document', () => {
  const page = node({ lang: 'de' });
  const own = node({ lang: 'ru', parent: page });
  assert.equal(languageOf(own, document('es')), 'ru');
});

test('without its own lang the nearest ancestor decides', () => {
  const outer = node({ lang: 'de' });
  const inner = node({ lang: 'pt-BR', parent: outer });
  const element = node({ parent: inner });
  assert.equal(languageOf(element, document('es')), 'pt');
});

test('with no lang on the way up, the document decides', () => {
  assert.equal(languageOf(node({ parent: node() }), document('zh-Hans')), 'zh');
  assert.equal(languageOf(node(), document('RU')), 'ru');
});

test('nothing at all is English', () => {
  assert.equal(languageOf(node(), document('')), 'en');
  assert.equal(languageOf(node(), {}), 'en');
  assert.equal(languageOf(node(), undefined), 'en');
});

test('a lang the element has no words for is English, and does not fall through to an ancestor', () => {
  const page = node({ lang: 'ru' });
  assert.equal(languageOf(node({ lang: 'fr', parent: page }), document('de')), 'en');
});

test('an empty lang says nothing and the walk goes on', () => {
  const page = node({ lang: 'de' });
  assert.equal(languageOf(node({ lang: '', parent: page }), document('ru')), 'de');
  assert.equal(languageOf(node({ lang: '  ' }), document('es')), 'es');
});

test('the walk goes up out of a shadow root to the element that hosts it', () => {
  const page = node({ lang: 'ru' });
  const host = node({ parent: page });
  const inside = node({ host });
  assert.equal(languageOf(inside, document('en')), 'ru');
});

test('a plain element without closest still resolves from the document', () => {
  assert.equal(languageOf({ getAttribute: () => null }, document('de')), 'de');
});

test('sizes take the unit and the decimal mark of the language', () => {
  assert.equal(formatBytes(36_500_000, 'en'), '36.5 MB');
  assert.equal(formatBytes(36_500_000, 'zh'), '36.5 MB');
  assert.equal(formatBytes(36_500_000, 'ru'), '36,5 МБ');
  assert.equal(formatBytes(36_500_000, 'de'), '36,5 MB');
  assert.equal(formatBytes(36_500_000, 'es'), '36,5 MB');
  assert.equal(formatBytes(36_500_000, 'pt'), '36,5 MB');
});

test('sizes below a thousand are whole bytes, with the byte of the language', () => {
  assert.equal(formatBytes(999, 'en'), '999 B');
  assert.equal(formatBytes(999, 'ru'), '999 Б');
  assert.equal(formatBytes(0, 'ru'), '0 Б');
  assert.equal(formatBytes(12_400, 'ru'), '12,4 КБ');
  assert.equal(formatBytes(1_600_000_000, 'ru'), '1,6 ГБ');
  assert.equal(formatBytes(1_600_000_000_000, 'ru'), '1,6 ТБ');
  assert.equal(formatBytes(1000, 'de'), '1,0 KB');
});

test('sizes without a language are English, and an unknown language is too', () => {
  assert.equal(formatBytes(12_400), '12.4 KB');
  assert.equal(formatBytes(12_400, 'fr'), '12.4 KB');
});

test('the progress is worded in the language, with the unit written once', () => {
  const detail = { bytesDone: 12_400_000, bytesTotal: 36_500_000, blocksVerified: 3, blocksTotal: 8 };
  assert.equal(bytesOf(12_400_000, 36_500_000, 'ru'), '12,4 из 36,5 МБ');
  assert.equal(bytesOf(900, 36_500_000, 'ru'), '900 Б из 36,5 МБ');
  assert.equal(bytesOf(12_400_000, 36_500_000, 'de'), '12,4 von 36,5 MB');
  assert.equal(bytesOf(12_400_000, 36_500_000, 'es'), '12,4 de 36,5 MB');
  assert.equal(bytesOf(12_400_000, 36_500_000, 'pt'), '12,4 de 36,5 MB');
  assert.equal(bytesOf(12_400_000, 36_500_000, 'zh'), '12.4 / 36.5 MB');
  assert.deepEqual(progressLines(detail, 'ru'), { bytes: '12,4 из 36,5 МБ', blocks: ' · проверено блоков: 3 из 8' });
  assert.deepEqual(progressLines(detail, 'de'), { bytes: '12,4 von 36,5 MB', blocks: ' · 3 von 8 Blöcken geprüft' });
  assert.deepEqual(progressLines(detail), { bytes: '12.4 of 36.5 MB', blocks: ' · 3 of 8 blocks verified' });
});

test('the download label puts the file name where the language wants it', () => {
  const manifest = { name: 'photo.svg', type: 'image/svg+xml' };
  assert.equal(downloadLabel(manifest, 'ru'), 'Скачать photo.svg');
  assert.equal(downloadLabel(manifest, 'de'), 'photo.svg herunterladen');
  assert.equal(downloadLabel(manifest, 'zh'), '下载 photo.svg');
  assert.equal(downloadLabel(manifest), 'Download photo.svg');
});

test('the caption says verified in the language', () => {
  assert.equal(captionParts({ phase: 'image', network: 'testnet', lang: 'ru' }).word, 'проверено · ');
  assert.equal(captionParts({ phase: 'image', network: 'testnet', lang: 'zh' }).word, '已校验 · ');
  assert.equal(captionParts({ phase: 'loading', network: 'testnet', lang: 'ru' }).word, '');
  assert.equal(captionState({ phase: 'text', network: 'mainnet', lang: 'de' }).text, '✓ geprüft · mainnet');
  assert.equal(captionState({ phase: 'text', network: 'mainnet' }).text, '✓ verified · mainnet');
});

test('every reading error has a sentence and a short title in every language', () => {
  const codes = ['InvalidLink', 'ManifestNotFound', 'NodeLacksHistory', 'CorruptManifest', 'ListMismatch', 'BlockMismatch', 'ChunkMissing', 'TooLarge', 'NodeUnreachable'];
  for (const language of LANGUAGES) {
    for (const code of codes) {
      const error = new XrplFileError(code, 'x', { block: 3 });
      assert.ok(messageFor(error, { lang: language }).length > 0, `${language} ${code}`);
      assert.ok(shortTitleFor(error, language).length > 0, `${language} ${code}`);
      if (language !== 'en') {
        assert.notEqual(messageFor(error, { lang: language }), messageFor(error), `${language} ${code} is worded`);
        assert.notEqual(shortTitleFor(error, language), shortTitleFor(error), `${language} ${code} title is worded`);
      }
    }
  }
});

test('a sentence in Russian names the block it stopped at', () => {
  const mismatch = new XrplFileError('BlockMismatch', 'x', { block: 12 });
  assert.equal(messageFor(mismatch, { lang: 'ru' }),
    'Этот файл не прошёл проверку. Блок 12 не совпадает с отпечатком файла. Ничего не показано.');
  assert.equal(messageFor(mismatch, { lang: 'ru', playing: true }), 'Воспроизведение остановлено на блоке 12: он не прошёл проверку.');
  assert.equal(saveMessageFor(mismatch, 'ru'), 'Сохранение остановлено на блоке 12: он не прошёл проверку.');
});

test('the element\'s own sentences are worded again from their English text', () => {
  for (const language of LANGUAGES.filter((l) => l !== 'en')) {
    const s = STRINGS[language];
    assert.equal(messageFor(elementError('NodeUnreachable', NO_NODE), { lang: language }), s.noNode);
    assert.equal(messageFor(elementError('NodeUnreachable', BAD_NODE), { lang: language }), s.badNode);
    assert.equal(messageFor(elementError(null, INSECURE), { lang: language }), s.insecure);
    assert.equal(messageFor(elementError('InvalidLink', NO_LINK), { lang: language }), s.noLink);
    assert.equal(messageFor(elementError('TooLarge', TOO_LARGE), { lang: language }), s.tooLarge);
    assert.equal(messageFor(elementError(null, DECODE_FAILED), { lang: language }), s.decodeFailed);
    assert.equal(saveMessageFor(elementError('TooLarge', TOO_LARGE), language), s.tooLarge);
    assert.equal(shortTitleFor(elementError('NodeUnreachable', NO_NODE), language), s.shortNoNode);
    assert.equal(shortTitleFor(elementError('NodeUnreachable', BAD_NODE), language), s.shortBadNode);
    assert.equal(shortTitleFor(elementError(null, INSECURE), language), s.shortNotSecure);
    assert.equal(shortTitleFor(elementError('InvalidLink', NO_LINK), language), s.shortNoLink);
  }
});

test('a sentence the element does not know is shown as written, in any language', () => {
  const odd = elementError(null, 'Something only a test says.');
  assert.equal(messageFor(odd, { lang: 'ru' }), 'Something only a test says.');
  assert.equal(shortTitleFor(odd, 'ru'), 'Ошибка');
});

test('an unexpected error is generic and never leaks its text, in any language', () => {
  for (const language of LANGUAGES) {
    assert.equal(messageFor(new TypeError('boom'), { lang: language }), STRINGS[language].generic);
    assert.equal(messageFor(null, { lang: language }), STRINGS[language].generic);
    assert.equal(saveMessageFor(new Error('disk full'), language), STRINGS[language].saveFailed);
    assert.equal(shortTitleFor(null, language), STRINGS[language].shortError);
  }
});

test('the error event stays English whatever the page speaks', () => {
  const error = new XrplFileError('BlockMismatch', 'x', { block: 4 });
  assert.deepEqual(detailFor(error), { name: 'BlockMismatch', message: STRINGS.en.blockMismatch(4) });
  assert.deepEqual(detailFor(error, { playing: true }), { name: 'BlockMismatch', message: STRINGS.en.blockMismatchPlaying(4) });
  assert.deepEqual(detailFor(elementError(null, INSECURE)), { name: null, message: INSECURE });
});
