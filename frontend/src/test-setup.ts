// antd measures its dropdowns with ResizeObserver, which jsdom does not
// implement. A no-op observer is enough for the tests: nothing asserts on a
// measured size, only on what the component renders once it is mounted.
globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

// antd's Select and Modal portal into the body and call matchMedia for the
// reduced-motion query; jsdom provides neither.
if (!globalThis.matchMedia) {
  globalThis.matchMedia = ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener() {},
    removeListener() {},
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
}
