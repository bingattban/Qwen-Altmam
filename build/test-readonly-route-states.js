"use strict";
// Phase 04 — real offline WebView2 module reads and truthful UI states.
// This test executes the shipped bridge-client.js (no mock business implementation).
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "..");
const source = fs.readFileSync(path.join(root,
  "src/AlTamam.WebUI/assets/js/bridge-client.js"), "utf8");
const pages = {
  Administration:"screens/Administration/index.html",
  Sites:"screens/Administration/StatusesAndSites/Sites/index.html",
  Statuses:"screens/Administration/StatusesAndSites/Statuses/index.html",
  PersonnelManagement:"screens/Administration/PersonnelManagement/index.html",
  PersonPickerAndSearch:"screens/SharedScreens/PersonPickerAndSearch/index.html",
  DraftsAndTemporarySave:"screens/SharedScreens/DraftsAndTemporarySave/index.html"
};

for (const [route, relative] of Object.entries(pages)) {
  const html=fs.readFileSync(path.join(root,"src/AlTamam.WebUI",relative),"utf8");
  assert.match(html, /id="routeResults"/, route+" needs a real result container");
  assert.doesNotMatch(html, /يجري ربط الشاشة خلال مراحل تنفيذ المشروع/,
    route+" must not claim the functional read-only screen is still a placeholder");
  if (route==="PersonnelManagement" || route==="PersonPickerAndSearch") {
    assert.match(html, /data-bridge-search/, route+" needs real HTML search form");
    assert.match(html, /id="routeSearch"/, route+" needs a named search input");
  } else assert.match(html, /data-bridge-action="REFRESH"/,
    route+" needs the real refresh control");
  if (route==="Sites" || route==="Statuses") {
    assert.match(html,/href="\.\.\/\.\.\/\.\.\/\.\.\/index\.html#admin"/,
      route+" return link must open the real administration surface");
    assert.match(html,/قراءة فقط/,route+" must not imply a standalone editing workflow");
  }
}
class Element {
  constructor(tag="div") {
    this.tagName=tag;this.children=[];this._text="";this.disabled=false;this.className="";
    const values=new Set();
    this.classList={add:(name)=>values.add(name),remove:(...names)=>names.forEach(n=>values.delete(n)),
      contains:(name)=>values.has(name)};
    this.dataset={};this.value="";
  }
  set textContent(s){this._text=String(s);this.children=[];}
  get textContent(){return this._text+this.children.map(c=>c.textContent).join("");}
  appendChild(child){this.children.push(child);return child;}
  append(...children){children.forEach(c=>this.appendChild(c));}
  replaceChildren(...children){this.children=[];this._text="";this.append(...children);}
  setAttribute(name,value){this[name]=String(value);}
}
function harness(route) {
  let response=()=>({success:true,data:[]}),receiver=null;
  const posted=[];
  const events={};
  const nodes={bridgeStatusText:new Element(),bridgeStatusBox:new Element(),
    routeResults:new Element(),routeSearch:new Element("input")};
  const refreshButton=new Element("button");
  refreshButton.dataset.bridgeAction="REFRESH";
  const form={matches:(selector)=>selector==="form[data-bridge-search]"};
  const document={
    body:{dataset:{routeKey:route}},
    getElementById:(id)=>nodes[id]||null,
    querySelectorAll:(sel)=>sel==='[data-bridge-action="REFRESH"]'?[refreshButton]:[],
    createElement:(tag)=>new Element(tag),
    addEventListener:(key,cb)=>{(events[key]??=[]).push(cb);},
    dispatchEvent:()=>{}
  };
  let sequence=0;
  const window={
    chrome:{webview:{
      addEventListener:(name,cb)=>{if(name==="message")receiver=cb;},
      postMessage:(json)=>{
        const req=JSON.parse(json);posted.push(req);
        const reply=response(req);
        Promise.resolve().then(()=>receiver({data:JSON.stringify(
          {requestId:req.requestId,...reply})}));
      }
    }}
  };
  const context={document,window,localStorage:{getItem:()=>null,setItem:()=>{},removeItem:()=>{}},
    crypto:{getRandomValues:(buffer)=>{buffer.fill((++sequence)%255);return buffer;}},
    setTimeout,clearTimeout,CustomEvent:class{},Uint8Array,console};
  vm.runInNewContext(source,context,{filename:"bridge-client.js",timeout:1000});
  (events.DOMContentLoaded||[]).forEach(cb=>cb());
  return {bridge:window.AltamamBridge,nodes,posted,refreshButton,form,events,
    respond:(fn)=>{response=fn;}};
}
async function run() {
  const search=harness("PersonPickerAndSearch");
  await search.bridge.refresh();
  assert.equal(search.posted.length,0,"empty search may not invoke C#");
  assert.match(search.nodes.bridgeStatusText.textContent,/أدخل.*بحث/);
  search.nodes.routeSearch.value="صالح";
  search.respond(()=>({success:true,data:{items:[{
    personId:"P-123",displayName:"صالح",serviceNumberA:"00012",
    siteName:"الموقع الأول",organizationalStatus:1}],count:1}}));
  await search.bridge.refresh();
  assert.equal(search.posted[0].action,"SearchPersons");
  assert.equal(search.posted[0].payload.term,"صالح");
  assert.match(search.nodes.routeResults.textContent,/00012/);
  assert.match(search.nodes.routeResults.textContent,/الموقع الأول/);
  assert.match(search.nodes.bridgeStatusText.textContent,/تم/);
  // Empty results must replace previous personal details, never show stale rows.
  search.respond(()=>({success:true,data:{items:[],count:0}}));
  await search.bridge.refresh();
  assert.match(search.nodes.routeResults.textContent,/لا توجد/);
  assert.doesNotMatch(search.nodes.routeResults.textContent,/00012/);
  // Role denial, validation, stale conflict and recovery are distinct visible states.
  for(const [errorCode,expected] of [
    ["Unauthorized",/صلاحية|غير مصرح/],
    ["ValidationError",/غير صالح|التحقق/],
    ["ConflictStale",/تعارض|تغيرت/],
    ["SessionExpired",/انتهت|سجّل الدخول/]
  ]) {
    search.respond(()=>({success:false,errorCode,userMessage:"رفض الخادم"}));
    await search.bridge.refresh();
    assert.match(search.nodes.bridgeStatusText.textContent,expected,errorCode);
    assert.equal(search.nodes.routeResults.children.length,0,errorCode+" clears stale rows");
    assert.equal(search.nodes.bridgeStatusBox.classList.contains("error"),true);
  }
  search.respond(()=>({success:true,data:{items:[],count:0}}));
  await search.bridge.refresh();
  assert.equal(search.nodes.bridgeStatusBox.classList.contains("error"),false,
    "recovered success removes stale error decoration");

  const people=harness("PersonnelManagement");
  people.nodes.routeSearch.value="00012";
  people.respond(()=>({success:true,data:{items:[],count:0}}));
  await people.bridge.refresh();
  assert.equal(people.posted[0].payload.term,"00012");

  const site=harness("Administration");
  site.respond(()=>({success:true,data:[{siteId:"SITE-1",name:"الموقع أ",active:true}]}));
  await site.bridge.refresh();
  assert.equal(site.posted[0].action,"QuerySites");
  assert.match(site.nodes.routeResults.textContent,/الموقع أ/);
  assert.match(site.nodes.routeResults.textContent,/نشط/);

  // Tests-first contract for two genuine standalone configuration reads.
  const dedicatedSites=harness("Sites");
  dedicatedSites.respond(()=>({success:true,data:[{siteId:"S-2",name:"موقع الاختبار",active:false}]}));
  const sitesRead=dedicatedSites.bridge.refresh();
  assert.match(dedicatedSites.nodes.bridgeStatusText.textContent,/جارٍ تحميل/,
    "Sites must expose a real loading state before the C# reply");
  await sitesRead;
  assert.equal(dedicatedSites.posted[0].action,"QuerySites");
  assert.match(dedicatedSites.nodes.routeResults.textContent,/موقع الاختبار/);
  assert.match(dedicatedSites.nodes.routeResults.textContent,/غير نشط/);
  assert.equal(dedicatedSites.nodes.bridgeStatusBox.classList.contains("success"),true);
  dedicatedSites.respond(()=>({success:true,data:[]}));
  await dedicatedSites.bridge.refresh();
  assert.match(dedicatedSites.nodes.routeResults.textContent,/لا توجد مواقع/);
  assert.doesNotMatch(dedicatedSites.nodes.routeResults.textContent,/موقع الاختبار/,
    "Sites empty state must clear previously displayed records");
  for(const [errorCode,pattern] of [
    ["ValidationError",/التحقق/],["ConflictStale",/تعارض/],
    ["Unauthorized",/غير مصرح/],["SessionExpired",/انتهت الجلسة/]
  ]) {
    dedicatedSites.respond(()=>({success:false,errorCode,userMessage:"رفض الخادم"}));
    await dedicatedSites.bridge.refresh();
    assert.match(dedicatedSites.nodes.bridgeStatusText.textContent,pattern);
    assert.equal(dedicatedSites.nodes.routeResults.children.length,0);
  }

  const statuses=harness("Statuses");
  statuses.respond(()=>({success:true,data:[{
    statusId:"ST-1",displayName:"موجود",shortSymbol:"✓",color:"#00FF00"
  }]}));
  await statuses.bridge.refresh();
  assert.equal(statuses.posted.length,1);
  assert.equal(statuses.posted[0].action,"QueryAttendanceStatuses");
  assert.match(statuses.nodes.routeResults.textContent,/موجود/);
  assert.match(statuses.nodes.routeResults.textContent,/✓/);
  assert.match(statuses.nodes.routeResults.textContent,/#00FF00/);
  assert.equal(statuses.nodes.bridgeStatusBox.classList.contains("success"),true);
  statuses.respond(()=>({success:true,data:[]}));
  await statuses.bridge.refresh();
  assert.match(statuses.nodes.routeResults.textContent,/لا توجد حالات/);
  assert.doesNotMatch(statuses.nodes.routeResults.textContent,/موجود/);
  statuses.respond(()=>({success:false,errorCode:"Unauthorized",userMessage:"رفض الخادم"}));
  await statuses.bridge.refresh();
  assert.match(statuses.nodes.bridgeStatusText.textContent,/غير مصرح/);
  statuses.respond(()=>({success:false,errorCode:"ConflictStale",userMessage:"تعارض"}));
  await statuses.bridge.refresh();
  assert.match(statuses.nodes.bridgeStatusText.textContent,/تعارض/);

  const drafts=harness("DraftsAndTemporarySave");
  drafts.respond(()=>({success:true,data:[{draftId:"D-1",businessDate:"2026-10-30",
    siteId:"SITE-1",targetStatusId:"S-1",selectedPersonIds:["P-1","P-2"]}]}));
  await drafts.bridge.refresh();
  assert.equal(drafts.posted[0].action,"ListAttendanceDrafts");
  assert.match(drafts.nodes.routeResults.textContent,/2026-10-30/);
  assert.match(drafts.nodes.routeResults.textContent,/2/);

  const unsupported=harness("DeleteSite");
  assert.equal(unsupported.refreshButton.disabled,true,
    "legacy unimplemented routes must not retain active fake refresh buttons");
  await unsupported.bridge.refresh();
  assert.equal(unsupported.posted.length,0,"no false success by GetApplicationVersion");
  assert.match(unsupported.nodes.bridgeStatusText.textContent,/قيد الاستكمال|قيد التطوير/);
  for(const item of [search,site,dedicatedSites,statuses,drafts,unsupported]) {
    assert.ok(item.posted.every(req=>!["CreateSite","DeleteAttendanceDraft",
      "UpdateAuditPresentation"].includes(req.action)),"read-only route may not mutate");
  }
  // Section 05.05 applies to EVERY canonical standalone route, not just
  // the positive read-only examples. Unsupported REFRESH must never send a
  // misleading real C# request or display a fake successful refresh.
  const manifest=JSON.parse(fs.readFileSync(
    path.join(root,"src/AlTamam.WebUI/screen-manifest.json"),"utf8"));
  assert.equal(manifest.screens.length,69);
  let disabledUnsupported=0;
  for(const route of manifest.screens){
    if(Object.hasOwn(pages,route.routeKey))continue;
    const html=fs.readFileSync(path.join(root,"src/AlTamam.WebUI",
      route.localAssetPath),"utf8");
    if(!html.includes('data-bridge-action="REFRESH"'))continue;
    const unsupportedRoute=harness(route.routeKey);
    assert.equal(unsupportedRoute.refreshButton.disabled,true,
      route.routeKey+" legacy refresh should disclose the missing integration");
    await unsupportedRoute.bridge.refresh();
    assert.equal(unsupportedRoute.posted.length,0,
      route.routeKey+" may not send a fake refresh request");
    assert.match(unsupportedRoute.nodes.bridgeStatusText.textContent,/قيد/,
      route.routeKey+" needs a truthful development-only state");
    disabledUnsupported++;
  }
  assert.ok(disabledUnsupported>=55,
    "review canonical route coverage when an old placeholder becomes functional");
  console.log("SECTION05 UI STATE COVERAGE — "+disabledUnsupported+
    " unsupported legacy refresh routes explicitly disabled");
  console.log("PHASE04 READONLY ROUTES PASS — real C# action envelopes, 6 states, no fake success");
}
run().catch(err=>{console.error(err);process.exitCode=1;});
