// The words the home page's scripts put on the page: the hero's status lines, callouts, the "Check a link"
// errors, the price table and the copy button. The page's own prose is in index.html and <lang>/index.html.
// Every function here is called with whatever the page has and must not throw. Each table is keyed by
// language with the same keys in every language; test/languages.test.mjs checks that for the active ones.

export const NETWORK_NAMES = {
  en: { mainnet: 'the XRP Ledger mainnet', testnet: 'the XRP Ledger test network' },
  ru: { mainnet: 'основной сети XRP Ledger', testnet: 'тестовой сети XRP Ledger' },
};

// The caption names the network in a few words; a network without an entry keeps its own name.
export const NETWORK_SHORT = {
  en: {},
  ru: { mainnet: 'основной сети', testnet: 'тестовой сети' },
};

const pluralRules = new Map();

/**
 * The noun form a count takes by the language's own plural rules (Intl.PluralRules). `forms` has the
 * categories the language uses (one, few, many, other) and `other` is the fallback. The count is not
 * included, so a language can place it where it likes.
 */
export function plural(lang, count, forms) {
  let rules = pluralRules.get(lang);
  if (!rules) {
    rules = new Intl.PluralRules(lang);
    pluralRules.set(lang, rules);
  }
  return forms[rules.select(count)] ?? forms.other;
}

function ledgerRange(first, last, word) {
  return first === last ? `${word.one} ${first}` : `${word.many} ${first}–${last}`;
}

const EN_CHUNKS = { one: 'chunk', other: 'chunks' };
const RU_CHUNKS = { one: 'фрагмент', few: 'фрагмента', many: 'фрагментов', other: 'фрагмента' };
const RU_BYTES = { one: 'байт', few: 'байта', many: 'байт', other: 'байта' };
const RU_LEDGERS = { one: 'версию реестра', few: 'версии реестра', many: 'версий реестра', other: 'версии реестра' };

// One entry per language, each a function of the hero file's network ("testnet": the caption names
// where the file was read). A language is a data change: add an entry with the same shape.
const CLOUD_STRINGS = {
  en: (network) => {
    const chunksOf = (count) => `${count} ${plural('en', count, EN_CHUNKS)}`;
    const ledgers = { one: 'ledger', many: 'ledgers' };
    return {
      label: (phase, { chunks }) => {
        const count = chunks === null ? '' : `, ${chunksOf(chunks)}`;
        return {
          reading: `Point cloud of a stretch of the XRP Ledger. The file's chunks appear in it as cubes while they are read${count}`,
          developing: `Point cloud of a stretch of the XRP Ledger. Every block is being checked${count}`,
          done: `Point cloud of a stretch of the XRP Ledger. The file is read from the ledger and checked${count}`,
          failed: 'The file could not be read from the ledger just now',
        }[phase];
      },
      caption: ({ chunks, firstLedger, lastLedger }) =>
        `read from ${NETWORK_SHORT.en[network] ?? network} · ${chunksOf(chunks)} · ${ledgerRange(firstLedger, lastLedger, ledgers)}`,
      failed: (code) => {
        if (code === 'NodeUnreachable' || code === 'Stalled') return 'The XRP Ledger node did not answer. Reload the page to try again.';
        if (code === 'BlockMismatch' || code === 'DecodeFailed') return 'The file did not pass the check, so it is not shown.';
        if (['NodeLacksHistory', 'ManifestNotFound', 'ChunkMissing', 'Incomplete'].includes(code)) {
          return 'This file is not available from the ledger right now.';
        }
        return 'The file could not be read just now.';
      },
      fileLinkLabel: 'Open the file page',
      verified: 'verified',
      // Nothing is said before the manifest names the ledgers: until then there are no cubes to describe.
      note: ({ ledgers: span, ledgersPerPad, capped }) => {
        if (span === null) return '';
        return capped
          ? `Each cube is one chunk of the file, standing on the ledger it was written in; the file spans ${span} ledgers, so each strip of ground covers up to ${ledgersPerPad} of them. Drawn, not measured: the cube's place within its strip, the ground and the gap before the manifest's cube.`
          : "Each cube is one chunk of the file, standing on the ledger it was written in. Drawn, not measured: the cube's place within its ledger, the ground and the gap before the manifest's cube.";
      },
      fileCallout: (chunks) => ({ caption: 'The file', value: String(chunks), unit: plural('en', chunks, EN_CHUNKS) }),
      linkCallout: (bytes) => ({
        caption: "The link, the manifest's hash",
        value: String(bytes),
        unit: plural('en', bytes, { one: 'byte', other: 'bytes' }),
      }),
    };
  },
  ru: (network) => {
    const chunksOf = (count) => `${count} ${plural('ru', count, RU_CHUNKS)}`;
    const ledgers = { one: 'версия реестра', many: 'версии реестра' };
    return {
      label: (phase, { chunks }) => {
        const count = chunks === null ? '' : `, ${chunksOf(chunks)}`;
        return {
          reading: `Облако точек: участок XRP Ledger. Фрагменты файла появляются в нём кубами по мере чтения${count}`,
          developing: `Облако точек: участок XRP Ledger. Все блоки проверяются${count}`,
          done: `Облако точек: участок XRP Ledger. Файл прочитан из реестра и проверен${count}`,
          failed: 'Файл сейчас не удалось прочитать из реестра',
        }[phase];
      },
      caption: ({ chunks, firstLedger, lastLedger }) =>
        `прочитано из ${NETWORK_SHORT.ru[network] ?? network} · ${chunksOf(chunks)} · ${ledgerRange(firstLedger, lastLedger, ledgers)}`,
      failed: (code) => {
        if (code === 'NodeUnreachable' || code === 'Stalled') return 'Узел XRP Ledger не ответил. Обновите страницу, чтобы попробовать ещё раз.';
        if (code === 'BlockMismatch' || code === 'DecodeFailed') return 'Файл не прошёл проверку, поэтому не показан.';
        if (['NodeLacksHistory', 'ManifestNotFound', 'ChunkMissing', 'Incomplete'].includes(code)) {
          return 'Этот файл сейчас недоступен в реестре.';
        }
        return 'Файл сейчас не удалось прочитать.';
      },
      fileLinkLabel: 'Страница файла',
      verified: 'проверено',
      // Real: the version of the ledger each chunk was written in. Drawn: the place inside it, the
      // surface and the gap before the manifest's cube. Nothing is said before the manifest names the
      // ledgers: until then there are no cubes to describe.
      note: ({ ledgers: span, ledgersPerPad, capped }) => {
        if (span === null) return '';
        return capped
          ? `Каждый куб — один фрагмент файла, он стоит на своей версии реестра; файл занимает ${span} ${plural('ru', span, RU_LEDGERS)}, поэтому каждая полоса поверхности охватывает до ${ledgersPerPad} из них. Нарисованы, а не измерены: место куба внутри полосы, поверхность и расстояние до куба манифеста.`
          : 'Каждый куб — один фрагмент файла, он стоит на той версии реестра, в которой фрагмент записан. Нарисованы, а не измерены: место куба внутри версии, поверхность и расстояние до куба манифеста.';
      },
      fileCallout: (chunks) => ({ caption: 'Файл', value: String(chunks), unit: plural('ru', chunks, RU_CHUNKS) }),
      linkCallout: (bytes) => ({ caption: 'Ссылка — хэш манифеста', value: String(bytes), unit: plural('ru', bytes, RU_BYTES) }),
    };
  },
};

/** `network` is the hero file's network, e.g. "testnet". A language without an entry gets English. */
export function cloudStrings(lang, network) {
  return (CLOUD_STRINGS[lang] ?? CLOUD_STRINGS.en)(network);
}

export const CHECK_LINK_STRINGS = {
  en: {
    empty: 'Paste a link first.',
    invalid: 'This is not an xrpl-file link. A link looks like xrpl-file://testnet/ followed by 64 hexadecimal characters.',
  },
  ru: {
    empty: 'Сначала вставьте ссылку.',
    invalid: 'Это не ссылка xrpl-file. Ссылка выглядит так: xrpl-file://testnet/ и 64 шестнадцатеричных символа.',
  },
};

export const COPY_STRINGS = {
  en: { copied: 'Copied to the clipboard.', failed: 'Could not copy. The text is selected: copy it by hand.' },
  ru: { copied: 'Скопировано в буфер обмена.', failed: 'Не удалось скопировать. Текст выделен: скопируйте его вручную.' },
};

export const PRICE_STRINGS = {
  en: {
    caption: 'What publishing a file costs',
    size: 'File size',
    fee: 'Fees, spent',
    held: 'Reserve, held',
    returned: 'Reserve, returned',
    unit: 'XRP',
    reserve: ({ held, returned, kept }) =>
      `While a file is being published, ${held} XRP is held as the reserve of a throwaway account. When the account is deleted, ${returned} XRP comes back. The other ${kept} XRP stays with the network as the price of deleting the account, whatever the size of the file.`,
    source: ({ network, date }) =>
      `Figures taken from ${NETWORK_NAMES.en[network] ?? network} on ${date}. The network sets fees and reserves, and they can change.`,
  },
  ru: {
    caption: 'Сколько стоит записать файл',
    size: 'Размер файла',
    fee: 'Комиссии, потрачены',
    held: 'Резерв, удержан',
    returned: 'Резерв, возвращён',
    unit: 'XRP',
    reserve: ({ held, returned, kept }) =>
      `Пока файл записывается, ${held} XRP удерживается как резерв одноразового аккаунта. Когда аккаунт удалён, ${returned} XRP возвращается. Остальные ${kept} XRP остаются у сети как плата за удаление аккаунта, каким бы ни был размер файла.`,
    source: ({ network, date }) =>
      `Данные ${NETWORK_NAMES.ru[network] ?? network} на ${date}. Комиссии и резервы задаёт сеть, и они могут меняться.`,
  },
};
