"use strict";
const assert=require("node:assert/strict"),fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
class El{
 constructor(tag){this.tag=tag;this.children=[];this.handlers={};this.attrs={};
   this.className="";this.textContent="";this.style={};this.value="";this.dataset={};this.disabled=false;}
 get classList(){return{
   toggle:(name,on)=>{const tags=this.className.split(/\s+/).filter(Boolean).filter(x=>x!==name);if(on)tags.push(name);this.className=tags.join(" ");},
   add:(name)=>{if(!this.className.split(/\s+/).includes(name))this.className+=" "+name;}
 };}
 get tBodies(){return this.children.filter(x=>x.tag==="tbody");}
 append(...nodes){this.children.push(...nodes);}
 appendChild(node){this.children.push(node);return node;}
 replaceChildren(...nodes){this.children=nodes;}
 addEventListener(name,fn){this.handlers[name]=fn;}
 setAttribute(key,value){this.attrs[key]=value;}
 removeAttribute(key){delete this.attrs[key];}
 all(){return [this,...this.children.flatMap(x=>x instanceof El?x.all():[])];}
}
const viewport=new El("main"),logout=new El("button"),sidebar=new El("aside"),
 badge=new El("span"),events={},calls=[],nav={};
for(const name of ["dashboard","monthly","admin","settings"])nav["navBtn-"+name]=new El("button");
let transferCount=0, approvals=0;
const bridge={hasBridge:()=>true,onInvalidation:()=>{},setSessionNonce:()=>{},
 invoke:async(action,payload)=>{
   calls.push({action,payload});
   if(action==="GetCurrentSession")
     return{data:{sessionNonce:"A".repeat(32),username:"tester",role:"Supervisor"}};
   if(action==="QuerySites")
     return{data:[{siteId:"SITE-UNSPECIFIED",name:"غير محدد",active:true}]};
   if(action==="GetDashboardSummary")
     return{data:{totalForce:3,unprepared:1,internalTransferDay:transferCount,completionPercentage:67,statusCounts:[
       {statusId:"STS-PRESENT",name:"موجود",count:1,color:"#157A52"},
       {statusId:"STS-ABSENT",name:"غياب",count:1,color:"#B42332"}]}};
   if(action==="PreviewUnpreparedCarryForward")
     return {data:{businessDate:payload.businessDate,referenceDate:"2026-10-25",
       previewToken:"P".repeat(64),persons:payload.personIds.map(id=>({
         personId:id,name:id,serviceNumberA:"12345",siteName:"غير محدد",
         statusName:"غياب",fromYesterday:true}))}};
   if(action==="CommitUnpreparedCarryForward"){
     approvals++;
     return {data:{count:1,approvedBeforeEight:true}};
   }
   if(action==="QueryDailyRoster"){
     const all=[{personId:"P1",statusId:null,statusName:"غير محضر"},
       {personId:"P2",statusId:"STS-PRESENT",statusName:"موجود"},
       {personId:"P3",statusId:"STS-ABSENT",statusName:"غياب"},
       {personId:"P4",statusId:"__TRANSFER_DAY__",statusName:"تم نقله اليوم"}];
     const selected=all.filter(p=>(!payload.term||p.personId.includes(payload.term))&&(!payload.statusId ||
       (payload.statusId==="__UNPREPARED__"?p.statusId===null:p.statusId===payload.statusId)));
     return{data:{items:selected.map(p=>({...p,name:p.personId,serviceNumberA:"12345",siteName:"غير محدد"})),
       total:selected.length,hasNext:false}};
   }
   throw Error("Unexpected action "+action);
 }};
const document={createElement:tag=>new El(tag),
 getElementById:id=>({appViewport:viewport,headerRoleBadge:badge,logoutAction:logout,...nav})[id]||null,
 querySelector:s=>s===".app-sidebar"?sidebar:null,
 addEventListener:(name,fn)=>{events[name]=fn;}};
const FakeDate=class extends Date {
  constructor(...args){super(...(args.length?args:["2026-10-26T17:59:00"]));}
  static now(){return new Date("2026-10-26T17:59:00").getTime();}
};
const context={window:{AltamamBridge:bridge,AltamamNavigation:{navigateTop:()=>{}}},
   AltamamDialog:{confirm:async()=>true}},
 document,location:{hash:"#dashboard"},history:{replaceState:()=>{}},Date:FakeDate,Intl,console};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
 "../src/AlTamam.WebUI/assets/js/functional-screens.js"),"utf8"),context);
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const counter=key=>viewport.all().find(x=>x.tag==="button"&&x.dataset.statusFilter===key);
const lastRoster=()=>calls.filter(x=>x.action==="QueryDailyRoster").at(-1);
async function settle(){await tick();await tick();await tick();}
async function main(){
 await events.DOMContentLoaded();await settle();
 assert.ok(counter("all"),"total-force counter must be interactive");
 assert.ok(counter("__UNPREPARED__"),"unprepared counter must be interactive");
 assert.ok(viewport.all().some(x=>x.className.includes("eight-pm-clock")&&
   /\d\d:\d\d:\d\d/.test(x.textContent)),
   "the live 20:00 countdown is displayed from local time");
 assert.ok(viewport.all().some(x=>x.textContent.includes("الساعة ٨ مساءً")),
   "the dashboard explains automatic fallback at 20:00");
 const search=viewport.all().find(x=>x.tag==="input"&&
   x.attrs["aria-label"]==="بحث بالاسم أو الرقم (ع)");
 assert.ok(search,"a boxed search is available on the dashboard");
 search.value="P2";
 const searchButton=viewport.all().find(x=>x.tag==="button"&&x.textContent==="بحث");
 searchButton.handlers.click();await settle();
 assert.equal(lastRoster().payload.term,"P2","search is executed server-side");
 search.value="";searchButton.handlers.click();await settle();
 assert.ok(counter("STS-PRESENT"),"saved attendance counter must be interactive");
 assert.ok(!counter("__TRANSFER_DAY__"),"zero transfers must hide the movement card");
 assert.ok(!viewport.all().some(x=>x.className.includes("functional-status-filters")),
   "remove redundant status chips below counters");
 assert.equal(lastRoster().payload.statusId,"");
 assert.equal(counter("all").attrs["aria-pressed"],"true");
 counter("__UNPREPARED__").handlers.click();await settle();
 assert.equal(lastRoster().payload.statusId,"__UNPREPARED__");
 const dialog=viewport.all().find(x=>x.attrs.role==="dialog");
 assert.ok(dialog,"unprepared count opens a centered selection dialog");
 assert.ok(viewport.all().some(x=>x.textContent.includes("حالة اليوم السابق")),
   "popup introduces individual/bulk yesterday carry-forward");
 assert.equal(approvals,0,"opening unprepared modal never writes");
 const check=dialog.all().find(x=>x.tag==="input"&&x.type==="checkbox");
 assert.ok(check,"popup allows individual selection");
 check.checked=true;check.handlers.change();
 const prepare=dialog.all().find(x=>x.tag==="button"&&
   x.textContent==="معاينة حالة أمس للمحددين");
 await prepare.handlers.click();await settle();
 assert.ok(calls.some(x=>x.action==="PreviewUnpreparedCarryForward"&&
   x.payload.personIds.length===1),"server receives per-person previous-day preview");
 assert.equal(approvals,0,"preview must not write");
 const save=dialog.all().find(x=>x.tag==="button"&&
   x.textContent.startsWith("تأكيد اعتماد"));
 assert.ok(save,"review displays a distinct explicit approval step");
 await save.handlers.click();await settle();
 assert.equal(approvals,1,"only explicit approval commits the selected person's prior status");
 assert.equal(counter("__UNPREPARED__").attrs["aria-pressed"],"true");
 assert.equal(counter("all").attrs["aria-pressed"],"false");
 assert.ok(viewport.all().some(x=>x.textContent==="P1"));
 assert.equal(lastRoster().payload.page,1);
 counter("STS-ABSENT").handlers.click();await settle();
 assert.equal(lastRoster().payload.statusId,"STS-ABSENT");
 assert.equal(counter("STS-ABSENT").attrs["aria-pressed"],"true");
 assert.ok(viewport.all().some(x=>x.textContent==="P3"));
 counter("all").handlers.click();await settle();
 assert.equal(lastRoster().payload.statusId,"");
 assert.equal(counter("all").attrs["aria-pressed"],"true");
 transferCount=1;
 const refresh=viewport.all().find(x=>x.tag==="button"&&x.textContent==="تحديث البيانات");
 assert.ok(refresh);refresh.handlers.click();await settle();
 assert.ok(counter("__TRANSFER_DAY__"),"movement card appears automatically when one transfer exists");
 counter("__TRANSFER_DAY__").handlers.click();await settle();
 assert.equal(lastRoster().payload.statusId,"__TRANSFER_DAY__");
 assert.ok(viewport.all().some(x=>x.textContent==="P4"));
 assert.equal(calls.filter(x=>["SubmitAttendance","ReviseTodayAttendance"].includes(x.action)).length,0);
 console.log("DASHBOARD COUNTER FILTERS PASS: total/unprepared/status buttons, active states, no redundant chips");
}
main().catch(error=>{console.error(error);process.exitCode=1;});
