import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// The API host only listens on HTTPS with a self-signed development
// certificate. Proxying keeps browser calls same-origin during development,
// so no backend CORS entry is needed locally. A deployed build points
// VITE_API_BASE at the real API origin instead of using this proxy.
const apiTarget = process.env.VITE_PROXY_TARGET ?? 'https://localhost:44311';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true, secure: false },
      '/connect': { target: apiTarget, changeOrigin: true, secure: false },
      '/.well-known': { target: apiTarget, changeOrigin: true, secure: false },
    },
  },
  build: {
    rollupOptions: {
      output: {
        // antd is by far the largest dependency and changes on its own
        // release cadence, so it gets its own chunk that browsers can cache
        // across app deploys.
        manualChunks: {
          antd: ['antd', '@ant-design/icons'],
        },
      },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['src/test-setup.ts'],
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
  },
});
