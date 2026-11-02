// The words of the file page and the 404 page. The two pages have one URL for both languages, so
// their text is switched in place; the header and footer words are in chrome-strings.mjs.
import { CHECK_LINK_STRINGS, COPY_STRINGS } from './home-strings.mjs';
import { localeOf } from './languages.mjs';

const EN = {
  fileTitle: 'File',
  download: 'Download',
  downloadSaved: 'Saved.',
  downloadStarted: 'Download started.',
  saveNotLoaded: 'The file has not loaded yet.',
  saveNotVerified: 'The file did not pass the check, so it was not saved.',
  saveUnavailable: 'Part of the file is not available from the ledger. Nothing was saved.',
  saveNodeDown: 'The XRP Ledger node did not answer. Try again later.',
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
    'xrpl-file writes a file into the XRP Ledger in chunks. Your browser collects the chunks, assembles the file and checks it against its fingerprint. A file that does not match its fingerprint is not shown. This site’s server takes no part in it.',
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
  saveNodeDown: 'Узел XRP Ledger не ответил. Попробуйте позже.',
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

const DE = {
  fileTitle: 'Datei',
  download: 'Herunterladen',
  downloadSaved: 'Gespeichert.',
  downloadStarted: 'Download gestartet.',
  saveNotLoaded: 'Die Datei ist noch nicht geladen.',
  saveNotVerified: 'Die Datei hat die Prüfung nicht bestanden und wurde deshalb nicht gespeichert.',
  saveUnavailable: 'Ein Teil der Datei ist im Ledger gerade nicht verfügbar. Es wurde nichts gespeichert.',
  saveNodeDown: 'Der Knoten des XRP Ledger hat nicht geantwortet. Versuchen Sie es später erneut.',
  saveTooLarge: 'Die Datei ist zu groß, um sie in diesem Browser zu speichern.',
  saveRefused: 'Der Browser hat das Speichern nicht erlaubt. Klicken Sie erneut auf „Herunterladen“.',
  saveInterrupted: 'Das Speichern wurde unterbrochen. Klicken Sie erneut auf „Herunterladen“.',
  saveFailed: 'Die Datei konnte nicht gespeichert werden.',
  sizeLabel: 'Größe',
  networkLabel: 'Netzwerk',
  linkLabel: 'Link',
  networkMainnet: 'Mainnet',
  networkTestnet: 'Testnet',
  sizeB: '{value} B',
  sizeKB: '{value} KB',
  sizeMB: '{value} MB',
  sizeGB: '{value} GB',
  copy: 'Kopieren',
  copyLabel: 'Link kopieren',
  copied: COPY_STRINGS.de.copied,
  copyFailed: COPY_STRINGS.de.failed,
  howTitle: 'So funktioniert es',
  howText:
    'xrpl-file schreibt eine Datei in Chunks in das XRP Ledger. Ihr Browser sammelt die Chunks, setzt die Datei zusammen und prüft sie anhand ihres Fingerabdrucks. Eine Datei, die nicht zu ihrem Fingerabdruck passt, wird nicht angezeigt. Der Server dieser Website ist daran nicht beteiligt.',
  howLink: 'Mehr auf der Startseite',
  checkTitle: 'Link prüfen',
  checkLabel: 'Fügen Sie einen xrpl-file-Link ein, um die Datei zu öffnen',
  checkOpen: 'Öffnen',
  checkEmpty: CHECK_LINK_STRINGS.de.empty,
  checkInvalid: CHECK_LINK_STRINGS.de.invalid,
  notFoundTitle: 'Seite nicht gefunden',
  notFoundText: 'Unter dieser Adresse gibt es keine Seite.',
  homeLink: 'Zur Startseite',
};

const ES = {
  fileTitle: 'Archivo',
  download: 'Descargar',
  downloadSaved: 'Guardado.',
  downloadStarted: 'Descarga iniciada.',
  saveNotLoaded: 'El archivo aún no se ha cargado.',
  saveNotVerified: 'El archivo no superó la comprobación, por eso no se guardó.',
  saveUnavailable: 'Una parte del archivo no está disponible ahora mismo en el ledger. No se guardó nada.',
  saveNodeDown: 'El nodo del XRP Ledger no respondió. Inténtalo de nuevo más tarde.',
  saveTooLarge: 'El archivo es demasiado grande para guardarlo en este navegador.',
  saveRefused: 'El navegador no permitió guardar. Pulsa «Descargar» otra vez.',
  saveInterrupted: 'Se interrumpió el guardado. Pulsa «Descargar» otra vez.',
  saveFailed: 'No se pudo guardar el archivo.',
  sizeLabel: 'Tamaño',
  networkLabel: 'Red',
  linkLabel: 'Enlace',
  networkMainnet: 'red principal',
  networkTestnet: 'red de pruebas',
  sizeB: '{value} B',
  sizeKB: '{value} KB',
  sizeMB: '{value} MB',
  sizeGB: '{value} GB',
  copy: 'Copiar',
  copyLabel: 'Copiar el enlace',
  copied: COPY_STRINGS.es.copied,
  copyFailed: COPY_STRINGS.es.failed,
  howTitle: 'Cómo funciona',
  howText:
    'xrpl-file escribe un archivo en el XRP Ledger por fragmentos. Tu navegador reúne los fragmentos, monta el archivo y lo comprueba con su huella digital. Un archivo que no coincide con su huella no se muestra. El servidor de este sitio no interviene.',
  howLink: 'Más en la página de inicio',
  checkTitle: 'Comprobar un enlace',
  checkLabel: 'Pega un enlace de xrpl-file para abrir su archivo',
  checkOpen: 'Abrir',
  checkEmpty: CHECK_LINK_STRINGS.es.empty,
  checkInvalid: CHECK_LINK_STRINGS.es.invalid,
  notFoundTitle: 'Página no encontrada',
  notFoundText: 'No hay ninguna página en esta dirección.',
  homeLink: 'Ir a la página de inicio',
};

const PT = {
  fileTitle: 'Arquivo',
  download: 'Baixar',
  downloadSaved: 'Salvo.',
  downloadStarted: 'O download começou.',
  saveNotLoaded: 'O arquivo ainda não foi carregado.',
  saveNotVerified: 'O arquivo não passou na verificação e por isso não foi salvo.',
  saveUnavailable: 'Uma parte do arquivo não está disponível no ledger agora. Nada foi salvo.',
  saveNodeDown: 'O nó do XRP Ledger não respondeu. Tente de novo mais tarde.',
  saveTooLarge: 'O arquivo é grande demais para ser salvo neste navegador.',
  saveRefused: 'O navegador não permitiu salvar. Clique em “Baixar” de novo.',
  saveInterrupted: 'O salvamento foi interrompido. Clique em “Baixar” de novo.',
  saveFailed: 'Não foi possível salvar o arquivo.',
  sizeLabel: 'Tamanho',
  networkLabel: 'Rede',
  linkLabel: 'Link',
  networkMainnet: 'rede principal',
  networkTestnet: 'rede de testes',
  sizeB: '{value} B',
  sizeKB: '{value} KB',
  sizeMB: '{value} MB',
  sizeGB: '{value} GB',
  copy: 'Copiar',
  copyLabel: 'Copiar o link',
  copied: COPY_STRINGS.pt.copied,
  copyFailed: COPY_STRINGS.pt.failed,
  howTitle: 'Como funciona',
  howText:
    'O xrpl-file grava um arquivo no XRP Ledger em fragmentos. Seu navegador reúne os fragmentos, monta o arquivo e o confere com a impressão digital. Um arquivo que não bate com a impressão digital não é exibido. O servidor deste site não participa disso.',
  howLink: 'Mais na página inicial',
  checkTitle: 'Verificar um link',
  checkLabel: 'Cole um link xrpl-file para abrir o arquivo',
  checkOpen: 'Abrir',
  checkEmpty: CHECK_LINK_STRINGS.pt.empty,
  checkInvalid: CHECK_LINK_STRINGS.pt.invalid,
  notFoundTitle: 'Página não encontrada',
  notFoundText: 'Não há nenhuma página neste endereço.',
  homeLink: 'Ir para a página inicial',
};

const ZH = {
  fileTitle: '文件',
  download: '下载',
  downloadSaved: '已保存。',
  downloadStarted: '已开始下载。',
  saveNotLoaded: '文件尚未加载完成。',
  saveNotVerified: '文件未通过校验，因此没有保存。',
  saveUnavailable: '文件的一部分暂时无法从账本读取，未保存任何内容。',
  saveNodeDown: 'XRP Ledger 节点没有响应，请稍后重试。',
  saveTooLarge: '文件太大，无法在此浏览器中保存。',
  saveRefused: '浏览器不允许保存，请再点一次“下载”。',
  saveInterrupted: '保存被中断，请再点一次“下载”。',
  saveFailed: '无法保存文件。',
  sizeLabel: '大小',
  networkLabel: '网络',
  linkLabel: '链接',
  networkMainnet: '主网',
  networkTestnet: '测试网',
  sizeB: '{value} B',
  sizeKB: '{value} KB',
  sizeMB: '{value} MB',
  sizeGB: '{value} GB',
  copy: '复制',
  copyLabel: '复制链接',
  copied: COPY_STRINGS.zh.copied,
  copyFailed: COPY_STRINGS.zh.failed,
  howTitle: '工作原理',
  howText:
    'xrpl-file 把文件分片写入 XRP Ledger。浏览器收集这些分片，拼成文件，并按指纹校验。与指纹不符的文件不会显示。本站服务器不参与这一过程。',
  howLink: '更多内容见首页',
  checkTitle: '检查链接',
  checkLabel: '粘贴 xrpl-file 链接以打开对应文件',
  checkOpen: '打开',
  checkEmpty: CHECK_LINK_STRINGS.zh.empty,
  checkInvalid: CHECK_LINK_STRINGS.zh.invalid,
  notFoundTitle: '页面不存在',
  notFoundText: '此地址没有页面。',
  homeLink: '前往首页',
};

export const TABLES = { en: EN, ru: RU, de: DE, es: ES, pt: PT, zh: ZH };

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
  const number = new Intl.NumberFormat(localeOf(lang), {
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
