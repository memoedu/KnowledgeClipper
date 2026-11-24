import { defineConfig } from 'vite';
export default defineConfig({ base: './', cacheDir: 'D:/Caches/KnowledgeClipper/chat-vite',
  build: { outDir: process.env.KC_CHAT_OUTPUT || 'D:/Artifacts/KnowledgeClipper/chat-view', emptyOutDir: false,
    assetsInlineLimit: 0, rollupOptions:{preserveEntrySignatures:'strict',input:{index:'index.html',material:'src/material-view.tsx'},
      output:{entryFileNames:chunk=>chunk.name==='material'?'material.js':'assets/[name]-[hash].js'}} } });
