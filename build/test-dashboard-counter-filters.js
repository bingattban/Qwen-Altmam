"use strict";
const assert=require("node:assert/strict"),fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
class El{
 constructor(tag){this.tag=tag;this.children=[];this.handlers={};this.attrs={};
   this.className="";this.textContent="";this.style={};this.value="";this.dataset={};this.disabled=false;}
 get classList(){const toggle=(name,on)=>{const tags=this.className.split(/\s+/).filter(Boolean).filter(x=>x!==name);if(on)tags.push(name);this.className=tags.join(" ");};return{toggle,add:name=>toggle(name,true),remove:name=>toggle(name,false)};}
 get tBodies(){return this.children.filter(x=>x.tag==="tbody");}
 append(...nodes){this.children.push(...nodes);}
 appendChild(node){this.children.push(node);return node;}
 remove(){this.removed=true;}
 replaceChildren(...nodes){this.children=nodes;}
 addEventListener(name,fn){this.handlers[name]=fn;}
 setAttribute(key,value){this.attrs[key]=value;}
 removeAttribute(key){delete this.attrs[key];}
 getAttribute(key){return this.attrs[key]??null;}
 focus(){document.activeElement=this;}
 contains(other){return this.all().includes(other);}
 querySelector(selector){
   if(selector==='[data-status-filter="__AUTO_PREPARED__"]')
     return this.all().find(x=>x.dataset.statusFilter==="__AUTO_PREPARED__")||null;
   return null;
 }
 querySelectorAll(){return this.all().filter(x=>
   ["button","input","select","textarea"].includes(x.tag) &&
   !x.disabled && !x.hidden && x.type!=="hidden");}
 all(){return [this,...this.children.flatMap(x=>x instanceof El?x.all():[])];}
}
const viewport=new El("main"),logout=new El("button"),sidebar=new El("aside"),
 badge=new El("span"),events={},calls=[],nav={};
for(const name of ["dashboard","monthly","admin","settings"])nav["navBtn-"+name]=new El("button");
let transferCount=0,expandedRoster=false,approveReview=true;
let discardApproved=false,discardPrompts=0;
let deferAutoReview=false,releaseAutoReview=null;
let autoPeople=[{personId:"P3",name:"P3",serviceNumberA:"12345",
  siteName:"غير محدد",statusId:"STS-ABSENT",statusName:"غياب",
  savedBusinessDate:"2026-10-29",autoSource:"تحضير تلقائي 08:00"}];
const bridge={hasBridge:()=>true,onInvalidation:()=>{},setSessionNonce:()=>{},
 invoke:async(action,payload)=>{
   calls.push({action,payload});
   if(action==="GetCurrentSession")
     return{data:{sessionNonce:"A".repeat(32),username:"tester",role:"Supervisor"}};
   if(action==="QuerySites")
     return{data:[{siteId:"SITE-UNSPECIFIED",name:"غير محدد",active:true}]};
   if(action==="GetActiveAttendanceStatuses")
     return{data:[{statusId:"STS-PRESENT",displayName:"موجود"},
       {statusId:"STS-ABSENT",displayName:"غياب"}]};
   if(action==="GetDashboardSummary")
     return{data:{totalForce:3,unprepared:1,automaticallyPrepared:1,internalTransferDay:transferCount,completionPercentage:67,statusCounts:[
       {statusId:"STS-PRESENT",name:"موجود",count:1,color:"#157A52"},
       {statusId:"STS-ABSENT",name:"غياب",count:1,color:"#B42332"}]}};
   if(action==="QueryDailyRoster"&&payload.statusId==="__AUTO_PREPARED__"){
     const matching=autoPeople.filter(p=>!payload.term||
       p.name.toLowerCase().includes(payload.term.toLowerCase())||
       p.serviceNumberA.includes(payload.term));
     return{data:{items:matching.slice((payload.page-1)*payload.pageSize,
       payload.page*payload.pageSize),total:matching.length,hasNext:false}};
   }
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
       mode:"revise",statusName:"موجود",persons:payload.personIds.map(id=>{
         const person=autoPeople.find(p=>p.personId===id);
         return{...person,previousStatusName:person.statusName,
           proposedStatusName:"موجود"};
       })}};
   if(action==="ReviseTodayAttendance")
     return{data:{count:calls.filter(x=>x.action==="PreviewAttendanceChange").at(-1).payload.personIds.length}};
   if(action==="PreviewAttendanceChange" && payload.mode==="carry")
     return {data:{mode:"carry",businessDate:payload.businessDate,previewToken:"a".repeat(64),
       persons:[{personId:"P1",name:"P1",serviceNumberA:"12345",siteName:"غير محدد",
         previousStatusName:"غير محضر",proposedStatusName:"غياب"}]}};
   if(action==="SubmitAttendance" && payload.mode==="carry")
     return {data:{count:1,carriedForward:true}};
   throw Error("Unexpected action "+action);
 }};
const document={activeElement:null,createElement:tag=>new El(tag),
 getElementById:id=>({appViewport:viewport,headerRoleBadge:badge,logoutAction:logout,...nav})[id]||null,
 querySelector:s=>s===".app-sidebar"?sidebar:null,
 addEventListener:(name,fn)=>{events[name]=fn;}};
const reviews=[];
const context={window:{AltamamBridge:bridge,AltamamNavigation:{navigateTop:()=>{}},
 AltamamDialog:{attendanceReview:async(data,opts)=>{
     reviews.push({data,opts});
     if(deferAutoReview)return new Promise(resolve=>{releaseAutoReview=resolve;});
     return approveReview;
   },
   confirm:async()=>{discardPrompts++;return discardApproved;}}},
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
 const originalAutoTrigger=counter("__AUTO_PREPARED__");
 originalAutoTrigger.focus();
 originalAutoTrigger.handlers.click();await settle();
 assert.equal(calls.filter(x=>x.action==="QueryDailyRoster").at(-1).payload.statusId,
   "__AUTO_PREPARED__");
 const readOnly=viewport.all().find(x=>x.className.includes("auto-attendance-overlay"));
 assert.ok(readOnly,"automatic attendance opens an independent read-only register");
 assert.equal(viewport.all().filter(x=>x===readOnly).length,1,
   "automatic attendance modal is mounted exactly once inside the dashboard viewport");
 assert.equal(readOnly.attrs["aria-modal"],"true",
   "independent register must announce that it is a real modal");
 const autoClose=readOnly.all().find(x=>x.tag==="button"&&x.textContent==="إغلاق");
 assert.equal(document.activeElement,autoClose,
   "register opening must move keyboard focus inside the modal");
 const tabbables=()=>readOnly.querySelectorAll();
 assert.ok(tabbables().length>2,"register contains several keyboard focusable controls");
 let trapped=0;
 readOnly.handlers.keydown({key:"Tab",shiftKey:true,
   preventDefault(){trapped++;}});
 assert.equal(trapped,1,"Shift-Tab at first control must wrap inside modal");
 assert.equal(document.activeElement,tabbables().at(-1),
   "Shift-Tab wraps focus to last available control");
 readOnly.handlers.keydown({key:"Tab",shiftKey:false,
   preventDefault(){trapped++;}});
 assert.equal(trapped,2,"Tab at last control must wrap inside modal");
 assert.equal(document.activeElement,autoClose,"Tab wraps back to the dialog close control");
 const disabledAutoAction=readOnly.all().find(x=>x.tag==="button"&&
   x.textContent==="معاينة تغيير المحددين");
 assert.ok(disabledAutoAction.disabled,"bulk action starts disabled");
 assert.ok(!tabbables().includes(disabledAutoAction),
   "disabled controls must be excluded from the focus trap");
 assert.ok(readOnly.all().some(x=>x.textContent==="P3"));
 assert.ok(readOnly.all().some(x=>x.textContent==="غياب"));
 assert.ok(readOnly.all().some(x=>x.textContent==="تحضير تلقائي 08:00"),
   "the automatic register must show the persisted source of preparation");
 assert.ok(readOnly.all().some(x=>x.textContent==="2026-10-29"),
   "the automatic register must display the date of the saved attendance");
 const selectedCheck=readOnly.all().find(x=>x.tag==="input"&&x.type==="checkbox");
 assert.ok(selectedCheck,"each automatic record allows individual selection");
 assert.equal(calls.filter(x=>x.action==="ReviseTodayAttendance").length,0,
   "opening register and selecting entries must not save attendance");
 const dropdown=readOnly.all().find(x=>x.tag==="select"&&
   x.attrs["aria-label"]==="الحالة الجديدة لـ P3");
 assert.ok(dropdown,"restore per-person inline status selector");
 dropdown.value="STS-PRESENT";
 const edit=readOnly.all().find(x=>x.tag==="button"&&x.textContent==="معاينة التعديل");
 approveReview=false;
 await edit.handlers.click();await settle();
 assert.equal(reviews.length,1,"single-person change opens native verified preview");
 assert.equal(reviews[0].data.persons[0].previousStatusName,"غياب");
 assert.equal(reviews[0].data.persons[0].proposedStatusName,"موجود");
 assert.equal(calls.filter(x=>x.action==="ReviseTodayAttendance").length,0,
   "cancelling the preview must never write a change");
 approveReview=true;
 await edit.handlers.click();await settle();
 assert.equal(reviews.length,2);
 assert.equal(calls.filter(x=>x.action==="ReviseTodayAttendance").length,1,
   "single change commits only after modal confirmation");
 assert.ok(readOnly.all().some(x=>x.textContent==="P3"),
   "auto-prepared register remains displayed following a successful revision");
 // Multi-selection with one common new state must use ONE server transaction.
 autoPeople.push({personId:"P5",name:"P5",serviceNumberA:"12346",
   siteName:"غير محدد",statusId:"STS-ABSENT",statusName:"غياب",
   savedBusinessDate:"2026-10-29",autoSource:"تحضير تلقائي 20:00"});
 const searchAuto=readOnly.all().find(x=>x.tag==="button"&&x.textContent==="بحث");
 searchAuto.handlers.click();await settle();
 const selectPage=readOnly.all().find(x=>x.tag==="button"&&x.textContent==="تحديد الصفحة");
 assert.ok(selectPage,"bulk-selection control exists");
 selectPage.handlers.click();await settle();
 const counterAuto=readOnly.all().find(x=>x.className==="auto-attendance-selection-info");
 assert.ok(counterAuto.textContent.includes("2"),"both visible records are selected");
 const commonState=readOnly.all().find(x=>x.tag==="select"&&
   x.attrs["aria-label"]==="الحالة الجديدة للمحددين");
 assert.ok(commonState,"bulk common-status selector exists");
 commonState.value="STS-PRESENT";
 const bulk=readOnly.all().find(x=>x.tag==="button"&&x.textContent==="معاينة تغيير المحددين");
 approveReview=false;
 const priorCommits=calls.filter(x=>x.action==="ReviseTodayAttendance").length;
 await bulk.handlers.click();await settle();
 assert.equal(reviews.at(-1).data.persons.length,2,
   "review must list every selected person");
 assert.deepEqual(Array.from(reviews.at(-1).data.persons.map(p=>p.personId)).sort(),
   ["P3","P5"]);
 assert.ok(reviews.at(-1).data.persons.every(p=>
   p.previousStatusName==="غياب"&&p.proposedStatusName==="موجود"),
   "preview identifies each former and proposed attendance status");
 assert.equal(calls.filter(x=>x.action==="ReviseTodayAttendance").length,priorCommits,
   "declining two-person preview does not commit");
 approveReview=true;
 await bulk.handlers.click();await settle();
 const commits=calls.filter(x=>x.action==="ReviseTodayAttendance");
 assert.equal(commits.length,priorCommits+1,
   "bulk approval issues exactly one atomic C# commit");
 assert.equal(counterAuto.textContent,"المحددون: 0 من 100",
   "selection resets only after successful confirmation");
 // Pending selections on the automatic register require a separate, explicit
 // discard confirmation; both refusal and approval must leave attendance saved
 // only by the verified preview/commit path.
 selectPage.handlers.click();await settle();
 assert.equal(counterAuto.textContent,"المحددون: 2 من 100");
 const clearPending=readOnly.all().find(x=>x.tag==="button"&&x.textContent==="إلغاء التحديد");
 const priorWrites=calls.filter(x=>x.action==="ReviseTodayAttendance").length;
 await clearPending.handlers.click();await settle();
 assert.equal(discardPrompts,1,"clearing a pending batch must ask for confirmation");
 assert.equal(counterAuto.textContent,"المحددون: 2 من 100",
   "declined discard must preserve both selected persons");
 // A dashboard refresh cannot erase a pending selection merely because
 // the background counters and roster are redrawn.
 const refreshBehindModal=viewport.all().find(x=>x.tag==="button"&&
   x.textContent==="تحديث البيانات");
 refreshBehindModal.handlers.click();await settle();
 assert.equal(viewport.all().filter(x=>x===readOnly).length,1,
   "refreshed dashboard must keep the active modal in the same viewport");
 assert.equal(counterAuto.textContent,"المحددون: 2 من 100",
   "refresh must preserve the operator's uncommitted selection");
 assert.equal(calls.filter(x=>x.action==="ReviseTodayAttendance").length,priorWrites);
 discardApproved=true;
 await clearPending.handlers.click();await settle();
 assert.equal(counterAuto.textContent,"المحددون: 0 من 100",
   "approved discard clears the selection without committing");
 assert.equal(calls.filter(x=>x.action==="ReviseTodayAttendance").length,priorWrites);
 selectPage.handlers.click();await settle();
 const closeRegister=readOnly.all().find(x=>x.tag==="button"&&x.textContent==="إغلاق");
 discardApproved=false;
 await closeRegister.handlers.click();await settle();
 assert.equal(readOnly.removed,undefined,"refusing close preserves the register");
 assert.equal(counterAuto.textContent,"المحددون: 2 من 100");
 discardApproved=true;
 assert.ok(readOnly.handlers.keydown,"modal handles keyboard dismissal at the overlay boundary");
 readOnly.handlers.keydown({key:"Escape",preventDefault(){}});
 await settle();
 assert.equal(readOnly.removed,true,"approved Escape closes the register through the same safe confirmation");
 assert.equal(document.activeElement,counter("__AUTO_PREPARED__"),
   "closing restores focus to the current auto-prepared counter after dashboard refresh");
 assert.equal(calls.filter(x=>x.action==="ReviseTodayAttendance").length,priorWrites);

 // A queued approval must never write attendance after the operator leaves
 // the automatic register (e.g. navigation to another top-level section).
 context.window.AltamamFunctional.render("dashboard");await settle();
 counter("__AUTO_PREPARED__").handlers.click();await settle();
 const abandoned=viewport.all().find(x=>x.className.includes("auto-attendance-overlay"));
 assert.ok(abandoned,"fresh automatic register can reopen after a prior close");
 const individualStatus=abandoned.all().find(x=>x.tag==="select"&&
   x.attrs["aria-label"]==="الحالة الجديدة لـ P3");
 individualStatus.value="STS-PRESENT";
 deferAutoReview=true;
 const writesBeforeNavigation=calls.filter(x=>x.action==="ReviseTodayAttendance").length;
 const startReview=abandoned.all().find(x=>x.tag==="button"&&x.textContent==="معاينة التعديل");
 startReview.handlers.click();await settle();
 assert.equal(typeof releaseAutoReview,"function",
   "a real attendanceReview must remain pending for the race regression");
 context.window.AltamamFunctional.render("settings");await settle();
 assert.equal(abandoned.removed,true,"leaving the dashboard unmounts automatic register");
 releaseAutoReview(true);await settle();
 assert.equal(calls.filter(x=>x.action==="ReviseTodayAttendance").length,writesBeforeNavigation,
   "late human approval after navigation must not commit stale attendance");
 deferAutoReview=false;releaseAutoReview=null;
 context.window.AltamamFunctional.render("dashboard");await settle();
 assert.ok(counter("all"),"navigation does not break the four main sections");

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
 assert.match(css,/#appViewport\.daily-dashboard>\.daily-roster-panel\s*\{/,
   "daily roster must fill the available desktop viewport");
 assert.match(css,/#appViewport\.daily-dashboard>\.daily-roster-panel>\.functional-table-wrap\s*\{/,
   "daily roster scrolling must be internal to the full-height table");
 console.log("DASHBOARD COUNTER FILTERS PASS: 100-row pages, counters, full-height layout and pager");
}
main().catch(error=>{console.error(error);process.exitCode=1;});
