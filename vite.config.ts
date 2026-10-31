import { readFileSync } from 'node:fs';
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
const { version } = JSON.parse(readFileSync(new URL('./package.json', import.meta.url), 'utf8'));
export default defineConfig({
  root: 'web',
  base: process.env.VITE_BASE_PATH || './',
  plugins: [react()],
  define: { __APP_VERSION__: JSON.stringify(version) },
  build: { outDir: '../dist', emptyOutDir: true },
  test: { include: ['../tests/unit/**/*.test.ts'], environment: 'node' },
} as Parameters<typeof defineConfig>[0]);
