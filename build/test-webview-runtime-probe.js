"use strict";
// Sections 05.06, 05.08, 05.14, 05.15: real smoke-probe fixtures + offline contract.
// This exact locally bundled JS probe is executed inside Windows WebView2 by the host.
const assert=require("node:assert/strict");
const fs=require("node:fs");
const vm=require("node:vm");
const path=require("node:path");
const root=path.resolve(__dirname,"..");
const ui=path.join(root,"src","AlTamam.WebUI");
const probe=fs.readFileSync(path.join(ui,"assets/js/webview-runtime-probe.js"),"utf8");
const ORIGIN="https://manger.adwas.example";
const IDS=new Set(["appViewport",...["dashboard","monthly","admin","settings"].map(x=>"navBtn-"+x)]);
function run(changes={}){
  const state={origin:ORIGIN,rtl:"rtl",css:true,bridge:true,inline:false,remote:false,missing:null,...changes};
  const location={origin:state.origin,href:state.origin+"/index.html"};
  const assets=[
    {src:state.remote?"https://evil.invalid/run.js":ORIGIN+"/assets/js/bridge-client.js"},
    {href:ORIGIN+"/assets/css/altamam.css"}
  ];
  const document={
    documentElement:{dir:state.rtl},body:{},
    getElementById:id=>id===state.missing?null:IDS.has(id)?{id}:null,
    styleSheets:state.css?[{href:ORIGIN+"/assets/css/altamam.css"}]:[],
    querySelectorAll:selector=>selector==='script:not([src])'?(state.inline?[{}]:[]):assets
  };
  const window={
    AltamamBridge:state.bridge?{invoke(){},protocolVersion:1}:undefined,
    getComputedStyle:()=>({direction:state.rtl})
  };
  return vm.runInNewContext(probe,{window,document,location,URL},{timeout:1000});
}
assert.equal(run(),"WEBVIEW-UI-PASS");
for(const [state,message] of [
  [{origin:"https://manger.adwas.example:8443"},"virtual-origin"],
  [{rtl:"ltr"},"document-rtl"],
  [{css:false},"local-css"],
  [{bridge:false},"bridge-script"],
  [{inline:true},"inline-script"],
  [{remote:true},"remote-asset"],
  [{missing:"navBtn-settings"},"nav-settings"],
  [{missing:"appViewport"},"app-viewport"]
])assert.ok(run(state).includes(message),"must reject: "+message);

// The ACTUAL Windows host must reuse the already tested exact bridge-origin policy.
// That policy rejects alternative HTTPS ports, credentialed URLs and other hosts.
const host=fs.readFileSync(path.join(root,"src/AlTamam.DesktopHost/MainWindow.xaml.cs"),"utf8");
assert.match(host,/private static bool IsLocalVirtualHost\(string uriString\)\s*=>\s*WebView2BridgeDispatcher\.IsAllowedOrigin\(uriString\);/);
assert.match(host,/SetVirtualHostNameToFolderMapping\([\s\S]*?CoreWebView2HostResourceAccessKind\.Deny\)/);
assert.match(host,/WebResourceRequested \+=/);
assert.match(host,/AddWebResourceRequestedFilter\("\*"/);
assert.match(host,/File\.ReadAllTextAsync\(scriptPath\)/);
assert.match(host,/WebView2 UI assets not ready/);

// ALL canonical HTML screens and root entry must keep JS/CSS local and CSP strict.
const manifest=JSON.parse(fs.readFileSync(path.join(ui,"screen-manifest.json"),"utf8"));
assert.equal(manifest.screens.length,manifest.screenCount);
const pages=[path.join(ui,"index.html"),...manifest.screens.map(r=>path.join(ui,r.localAssetPath))];
for(const file of pages){
  const html=fs.readFileSync(file,"utf8");
  assert.match(html,/Content-Security-Policy/);
  assert.match(html,/connect-src 'none'/);
  assert.doesNotMatch(html,/unsafe-inline|unsafe-eval/);
  assert.doesNotMatch(html,/<script\b(?![^>]*\bsrc=)[^>]*>/i,"inline script: "+file);
  assert.doesNotMatch(html,/\son[a-z]+\s*=/i,"inline event: "+file);
  for(const expr of [/<script[^>]+src=["']([^"']+)["']/g,/<link[^>]+href=["']([^"']+\.css)["']/g]){
    for(const match of html.matchAll(expr)){
      const asset=match[1];
      assert.ok(!/^(?:https?:)?\/\//i.test(asset),"remote resource: "+asset+" in "+file);
      const abs=path.resolve(path.dirname(file),asset);
      assert.ok(abs.startsWith(ui+path.sep),"resource path escapes local UI: "+asset);
      assert.ok(fs.existsSync(abs),"missing local resource: "+asset+" in "+file);
    }
  }
}
const css=fs.readFileSync(path.join(ui,"assets/css/altamam.css"),"utf8")+
  fs.readFileSync(path.join(ui,"assets/css/presentation.css"),"utf8");
assert.doesNotMatch(css,/@import\s*(?:url\()?\s*["']?https?:/i,"external CSS import");
assert.doesNotMatch(css,/url\(\s*["']?https?:/i,"external CSS resource");

// Only read-only restore preview is wired. Never silently enable destructive cutover.
const catalog=fs.readFileSync(path.join(root,"src/AlTamam.Application/Bridge/BridgeHandlerCatalog.cs"),"utf8");
assert.doesNotMatch(catalog,/"RestoreDatabaseAtomically"\s*=>/,"destructive restore must remain DISABLED");
assert.match(catalog,/"PreviewRestore"\s*=>/,"read-only backup preview stays available");
console.log("WEBVIEW OFFLINE/RESTORE CONTRACT PASS: "+pages.length+
  " HTML routes; 9 local runtime probe executions and 8 negative fixtures");
