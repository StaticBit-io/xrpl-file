// The markup and styles inside the element's shadow root, written once. Nothing in it is built from
// data: names, sizes, messages and file content are put in with textContent or attributes afterwards.
// Styles follow the approved mockup.
//
// Colours come in two layers. A theme is a palette of private --t-* properties on .frame, picked by its
// data-theme (the element's `theme` attribute, parsed in attributes.mjs); the public --xrpl-file-*
// properties are read over it, so a site's own value always wins over the theme's. Dark is the default
// and the original look. Media surfaces (the caption, the overlay on a video, the idle view over a
// cover) sit on pictures, not on the page, so they keep the dark palette in every theme.
//
// Size tiers follow the element's own width, not the page's: .frame is a size container, and the
// @container rules at the end switch to a compact layout under 240px and an icon layout under 120px
// (or under 56px of height, where even the compact one would not fit). The breakpoints are written
// out in range syntax, so 240px and 120px belong to the tier above, because a custom property cannot
// sit in a query. Engines without container units keep the fixed sizes written before each clamp().
// Every piece of every tier is in the markup; the tiers only show and hide them. Above the compact
// tier, sizes follow clamp() with cqi, which resolves against .frame, and reach today's values at
// about 320px.
//
// The styles are text apart from the markup: a strict Content Security Policy drops <style> elements
// that arrive as parsed markup, so the element hands them to a constructable stylesheet instead.

// Clipped away but still read by a screen reader: for text a narrow tier replaces with something shorter.
const VISUALLY_HIDDEN = `position: absolute !important; width: 1px; height: 1px; margin: -1px; padding: 0; border: 0; overflow: hidden; clip-path: inset(50%); white-space: nowrap;`;

// The two palettes. --t-scrim is the scrim over a box without a cover: the original dark look, which
// the light theme leaves out.
const DARK = `
    color-scheme: dark;
    --t-accent: #4da3ff; --t-on-accent: #06121f;
    --t-background: #14161a; --t-foreground: #e8e8ea; --t-muted: #9a9ca3;
    --t-track: rgba(255,255,255,.15); --t-danger: #ff8a8a;
    --t-text-background: #1b1e23; --t-text-foreground: #e8e8ea; --t-text-bar: #262a30;
    --t-scrim: linear-gradient(to top, rgba(0,0,0,.65), rgba(0,0,0,.15));`;

const LIGHT = `
    color-scheme: light;
    --t-accent: #0b62c4; --t-on-accent: #ffffff;
    --t-background: #f3f4f6; --t-foreground: #1b1d22; --t-muted: #5b5f6b;
    --t-track: rgba(0,0,0,.16); --t-danger: #b3261e;
    --t-text-background: #ffffff; --t-text-foreground: #1f2328; --t-text-bar: #e6e8eb;
    --t-scrim: none;`;

export const STYLES = `
  *, *::before, *::after { box-sizing: border-box; }
  [hidden] { display: none !important; }
  .frame {
    ${DARK}
    --accent: var(--xrpl-file-accent, var(--t-accent));
    --on-accent: var(--xrpl-file-on-accent, var(--t-on-accent));
    --background: var(--xrpl-file-background, var(--t-background));
    --fg: var(--xrpl-file-foreground, var(--t-foreground));
    --muted: var(--xrpl-file-muted, var(--t-muted));
    --track: var(--xrpl-file-track, var(--t-track));
    --danger: var(--xrpl-file-danger, var(--t-danger));
    --text-background: var(--xrpl-file-text-background, var(--t-text-background));
    --text-foreground: var(--xrpl-file-text-foreground, var(--t-text-foreground));
    --text-bar: var(--xrpl-file-text-bar, var(--t-text-bar));
    --media: var(--xrpl-file-media-background, #000);
    --overlay: var(--xrpl-file-overlay, rgba(0,0,0,.72));
    --overlay-foreground: var(--xrpl-file-overlay-foreground, #ddd);
    position: absolute; inset: 0; overflow: hidden;
    container-type: size;
    border-radius: var(--xrpl-file-radius, 10px);
    background: var(--background);
    color: var(--fg);
    font: 13px/1.6 system-ui, -apple-system, "Segoe UI", sans-serif;
  }
  .frame[data-theme="light"] { ${LIGHT} }
  @media (prefers-color-scheme: light) { .frame[data-theme="auto"] { ${LIGHT} } }
  .caption, .overlay, .idle.covered {
    --accent: var(--xrpl-file-overlay-accent, var(--xrpl-file-accent, #4da3ff));
    --on-accent: var(--xrpl-file-overlay-on-accent, var(--xrpl-file-on-accent, #06121f));
    --fg: var(--overlay-foreground);
    --muted: var(--overlay-foreground);
    --track: var(--xrpl-file-overlay-track, rgba(255,255,255,.15));
    --danger: var(--xrpl-file-overlay-danger, #ffa0a0);
    --success: var(--xrpl-file-overlay-success, #6fd08c);
    color: var(--fg);
  }
  .frame.bare { background: transparent; }
  .view {
    position: absolute; inset: 0; display: flex; text-align: center; overflow: hidden; min-width: 0; min-height: 0;
    align-items: center; align-items: safe center; justify-content: center; justify-content: safe center;
  }
  .fill { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; }
  .dim { opacity: 0.45; }
  .center { position: relative; padding: 0 18px; padding: 0 clamp(8px, 5.6cqi, 18px); max-width: 100%; max-height: 100%; min-width: 0; }
  .big { font-size: 22px; font-size: clamp(16px, 7cqi, 22px); font-weight: 600; line-height: 1.3; overflow-wrap: anywhere; }
  .muted { color: var(--muted); overflow-wrap: anywhere; font-size: 13px; font-size: clamp(11px, 4cqi, 13px); }
  .note { margin-top: 6px; font-size: 12.5px; font-size: clamp(11px, 3.9cqi, 12.5px); }
  .sr, .sr-name { ${VISUALLY_HIDDEN} }

  .ring { width: 38px; height: 38px; width: clamp(24px, 12cqi, 38px); height: clamp(24px, 12cqi, 38px); border-radius: 50%; margin: 0 auto 10px; border: 3px solid var(--track); border-top-color: var(--accent); animation: spin 0.9s linear infinite; }
  @keyframes spin { to { transform: rotate(360deg); } }
  @media (prefers-reduced-motion: reduce) { .ring { animation: none; } }
  .bar { position: absolute; left: 0; right: 0; bottom: 0; height: 4px; background: var(--track); }
  .bar > i { display: block; height: 100%; width: 0; background: var(--accent); }

  .btn { display: inline-block; margin-top: 10px; padding: 6px 14px; padding: clamp(4px, 1.9cqi, 6px) clamp(10px, 4.4cqi, 14px); border: 0; border-radius: 6px; background: var(--accent); color: var(--on-accent); font: inherit; font-size: 13px; font-size: clamp(11px, 4cqi, 13px); font-weight: 600; cursor: pointer; }
  .btn:disabled { opacity: 0.6; cursor: default; }
  .btn.play { width: 56px; height: 56px; width: clamp(24px, 30cqmin, 56px); height: clamp(24px, 30cqmin, 56px); padding: 0; border-radius: 50%; background: var(--overlay); color: var(--overlay-foreground); font-size: 22px; font-size: clamp(11px, 9cqmin, 22px); line-height: 56px; line-height: clamp(24px, 30cqmin, 56px); }
  /* The dark fill alone vanishes on a dark cover (1.2:1); a light ring around it keeps the button at 3:1
     on any picture, since one of the two edges always stands out. */
  .btn.play { box-shadow: 0 0 0 2px var(--overlay-foreground); }
  button:focus-visible, pre:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
  .status { margin-top: 6px; min-height: 1.6em; font-size: 12.5px; font-size: clamp(11px, 3.9cqi, 12.5px); }
  .btn .ico { display: none; width: 1.25em; height: 1.25em; vertical-align: -0.25em; }
  .lbl-short, .badge { display: none; }

  .player video { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; background: var(--media); }
  .overlay { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; text-align: center; pointer-events: none; }
  .overlay.failed { background: var(--overlay); }
  .overlay.failed .ring, .overlay.failed .bar { display: none; }
  .overlay.failed .overlay-text { color: var(--danger); font-size: 14px; }
  .overlay .bar > i { width: 30%; animation: slide 1.2s ease-in-out infinite; }
  @keyframes slide { from { margin-left: -30%; } to { margin-left: 100%; } }
  @media (prefers-reduced-motion: reduce) { .overlay .bar > i { animation: none; width: 100%; opacity: 0.5; } }

  .idle.covered { background: var(--media); }
  .idle .scrim { position: absolute; inset: 0; background: var(--xrpl-file-scrim, var(--t-scrim)); }
  .idle.covered .scrim { background: var(--xrpl-file-scrim, linear-gradient(to top, rgba(0,0,0,.65), rgba(0,0,0,.15))); }
  .error .message { color: var(--danger); font-size: 14px; font-size: clamp(11px, 4.4cqi, 14px); padding: 0 18px; padding: 0 clamp(8px, 5.6cqi, 18px); max-width: 100%; min-width: 0; overflow-wrap: anywhere; }
  .msg-icon, .msg-short { display: none; }
  .msg-icon svg { width: 22px; height: 22px; width: clamp(16px, 30cqmin, 22px); height: clamp(16px, 30cqmin, 22px); vertical-align: middle; }

  .text { flex-direction: column; align-items: stretch; justify-content: flex-start; text-align: left; background: var(--text-background); color: var(--text-foreground); }
  .text pre { flex: 1; min-height: 0; margin: 0; padding: 14px 16px; padding: clamp(6px, 4.4cqi, 14px) clamp(8px, 5cqi, 16px); overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; font: 13px/1.5 ui-monospace, Consolas, monospace; font: clamp(11px, 4cqi, 13px)/1.5 ui-monospace, Consolas, monospace; }
  .textbar { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 12px; padding: 5px 12px; padding: clamp(3px, 1.6cqi, 5px) clamp(6px, 3.7cqi, 12px); background: var(--text-bar); color: var(--text-foreground); font-size: 12.5px; font-size: clamp(11px, 3.9cqi, 12.5px); }
  .textbar .btn { margin: 0; padding: 2px 10px; }
  .textbar .status { margin: 0; min-height: 0; }

  .caption {
    position: absolute; left: 8px; right: 8px; bottom: 10px;
    left: clamp(4px, 2.5cqi, 8px); right: clamp(4px, 2.5cqi, 8px); bottom: clamp(4px, 3.1cqi, 10px);
    display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 2px 10px;
    padding: 5px 8px; padding: clamp(3px, 1.6cqi, 5px) clamp(5px, 2.5cqi, 8px); border-radius: 6px; background: var(--overlay); font-size: 11.5px; font-size: clamp(11px, 3.6cqi, 11.5px); line-height: 1.5;
    opacity: 0; pointer-events: none; transition: opacity .15s;
  }
  @media (prefers-reduced-motion: reduce) { .caption { transition: none; } }
  .frame.video .caption { top: 8px; top: clamp(4px, 2.5cqi, 8px); bottom: auto; }
  .frame.bare .caption { display: none; }
  .frame:hover .caption, .frame:focus-within .caption, .frame.tapped .caption { opacity: 1; pointer-events: auto; }
  .caption .name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .caption .side { display: flex; align-items: center; gap: 8px; min-width: 0; }
  .caption .net { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .caption .ok { color: var(--success); }
  .caption button { padding: 1px 8px; border: 1px solid var(--xrpl-file-overlay-border, rgba(255,255,255,.6)); border-radius: 4px; background: transparent; color: inherit; font: inherit; cursor: pointer; }
  .caption button[data-state="copied"] { border-color: var(--success); color: var(--success); }
  .caption button[data-state="failed"] { border-color: var(--danger); color: var(--danger); }
  .caption .cp-ico, .caption .cp-mark { display: none; }
  .caption .cp-ico { width: 14px; height: 14px; vertical-align: -3px; }

  /* Compact: 120 to 239px wide. One line per text, the essentials only. */
  @container (width < 240px) {
    .center { padding: 0 clamp(6px, 5cqi, 12px); }
    .big { font-size: clamp(12px, 8cqi, 16px); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .muted { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .ring { display: none; }
    .btn { margin-top: clamp(4px, 3cqi, 8px); padding: clamp(3px, 2cqi, 5px) clamp(8px, 4cqi, 12px); font-size: clamp(11px, 6cqi, 12.5px); }
    .status { margin-top: 2px; min-height: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .note { ${VISUALLY_HIDDEN} }
    .dl-type, .dl-net, .lo-blocks { display: none; }
    .download .lbl-full { display: none; }
    .download .lbl-short { display: inline; }
    .download .btn .ico { display: inline-block; margin-right: 4px; }
    .error .message { font-size: clamp(11px, 7cqi, 14px); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .error .msg-short { display: inline; }
    .error .msg-full { ${VISUALLY_HIDDEN} }
    /* At the top, so that on hover it covers the name and not the button in the middle of a box this small. */
    .caption, .frame.video .caption { justify-content: center; flex-wrap: nowrap; top: 4px; bottom: auto; }
    .caption .name, .caption .word, .caption .cp-text { display: none; }
    .caption .cp-ico { display: inline-block; }
    .caption button { min-width: 24px; min-height: 24px; padding: 1px 4px; }
  }

  /* Icon: under 120px wide, or under 56px high where the compact layout would overflow. */
  @container (width < 120px) or (height < 56px) {
    .center { padding: 0 2px; }
    .ring { display: none; }
    .download .big, .download .muted, .idle .big, .idle .muted, .loading .muted { display: none; }
    .download .status { ${VISUALLY_HIDDEN} }
    .loading .big { font-size: clamp(11px, 12cqi, 14px); }
    /* Before the first percent there is only the status line, which this size hides: show that something is going on. */
    .loading .big:empty::before { content: "\\2026"; }
    .badge { display: inline-block; padding: 0 5px; border: 1px solid var(--muted); border-radius: 4px; font-size: 11px; font-weight: 700; line-height: 1.5; letter-spacing: .04em; color: var(--fg); }
    .download .btn { display: inline-flex; align-items: center; justify-content: center; width: min(26px, calc(100cqb - 6px)); height: min(26px, calc(100cqb - 6px)); margin-top: 4px; padding: 0; }
    .download .btn .ico { display: inline-block; margin: 0; }
    .download .lbl-full { ${VISUALLY_HIDDEN} display: inline; }
    .download .lbl-short { display: none; }
    .idle .btn { margin-top: 0; }
    .error .msg-icon { display: inline; }
    .error .msg-short { display: none; }
    .error .msg-full { ${VISUALLY_HIDDEN} }
    .error .message { padding: 0 2px; }
    .caption, .frame.video .caption { left: auto; right: 3px; top: 3px; bottom: auto; padding: 0; background: transparent; gap: 0; }
    .caption .name, .caption .net, .caption .cp-text, .caption .cp-ico { display: none; }
    .caption.verified .cp-mark { display: inline; }
    .caption:not(.verified) .cp-ico { display: inline-block; }
    .caption button { width: 24px; height: 24px; padding: 0; border-radius: 50%; background: var(--overlay); line-height: 1; }
    .caption button:focus-visible, .btn:focus-visible { outline-offset: -2px; }
    /* Drawn inside the button, the ring takes the colour of what is drawn on its fill. */
    .btn:not(.play):focus-visible { outline-color: var(--on-accent); }
    .btn.play:focus-visible { outline-color: var(--overlay-foreground); }
  }

  /* Too short for a badge above the button. */
  @container (height < 52px) {
    .badge { display: none; }
  }

  /* A short box keeps at least a line of a cut-short text: wrapped, and with paddings that follow the
     width, the bar above it took the whole box. The note and the status give way first; both keep their
     full text as a tooltip, and the status is still announced whole. */
  @container (height < 120px) {
    .textbar { flex-wrap: nowrap; padding: 2px 8px; }
    .textbar .btn { flex: none; padding: 0 8px; line-height: 1.4; }
    .textbar .text-note, .textbar .status { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .text pre { padding: 2px 8px; }
  }
`;

export const TEMPLATE = `
<div class="frame" id="frame">
  <div class="view idle" id="idle" hidden>
    <img class="fill dim" id="cover" alt="" hidden>
    <div class="scrim"></div>
    <div class="center">
      <div class="big" id="idleName"></div>
      <div class="muted" id="idleMeta"></div>
      <button type="button" class="btn" id="open">Open</button>
    </div>
  </div>
  <div class="view loading" id="loading" part="loader" role="progressbar" aria-label="Loading the file" aria-valuemin="0" aria-valuemax="100" hidden>
    <div class="center">
      <div class="ring" aria-hidden="true"></div>
      <div class="big" id="percent"></div>
      <div class="muted" id="progressDetail"><span id="progressBytes"></span><span class="lo-blocks" id="progressBlocks"></span></div>
    </div>
    <div class="bar" aria-hidden="true"><i id="barFill"></i></div>
  </div>
  <div class="view image" id="image" hidden>
    <img class="fill" id="img" alt="">
  </div>
  <div class="view player" id="video" hidden>
    <div class="overlay" id="overlay" part="loader" hidden>
      <div class="center">
        <div class="ring" aria-hidden="true"></div>
        <div class="muted overlay-text" id="overlayText" role="status"></div>
      </div>
      <div class="bar" aria-hidden="true"><i></i></div>
    </div>
  </div>
  <div class="view text" id="text" hidden>
    <div class="textbar" id="textBar" hidden>
      <span class="text-note" id="textNote"></span>
      <button type="button" class="btn" id="textDownload">Download</button>
      <span class="status" id="textStatus" role="status"></span>
    </div>
    <pre id="pre" tabindex="0"></pre>
  </div>
  <div class="view download" id="download" hidden>
    <div class="center">
      <div class="badge" id="dlBadge" aria-hidden="true"></div>
      <div class="big" id="dlName"></div>
      <div class="muted" id="dlMeta"><span class="dl-type" id="dlType"></span><span id="dlSize"></span><span class="dl-net" id="dlNet"></span></div>
      <div class="muted note" id="dlNote" hidden></div>
      <button type="button" class="btn" id="dlButton"><svg class="ico" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M12 4v11m0 0-5-5m5 5 5-5M5 20h14" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/></svg><span class="lbl-full">Download</span><span class="lbl-short">Save</span> <span class="sr-name" id="dlLabel"></span></button>
      <div class="status muted" id="dlStatus" role="status"></div>
    </div>
  </div>
  <div class="view error" id="error" hidden>
    <div class="message" id="message" part="message" role="alert">
      <span class="msg-icon" id="msgIcon" aria-hidden="true"><svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M12 3.5 2.8 19.5h18.4L12 3.5zM12 10v4.5M12 17.2v.1" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg></span>
      <span class="msg-short" id="msgShort" aria-hidden="true"></span>
      <span class="msg-full" id="msgFull"></span>
    </div>
  </div>
  <div class="caption" id="caption" part="caption" hidden>
    <span class="name" id="capName"></span>
    <span class="side">
      <span class="net" id="capNet"><span id="capMark"></span><span class="word" id="capWord"></span><span id="capNetwork"></span></span>
      <button type="button" id="copy" aria-label="Copy link"><span class="cp-text" id="copyText">Copy link</span><svg class="cp-ico" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M9 9h11v11H9zM5 15V4h11" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg><span class="cp-mark" id="copyMark" aria-hidden="true">&#10003;</span></button>
      <span class="sr" id="copyStatus" role="status"></span>
    </span>
  </div>
</div>
`;
