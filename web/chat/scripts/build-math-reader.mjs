import {build} from 'esbuild';
import {resolve} from 'node:path';
const out=resolve(process.env.KC_CHAT_OUTPUT||'D:/Artifacts/KnowledgeClipper/chat-view','math-reader.mjs');
await build({entryPoints:['src/math-reader.js'],outfile:out,bundle:true,minify:true,platform:'neutral',mainFields:['module','main'],
  format:'esm',target:'es2020',legalComments:'eof'});
