import {readFile} from 'node:fs/promises';
import init from '../chat-view/math/mathcat.js';
import {mathReader} from '../chat-view/math-reader.mjs';
const wasm=await init({module_or_path:await readFile(new URL('../chat-view/math/mathcat_bg.wasm',import.meta.url))});
process.stdin.setEncoding('utf8');
let text='';for await(const chunk of process.stdin)text+=chunk;
process.stdout.write(mathReader(wasm)(JSON.parse(text)).text);
