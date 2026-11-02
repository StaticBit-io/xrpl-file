// The header and footer's own words. build.mjs renders the page-time copy from this table, and
// chrome.mjs swaps it in place on pages whose language is the stored choice (the file page, 404).
export const STRINGS = {
  en: {
    skip: 'Skip to content',
    homeLabel: 'xrpl-file, home',
    languageLabel: 'Language',
    projectLabel: 'Project',
    repository: 'Repository',
    specification: 'Specification',
    themeLabel: 'Theme',
    lightTheme: 'Light theme',
  },
  ru: {
    skip: 'К содержимому',
    homeLabel: 'xrpl-file, на главную',
    languageLabel: 'Язык',
    projectLabel: 'Проект',
    repository: 'Репозиторий',
    specification: 'Спецификация',
    themeLabel: 'Тема',
    lightTheme: 'Светлая тема',
  },
};
