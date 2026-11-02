// The words the home page's scripts put on the page: the mosaic's status lines, the "Check a link"
// errors, the price table and the copy button. The page's own prose is in index.html and ru/index.html.
// Every function here is called with whatever the page has and must not throw.

const NETWORK_NAMES = {
  en: { mainnet: 'the XRP Ledger mainnet', testnet: 'the XRP Ledger test network' },
  ru: { mainnet: 'основной сети XRP Ledger', testnet: 'тестовой сети XRP Ledger' },
};

const russianPlurals = new Intl.PluralRules('ru');

function ruCount(count, one, few, many) {
  const form = russianPlurals.select(count);
  return `${count} ${form === 'one' ? one : form === 'few' ? few : many}`;
}

function enChunks(count) {
  return `${count} ${count === 1 ? 'chunk' : 'chunks'}`;
}

function ledgerRange(first, last, word) {
  return first === last ? `${word.one} ${first}` : `${word.many} ${first}–${last}`;
}

/** `network` is the hero file's network, e.g. "testnet": the caption names where the file was read. */
export function mosaicStrings(lang, network) {
  if (lang === 'ru') {
    const ledgers = { one: 'леджер', many: 'леджеры' };
    return {
      label: (phase, { chunks }) => {
        const count = chunks === null ? '' : `, ${ruCount(chunks, 'фрагмент', 'фрагмента', 'фрагментов')}`;
        return {
          reading: `Картинка, которую читают из реестра${count}`,
          developing: `Картинка проверена и проявляется${count}`,
          done: `Картинка, прочитанная из реестра и проверенная${count}`,
          failed: 'Картинку сейчас не удалось прочитать из реестра',
        }[phase];
      },
      caption: ({ chunks, firstLedger, lastLedger }) =>
        `проверено в ${network} · ${ruCount(chunks, 'фрагмент', 'фрагмента', 'фрагментов')} · ${ledgerRange(firstLedger, lastLedger, ledgers)}`,
      failed: (code) => {
        if (code === 'NodeUnreachable' || code === 'Stalled') return 'Реестр не ответил. Обновите страницу, чтобы попробовать ещё раз.';
        if (code === 'BlockMismatch' || code === 'DecodeFailed') return 'Картинка не прошла проверку, поэтому не показана.';
        if (['NodeLacksHistory', 'ManifestNotFound', 'ChunkMissing', 'Incomplete'].includes(code)) {
          return 'Эта картинка сейчас недоступна в реестре.';
        }
        return 'Картинку сейчас не удалось прочитать.';
      },
      fileLinkLabel: 'Страница файла',
    };
  }
  const ledgers = { one: 'ledger', many: 'ledgers' };
  return {
    label: (phase, { chunks }) => {
      const count = chunks === null ? '' : `, ${enChunks(chunks)}`;
      return {
        reading: `A picture being read from the ledger${count}`,
        developing: `The picture is verified and appearing${count}`,
        done: `The picture, read from the ledger and verified${count}`,
        failed: 'The picture could not be read from the ledger just now',
      }[phase];
    },
    caption: ({ chunks, firstLedger, lastLedger }) =>
      `verified on ${network} · ${enChunks(chunks)} · ${ledgerRange(firstLedger, lastLedger, ledgers)}`,
    failed: (code) => {
      if (code === 'NodeUnreachable' || code === 'Stalled') return 'The ledger did not answer. Reload the page to try again.';
      if (code === 'BlockMismatch' || code === 'DecodeFailed') return 'The picture did not pass the check, so it is not shown.';
      if (['NodeLacksHistory', 'ManifestNotFound', 'ChunkMissing', 'Incomplete'].includes(code)) {
        return 'This picture is not available from the ledger right now.';
      }
      return 'The picture could not be read just now.';
    },
    fileLinkLabel: 'Open the file page',
  };
}

export const CHECK_LINK_STRINGS = {
  en: {
    empty: 'Paste a link first.',
    invalid: 'This is not an xrpl-file link. A link looks like xrpl-file://testnet/ followed by 64 letters and digits.',
  },
  ru: {
    empty: 'Сначала вставьте ссылку.',
    invalid: 'Это не ссылка xrpl-file. Ссылка выглядит так: xrpl-file://testnet/ и 64 буквы и цифры.',
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
      `Цифры сняты в ${NETWORK_NAMES.ru[network] ?? network} ${date}. Комиссии и резервы задаёт сеть, и они могут меняться.`,
  },
};
