// The markup and styles inside the element's shadow root, written once. Nothing in it is built from
// data: names, sizes, messages and file content are put in with textContent or attributes afterwards.
// Styles follow the approved mockup; the four --xrpl-file-* properties are the site's hooks and the
// values after the commas are their defaults.
//
// The styles are text apart from the markup: a strict Content Security Policy drops <style> elements
// that arrive as parsed markup, so the element hands them to a constructable stylesheet instead.

export const STYLES = `
  *, *::before, *::after { box-sizing: border-box; }
  [hidden] { display: none !important; }
  .frame {
    --accent: var(--xrpl-file-accent, #4da3ff);
    position: absolute; inset: 0; overflow: hidden;
    border-radius: var(--xrpl-file-radius, 10px);
    background: var(--xrpl-file-background, #14161a);
    color: var(--xrpl-file-foreground, #e8e8ea);
    font: 13px/1.6 system-ui, -apple-system, "Segoe UI", sans-serif;
  }
  .frame.bare { background: transparent; }
  .view { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; text-align: center; }
  .fill { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; }
  .dim { opacity: 0.45; }
  .center { position: relative; padding: 0 18px; max-width: 100%; }
  .big { font-size: 22px; font-weight: 600; line-height: 1.3; overflow-wrap: anywhere; }
  .muted { color: #9a9ca3; overflow-wrap: anywhere; }
  .note { margin-top: 6px; font-size: 12.5px; }

  .ring { width: 38px; height: 38px; border-radius: 50%; margin: 0 auto 10px; border: 3px solid rgba(255,255,255,.15); border-top-color: var(--accent); animation: spin 0.9s linear infinite; }
  @keyframes spin { to { transform: rotate(360deg); } }
  @media (prefers-reduced-motion: reduce) { .ring { animation: none; } }
  .bar { position: absolute; left: 0; right: 0; bottom: 0; height: 4px; background: rgba(255,255,255,.12); }
  .bar > i { display: block; height: 100%; width: 0; background: var(--accent); }

  .btn { display: inline-block; margin-top: 10px; padding: 6px 14px; border: 0; border-radius: 6px; background: var(--accent); color: #06121f; font: inherit; font-weight: 600; cursor: pointer; }
  .btn:disabled { opacity: 0.6; cursor: default; }
  .btn.play { width: 56px; height: 56px; padding: 0; border-radius: 50%; background: rgba(0,0,0,.6); color: #fff; font-size: 22px; line-height: 56px; }
  button:focus-visible, pre:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
  .status { margin-top: 6px; min-height: 1.6em; font-size: 12.5px; }

  .player video { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; background: #000; }
  .overlay { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; text-align: center; pointer-events: none; }
  .overlay.failed { background: rgba(0,0,0,.72); }
  .overlay.failed .ring, .overlay.failed .bar { display: none; }
  .overlay.failed .overlay-text { color: #ff8a8a; font-size: 14px; }
  .overlay .bar > i { width: 30%; animation: slide 1.2s ease-in-out infinite; }
  @keyframes slide { from { margin-left: -30%; } to { margin-left: 100%; } }
  @media (prefers-reduced-motion: reduce) { .overlay .bar > i { animation: none; width: 100%; opacity: 0.5; } }

  .idle .scrim { position: absolute; inset: 0; background: linear-gradient(to top, rgba(0,0,0,.65), rgba(0,0,0,.15)); }
  .error .message { color: #ff8a8a; font-size: 14px; padding: 0 18px; max-width: 100%; overflow-wrap: anywhere; }

  .text { flex-direction: column; align-items: stretch; justify-content: flex-start; text-align: left; background: #f7f7f5; color: #222; }
  .text pre { flex: 1; min-height: 0; margin: 0; padding: 14px 16px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; font: 13px/1.5 ui-monospace, Consolas, monospace; }
  .textbar { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 12px; padding: 5px 12px; background: #e6e6e1; color: #444; font-size: 12.5px; }
  .textbar .btn { margin: 0; padding: 2px 10px; }
  .textbar .status { margin: 0; min-height: 0; }

  .caption {
    position: absolute; left: 8px; right: 8px; bottom: 10px;
    display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 2px 10px;
    padding: 5px 8px; border-radius: 6px; background: rgba(0,0,0,.72); color: #ddd; font-size: 11.5px; line-height: 1.5;
    opacity: 0; pointer-events: none; transition: opacity .15s;
  }
  @media (prefers-reduced-motion: reduce) { .caption { transition: none; } }
  .frame.video .caption { top: 8px; bottom: auto; }
  .frame.bare .caption { display: none; }
  .frame:hover .caption, .frame:focus-within .caption, .frame.tapped .caption { opacity: 1; pointer-events: auto; }
  .caption .name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .caption .side { display: flex; align-items: center; gap: 8px; }
  .caption .ok { color: #6fd08c; }
  .caption button { padding: 1px 8px; border: 1px solid rgba(255,255,255,.4); border-radius: 4px; background: transparent; color: #ddd; font: inherit; cursor: pointer; }
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
      <div class="muted" id="progressDetail"></div>
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
      <span id="textNote"></span>
      <button type="button" class="btn" id="textDownload">Download</button>
      <span class="status" id="textStatus" role="status"></span>
    </div>
    <pre id="pre" tabindex="0"></pre>
  </div>
  <div class="view download" id="download" hidden>
    <div class="center">
      <div class="big" id="dlName"></div>
      <div class="muted" id="dlMeta"></div>
      <div class="muted note" id="dlNote" hidden></div>
      <button type="button" class="btn" id="dlButton">Download</button>
      <div class="status muted" id="dlStatus" role="status"></div>
    </div>
  </div>
  <div class="view error" id="error" hidden>
    <div class="message" id="message" part="message" role="alert"></div>
  </div>
  <div class="caption" id="caption" part="caption" hidden>
    <span class="name" id="capName"></span>
    <span class="side">
      <span id="capNet"></span>
      <button type="button" id="copy" aria-label="Copy link">Copy link</button>
    </span>
  </div>
</div>
`;
