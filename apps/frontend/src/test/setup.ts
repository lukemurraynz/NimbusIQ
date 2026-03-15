import "@testing-library/jest-dom/vitest";

if (typeof globalThis.crypto === "undefined") {
  Object.defineProperty(globalThis, "crypto", {
    value: {
      randomUUID: () => `test-uuid-${Math.random().toString(16).slice(2)}`,
    },
    configurable: true,
  });
} else if (typeof globalThis.crypto.randomUUID !== "function") {
  Object.defineProperty(globalThis.crypto, "randomUUID", {
    value: () => `test-uuid-${Math.random().toString(16).slice(2)}`,
    configurable: true,
  });
}
