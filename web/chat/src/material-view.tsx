import React from 'react';
import {createRoot, type Root} from 'react-dom/client';
import {Streamdown} from 'streamdown';
import {createMathPlugin} from '@streamdown/math';
import {cjk} from '@streamdown/cjk';
import katex from 'katex';
import 'katex/dist/katex.min.css';

const math=createMathPlugin({singleDollarTextMath:true});
const mounted=new WeakMap<HTMLElement,Root>();
const media=/^(?:asset:([a-f0-9]{64})|https:\/\/clipper-media\.local\/([a-f0-9]{64})\.png)$/;
export const toMathML=(latex:string)=>katex.renderToString(latex,{output:'mathml',throwOnError:true,trust:false,strict:'ignore'})
  .replace(/^<span[^>]*>|<\/span>$/g,'').replace(/<annotation encoding="application\/x-tex">[\s\S]*?<\/annotation>/g,'');

export function displayMarkdown(text:string){
  // Preserve literal delimiters inside fenced code and inline code.
  return text.replace(/(`+)[\s\S]*?\1|(^|\n)(~{3,})[^\n]*\n[\s\S]*?\n\3[^\n]*|\\\[([\s\S]+?)\\\]|\\\(([^\n]+?)\\\)|(!\[(?:\\.|[^\]\\])*\])\(asset:([a-f0-9]{64})\)/g,
    (source,_code,_line,_fence,block,inline,alt,digest)=>block!==undefined?'\n\n$$\n'+block+'\n$$\n\n':
      inline!==undefined?'$'+inline+'$':digest?alt+'(https://clipper-media.local/'+digest+'.png)':source);
}
export function renderMaterial(element:HTMLElement,text:string,image:(digest:string)=>string=()=>''){
  let root=mounted.get(element);if(!root){root=createRoot(element);mounted.set(element,root);}
  root.render(<Streamdown mode="static" plugins={{math,cjk}} controls={false} remarkRehypeOptions={{allowDangerousHtml:false}}
    urlTransform={url=>media.test(url)?url:''}
    components={{a:({children}:any)=><span>{children}</span>,img:({src,alt}:any)=>{
      const match=media.exec(src||'');const url=match&&image(match[1]||match[2]);
      return url?<img src={url} alt={alt||'图片'}/>:<span>{alt}</span>;
    }}}>{displayMarkdown(text)}</Streamdown>);
  return ()=>{root!.unmount();mounted.delete(element);};
}
