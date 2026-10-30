"use strict";
// Section 05.05 — route-return links must describe their real action.
const assert=require("node:assert/strict");
const fs=require("node:fs");
const path=require("node:path");
const vm=require("node:vm");

const root=path.resolve(__dirname,"..");
const web=root+"/src/AlTamam.WebUI";
const manifest=JSON.parse(fs.readFileSync(web+"/screen-manifest.json","utf8"));
const screenModule=fs.readFileSync(web+"/assets/js/screen-module.js","utf8");
let scanned=0,legacyLinks=0;
for(const route of manifest.screens){
  const file=web+"/"+route.localAssetPath;
  const html=fs.readFileSync(file,"utf8");
  assert.match(html,/assets\/js\/screen-module\.js/,route.routeKey+" missing shared route behavior");
  scanned++;
  const matches=[...html.matchAll(/<a\b[^>]*href="([^"]*index\.html#[^"]*)"[^>]*>([^<]+)<\/a>/g)];
  legacyLinks+=matches.filter(m=>/تشغيل التفاعل الكامل|فتح في المنظومة الكاملة/.test(m[2])).length;
}
assert.equal(scanned,manifest.screenCount,"every canonical route must be checked");
assert.ok(legacyLinks>0,"expected legacy route return links to be covered");

const elements=[
  {href:"../../../index.html#AuditLog",textContent:"تشغيل التفاعل الكامل",title:""},
  {href:"../../../index.html#AuditLog",textContent:"↗ فتح في المنظومة الكاملة",title:""},
  {href:"../../../index.html#AuditLog",textContent:"العودة إلى الرئيسية",title:""},
  {href:"https://example.invalid",textContent:"تشغيل التفاعل الكامل",title:""}
];
const events={};
const document={
  addEventListener:(name,fn)=>{events[name]=fn;},
  querySelectorAll:(selector)=>{
    assert.equal(selector,'a[href*="index.html#"]');
    return elements.filter(e=>e.href.includes("index.html#"));
  },
  querySelector:(selector)=>{
    assert.equal(selector,'a[href*="index.html"]');
    return currentReturnLink;
  }
};
let currentReturnLink=elements[0];
const destinations=[];
const window={location:{assign:(href)=>destinations.push(href)}};
vm.runInNewContext(screenModule,{document,window},{filename:"screen-module.js",timeout:1000});
assert.equal(typeof events.DOMContentLoaded,"function","normalization must run when route loads");
events.DOMContentLoaded();
assert.equal(elements[0].textContent,"العودة إلى الواجهة الرئيسية");
assert.equal(elements[1].textContent,"العودة إلى الواجهة الرئيسية");
assert.match(elements[0].title,/لا يعني اكتمال/);
assert.equal(elements[2].textContent,"العودة إلى الرئيسية","existing honest link must be preserved");
assert.equal(elements[3].textContent,"تشغيل التفاعل الكامل","external unrelated links must be preserved");
assert.equal(elements[0].href,"../../../index.html#AuditLog","no route destination changed");
assert.equal(typeof events.keydown,"function","Escape navigation must remain supported");
events.keydown({key:"Escape"});
assert.equal(destinations.at(-1),"../../../index.html#AuditLog");
currentReturnLink={href:"../../index.html"};
events.keydown({key:"Escape"});
assert.equal(destinations.at(-1),"../../index.html","root-level modules must also support Escape");
events.keydown({key:"Enter"});
assert.equal(destinations.length,2,"other keys must not trigger navigation");
console.log("SECTION05 ROUTE LINK HONESTY PASS — "+scanned+" routes checked");
