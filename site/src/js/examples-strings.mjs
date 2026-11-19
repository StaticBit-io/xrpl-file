// The words of the examples page's cards: a title and one line per example, and the text of the link
// to the file page. Same keys in every active language (test/examples.test.mjs checks that).
// de, es, pt and zh are translations from English awaiting a native speaker's review.

const DRAWING_LINE = {
  en: 'A picture read from the ledger and checked block by block before it is shown.',
  ru: 'Картинка читается из реестра и проверяется блок за блоком, прежде чем появиться на экране.',
  de: 'Ein Bild, das aus dem Ledger gelesen und Block für Block geprüft wird, bevor es erscheint.',
  es: 'Una imagen leída del ledger y comprobada bloque a bloque antes de mostrarse.',
  pt: 'Uma imagem lida do ledger e conferida bloco a bloco antes de aparecer.',
  zh: '图片从账本读取，逐块校验后才会显示。',
};
const DRAWING_TITLE = { en: 'Drawing', ru: 'Рисунок', de: 'Zeichnung', es: 'Dibujo', pt: 'Desenho', zh: '图画' };

export const EXAMPLE_STRINGS = {
  en: {
    'forest-guardian': { title: DRAWING_TITLE.en, line: DRAWING_LINE.en },
    'xrpl-file-en': { title: 'Short video', line: 'A video that starts playing before the whole file has been read.' },
    chunks: { title: 'SVG file', line: 'The element offers this SVG for download instead of drawing it, because an SVG can carry script.' },
    about: { title: 'Text in six languages', line: 'A text file stored in the ledger, in the six languages of this site.' },
    logo: { title: 'The StaticBit logo', line: 'The picture from the home page, read from the ledger the same way.' },
  },
  ru: {
    'forest-guardian': { title: DRAWING_TITLE.ru, line: DRAWING_LINE.ru },
    'xrpl-file-en': { title: 'Короткое видео', line: 'Видео начинает воспроизводиться, не дожидаясь, пока будет прочитан весь файл.' },
    chunks: { title: 'Файл SVG', line: 'Элемент не рисует этот SVG, а предлагает его скачать: SVG может содержать скрипт.' },
    about: { title: 'Текст на шести языках', line: 'Текстовый файл, записанный в реестр, на шести языках этого сайта.' },
    logo: { title: 'Логотип StaticBit', line: 'Картинка с главной страницы, прочитанная из реестра тем же способом.' },
  },
  de: {
    'forest-guardian': { title: DRAWING_TITLE.de, line: DRAWING_LINE.de },
    'xrpl-file-en': { title: 'Kurzes Video', line: 'Ein Video, das abgespielt wird, bevor die ganze Datei gelesen ist.' },
    chunks: { title: 'SVG-Datei', line: 'Das Element zeichnet diese SVG-Datei nicht, sondern bietet sie zum Download an, weil eine SVG-Datei Skripte enthalten kann.' },
    about: { title: 'Text in sechs Sprachen', line: 'Eine Textdatei im Ledger, in den sechs Sprachen dieser Website.' },
    logo: { title: 'Das StaticBit-Logo', line: 'Das Bild von der Startseite, auf dieselbe Weise aus dem Ledger gelesen.' },
  },
  es: {
    'forest-guardian': { title: DRAWING_TITLE.es, line: DRAWING_LINE.es },
    'xrpl-file-en': { title: 'Vídeo corto', line: 'Un vídeo que empieza a reproducirse antes de haber leído todo el archivo.' },
    chunks: { title: 'Archivo SVG', line: 'El elemento no dibuja este SVG, sino que lo ofrece para descargar, porque un SVG puede llevar scripts.' },
    about: { title: 'Texto en seis idiomas', line: 'Un archivo de texto guardado en el ledger, en los seis idiomas de este sitio.' },
    logo: { title: 'El logotipo de StaticBit', line: 'La imagen de la página de inicio, leída del ledger de la misma manera.' },
  },
  pt: {
    'forest-guardian': { title: DRAWING_TITLE.pt, line: DRAWING_LINE.pt },
    'xrpl-file-en': { title: 'Vídeo curto', line: 'Um vídeo que começa a tocar antes de o arquivo inteiro ter sido lido.' },
    chunks: { title: 'Arquivo SVG', line: 'O elemento não desenha este SVG, mas o oferece para download, porque um SVG pode conter script.' },
    about: { title: 'Texto em seis idiomas', line: 'Um arquivo de texto guardado no ledger, nos seis idiomas deste site.' },
    logo: { title: 'O logotipo da StaticBit', line: 'A imagem da página inicial, lida do ledger da mesma forma.' },
  },
  zh: {
    'forest-guardian': { title: DRAWING_TITLE.zh, line: DRAWING_LINE.zh },
    'xrpl-file-en': { title: '短视频', line: '视频在整个文件读完之前就开始播放。' },
    chunks: { title: 'SVG 文件', line: '元素不绘制这个 SVG，而是提供下载，因为 SVG 可能带有脚本。' },
    about: { title: '六种语言的文本', line: '存放在账本中的文本文件，包含本站的六种语言。' },
    logo: { title: 'StaticBit 标志', line: '首页上的图片，以同样的方式从账本读取。' },
  },
};

/** The text of the link from a card to the file's own page. */
export const EXAMPLE_FILE_LINK = {
  en: 'Open the file page',
  ru: 'Открыть страницу файла',
  de: 'Dateiseite öffnen',
  es: 'Abrir la página del archivo',
  pt: 'Abrir a página do arquivo',
  zh: '打开文件页面',
};

// The embed code of each card: the "</>" button over the preview (its name is spoken and shown as a
// tooltip), the dialog's heading, its copy and close buttons. The words follow the home page's snippet.
export const EXAMPLE_CODE = {
  en: { show: (title) => `Show the embed code: ${title}`, code: (title) => `Embed code: ${title}`, copy: 'Copy', copyLabel: (title) => `Copy the embed code: ${title}`, close: 'Close' },
  ru: { show: (title) => `Показать код для вставки: ${title}`, code: (title) => `Код для вставки: ${title}`, copy: 'Копировать', copyLabel: (title) => `Копировать код для вставки: ${title}`, close: 'Закрыть' },
  de: { show: (title) => `Code zum Einbetten anzeigen: ${title}`, code: (title) => `Code zum Einbetten: ${title}`, copy: 'Kopieren', copyLabel: (title) => `Code zum Einbetten kopieren: ${title}`, close: 'Schließen' },
  es: { show: (title) => `Mostrar el código para incrustar: ${title}`, code: (title) => `Código para incrustar: ${title}`, copy: 'Copiar', copyLabel: (title) => `Copiar el código para incrustar: ${title}`, close: 'Cerrar' },
  pt: { show: (title) => `Mostrar o código para incorporar: ${title}`, code: (title) => `Código para incorporar: ${title}`, copy: 'Copiar', copyLabel: (title) => `Copiar o código para incorporar: ${title}`, close: 'Fechar' },
  zh: { show: (title) => `显示嵌入代码：${title}`, code: (title) => `嵌入代码：${title}`, copy: '复制', copyLabel: (title) => `复制嵌入代码：${title}`, close: '关闭' },
};
