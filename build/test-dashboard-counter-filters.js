"use strict";
const assert=require("node:assert/strict"),fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
class El{
 constructor(tag){this.tag=tag;this.children=[];this.handlers={};this.attrs={};
   this.className="";this.textContent="";this.style={};this.value="";this.dataset={};this.disabled=false;}
 get classList(){const toggle=(name,on)=>{const tags=this.className.split(/\s+/).filter(Boolean).filter(x=>x!==name);if(on)tags.push(name);this.className=tags.join(" ");};return{toggle,add:name=>toggle(name,true),remove:name=>toggle(name,false)};}
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
let transferCount=0,expandedRoster=false,activeStatusesAvailable=false;
const bridge={hasBridge:()=>true,onInvalidation:()=>{},setSessionNonce:()=>{},
 invoke:async(action,payload)=>{
   calls.push({action,payload});
   if(action==="GetCurrentSession")
     return{data:{sessionNonce:"A".repeat(32),username:"tester",role:"Supervisor"}};
   if(action==="QuerySites")
     return{data:[{siteId:"SITE-UNSPECIFIED",name:"غير محدد",active:true}]};
   if(action==="GetActiveAttendanceStatuses")
     return{data:activeStatusesAvailable
       ?[{statusId:"STS-PRESENT",displayName:"موجود"},
         {statusId:"STS-ABSENT",displayName:"غياب"}]
       :[]};
   if(action==="GetDashboardSummary")
     return{data:{totalForce:3,unprepared:1,automaticallyPrepared:1,internalTransferDay:transferCount,completionPercentage:67,statusCounts:[
       {statusId:"STS-PRESENT",name:"موجود",count:1,color:"#157A52"},
       {statusId:"STS-ABSENT",name:"غياب",count:1,color:"#B42332"}]}};
   if(action==="QueryDailyRoster"&&payload.statusId==="__AUTO_PREPARED__")
     return{data:{items:[{personId:"P3",name:"P3",serviceNumberA:"12345",
       siteName:"غير محدد",statusName:"غياب",savedBusinessDate:"2026-10-29",
       autoSource:"تحضير تلقائي 08:00"}],total:1,hasNext:false}};
   if(action==="QueryDailyRoster"){
     const all=expandedRoster?Array.from({length:235},(_,i)=>({personId:"R"+(i+1),statusId:null,statusName:"غير محضر"})):[{personId:"P1",statusId:null,statusName:"غير محضر"},
       {personId:"P2",statusId:"STS-PRESENT",statusName:"موجود"},
       {personId:"P3",statusId:"STS-ABSENT",statusName:"غياب"},
       {personId:"P4",statusId:"__TRANSFER_DAY__",statusName:"تم نقله اليوم"}];
     const selected=all.filter(p=>(!payload.statusId ||
       (payload.statusId==="__UNPREPARED__"?p.statusId===null:p.statusId===payload.statusId))
       &&(!payload.term || p.personId.toLowerCase().includes(payload.term.toLowerCase())));
     const visible=selected.slice((payload.page-1)*payload.pageSize,payload.page*payload.pageSize);
     return{data:{items:visible.map(p=>({...p,name:p.personId,serviceNumberA:"12345",siteName:"غير محدد"})),
       total:selected.length,hasNext:payload.page*payload.pageSize<selected.length}};
   }
   if(action==="PreviewAttendanceChange" && payload.mode==="revise")
     return{data:{businessDate:payload.businessDate,previewToken:"R".repeat(64),
       mode:"revise",statusName:"موجود",persons:[{personId:"P3",name:"P3",
         serviceNumberA:"12345",siteName:"غير محدد",previousStatusName:"غياب",
         proposedStatusName:"موجود"}]}};
   if(action==="ReviseTodayAttendance")return{data:{count:1}};
   if(action==="PreviewAttendanceChange" && payload.mode==="carry")
     return {data:{mode:"carry",businessDate:payload.businessDate,previewToken:"a".repeat(64),
       persons:[{personId:"P1",name:"P1",serviceNumberA:"12345",siteName:"غير محدد",
         previousStatusName:"غير محضر",proposedStatusName:"غياب"}]}};
   if(action==="SubmitAttendance" && payload.mode==="carry")
     return {data:{count:1,carriedForward:true}};
   throw Error("Unexpected action "+action);
 }};
const document={createElement:tag=>new El(tag),
 getElementById:id=>({appViewport:viewport,headerRoleBadge:badge,logoutAction:logout,...nav})[id]||null,
 querySelector:s=>s===".app-sidebar"?sidebar:null,
 addEventListener:(name,fn)=>{events[name]=fn;}};
const reviews=[];
const context={window:{AltamamBridge:bridge,AltamamNavigation:{navigateTop:()=>{}},
 AltamamDialog:{attendanceReview:async(data,opts)=>{reviews.push({data,opts});return true;}}},
 document,location:{hash:"#dashboard"},history:{replaceState:()=>{}},Date,Intl,console};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
 "../src/AlTamam.WebUI/assets/js/functional-screens.js"),"utf8"),context);
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const counter=key=>viewport.all().find(x=>x.tag==="button"&&x.dataset.statusFilter===key);
const lastRoster=()=>calls.filter(x=>x.action==="QueryDailyRoster").at(-1);
async function settle(){await tick();await tick();await tick();}
async function main(){
 await events.DOMContentLoaded();await settle();
 assert.ok(counter("all"),"total-force counter must be interactive");
 assert.ok(!counter("__UNPREPARED__"),"unprepared counter must not appear");
 assert.ok(!viewport.all().some(x=>x.textContent==="اكتمال التحضير"),
   "completion card must be removed");
 const cards=viewport.all().filter(x=>x.tag==="button"&&
   x.className.includes("dashboard-filter-counter"));
 assert.deepEqual(cards.map(x=>x.dataset.statusFilter),
   ["all","STS-PRESENT","STS-ABSENT","__AUTO_PREPARED__"],
   "order must be force, present, configured statuses then auto-prepared");
 assert.ok(counter("STS-PRESENT"),"saved attendance counter must be interactive");
 assert.ok(!counter("__TRANSFER_DAY__"),"zero transfers must hide the movement card");
 assert.ok(!viewport.all().some(x=>x.className.includes("functional-status-filters")),
   "remove redundant status chips below counters");
 assert.equal(lastRoster().payload.statusId,"");
 assert.equal(counter("all").attrs["aria-pressed"],"true");
 const search=viewport.all().find(x=>x.tag==="input"&&x.type==="search");
 assert.ok(search,"dashboard needs a search field");
 search.value="P2";
 const submit=viewport.all().find(x=>x.tag==="button"&&x.textContent==="بحث");
 assert.ok(submit);submit.handlers.click();await settle();
 assert.equal(lastRoster().payload.term,"P2");
 assert.equal(lastRoster().payload.page,1);
 assert.equal(lastRoster().payload.statusId,"");
 assert.ok(viewport.all().some(x=>x.textContent==="P2"));
 assert.ok(!viewport.all().some(x=>x.textContent==="P1"));
 const clear=viewport.all().find(x=>x.tag==="button"&&x.textContent==="مسح البحث");
 clear.handlers.click();await settle();
 assert.equal(lastRoster().payload.term,"");
 assert.equal(counter("all").attrs["aria-pressed"],"true");
 counter("STS-PRESENT").handlers.click();await settle();
 assert.equal(lastRoster().payload.statusId,"STS-PRESENT");
 assert.equal(counter("STS-PRESENT").attrs["aria-pressed"],"true");
 assert.equal(counter("all").attrs["aria-pressed"],"false");
 assert.ok(viewport.all().some(x=>x.textContent==="P2"));
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
 assert.ok(viewport.all().some(x=>x.className==="dashboard-eight-clock"),
   "08:00 clock absent. DOM classes: "+viewport.all().map(x=>x.className).filter(Boolean).join(", "));
 // No automatic manual carry-forward is triggered by selecting status counters.
 assert.equal(calls.filter(x=>["SubmitAttendance","ReviseTodayAttendance",
   "PreviewAttendanceChange"].includes(x.action)).length,0);
 assert.ok(counter("__AUTO_PREPARED__"),"automatic-attendance counter is always visible");
 counter("__AUTO_PREPARED__").handlers.click();await settle();
 assert.equal(calls.filter(x=>x.action==="QueryDailyRoster").at(-1).payload.statusId,
   "__AUTO_PREPARED__");
 const readOnly=viewport.all().find(x=>x.className.includes("auto-attendance-overlay"));
 assert.ok(readOnly,"automatic attendance opens an independent read-only register");
 assert.ok(readOnly.all().some(x=>x.textContent==="P3"));
 assert.ok(readOnly.all().some(x=>x.textContent==="غياب"));
 assert.ok(readOnly.all().some(x=>x.textContent==="تحضير تلقائي 08:00"),
   "the automatic register must show the persisted source of preparation");
 assert.ok(readOnly.all().some(x=>x.textContent==="2026-10-29"),
   "the automatic register must display the date of the saved attendance");
 assert.ok(!readOnly.all().some(x=>x.tag==="input"&&x.type==="checkbox"),
   "automatic register allows targeted changes without selecting bulk checkboxes");
 assert.equal(readOnly.all().filter(x=>x.tag==="select").length,0,
   "the automatic attendance register must be a read-only table, not an editable grid");
 assert.equal(calls.filter(x=>x.action==="ReviseTodayAttendance").length,0);
 // No active status choices must NOT block reading already committed automatic records.
 activeStatusesAvailable=true;
 counter("__AUTO_PREPARED__").handlers.click();await settle();
 const refreshedRegister=viewport.all().filter(x=>
   x.className.includes("auto-attendance-overlay")).at(-1);
 assert.ok(refreshedRegister?.all().some(x=>x.textContent==="P3"));
 const entry=refreshedRegister.all().find(x=>
   x.tag==="button"&&x.textContent==="فتح مراجعة الحالة");
 assert.ok(entry,"a row can open a separate controlled revision dialog");
 assert.equal(calls.filter(x=>x.action==="ReviseTodayAttendance").length,0,
   "opening the register must never commit attendance");
 await entry.handlers.click();await settle();
 const separate=viewport.all().find(x=>x.className.includes("auto-revision-overlay"));
 assert.ok(separate,"reviewing an automatic record must open a distinct dialog");
 assert.equal(separate.attrs.role,"dialog");
 assert.ok(separate.all().some(x=>x.textContent==="غياب"),
   "the separate review must present the currently saved status");
 assert.equal(calls.filter(x=>x.action==="ReviseTodayAttendance").length,0,
   "opening the revision dialog must not commit attendance");
 const dropdown=separate.all().find(x=>x.tag==="select");
 assert.ok(dropdown,"the separate review dialog has a new-status selector");
 dropdown.value="STS-PRESENT";
 const edit=separate.all().find(x=>x.tag==="button"&&x.textContent==="معاينة تغيير الحالة");
 await edit.handlers.click();await settle();
 assert.equal(reviews.length,1,"changes require a server-verified modal review");
 assert.equal(calls.filter(x=>x.action==="ReviseTodayAttendance").length,1,
   "server-side revision is committed only following modal approval");
 assert.ok(readOnly.all().some(x=>x.textContent==="P3"),
   "auto-prepared register remains displayed following a successful revision");
 expandedRoster=true;
 counter("all").handlers.click();await settle();
 const dailyPanel=()=>viewport.all().find(x=>x.className.includes("daily-roster-panel"));
 const dailyRows=()=>dailyPanel().all().find(x=>x.tag==="tbody").children;
 const navButton=name=>viewport.all().find(x=>x.tag==="button"&&x.textContent===name);
 assert.ok(dailyPanel(),"only daily roster gets dedicated spacing");
 assert.equal(lastRoster().payload.pageSize,100,"request 100 roster entries per page");
 assert.equal(dailyRows().length,100,"100 names loaded on the first roster page");
 assert.equal(dailyRows()[0].children[0].textContent,"1");
 assert.equal(dailyRows()[99].children[0].textContent,"100");
 assert.equal(navButton("السابق").disabled,true);
 navButton("التالي").handlers.click();await settle();
 assert.equal(lastRoster().payload.page,2);
 assert.equal(dailyRows().length,100);
 assert.equal(dailyRows()[0].children[0].textContent,"101");
 navButton("التالي").handlers.click();await settle();
 assert.equal(dailyRows().length,35,"235 names require 100+100+35 rows");
 assert.equal(dailyRows()[0].children[0].textContent,"201");
 assert.equal(navButton("التالي").disabled,true);
 navButton("السابق").handlers.click();await settle();
 assert.equal(dailyRows().length,100);
 assert.ok(viewport.className.includes("daily-dashboard"),"daily view activates flexible full-height layout");
 const css=fs.readFileSync(path.join(__dirname,"../src/AlTamam.WebUI/assets/css/altamam.css"),"utf8");
 assert.match(css,/\.auto-revision-overlay\s*\{[^}]*position:fixed;inset:0;z-index:1300/s,
   "the separate review must be a real overlay above the read-only register");
 assert.match(css,/body:has\(\.auto-attendance-overlay\) #appModalBackdrop\.open\{z-index:1400\}/,
   "the native verified confirmation remains above both local dialog layers");
 assert.match(css,/#appViewport\.daily-dashboard>\.daily-roster-panel\s*\{/,
   "daily roster must fill the available desktop viewport");
 assert.match(css,/#appViewport\.daily-dashboard>\.daily-roster-panel>\.functional-table-wrap\s*\{/,
   "daily roster scrolling must be internal to the full-height table");
 console.log("DASHBOARD COUNTER FILTERS PASS: 100-row pages, counters, full-height layout and pager");
}
main().catch(error=>{console.error(error);process.exitCode=1;});
