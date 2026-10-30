"use strict";
/* Windows-only Phase 15 WebView2 probe, loaded by native selfcheck from this
   LOCAL bundled file. No navigation, storage writes, network, or bridge calls. */
(() => {
  const errors=[];
  const origin="https://manger.adwas.example";
  if(location.origin!==origin)errors.push("virtual-origin");
  if(document.documentElement.dir!=="rtl")errors.push("document-rtl");
  if(window.getComputedStyle(document.body).direction!=="rtl")errors.push("computed-rtl");
  if(!document.getElementById("appViewport"))errors.push("app-viewport");
  for(const section of ["dashboard","monthly","admin","settings"]){
    if(!document.getElementById("navBtn-"+section))errors.push("nav-"+section);
  }
  if(typeof window.AltamamBridge?.invoke!=="function" ||
     window.AltamamBridge?.protocolVersion!==1)errors.push("bridge-script");
  const localCss=[...document.styleSheets].some(sheet=>{
    try{
      const uri=new URL(sheet.href,location.href);
      return uri.origin===origin && uri.pathname==="/assets/css/altamam.css";
    }catch{return false;}
  });
  if(!localCss)errors.push("local-css");
  if(document.querySelectorAll('script:not([src])').length)errors.push("inline-script");
  for(const asset of document.querySelectorAll('script[src],link[rel="stylesheet"][href]')){
    const ref=asset.src||asset.href;
    try{
      if(new URL(ref,location.href).origin!==origin)errors.push("remote-asset");
    }catch{errors.push("bad-asset-url");}
  }
  return errors.length?"WEBVIEW-UI-FAIL:"+errors.join("|"):"WEBVIEW-UI-PASS";
})()
