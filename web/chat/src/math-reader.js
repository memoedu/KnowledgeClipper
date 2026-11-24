import katex from 'katex';

// The same ES module runs in the browser workspace and the desktop's Node speech adapter.
export function mathReader(wasm){
  const encoder=new TextEncoder(),decoder=new TextDecoder();
  function say(tex,chinese){
    const markup=katex.renderToString(tex,{output:'mathml',throwOnError:true,trust:false,strict:'ignore'})
      .replace(/^<span[^>]*>|<\/span>$/g,'').replace(/<annotation encoding="application\/x-tex">[\s\S]*?<\/annotation>/g,'');
    const bytes=encoder.encode(markup);
    const at=wasm.input_buffer(bytes.length);new Uint8Array(wasm.memory.buffer,at,bytes.length).set(bytes);
    const length=wasm.read_math(chinese?1:0);
    if(!length)throw new Error('无法解析选区中的公式读法。');
    return decoder.decode(new Uint8Array(wasm.memory.buffer,wasm.output_buffer(),length));
  }
  return parts=>{
    const prose=parts.filter(part=>!part.math).map(part=>part.text).join('');
    const chinese=/\p{Script=Han}/u.test(prose)||(prose.trim()===''&&navigatorLanguage().startsWith('zh'));
    return {text:parts.map(part=>part.math?' '+say(part.text,chinese)+' ':part.text).join('').trim(),chinese};
  };
}
function navigatorLanguage(){return globalThis.navigator?.language||'zh-CN';}
