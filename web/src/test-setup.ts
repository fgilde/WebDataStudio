/// What jsdom does not have and Mantine expects.
///
/// Every component test used to carry its own copy of these three stubs, which is why a new test
/// crashed in an autosize textarea rather than in its own assertion. They live here now, and
/// vite.config.ts hands this file to vitest before a suite runs.

// The pure suites run in node, where there is no window to patch at all.
if (typeof window !== "undefined") {
  window.matchMedia ??= ((query: string) => ({
  matches: false, media: query, onchange: null,
  addListener: () => {}, removeListener: () => {},
  addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false,
  })) as typeof window.matchMedia;

  // Mantine's ScrollArea and the dock measure themselves.
  globalThis.ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;

  // An autosize textarea re-measures when a font finishes loading, and jsdom has no font set.
  if (!("fonts" in document))
    Object.defineProperty(document, "fonts", {
      value: {
        addEventListener: () => {},
        removeEventListener: () => {},
        ready: Promise.resolve(),
      },
    });
}
