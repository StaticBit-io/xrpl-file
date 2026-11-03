// The words the home page's scripts put on the page: the hero's status lines, callouts, the "Check a link"
// errors, the price table and the copy button. The page's own prose is in index.html and <lang>/index.html.
// Every function here is called with whatever the page has and must not throw. Each table is keyed by
// language with the same keys in every language; test/languages.test.mjs checks that for the active ones.

export const NETWORK_NAMES = {
  en: { mainnet: 'the XRP Ledger mainnet', testnet: 'the XRP Ledger test network' },
  ru: { mainnet: 'основной сети XRP Ledger', testnet: 'тестовой сети XRP Ledger' },
  // Each phrase fits its sentence: "Angaben aus dem ...", "Datos de la ...", "Dados da ...", "数据取自 ...".
  de: { mainnet: 'dem Mainnet des XRP Ledger', testnet: 'dem Testnet des XRP Ledger' },
  es: { mainnet: 'la red principal del XRP Ledger', testnet: 'la red de pruebas del XRP Ledger' },
  pt: { mainnet: 'da rede principal do XRP Ledger', testnet: 'da rede de testes do XRP Ledger' },
  zh: { mainnet: 'XRP Ledger 主网', testnet: 'XRP Ledger 测试网' },
};

// The caption names the network in a few words; a network without an entry keeps its own name.
export const NETWORK_SHORT = {
  en: {},
  ru: { mainnet: 'основной сети', testnet: 'тестовой сети' },
  de: {},
  es: {},
  pt: {},
  zh: {},
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
const DE_CHUNKS = { one: 'Chunk', other: 'Chunks' };
const ES_CHUNKS = { one: 'fragmento', other: 'fragmentos' };
const PT_CHUNKS = { one: 'fragmento', other: 'fragmentos' };
// Chinese has no plural: one form for every count.
const ZH_CHUNKS = { other: '个分片' };

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
          reading: `Облако точек: участок XRP Ledger. Фрагменты файла появляются в этом облаке кубами по мере чтения${count}`,
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
  de: (network) => {
    const chunksOf = (count) => `${count} ${plural('de', count, DE_CHUNKS)}`;
    const ledgers = { one: 'Ledger', many: 'Ledger' };
    return {
      label: (phase, { chunks }) => {
        const count = chunks === null ? '' : `, ${chunksOf(chunks)}`;
        return {
          reading: `Punktwolke eines Ausschnitts des XRP Ledger. Die Chunks der Datei erscheinen darin als Würfel, während sie gelesen werden${count}`,
          developing: `Punktwolke eines Ausschnitts des XRP Ledger. Alle Blöcke werden geprüft${count}`,
          done: `Punktwolke eines Ausschnitts des XRP Ledger. Die Datei ist aus dem Ledger gelesen und geprüft${count}`,
          failed: 'Die Datei konnte gerade nicht aus dem Ledger gelesen werden',
        }[phase];
      },
      caption: ({ chunks, firstLedger, lastLedger }) =>
        `gelesen aus ${NETWORK_SHORT.de[network] ?? network} · ${chunksOf(chunks)} · ${ledgerRange(firstLedger, lastLedger, ledgers)}`,
      failed: (code) => {
        if (code === 'NodeUnreachable' || code === 'Stalled') return 'Der Knoten des XRP Ledger hat nicht geantwortet. Laden Sie die Seite neu, um es erneut zu versuchen.';
        if (code === 'BlockMismatch' || code === 'DecodeFailed') return 'Die Datei hat die Prüfung nicht bestanden und wird deshalb nicht angezeigt.';
        if (['NodeLacksHistory', 'ManifestNotFound', 'ChunkMissing', 'Incomplete'].includes(code)) {
          return 'Diese Datei ist im Ledger gerade nicht verfügbar.';
        }
        return 'Die Datei konnte gerade nicht gelesen werden.';
      },
      fileLinkLabel: 'Dateiseite öffnen',
      verified: 'geprüft',
      note: ({ ledgers: span, ledgersPerPad, capped }) => {
        if (span === null) return '';
        return capped
          ? `Jeder Würfel ist ein Chunk der Datei und steht auf dem Ledger, in dem er geschrieben wurde. Die Datei umfasst ${span} Ledger, deshalb deckt jeder Streifen Boden bis zu ${ledgersPerPad} davon ab. Gezeichnet, nicht gemessen: die Position des Würfels innerhalb seines Streifens, der Boden und der Abstand bis zum Würfel des Manifests.`
          : 'Jeder Würfel ist ein Chunk der Datei und steht auf dem Ledger, in dem er geschrieben wurde. Gezeichnet, nicht gemessen: die Position des Würfels innerhalb seines Ledgers, der Boden und der Abstand bis zum Würfel des Manifests.';
      },
      fileCallout: (chunks) => ({ caption: 'Die Datei', value: String(chunks), unit: plural('de', chunks, DE_CHUNKS) }),
      linkCallout: (bytes) => ({ caption: 'Der Link, der Hash des Manifests', value: String(bytes), unit: 'Byte' }),
    };
  },
  es: (network) => {
    const chunksOf = (count) => `${count} ${plural('es', count, ES_CHUNKS)}`;
    const ledgers = { one: 'ledger', many: 'ledgers' };
    return {
      label: (phase, { chunks }) => {
        const count = chunks === null ? '' : `, ${chunksOf(chunks)}`;
        return {
          reading: `Nube de puntos de un tramo del XRP Ledger. Los fragmentos del archivo aparecen en ella como cubos mientras se leen${count}`,
          developing: `Nube de puntos de un tramo del XRP Ledger. Se están comprobando todos los bloques${count}`,
          done: `Nube de puntos de un tramo del XRP Ledger. El archivo se ha leído del ledger y está comprobado${count}`,
          failed: 'Ahora mismo no se puede leer el archivo del ledger',
        }[phase];
      },
      caption: ({ chunks, firstLedger, lastLedger }) =>
        `leído de ${NETWORK_SHORT.es[network] ?? network} · ${chunksOf(chunks)} · ${ledgerRange(firstLedger, lastLedger, ledgers)}`,
      failed: (code) => {
        if (code === 'NodeUnreachable' || code === 'Stalled') return 'El nodo del XRP Ledger no respondió. Recarga la página para intentarlo de nuevo.';
        if (code === 'BlockMismatch' || code === 'DecodeFailed') return 'El archivo no superó la comprobación, por eso no se muestra.';
        if (['NodeLacksHistory', 'ManifestNotFound', 'ChunkMissing', 'Incomplete'].includes(code)) {
          return 'Este archivo no está disponible ahora mismo en el ledger.';
        }
        return 'Ahora mismo no se puede leer el archivo.';
      },
      fileLinkLabel: 'Abrir la página del archivo',
      verified: 'comprobado',
      note: ({ ledgers: span, ledgersPerPad, capped }) => {
        if (span === null) return '';
        return capped
          ? `Cada cubo es un fragmento del archivo y está sobre el ledger en el que se escribió; el archivo abarca ${span} ledgers, así que cada franja de suelo cubre hasta ${ledgersPerPad} de ellos. Se dibujan, no se miden: la posición del cubo dentro de su franja, el suelo y la distancia hasta el cubo del manifiesto.`
          : 'Cada cubo es un fragmento del archivo y está sobre el ledger en el que se escribió. Se dibujan, no se miden: la posición del cubo dentro de su ledger, el suelo y la distancia hasta el cubo del manifiesto.';
      },
      fileCallout: (chunks) => ({ caption: 'El archivo', value: String(chunks), unit: plural('es', chunks, ES_CHUNKS) }),
      linkCallout: (bytes) => ({
        caption: 'El enlace, el hash del manifiesto',
        value: String(bytes),
        unit: plural('es', bytes, { one: 'byte', other: 'bytes' }),
      }),
    };
  },
  pt: (network) => {
    const chunksOf = (count) => `${count} ${plural('pt', count, PT_CHUNKS)}`;
    const ledgers = { one: 'ledger', many: 'ledgers' };
    return {
      label: (phase, { chunks }) => {
        const count = chunks === null ? '' : `, ${chunksOf(chunks)}`;
        return {
          reading: `Nuvem de pontos de um trecho do XRP Ledger. Os fragmentos do arquivo aparecem nela como cubos enquanto são lidos${count}`,
          developing: `Nuvem de pontos de um trecho do XRP Ledger. Todos os blocos estão sendo verificados${count}`,
          done: `Nuvem de pontos de um trecho do XRP Ledger. O arquivo foi lido do ledger e verificado${count}`,
          failed: 'Não foi possível ler o arquivo do ledger agora',
        }[phase];
      },
      caption: ({ chunks, firstLedger, lastLedger }) =>
        `lido de ${NETWORK_SHORT.pt[network] ?? network} · ${chunksOf(chunks)} · ${ledgerRange(firstLedger, lastLedger, ledgers)}`,
      failed: (code) => {
        if (code === 'NodeUnreachable' || code === 'Stalled') return 'O nó do XRP Ledger não respondeu. Recarregue a página para tentar de novo.';
        if (code === 'BlockMismatch' || code === 'DecodeFailed') return 'O arquivo não passou na verificação e por isso não é exibido.';
        if (['NodeLacksHistory', 'ManifestNotFound', 'ChunkMissing', 'Incomplete'].includes(code)) {
          return 'Este arquivo não está disponível no ledger agora.';
        }
        return 'Não foi possível ler o arquivo agora.';
      },
      fileLinkLabel: 'Abrir a página do arquivo',
      verified: 'verificado',
      note: ({ ledgers: span, ledgersPerPad, capped }) => {
        if (span === null) return '';
        return capped
          ? `Cada cubo é um fragmento do arquivo e fica sobre o ledger em que foi gravado; o arquivo abrange ${span} ledgers, então cada faixa de solo cobre até ${ledgersPerPad} deles. São desenhados, não medidos: a posição do cubo dentro da sua faixa, o solo e a distância até o cubo do manifesto.`
          : 'Cada cubo é um fragmento do arquivo e fica sobre o ledger em que foi gravado. São desenhados, não medidos: a posição do cubo dentro do seu ledger, o solo e a distância até o cubo do manifesto.';
      },
      fileCallout: (chunks) => ({ caption: 'O arquivo', value: String(chunks), unit: plural('pt', chunks, PT_CHUNKS) }),
      linkCallout: (bytes) => ({
        caption: 'O link, o hash do manifesto',
        value: String(bytes),
        unit: plural('pt', bytes, { one: 'byte', other: 'bytes' }),
      }),
    };
  },
  zh: (network) => {
    const chunksOf = (count) => `${count} ${plural('zh', count, ZH_CHUNKS)}`;
    const ledgers = { one: '账本', many: '账本' };
    return {
      label: (phase, { chunks }) => {
        const count = chunks === null ? '' : `，共 ${chunksOf(chunks)}`;
        return {
          reading: `XRP Ledger 一段区间的点云。文件的分片在读取时以立方体的形式出现在其中${count}`,
          developing: `XRP Ledger 一段区间的点云。正在校验每一个数据块${count}`,
          done: `XRP Ledger 一段区间的点云。文件已从账本读取并通过校验${count}`,
          failed: '刚才未能从账本读取该文件',
        }[phase];
      },
      caption: ({ chunks, firstLedger, lastLedger }) =>
        `读取自 ${NETWORK_SHORT.zh[network] ?? network} · ${chunksOf(chunks)} · ${ledgerRange(firstLedger, lastLedger, ledgers)}`,
      failed: (code) => {
        if (code === 'NodeUnreachable' || code === 'Stalled') return 'XRP Ledger 节点没有响应。请刷新页面重试。';
        if (code === 'BlockMismatch' || code === 'DecodeFailed') return '文件未通过校验，因此不显示。';
        if (['NodeLacksHistory', 'ManifestNotFound', 'ChunkMissing', 'Incomplete'].includes(code)) {
          return '该文件目前无法从账本读取。';
        }
        return '刚才未能读取该文件。';
      },
      fileLinkLabel: '打开文件页面',
      verified: '已校验',
      note: ({ ledgers: span, ledgersPerPad, capped }) => {
        if (span === null) return '';
        return capped
          ? `每个立方体是文件的一个分片，立在它被写入的那个账本上；文件跨越 ${span} 个账本，因此每条地面带最多覆盖其中的 ${ledgersPerPad} 个。绘制而非测量的部分：立方体在地面带内的位置、地面，以及与清单立方体之间的间距。`
          : '每个立方体是文件的一个分片，立在它被写入的那个账本上。绘制而非测量的部分：立方体在该账本内的位置、地面，以及与清单立方体之间的间距。';
      },
      fileCallout: (chunks) => ({ caption: '文件', value: String(chunks), unit: plural('zh', chunks, ZH_CHUNKS) }),
      linkCallout: (bytes) => ({ caption: '链接，即清单的哈希', value: String(bytes), unit: '字节' }),
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
  de: {
    empty: 'Fügen Sie zuerst einen Link ein.',
    invalid: 'Das ist kein xrpl-file-Link. Ein Link sieht so aus: xrpl-file://testnet/ gefolgt von 64 Hexadezimalzeichen.',
  },
  es: {
    empty: 'Pega primero un enlace.',
    invalid: 'Esto no es un enlace de xrpl-file. Un enlace tiene este aspecto: xrpl-file://testnet/ seguido de 64 caracteres hexadecimales.',
  },
  pt: {
    empty: 'Cole um link primeiro.',
    invalid: 'Isto não é um link xrpl-file. Um link tem este formato: xrpl-file://testnet/ seguido de 64 caracteres hexadecimais.',
  },
  zh: {
    empty: '请先粘贴链接。',
    invalid: '这不是 xrpl-file 链接。链接的格式是 xrpl-file://testnet/ 后面跟 64 个十六进制字符。',
  },
};

export const COPY_STRINGS = {
  en: { copied: 'Copied to the clipboard.', failed: 'Could not copy. The text is selected: copy it by hand.' },
  ru: { copied: 'Скопировано в буфер обмена.', failed: 'Не удалось скопировать. Текст выделен: скопируйте его вручную.' },
  de: { copied: 'In die Zwischenablage kopiert.', failed: 'Kopieren nicht möglich. Der Text ist markiert: Kopieren Sie ihn von Hand.' },
  es: { copied: 'Copiado al portapapeles.', failed: 'No se pudo copiar. El texto está seleccionado: cópialo a mano.' },
  pt: { copied: 'Copiado para a área de transferência.', failed: 'Não foi possível copiar. O texto está selecionado: copie-o manualmente.' },
  zh: { copied: '已复制到剪贴板。', failed: '无法复制。文本已选中，请手动复制。' },
};

export const PRICE_STRINGS = {
  en: {
    caption: 'What publishing a file costs',
    size: 'File size',
    fee: 'Fees, spent',
    unit: 'XRP',
    reserve: ({ held, returned, kept }) =>
      `A publishing account holds ${held} XRP of reserve for as long as it exists, however many files it publishes. When the account is closed, ${returned} XRP comes back. The other ${kept} XRP stays with the network as the price of deleting the account.`,
    source: ({ network, date }) =>
      `Figures taken from ${NETWORK_NAMES.en[network] ?? network} on ${date}. The network sets fees and reserves, and they can change.`,
  },
  ru: {
    caption: 'Сколько стоит записать файл',
    size: 'Размер файла',
    fee: 'Комиссии, потрачены',
    unit: 'XRP',
    reserve: ({ held, returned, kept }) =>
      `На аккаунте для публикации удерживается ${held} XRP резерва, пока он существует, сколько бы файлов с него ни было записано. Когда аккаунт закрыт, ${returned} XRP возвращается. Остальные ${kept} XRP остаются у сети как плата за удаление аккаунта.`,
    source: ({ network, date }) =>
      `Данные ${NETWORK_NAMES.ru[network] ?? network} на ${date}. Комиссии и резервы задаёт сеть, и они могут меняться.`,
  },
  de: {
    caption: 'Was das Veröffentlichen einer Datei kostet',
    size: 'Dateigröße',
    fee: 'Gebühren, verbraucht',
    unit: 'XRP',
    reserve: ({ held, returned, kept }) =>
      `Auf einem Veröffentlichungskonto ist ein Betrag von ${held} XRP als Reserve gebunden, solange es besteht, unabhängig davon, wie viele Dateien es veröffentlicht. Wird das Konto geschlossen, kommen ${returned} XRP zurück. Der Rest, ${kept} XRP, bleibt beim Netzwerk, als Preis für das Löschen des Kontos.`,
    source: ({ network, date }) =>
      `Angaben aus ${NETWORK_NAMES.de[network] ?? network} vom ${date}. Gebühren und Reserven legt das Netzwerk fest, und sie können sich ändern.`,
  },
  es: {
    caption: 'Cuánto cuesta publicar un archivo',
    size: 'Tamaño del archivo',
    fee: 'Comisiones, gastadas',
    unit: 'XRP',
    reserve: ({ held, returned, kept }) =>
      `En una cuenta de publicación queda retenido un importe de ${held} XRP como reserva mientras la cuenta existe, sean cuantos sean los archivos que publique. Cuando la cuenta se cierra, se recuperan ${returned} XRP. El resto, ${kept} XRP, se queda en la red como precio de eliminar la cuenta.`,
    source: ({ network, date }) =>
      `Datos de ${NETWORK_NAMES.es[network] ?? network} a fecha de ${date}. La red fija las comisiones y las reservas, y pueden cambiar.`,
  },
  pt: {
    caption: 'Quanto custa publicar um arquivo',
    size: 'Tamanho do arquivo',
    fee: 'Taxas, gastas',
    unit: 'XRP',
    reserve: ({ held, returned, kept }) =>
      `Numa conta de publicação fica retido um valor de ${held} XRP como reserva enquanto a conta existir, seja qual for o número de arquivos publicados. Quando a conta é fechada, voltam ${returned} XRP. O restante, ${kept} XRP, fica com a rede como preço da exclusão da conta.`,
    // The network phrase carries its own "da" ("de" + "a rede"), so an unnamed network gets one too.
    source: ({ network, date }) =>
      `Dados ${NETWORK_NAMES.pt[network] ?? `da rede ${network}`} em ${date}. A rede define as taxas e as reservas, e elas podem mudar.`,
  },
  zh: {
    caption: '发布一个文件的费用',
    size: '文件大小',
    fee: '手续费，已花费',
    unit: 'XRP',
    reserve: ({ held, returned, kept }) =>
      `发布账户存在期间，${held} XRP 作为其储备金被锁定，与发布了多少文件无关。账户关闭后，${returned} XRP 会退回。其余 ${kept} XRP 留在网络中，作为删除账户的费用。`,
    source: ({ network, date }) =>
      `数据取自 ${NETWORK_NAMES.zh[network] ?? network}，日期为 ${date}。手续费和储备金由网络设定，可能变化。`,
  },
};
