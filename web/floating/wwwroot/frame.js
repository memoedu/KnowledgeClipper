import {localArchive,blobFrom,png} from './storage.js';

const root=document.querySelector('#content'),notice=document.querySelector('#notice');
const listeners=new Set(), waiters=new Map(), urls=new Map(), imageLoads=new Map();
let channel,db,source,owner,lang,view,sequence=0,pipeline=Promise.resolve(),editor,reader,audioRender,closed=false;
const tx=(zh,en)=>lang==='en'?en:zh;
const node=(tag,text)=>{const el=document.createElement(tag);if(text!=null)el.textContent=text;return el;};
const action=(label,run)=>{const button=node('button',label);button.type='button';button.onclick=()=>Promise.resolve(run()).catch(fail);return button;};
const group=(...children)=>{const div=node('div');div.className='actions';div.append(...children);return div;};
const fail=error=>{notice.textContent=error?.message||String(error);};
const invoke=(op,input)=>new Promise((resolve,reject)=>{
  if(closed)return reject(new Error('窗口已关闭。'));
  const id=++sequence;waiters.set(id,{resolve,reject});channel.postMessage({id,op,input});
});
const dotnet=(name,...args)=>DotNet.invokeMethodAsync('KnowledgeClipper.Browser',name,...args);
async function image(digest){
  if(urls.has(digest))return urls.get(digest);
  if(!imageLoads.has(digest))imageLoads.set(digest,(async()=>{
    const data=await dotnet('Command',JSON.stringify({command:'asset',sha256:digest}));
    if(!data)return '';
    const url=URL.createObjectURL(blobFrom(data,'image/png'));urls.set(digest,url);return url;
  })().finally(()=>imageLoads.delete(digest)));
  return imageLoads.get(digest);
}
function update(raw){
  if(closed)return;
  view=typeof raw==='string'?JSON.parse(raw):raw;
  if(view.shared.error||view.shared.speechError||view.shared.lookupError)
    notice.textContent=[view.shared.error,view.shared.speechError,view.shared.lookupError].filter(Boolean).join('\n');
  else notice.textContent=view.shared.status;
  root.setAttribute('aria-busy',String(view.shared.working));
  if(view.chat){
    if(!editor){root.replaceChildren();root.className='chat';editor=node('iframe');editor.title=tx('问答与卡片编辑','Conversation and card editor');editor.src='./chat/index.html';root.append(editor);}
    const next=view.chat;
    const hashes=new Set([...next.attachments.map(i=>i.sha256),...next.messages.flatMap(m=>m.parts.flatMap(p=>p.image?[p.image.sha256]:[])),
      ...[next.cardPreview?.front||'',next.cardPreview?.back||''].flatMap(v=>[...v.matchAll(/clipper-media\.local\/([a-f0-9]{64})/g)].map(m=>m[1]))]);
    void Promise.all([...hashes].map(image)).then(()=>{if(view?.chat===next)listeners.forEach(handler=>handler({data:next}));}).catch(fail);
    return;
  }
  // Keep native text selection and IME composition intact during background operations.
  if(view.shared.working&&root.childElementCount)return;
  root.className='';editor=null;render(view);
}
function command(command,payload={}){
  const work=async()=>{
    const raw=await dotnet('Command',JSON.stringify({command,...payload}));update(raw);return raw;
  };
  if(command==='stop')return work();
  const job=pipeline.then(work);
  pipeline=job.catch(fail);return job;
}
function choose(multiple=true){
  return new Promise(resolve=>{
    const input=node('input');input.type='file';input.accept='image/*';input.multiple=multiple;
    input.onchange=()=>resolve([...input.files]);input.oncancel=()=>resolve([]);input.click();
  });
}
async function clipboardImages(){
  if(!navigator.clipboard?.read)throw new Error(tx('请在输入框粘贴图片，或使用「添加图片」。','Paste into the editor or use Add image.'));
  const result=[];for(const item of await navigator.clipboard.read()){const type=item.types.find(t=>t.startsWith('image/'));if(type)result.push(await item.getType(type));}
  return result;
}
window.knowledgeClipperEditor={
  imageUrl:digest=>urls.get(digest)||'',
  addEventListener:(_name,handler)=>listeners.add(handler),removeEventListener:(_name,handler)=>listeners.delete(handler),
  async postMessage(message){
    try{
      const {command:name,...payload}=message;
      if(name==='ready'){if(view?.chat)listeners.forEach(handler=>handler({data:view.chat}));return;}
      if(['attach','cardImage','pasteImage','pasteImageData'].includes(name)){
        let images;
        if(name==='pasteImageData')images=await Promise.all(payload.images.map(data=>png(blobFrom(data,''))));
        else images=await Promise.all((name==='pasteImage'?await clipboardImages():await choose()).map(png));
        if(images.length)await command('pasteImageData',{...payload,images});
      }else await command(name,payload);
    }catch(error){fail(error);}
    finally{if(message.clientCommandId)listeners.forEach(handler=>handler({data:{kind:'commandAcknowledged',requestId:message.clientCommandId}}));}
  }
};
async function rich(host,text){
  if(!text)return;
  const source=String(text);host.textContent=source;
  const hashes=[...source.matchAll(/(?:asset:|clipper-media\.local\/)([a-f0-9]{64})/g)].map(m=>m[1]);
  const [module]=await Promise.all([reader||=import('./chat/material.js'),...hashes.map(image)]);
  if(host.isConnected)module.renderMaterial(host,source,d=>urls.get(d)||'');
}
function material(text){const el=node('div');queueMicrotask(()=>rich(el,text).catch(fail));return el;}
function textbox(label,text){
  const wrapper=node('label',label),input=node('textarea');input.value=text||'';wrapper.append(input);return {wrapper,input};
}
function select(options,value){
  const el=node('select');for(const [key,label] of options){const option=node('option',label);option.value=key;el.append(option);}
  if(value!=null)el.value=value;return el;
}
function picture(sha){const el=node('img');el.alt=tx('剪藏图片','Clipped image');image(sha).then(url=>{if(url)el.src=url;}).catch(fail);return el;}
function render(state){
  const s=state.shared, nodes=[];
  if(s.screen!=='images'){
    if(s.screen!=='excerpt'||!state.cards.some(card=>!card.supersededBy&&card.front===s.text)){
      const quote=node('blockquote');quote.append(material(s.text));nodes.push(quote);
    }
    if(s.context){const ctx=node('details');ctx.append(node('summary',s.scope==='paragraph'?tx('来源段落','Source paragraph'):tx('来源句子','Source sentence')),material(s.context));nodes.push(ctx);}
  }
  if(s.screen==='language'){
    if(s.lookup){nodes.push(node('p',[s.lookup.meaningZh,s.lookup.pronunciation].filter(Boolean).join(' · ')));}
    nodes.push(group(action(s.audio?tx('播放原录音','Play recording'):tx('朗读','Read aloud'),()=>command('readAgain'))));
    if(s.showDictionary&&s.dictionary){const details=node('details');details.append(node('summary',tx('词典详情','Dictionary details')),material(s.dictionary));nodes.push(details);}
    const modes=select([['',tx('按语言和练习设置','Use language and practice settings')],...state.modes.map(m=>[m.id,m.name])],s.readMode||'');
    nodes.push(group(modes,action(tx('生成语言卡','Generate language cards'),()=>command('readCards',{mode:modes.value||null}))));
    for(const c of state.cards){
      const card=node('article');card.append(node('h3',state.modes.find(m=>m.id===c.modeId)?.name||c.modeId),material(c.front),material(c.back));
      if(c.reason?.length)card.append(node('p',c.reason.join(' · ')));
      const save=action(c.saved?tx('已保存','Saved'):tx('保存到个人自由学习','Save to personal study'),()=>command('saveLanguage',{id:c.id}));
      save.disabled=c.saved||!c.back;card.append(save);nodes.push(card);
    }
  }else if(s.screen==='excerpt'){
    const front=textbox(tx('手动正面','Front'),state.manualFront), back=textbox(tx('手动反面','Back'),state.manualBack);
    const fields=()=>({front:front.input.value,back:back.input.value});
    const manual=node('details');manual.open=!!state.cards.some(c=>!c.saved)&&!s.route.quick;
    manual.append(node('summary',tx('手动制卡与挖空','Manual and cloze cards')),front.wrapper,back.wrapper,group(
      action(tx('添加手动卡','Add card'),()=>command('addExcerpt',fields())),
      action(tx('挖空所选文字','Cloze selection'),()=>command('cloze',{...fields(),start:front.input.selectionStart,length:front.input.selectionEnd-front.input.selectionStart}))));
    // Text edits have their own checkpoint so blurring a field never replaces the clicked button.
    let timer;
    for(const input of [front.input,back.input])input.addEventListener('input',()=>{
      clearTimeout(timer);const value=fields();
      timer=setTimeout(()=>{void dotnet('Command',JSON.stringify({command:'manual',...value})).catch(fail);},250);
    });
    if(state.modes.length){const modes=select(state.modes.map(m=>[m.id,m.name]));manual.append(group(modes,
      action(tx('按模式生成','Generate with mode'),()=>command('generateExcerpt',{...fields(),mode:modes.value}))));}nodes.push(manual);
    const checked=new Set();
    for(const c of state.cards.filter(c=>!c.supersededBy)){
      const row=node('article');row.append(node('h3',c.label),material(c.front),material(c.back));
      for(const hash of [...c.frontImages,...c.backImages])if(![c.front,c.back].some(v=>v.includes('asset:'+hash)))row.append(picture(hash));
      const controls=group();if(!c.saved){
        const edit=node('details'), ef=textbox(tx('正面原文','Front source'),c.front),eb=textbox(tx('反面原文','Back source'),c.back);
        edit.append(node('summary',tx('查看或修改原文','View or edit source')),ef.wrapper,eb.wrapper,
          action(tx('应用修改','Apply edit'),()=>command('editExcerpt',{id:c.id,front:ef.input.value,back:eb.input.value})));row.append(edit);
        controls.append(action(tx('保存到个人自由学习','Save to personal study'),()=>command('saveExcerpt',{id:c.id})));
        if(c.kind==='Cloze'){const pick=node('input');pick.type='checkbox';pick.onchange=()=>pick.checked?checked.add(c.id):checked.delete(c.id);
          const label=node('label',tx('选择合并','Select to merge'));label.append(pick);controls.append(label);}
        if(c.replaces?.length)controls.append(action(tx('撤销合并','Undo merge'),()=>command('undoMerge',{id:c.id})));
      }else controls.append(node('span',tx('已保存到个人自由学习','Saved to personal study')));
      row.append(controls);nodes.push(row);
    }
    if(state.cards.filter(c=>c.kind==='Cloze'&&!c.saved&&!c.supersededBy).length>1)nodes.push(action(tx('合并所选挖空','Merge selected clozes'),()=>command('merge',{ids:[...checked]})));
  }else if(s.screen==='images'){
    const add=async role=>{const files=await choose();if(files.length)await command('addImages',{images:await Promise.all(files.map(async f=>({role,data:await png(f)})))});};
    nodes.push(group(action(tx('添加正面图片','Add front images'),()=>add('question')),action(tx('添加反面图片','Add back images'),()=>add('answer'))));
    const strip=node('div');strip.className='collection';
    state.collection.forEach((i,index)=>{const fig=node('figure'),role=select([['question',tx('正面','Front')],['answer',tx('反面','Back')]],i.role);
      role.onchange=()=>command('imageRole',{index,role:role.value});
      fig.append(picture(i.sha256),role,group(action('←',()=>command('moveCollected',{index,offset:-1})),action('→',()=>command('moveCollected',{index,offset:1})),
        action(tx('移除','Remove'),()=>command('removeCollected',{index}))));strip.append(fig);});nodes.push(strip);
    const format=select([['original',tx('保留原图','Original images')],['ocr',tx('识别为文字','OCR text')]],state.original?'original':'ocr');
    format.onchange=()=>command('imageRoutes',{original:format.value==='original'});
    const route=select(state.routes.map(r=>[r.id,r.name]),state.imageRoute||state.routes[0]?.id);
    nodes.push(group(format,route,action(tx('处理图片','Process images'),()=>command('processImages',{original:format.value==='original',route:route.value}))));
  }
  const history=node('details');history.append(node('summary',tx('本浏览器草稿和历史','Drafts and history on this browser')));
  history.ontoggle=async()=>{if(!history.open||history.children.length>1)return;
    for(const item of await db.sessions())if(item.id!==s.id)history.append(action(item.title,()=>invoke('resume',item)));};
  nodes.push(history);root.replaceChildren(...nodes);
}
window.clipperHost={async call(op,data){
  switch(op){
    case 'view':update(data);return true;
    case 'draftFiles':return db.files(data);
    case 'checkpoint':await db.checkpoint({...data,source});await invoke('checkpoint',data.id);return true;
    case 'history':return db.history(data);
    case 'remember':await db.remember(data);return true;
    case 'recording':{const r=await db.get('recordings',data);return r?{...r,side:'back'}:null;}
    case 'save':return invoke(op,{...data,context:view?.shared.context});
    case 'model':return invoke(op,data);
    case 'reference':return invoke(op,data);
    case 'ocr':return (await invoke(op,data)).text;
    case 'synthesize':{
      if(data.math){audioRender||=(async()=>{const [{default:init},{mathReader}]=await Promise.all([import('./math/mathcat.js'),import('./chat/math-reader.mjs')]);
        return mathReader(await init());})();const spoken=(await audioRender)(data.math);data={...data,text:spoken.text};}
      const {mime,data:bytes}=await invoke('speech',data),blob=blobFrom(bytes,mime);
      const hash=[...new Uint8Array(await crypto.subtle.digest('SHA-256',await blob.arrayBuffer()))].map(n=>n.toString(16).padStart(2,'0')).join('');
      await db.put('recordings',hash,{mime,data:bytes,hash});return hash;
    }
    case 'play':case 'settings':return invoke(op,data);
    case 'notice':notice.textContent=data;return true;
    case 'copy':await navigator.clipboard.writeText(data);return true;
    default:throw new Error('Unknown host action');
  }
}};
window.addEventListener('message',async event=>{
  if(channel||event.origin!==location.origin||event.data?.kind!=='knowledge-clipper:open'||!event.ports[0])return;
  if(event.source!==parent&&event.source!==parent.opener)return;
  channel=event.ports[0];channel.onmessage=({data})=>{const pending=waiters.get(data.id);if(!pending)return;waiters.delete(data.id);data.error?pending.reject(new Error(data.error)):pending.resolve(data.result);};
  ({owner,language:lang}=event.data);document.documentElement.lang=lang;source=event.data.options.source;
  try{
    notice.textContent=tx('正在打开剪藏工作区…','Opening Knowledge Clipper…');
    db=await localArchive(owner);await Blazor.start();
    const initial=await dotnet('Open',JSON.stringify(event.data.options),event.data.images);
    update(initial);await command('start');
  }catch(error){fail(error);}
});
window.addEventListener('pagehide',()=>{closed=true;channel?.close();db?.close();for(const url of urls.values())URL.revokeObjectURL(url);
  for(const item of waiters.values())item.reject(new Error('窗口已关闭。'));waiters.clear();},{once:true});
