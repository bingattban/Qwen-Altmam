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
  // Every standalone route remains registered, but no developer-only scaffold or
  // disabled pretend-refresh may leak to the operator.
  assert.doesNotMatch(html,/يجري ربط الشاشة خلال مراحل تنفيذ المشروع|ربط هذه الشاشة بخدمات البرنامج لم يكتمل بعد|المرحلة: Phase|المعرف القياسي|الصلاحية المعتمدة:|مرتبط بعقد WebView2 المحلي|Loading \/ Ready State|RBAC &amp; Validation|Historical Immutability|قيد التطوير/,
    route.routeKey+" contains internal development guidance");
  assert.doesNotMatch(html,/↗ فتح في المنظومة الكاملة|تشغيل التفاعل الكامل/,
    route.routeKey+" advertises a misleading standalone interaction");
  const actualReadRoutes=new Set(["Administration","Sites","Statuses","PersonnelManagement",
    "PersonPickerAndSearch","DraftsAndTemporarySave"]);
  if(!actualReadRoutes.has(route.routeKey)&&route.routeKey!=="AddPerson")
    assert.doesNotMatch(html,/data-bridge-action="REFRESH"/,
      route.routeKey+" must not render a non-functional refresh button");
  scanned++;
  const matches=[...html.matchAll(/<a\b[^>]*href="([^"]*index\.html#[^"]*)"[^>]*>([^<]+)<\/a>/g)];
  legacyLinks+=matches.filter(m=>/تشغيل التفاعل الكامل|فتح في المنظومة الكاملة/.test(m[2])).length;
}
assert.equal(scanned,manifest.screenCount,"every canonical route must be checked");
assert.equal(legacyLinks,0,"developer-facing legacy return labels must be gone");

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
// A legacy standalone document with no real independent write owner MUST
// hand off to the already implemented authenticated main-window workflow.
// Read-only modules and the existing dashboard automatic-register redirects stay intact.
const stayStandalone=new Set(["Administration","Sites","Statuses","PersonnelManagement",
  "PersonPickerAndSearch","DraftsAndTemporarySave","Dashboard",
  "UnpreparedPersonnel","SelectStatusAndConfirm","ConfirmationAndWarning",
  "ConflictsAndNotifications"]);
let handedOver=0;
for (const route of manifest.screens) {
  if (stayStandalone.has(route.routeKey)) continue;
  const html=fs.readFileSync(web+"/"+route.localAssetPath,"utf8");
  const routeKey=html.match(/<body[^>]*data-route-key="([^"]+)"/)?.[1];
  if(routeKey!==route.routeKey)continue; // AddPerson is an embedded action, not another file.
  const link=html.match(/href="((?:\.\.\/)+index\.html#[^"]*)"/)?.[1];
  assert.ok(link,route.routeKey+" must retain a safe relative main-window link");
  const routeDocument={
    body:{dataset:{routeKey}},
    addEventListener:(name,fn)=>{routeEvents[name]=fn;},
    querySelector:selector=>selector==='a[href*="index.html#"]'
      ?{getAttribute:(name)=>name==="href"?link:null}:null,
    querySelectorAll:()=>[]
  };
  const routeEvents={};
  const redirected=[];
  vm.runInNewContext(screenModule,{document:routeDocument,
    window:{location:{replace:url=>redirected.push(url)}}},
    {filename:"screen-module.js",timeout:1000});
  routeEvents.DOMContentLoaded();
  assert.deepEqual(redirected,[link.split("#")[0]+"#"+routeKey],
    route.routeKey+" must open its canonical owner, not an inert HTML shell");
  handedOver++;
}
assert.ok(handedOver>=50,"standalone handover should cover existing business routes");

for (const key of stayStandalone) {
  const e={};
  const jumps=[];
  const doc={body:{dataset:{routeKey:key}},
    addEventListener:(name,fn)=>{e[name]=fn;},
    querySelector:()=>({getAttribute:()=> "../../index.html#"+key}),
    querySelectorAll:()=>[]};
  vm.runInNewContext(screenModule,{document:doc,window:{location:{replace:x=>jumps.push(x)}}});
  e.DOMContentLoaded();
  assert.deepEqual(jumps,[],key+" has its own legitimate surface or dedicated redirect");
}
console.log("SECTION05 ROUTE LINK HONESTY PASS — "+scanned+" routes checked; "+handedOver+" canonical handovers");
