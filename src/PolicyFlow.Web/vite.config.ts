import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react()],

  server: {
    port: 5173,
    // Binds 0.0.0.0 so the dev server is reachable from a phone on the same
    // network — the mobile lookup flow has to be tested on a real device, not
    // just a narrowed desktop window.
    host: true,

    proxy: {
      // Proxies API calls to the container in development, so the browser makes
      // a same-origin request and CORS never enters the picture.
      '/api': {
        target: 'http://localhost:8081',
        changeOrigin: true,
      },
      '/legacy': {
        target: 'http://localhost:8081',
        changeOrigin: true,
      },
      '/health': {
        target: 'http://localhost:8081',
        changeOrigin: true,
      },
    },
  },

  build: {
    outDir: 'dist',
    // Source maps ship so a production stack trace is readable. The bundle is
    // already public, so this reveals nothing a determined reader could not
    // recover from the minified output.
    sourcemap: true,
    // The default 500kB warning fires on a bundle this size for no useful reason;
    // 600 keeps the signal meaningful.
    chunkSizeWarningLimit: 600,
  },

  // Test configuration lives in vitest.config.ts, not here. Keeping it separate
  // means the production build never has to typecheck against vitest, so the
  // Docker build stage does not depend on test tooling being installed.
});
