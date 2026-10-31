"use strict";
const assert=require("node:assert/strict");
const fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
class El{
 constructor(tag){this.tag=tag;this.children=[];this.textContent="";
 this.className="";this.style={};this.handlers={};this.isConnected=true;
 this.attrs={};this.disabled=false;this.value="";this.checked=false;}
 append(...x){this.children.push(...x);}
 appendChild(x){this.children.push(x);return x;}
 replaceChildren(...x){this.children=x;}
 insertBefore(x,b){const i=this.children.indexOf(b);this.children.splice(i<0?0:i,0,x);}
 setAttribute(k,v){this.attrs[k]=v;}
 addEventListener(e,cb){this.handlers[e]=cb;}
 all(){return [this,...this.children.flatMap(x=>x instanceof El?x.all():[])];}
}
const root=new El("main"),calls=[],writes=[],reviews=[];
let approved=false, errorName="",expandedRoster=false;
let cancelConfirmed=false,confirmations=0,navigations=0;
const bridge={invoke:async(name,payload)=>{
 calls.push({name,payload});
 if(name==="QueryDailyRoster"&&expandedRoster){
    const all=Array.from({length:112},(_,i)=>({personId:"P"+(i+1),name:"فرد "+(i+1),
      serviceNumberA:String(100+i),siteName:"غير محدد",statusName:"غير محضر"}));
    return {data:{items:all.slice((payload.page-1)*payload.pageSize,payload.page*payload.pageSize),
      total:all.length,hasNext:payload.page*payload.pageSize<all.length}};
  }
  if(name==="PreviewAttendanceChange"&&errorName)
   throw Error("تعذر تحضير «"+errorName+"» (الرقم (ع): 12345): الحالة مسجلة اليوم.");
 const data={
   QuerySites:[{siteId:"SITE-UNSPECIFIED",name:"غير محدد",active:true}],
   GetActiveAttendanceStatuses:[
      {statusId:"STS-PRESENT",displayName:"موجود",color:"#22AA55"},
      {statusId:"STS-ABSENT",displayName:"غياب",color:"#BB3355"}],
   QueryDailyRoster:{total:2,hasNext:false,items:[
      {personId:"P1",name:"شخص تجريبي",serviceNumberA:"12345",siteName:"غير محدد",
       statusName:"غير محضر",statusColor:"#22AA55"},
      {personId:"P2",name:"شخص تجريبي آخر",serviceNumberA:"67890",siteName:"غير محدد",
       statusName:"غير محضر"}]},
   PreviewAttendanceChange:{previewToken:"SERVER_ONE_TIME_TOKEN",
      mode:payload?.mode||"initial",count:1,businessDate:"2026-10-24",
      statusName:payload?.mode==="revise"?"غياب":"موجود",
      persons:[{personId:"P1",name:"شخص تجريبي",serviceNumberA:"12345",
        previousStatusName:payload?.mode==="revise"?"موجود":"غير محضر"}]},
   SubmitAttendance:{count:1},
   ReviseTodayAttendance:{count:1}
 };
 if(name==="SubmitAttendance"||name==="ReviseTodayAttendance")
   writes.push({name,payload});
 if(!Object.hasOwn(data,name))throw Error("Unexpected action: "+name);
 return {data:data[name]};
}};
const context={
 document:{createElement:tag=>new El(tag)},window:{AltamamBridge:bridge,
   AltamamAdminSettings:{renderGroup:()=>{navigations++;}},
   AltamamDialog:{
     attendanceReview:async(data,opts)=>{
       reviews.push({data,opts});
       return approved;
     },
     confirm:async()=>{confirmations++;return cancelConfirmed;}
   }},
 console,Date
};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
 "../src/AlTamam.WebUI/assets/js/attendance-ui.js"),"utf8"),context);
const one=text=>root.all().find(x=>x.tag==="button"&&x.textContent===text);
const selects=()=>root.all().filter(x=>x.tag==="select");
async function flush(){await new Promise(resolve=>setImmediate(resolve));}
async function choose(){
 const checkbox=root.all().find(x=>x.tag==="input"&&x.type==="checkbox");
 checkbox.checked=true;checkbox.handlers.change();
}
async function main(){
 await context.window.AltamamAttendance.render(root);
 assert.ok(one("معاينة التحضير"),"actual attendance page");
 assert.ok(calls.some(x=>x.name==="QueryDailyRoster"&&x.payload.statusId==="__ELIGIBLE__"&&x.payload.pageSize===100),
   "attendance selection must exclude the movement-day roster on the C# query");
 assert.equal(root.all().filter(x=>x.tag==="input"&&x.type==="checkbox").length,2);
 assert.equal(selects()[1].children[1].style.color,"#22AA55",
   "allowed statuses expose their configured color");
 assert.ok(root.all().some(x=>x.className.includes("attendance-status-pill") &&
   x.style.borderColor==="#22AA55"),
   "saved current status uses its real color on the roster");
 assert.equal(writes.length,0,"no write on load");
 selects()[1].value="STS-PRESENT";await choose();
 await one("معاينة التحضير").handlers.click();await flush();
 assert.equal(reviews.length,1,"one modal with the review list is opened");
 assert.equal(reviews[0].data.persons[0].name,"شخص تجريبي");
 assert.equal(reviews[0].data.persons[0].serviceNumberA,"12345");
 assert.equal(reviews[0].opts.confirmLabel,"تأكيد وحفظ التحضير");
 assert.equal(writes.length,0,"canceling review must not commit");
 assert.equal(root.all().filter(x=>x.className.includes("attendance-confirm-action")).length,0,
   "do not duplicate the review or confirmation inline below the roster");
 approved=true;
 await one("معاينة التحضير").handlers.click();await flush();
 assert.equal(writes.length,1,"user confirms directly in one modal");
 assert.equal(writes[0].payload.previewToken,"SERVER_ONE_TIME_TOKEN");
 assert.match(writes[0].payload.operationId,/^[A-Za-z0-9_-]{16,128}$/,
   "confirmed initial attendance has a durable operationId");
 await context.window.AltamamAttendance.render(root);
 const choices=selects();
 choices[1].value="STS-ABSENT";
 choices[2].value="revise";choices[2].handlers.change();
 assert.ok(one("معاينة تغيير الحالة"));
 assert.equal(root.all().filter(x=>x.tag==="input"&&
   !["checkbox","search"].includes(x.type)).length,0,
   "routine status revision must not require a reason; the search box stays usable");
 await choose();
 await one("معاينة تغيير الحالة").handlers.click();await flush();
 assert.equal(reviews.at(-1).opts.confirmLabel,"تأكيد تغيير الحالة");
 assert.equal(writes.length,2,"status change requires same preview and approval");
 assert.equal(writes[1].name,"ReviseTodayAttendance");
 assert.equal(writes[1].payload.previewToken,"SERVER_ONE_TIME_TOKEN");
 assert.match(writes[1].payload.operationId,/^[A-Za-z0-9_-]{16,128}$/,
   "confirmed revision has a durable operationId");
 assert.notEqual(writes[1].payload.operationId,writes[0].payload.operationId,
   "different user intents never reuse an operationId");
 await context.window.AltamamAttendance.render(root);
 selects()[1].value="STS-PRESENT";
 await choose();
 errorName="شخص تجريبي";
 const previous=reviews.length, committed=writes.length;
 await one("معاينة التحضير").handlers.click();await flush();
 assert.equal(reviews.length,previous,"failed preview must never open approval modal");
 assert.equal(writes.length,committed,"failed individual preview never commits");
 assert.ok(root.all().some(x=>x.textContent.includes("شخص تجريبي")&&
   x.textContent.includes("12345")),"rejection identifies the person and الرقم (ع)");
 // Search must reach the authorized QueryDailyRoster service before pagination.
  const termBox=()=>root.all().find(x=>x.tag==="input"&&x.type==="search");
  assert.ok(termBox(),"attendance selector has a direct search input");
  termBox().value="67890";
  one("بحث").handlers.click();await flush();
  assert.equal(calls.filter(x=>x.name==="QueryDailyRoster").at(-1).payload.term,"67890");
  one("مسح البحث").handlers.click();await flush();
  assert.equal(calls.filter(x=>x.name==="QueryDailyRoster").at(-1).payload.term,"");
 expandedRoster=true;errorName="";
  await context.window.AltamamAttendance.render(root);
  const dailyPanel=()=>root.all().find(x=>x.className.includes("daily-roster-panel"));
  const dailyRows=()=>dailyPanel().all().find(x=>x.tag==="tbody").children;
  assert.equal(dailyRows().length,100,"load 100 names into scrollable attendance table");
  one("التالي").handlers.click();await flush();
  assert.equal(dailyRows().length,12,"last attendance page contains twelve remaining names");
  assert.equal(calls.filter(x=>x.name==="QueryDailyRoster").at(-1).payload.page,2);
  one("السابق").handlers.click();await flush();
  assert.equal(dailyRows().length,100);
  assert.equal(writes.length,committed,"paging must never save attendance");

  // 04.03(b): cancel/back with a pending human selection requires
  // an explicit confirmation, and neither path may commit attendance.
  await choose();
  assert.ok(root.all().some(x=>x.textContent==="الأفراد المحددون: 1 من 100"));
  const cancelSelections=one("إلغاء التحديد");
  assert.ok(cancelSelections,"attendance has a visible Cancel selection action");
  await cancelSelections.handlers.click();await flush();
  assert.equal(confirmations,1);
  assert.equal(root.all().filter(x=>x.tag==="input"&&x.type==="checkbox")[0].checked,true,
    "refusing cancellation must preserve pending selections");
  cancelConfirmed=true;
  await cancelSelections.handlers.click();await flush();
  assert.equal(confirmations,2);
  assert.ok(root.all().some(x=>x.textContent==="الأفراد المحددون: 0 من 100"));
  assert.equal(writes.length,committed);
  await choose();
  cancelConfirmed=false;
  await one("العودة إلى إدارة القوة").handlers.click();await flush();
  assert.equal(navigations,0,"declined back confirmation must keep attendance editing open");
  cancelConfirmed=true;
  await one("العودة إلى إدارة القوة").handlers.click();await flush();
  assert.equal(navigations,1,"approved back confirmation may leave attendance");
  assert.equal(writes.length,committed,"cancel/back never saves attendance");
  console.log("ATTENDANCE UI PASS: 100-person roster, preview and confirmed pending-selection cancellation");
}
main().catch(e=>{console.error(e);process.exitCode=1;});
