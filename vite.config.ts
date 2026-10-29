import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
export default defineConfig({
  root: 'web',
  base: process.env.VITE_BASE_PATH || './',
  plugins: [react()],
  build: { outDir: '../dist', emptyOutDir: true },
  test: { include: ['../tests/unit/**/*.test.ts'], environment: 'node' },
} as Parameters<typeof defineConfig>[0]);
