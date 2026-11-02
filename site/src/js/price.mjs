// Formatting for the price table. The figures come from data/price.json, written by the publisher's
// own estimate; nothing here computes a price, it only spells the numbers for a reader.
const DROPS_PER_XRP = 1_000_000;
const LOCALES = { en: 'en-GB', ru: 'ru-RU' };
const UNITS = { en: { B: 'B', KB: 'KB', MB: 'MB' }, ru: { B: 'Б', KB: 'КБ', MB: 'МБ' } };

const numberFormats = new Map();
const dateFormats = new Map();

function localeOf(lang) {
  return LOCALES[lang] ?? LOCALES.en;
}

function numberFormat(lang) {
  let format = numberFormats.get(lang);
  if (!format) {
    // XRP has six decimal places; showing all of them and no padding keeps 0.00027 exact and 1 short.
    format = new Intl.NumberFormat(localeOf(lang), { maximumFractionDigits: 6, minimumFractionDigits: 0 });
    numberFormats.set(lang, format);
  }
  return format;
}

/** Drops as XRP: up to six decimals, no trailing zeros, the language's decimal separator. */
export function formatXrp(drops, lang) {
  return numberFormat(lang).format(drops / DROPS_PER_XRP);
}

/** A byte count as the publisher's estimate labels sizes: 1024-based KB and MB. */
export function formatSize(bytes, lang) {
  const units = UNITS[lang] ?? UNITS.en;
  const number = new Intl.NumberFormat(localeOf(lang), { maximumFractionDigits: 1 });
  if (bytes >= 1024 * 1024) return `${number.format(bytes / (1024 * 1024))} ${units.MB}`;
  if (bytes >= 1024) return `${number.format(bytes / 1024)} ${units.KB}`;
  return `${number.format(bytes)} ${units.B}`;
}

/** The UTC calendar day of an ISO timestamp, with "UTC" spelled out so the day is not read as local. */
export function formatDate(iso, lang) {
  let format = dateFormats.get(lang);
  if (!format) {
    format = new Intl.DateTimeFormat(localeOf(lang), { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' });
    dateFormats.set(lang, format);
  }
  return `${format.format(new Date(iso))} UTC`;
}

/**
 * The table's rows, in the file's order.
 * @returns {Array<{ size: string, fee: string, held: string, returned: string }>}
 */
export function priceRows(json, lang) {
  return json.rows.map((row) => ({
    size: formatSize(row.bytes, lang),
    fee: formatXrp(row.feeDrops, lang),
    held: formatXrp(row.heldDrops, lang),
    returned: formatXrp(row.returnedDrops, lang),
  }));
}

/**
 * What the network keeps of the reserve when the throwaway account is deleted, in drops. Null when the
 * rows disagree (or there are none): then the page does not state a single figure.
 */
export function keptDrops(json) {
  const kept = new Set(json.rows.map((row) => row.heldDrops - row.returnedDrops));
  return kept.size === 1 ? [...kept][0] : null;
}

/**
 * The reserve a run holds, gives back and leaves with the network, in drops, when every row agrees;
 * otherwise null and the page states no single figure.
 */
export function reserveSummary(json) {
  const kept = keptDrops(json);
  const held = new Set(json.rows.map((row) => row.heldDrops));
  if (kept === null || held.size !== 1) return null;
  const [heldDrops] = [...held];
  return { heldDrops, returnedDrops: heldDrops - kept, keptDrops: kept };
}
