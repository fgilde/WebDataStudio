// gilde.org's contact and support forms, in this site's colours.
//
// The widgets draw into an open shadow root and set their palette on an element inside it, so no
// rule on this page reaches them — not even a custom property, which the inner element overrides.
// A stylesheet adopted by the shadow root does: it comes after the widget's own <style>, and it
// survives the widget rebuilding its content, which it does whenever an attribute changes.
(() => {
  const css = `
    .surface.surface {
      --bg: #171e35; --raised: #1d2542; --input: #0f1428; --line: #26304f;
      --text: #e8edfb; --muted: #9aa7c7;
      font-family: inherit;
    }
    .card {
      max-width: none;
      background: linear-gradient(180deg, #1a2240 0%, #171e35 55%);
      border-radius: 14px;
      box-shadow: 0 24px 60px -30px rgba(0, 0, 0, .9);
    }
    /* One bar for both cards: the site's gradient rather than each form's own accent. */
    .bar { height: 3px; background: linear-gradient(90deg, #2b8cff 0%, #22d3c5 50%, #8b5cf6 100%); }
    h2 { letter-spacing: -.01em; font-weight: 700; }
    .eyebrow { color: #22d3c5; letter-spacing: .14em; }
    .logo { background: #0f1428; border: 1px solid var(--line); border-radius: 12px; }

    label { color: var(--muted); font-weight: 600; }
    input, textarea {
      border-radius: 9px; background: var(--input);
      transition: border-color .15s, box-shadow .15s;
    }
    input:focus, textarea:focus {
      outline: none; border-color: #22d3c5; box-shadow: 0 0 0 3px rgba(34, 211, 197, .15);
    }

    button.primary {
      border-radius: 9px; border: none; min-height: 44px;
      background: linear-gradient(90deg, #2b8cff 0%, #22d3c5 50%, #8b5cf6 100%);
      color: #06101f; font-weight: 700;
    }
    button.primary:hover:not(:disabled) { filter: brightness(1.08); }

    .support-link { border-radius: 12px; background: #1b2340; }
    .support-link:hover { border-color: #22d3c5; box-shadow: 0 14px 34px -20px rgba(0, 0, 0, .9); }
    .support-qr { border-width: 4px; border-radius: 8px; }
    .star-support { border-radius: 12px; }

    .notice, .status { background: #1b2340; border-color: var(--line); }
    .error { background: rgba(248, 113, 113, .1); color: #fca5a5; border-color: rgba(248, 113, 113, .35); }
    .footer { border-top-color: var(--line); }
    dialog::backdrop { background: rgba(6, 9, 18, .8); backdrop-filter: blur(4px); }
  `;

  const sheet = new CSSStyleSheet();
  sheet.replaceSync(css);

  const theme = element => {
    const root = element.shadowRoot;
    if (root && !root.adoptedStyleSheets.includes(sheet))
      root.adoptedStyleSheets = [...root.adoptedStyleSheets, sheet];
  };

  const all = () => document.querySelectorAll("gilde-contact, gilde-support").forEach(theme);

  for (const name of ["gilde-contact", "gilde-support"])
    customElements.whenDefined(name).then(all);

  // The documentation renders its pages after load, one route at a time.
  new MutationObserver(all).observe(document.documentElement, { childList: true, subtree: true });
})();
