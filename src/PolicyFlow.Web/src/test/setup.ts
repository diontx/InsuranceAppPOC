import '@testing-library/jest-dom/vitest';
import { afterEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';

// Unmounts components between tests so one test's DOM cannot leak into the next.
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.useRealTimers();
});

// jsdom does not implement crypto.randomUUID. The idempotency-key hook depends
// on it, and a deterministic stub also makes the key assertable.
if (typeof crypto.randomUUID !== 'function') {
  let counter = 0;
  Object.defineProperty(crypto, 'randomUUID', {
    configurable: true,
    value: () => {
      counter += 1;
      return `00000000-0000-4000-8000-${String(counter).padStart(12, '0')}`;
    },
  });
}
