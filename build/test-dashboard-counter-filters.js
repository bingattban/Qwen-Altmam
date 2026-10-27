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
let transferCount=0,expandedRoster=false;
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
   if(action==="QueryDailyRoster"){
     const all=expandedRoster?Array.from({length:23},(_,i)=>({personId:"R"+(i+1),statusId:null,statusName:"غير محضر"})):[{personId:"P1",statusId:null,statusName:"غير محضر"},
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
const context={window:{AltamamBridge:bridge,AltamamNavigation:{navigateTop:()=>{}}},
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
 assert.ok(counter("__UNPREPARED__"),"unprepared counter must be interactive");
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
 counter("__UNPREPARED__").handlers.click();await settle();
 assert.equal(lastRoster().payload.statusId,"__UNPREPARED__");
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
 assert.ok(viewport.all().some(x=>x.className==="dashboard-eight-clock"),
   "20:00 clock absent. DOM classes: "+viewport.all().map(x=>x.className).filter(Boolean).join(", "));
 let selectionCalled=0,reviewCalled=0;
 context.window.AltamamDialog={
   unpreparedSelection:async loader=>{
     selectionCalled++;
     const data=await loader(1);
     assert.equal(data.items.length,1);
     return ["P1"];
   },
   attendanceReview:async plan=>{
     reviewCalled++;
     assert.equal(plan.persons[0].proposedStatusName,"غياب");
     return true;
   },
   alert:async error=>{throw Error("Unexpected modal error "+error);}
 };
 counter("__UNPREPARED__").handlers.click();await settle();
 assert.equal(selectionCalled,1,"counter click must open the independent selector");
 assert.equal(reviewCalled,1,"a separate confirmable preview is mandatory");
 assert.equal(calls.filter(x=>x.action==="SubmitAttendance"&&x.payload.mode==="carry").length,1,
   "write must happen only after both the picker and confirmation");
 expandedRoster=true;
 counter("all").handlers.click();await settle();
 const dailyPanel=()=>viewport.all().find(x=>x.className.includes("daily-roster-panel"));
 const dailyRows=()=>dailyPanel().all().find(x=>x.tag==="tbody").children;
 const navButton=name=>viewport.all().find(x=>x.tag==="button"&&x.textContent===name);
 assert.ok(dailyPanel(),"only daily roster gets dedicated spacing");
 assert.equal(lastRoster().payload.pageSize,10,"server request must limit to ten names");
 assert.equal(dailyRows().length,10,"ten names on first daily page");
 assert.equal(dailyRows()[0].children[0].textContent,"1");
 assert.equal(dailyRows()[9].children[0].textContent,"10");
 assert.equal(navButton("السابق").disabled,true);
 navButton("التالي").handlers.click();await settle();
 assert.equal(lastRoster().payload.page,2);
 assert.equal(dailyRows().length,10);
 assert.equal(dailyRows()[0].children[0].textContent,"11");
 navButton("التالي").handlers.click();await settle();
 assert.equal(dailyRows().length,3,"23 names require 10+10+3 rows");
 assert.equal(dailyRows()[0].children[0].textContent,"21");
 assert.equal(navButton("التالي").disabled,true);
 navButton("السابق").handlers.click();await settle();
 assert.equal(dailyRows().length,10);
 console.log("DASHBOARD COUNTER FILTERS PASS: all filters, ten-name pages and navigation");
}
main().catch(error=>{console.error(error);process.exitCode=1;});
