const completed = tx => new Promise((resolve,reject) => {
  tx.oncomplete=()=>resolve();tx.onerror=tx.onabort=()=>reject(tx.error||new Error('浏览器未能保存草稿。'));
});
const result = req => new Promise((resolve,reject)=>{req.onsuccess=()=>resolve(req.result);req.onerror=()=>reject(req.error);});
export async function localArchive(owner) {
  if(!owner)throw new Error('需要登录账号。');
  const open=indexedDB.open('KnowledgeClipper.study.'+encodeURIComponent(owner),1);
  open.onupgradeneeded=()=>{for(const name of ['sessions','files','words','recordings','meta'])open.result.createObjectStore(name);};
  const db=await result(open);
  const keyRange=id=>IDBKeyRange.bound([id,''],[id,'\uffff']);
  const get=(name,key)=>result(db.transaction(name).objectStore(name).get(key));
  async function put(name,key,value){const tx=db.transaction(name,'readwrite');tx.objectStore(name).put(value,key);await completed(tx);}
  return {
    close:()=>db.close(), get, put,
    async sessions(){return (await result(db.transaction('sessions').objectStore('sessions').getAll())).sort((a,b)=>b.updated-a.updated);},
    async checkpoint({id,screen,title,files,source}) {
      const tx=db.transaction(['files','sessions'],'readwrite');
      for(const [name,value] of Object.entries(files))tx.objectStore('files').put(value,[id,name]);
      tx.objectStore('sessions').put({id,screen,title,source,updated:Date.now()},id);
      await completed(tx);return true;
    },
    async files(id){
      const store=db.transaction('files').objectStore('files'), range=keyRange(id);
      const [keys,values]=await Promise.all([result(store.getAllKeys(range)),result(store.getAll(range))]);
      return Object.fromEntries(keys.map((key,i)=>[key[1],values[i]]));
    },
    async history(key){return (await result(db.transaction('words').objectStore('words').getAll(keyRange(key)))).sort((a,b)=>b.at-a.at).map(r=>r.candidate);},
    remember({key,candidate}){return put('words',[key,candidate.sessionId],{at:Date.now(),candidate});}
  };
}
export function blobFrom(data,mime) {
  const bytes=Uint8Array.from(atob(data),c=>c.charCodeAt(0));return new Blob([bytes],{type:mime});
}
export async function encoded(blob){
  return new Promise((resolve,reject)=>{const r=new FileReader();r.onerror=()=>reject(r.error);
    r.onload=()=>resolve(String(r.result).split(',')[1]);r.readAsDataURL(blob);});
}
export async function png(blob) {
  if(blob.type==='image/png')return encoded(blob);
  const url=URL.createObjectURL(blob);
  try{const img=new Image();img.src=url;await img.decode();
    if(img.naturalWidth*img.naturalHeight>24000000)throw new Error('图片尺寸过大。');
    const canvas=document.createElement('canvas');canvas.width=img.naturalWidth;canvas.height=img.naturalHeight;
    canvas.getContext('2d').drawImage(img,0,0);return encoded(await new Promise((ok,fail)=>canvas.toBlob(v=>v?ok(v):fail(new Error('无法读取图片。')),'image/png')));
  }finally{URL.revokeObjectURL(url);}
}
