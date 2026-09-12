import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

/**
 * Vitest configuration, deliberately separate from `vite.config.ts`.
 *
 * Keeping them apart means the production build never typechecks against vitest,
 * so the Docker build stage does not need test tooling installed or present in
 * the image.
 */
export default defineConfig({
  plugins: [react()],

  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // Component styling is not asserted anywhere, and parsing the stylesheets
    // into jsdom is pure overhead.
    css: false,

    coverage: {
      provider: 'v8',
      // cobertura is what Azure DevOps publishes as a coverage report.
      reporter: ['text', 'cobertura'],
      reportsDirectory: './coverage',
      // Only application logic. Config, the entry point and type-only modules
      // have nothing to assert and would dilute the numbers.
      include: ['src/api/**', 'src/hooks/**', 'src/components/**', 'src/pages/**'],
      exclude: ['src/api/types.ts', 'src/main.tsx'],
    },
  },
});
