// The words of the file page and the 404 page. The two pages have one URL for both languages, so
// their text is switched in place; the header and footer words are in chrome-strings.mjs.
import { CHECK_LINK_STRINGS, COPY_STRINGS } from './home-strings.mjs';

export const LANGS = ['en', 'ru'];

const EN = {
  fileTitle: 'File',
  download: 'Download',
  downloadSaved: 'Saved.',
  downloadStarted: 'Download started.',
  saveNotLoaded: 'The file has not loaded yet.',
  saveNotVerified: 'The file did not pass the check, so it was not saved.',
  saveUnavailable: 'Part of the file is not available from the ledger. Nothing was saved.',
  saveNodeDown: 'The ledger did not answer. Try again later.',
  saveTooLarge: 'The file is too large to save in this browser.',
  saveRefused: 'The browser did not allow saving. Press Download again.',
  saveInterrupted: 'Saving was interrupted. Press Download again.',
  saveFailed: 'The file could not be saved.',
  sizeLabel: 'Size',
  networkLabel: 'Network',
  linkLabel: 'Link',
  networkMainnet: 'mainnet',
  networkTestnet: 'testnet',
  sizeB: '{value} B',
  sizeKB: '{value} KB',
  sizeMB: '{value} MB',
  sizeGB: '{value} GB',
  copy: 'Copy',
  copyLabel: 'Copy the link',
  copied: COPY_STRINGS.en.copied,
  copyFailed: COPY_STRINGS.en.failed,
  howTitle: 'How this works',
  howText:
    'xrpl-file writes a file into the XRP Ledger in chunks. Your browser collects the chunks, assembles the file and checks it against its fingerprint. A file that does not match is not shown. This site’s server takes no part in it.',
  howLink: 'More on the home page',
  checkTitle: 'Check a link',
  checkLabel: 'Paste an xrpl-file link to open its file',
  checkOpen: 'Open',
  checkEmpty: CHECK_LINK_STRINGS.en.empty,
  checkInvalid: CHECK_LINK_STRINGS.en.invalid,
  notFoundTitle: 'Page not found',
  notFoundText: 'There is no page at this address.',
  homeLink: 'Go to the home page',
};

const RU = {
  fileTitle: 'Файл',
  download: 'Скачать',
  downloadSaved: 'Сохранено.',
  downloadStarted: 'Скачивание началось.',
  saveNotLoaded: 'Файл ещё не загрузился.',
  saveNotVerified: 'Файл не прошёл проверку, поэтому не сохранён.',
  saveUnavailable: 'Часть файла сейчас недоступна в реестре. Ничего не сохранено.',
  saveNodeDown: 'Реестр не ответил. Попробуйте позже.',
  saveTooLarge: 'Файл слишком велик, чтобы сохранить его в этом браузере.',
  saveRefused: 'Браузер не разрешил сохранение. Нажмите «Скачать» ещё раз.',
  saveInterrupted: 'Сохранение прервано. Нажмите «Скачать» ещё раз.',
  saveFailed: 'Не удалось сохранить файл.',
  sizeLabel: 'Размер',
  networkLabel: 'Сеть',
  linkLabel: 'Ссылка',
  networkMainnet: 'основная сеть',
  networkTestnet: 'тестовая сеть',
  sizeB: '{value} Б',
  sizeKB: '{value} КБ',
  sizeMB: '{value} МБ',
  sizeGB: '{value} ГБ',
  copy: 'Копировать',
  copyLabel: 'Скопировать ссылку',
  copied: COPY_STRINGS.ru.copied,
  copyFailed: COPY_STRINGS.ru.failed,
  howTitle: 'Как это работает',
  howText:
    'xrpl-file записывает файл в XRP Ledger фрагментами. Браузер собирает фрагменты в файл и проверяет его по отпечатку. Файл, который не совпал с отпечатком, не показывается. Сервер этого сайта в этом не участвует.',
  howLink: 'Подробнее на главной',
  checkTitle: 'Проверить ссылку',
  checkLabel: 'Вставьте ссылку xrpl-file, чтобы открыть файл',
  checkOpen: 'Открыть',
  checkEmpty: CHECK_LINK_STRINGS.ru.empty,
  checkInvalid: CHECK_LINK_STRINGS.ru.invalid,
  notFoundTitle: 'Страница не найдена',
  notFoundText: 'По этому адресу страницы нет.',
  homeLink: 'На главную',
};

export const TABLES = { en: EN, ru: RU };

/**
 * The string for `key` in `lang`, with {name} placeholders filled from `values`. A missing key,
 * language or value throws: a typo must fail a test, not put "undefined" on the page.
 */
export function t(key, lang, values = {}) {
  const table = TABLES[lang];
  if (!table) throw new Error(`unknown language: ${lang}`);
  if (!Object.hasOwn(table, key)) throw new Error(`missing string "${key}" for ${lang}`);
  return table[key].replace(/\{(\w+)\}/g, (_, name) => {
    if (!Object.hasOwn(values, name)) throw new Error(`string "${key}" needs a value for {${name}}`);
    return String(values[name]);
  });
}

const SIZE_KEYS = ['sizeB', 'sizeKB', 'sizeMB', 'sizeGB'];

/**
 * A human size in decimal units, like the element's own ("178 KB"); null when `bytes` is not a size.
 * Three figures are enough to tell sizes apart: one decimal below 10, none from 10 up.
 */
export function formatSize(bytes, lang) {
  if (typeof bytes !== 'number' || !Number.isFinite(bytes) || bytes < 0) return null;
  let value = bytes;
  let unit = 0;
  while (value >= 1000 && unit < SIZE_KEYS.length - 1) {
    value /= 1000;
    unit += 1;
  }
  let digits = unit === 0 || value >= 10 ? 0 : 1;
  // 999.96 KB rounds to 1000: say 1.0 MB, not "1000 KB".
  if (Number(value.toFixed(digits)) >= 1000 && unit < SIZE_KEYS.length - 1) {
    value /= 1000;
    unit += 1;
    digits = 1;
  }
  const number = new Intl.NumberFormat(lang === 'ru' ? 'ru-RU' : 'en-US', {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  }).format(value);
  return t(SIZE_KEYS[unit], lang, { value: number });
}

/** The network as a visitor reads it; a network without a name here (a decimal NetworkID) stays as it is. */
export function networkName(network, lang) {
  if (network === 'mainnet') return t('networkMainnet', lang);
  if (network === 'testnet') return t('networkTestnet', lang);
  return String(network);
}
