type Listener = (event:{data:unknown})=>void;
export type EditorHost = { postMessage:(value:unknown)=>void; addEventListener:(name:string,listener:Listener)=>void;
  removeEventListener:(name:string,listener:Listener)=>void; imageUrl?:(digest:string)=>string };
declare global { interface Window { chrome?:{webview?:EditorHost}; knowledgeClipperEditor?:EditorHost } }
export function host(): EditorHost|undefined {
  if(window.chrome?.webview)return window.chrome.webview;
  try { return window.parent!==window && window.parent.location.origin===location.origin
    ? window.parent.knowledgeClipperEditor : undefined; } catch { return undefined; }
}
export const imageUrl=(digest:string)=>host()?.imageUrl?.(digest) || `https://clipper-media.local/${digest}.png`;
