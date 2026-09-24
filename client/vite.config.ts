import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Dev: proxy API calls to the .NET backend. Build: output into the server's wwwroot.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5175,
    proxy: { '/api': 'http://localhost:5140' },
  },
  build: { outDir: '../server/wwwroot', emptyOutDir: true },
})
