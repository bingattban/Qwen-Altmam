"use strict";
// Real report-screen script executed with a minimal, deterministic DOM.
// Assert same-number people remain distinct across selection and group rendering.
const assert=require("node:assert/strict"),fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
class El{
  constructor(tag){this.tag=tag;this.children=[];this.handlers={};this.textContent="";
    this.className="";this.attrs={};this.value="";this.type="";this.disabled=false;this.isConnected=true;}
  get tBodies(){return this.children.filter(c=>c.tag==="tbody");}
  append(...parts){this.children.push(...parts);}
  appendChild(node){this.children.push(node);return node;}
  replaceChildren(...parts){this.children=parts;}
  addEventListener(event,callback){this.handlers[event]=callback;}
  setAttribute(name,value){this.attrs[name]=value;}
  all(){return [this,...this.children.flatMap(c=>c instanceof El?c.all():[])];}
}
const viewport=new El("main"),calls=[];
const records=[
  {personId:"OLD-ID",serviceNumberA:"5151",displayName:"مفصول سابق",active:false,
    organizationalStatus:3,siteName:"موقع سابق"},
  {personId:"NEW-ID",serviceNumberA:"5151",displayName:"فرد جديد مستقل",active:true,
    organizationalStatus:1,siteName:"الموقع الحالي"}
];
const bridge={
  invoke:async(action,payload)=>{
    calls.push({action,payload});
    if(action==="QueryPersons")
      return {data:{items:records,total:2}};
    if(action==="BuildPersonLifetimeReport"&&Array.isArray(payload.personIds))
      return {data:{
        mode:"group",personCount:2,dateFrom:payload.dateFrom,dateTo:payload.dateTo,
        attendanceTotal:2,movementTotal:1,
        statusTotals:[{statusId:"STS-ABSENT",statusName:"غياب",count:2}],
        persons:[
          {personId:"OLD-ID",serviceNumberA:"5151",name:"مفصول سابق",
            organizationalStatus:3,active:false,attendanceTotal:2,movementTotal:1,
            statusTotals:[{statusName:"غياب",count:2}]},
          {personId:"NEW-ID",serviceNumberA:"5151",name:"فرد جديد مستقل",
            organizationalStatus:1,active:true,attendanceTotal:0,movementTotal:0,
            statusTotals:[]}
        ]
      }};
    if(action==="BuildPersonLifetimeReport"&&payload.personId==="NEW-ID")
      return {data:{person:{name:"فرد جديد مستقل",serviceNumberA:"5151",
        active:true,firstRecordedDate:"2026-10-26"},
        statusTotals:[],monthlyTotals:[],attendance:[],attendancePage:1,
        attendanceTotal:0,attendanceHasNext:false,identityHistory:[],identityPage:1,
        identityTotal:0,identityHasNext:false,movements:[],movementPage:1,movementTotal:0,
        movementHasNext:false,audit:[],auditPage:1,auditTotal:0,auditHasNext:false}};
    throw Error("Unexpected report request "+action+" "+JSON.stringify(payload));
  }
};
const context={window:{AltamamBridge:bridge},document:{createElement:tag=>new El(tag)},console};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/js/personnel-report.js"),"utf8"),context);
const settle=()=>new Promise(resolve=>setImmediate(resolve));
const buttons=label=>viewport.all().filter(x=>x.tag==="button"&&x.textContent===label);
async function main(){
  context.window.AltamamReports.render(viewport,()=>{});
  const term=viewport.all().find(x=>x.tag==="input"&&x.type==="search");
  assert.ok(term,"search must be present in the ONE report screen");
  term.value="5151";
  assert.equal(buttons("بحث").length,1);
  buttons("بحث")[0].handlers.click();await settle();await settle();
  assert.equal(buttons("إضافة للمجموعة").length,2);
  buttons("إضافة للمجموعة")[0].handlers.click();
  buttons("إضافة للمجموعة")[0].handlers.click();
  assert.equal(buttons("عرض التقرير الجماعي").length,1);
  const fields=viewport.all().filter(x=>x.tag==="input"&&x.type==="date");
  assert.equal(fields.length,2);
  fields[0].value="2026-01-01";fields[1].value="2026-10-26";
  buttons("عرض التقرير الجماعي")[0].handlers.click();
  await settle();await settle();
  const request=calls.find(x=>x.action==="BuildPersonLifetimeReport"&&x.payload.personIds);
  assert.ok(request,"C# report service must be invoked for a group");
  assert.equal(request.payload.personIds.length,2);
  assert.equal(request.payload.personIds[0],"OLD-ID");
  assert.equal(request.payload.personIds[1],"NEW-ID");
  assert.equal(request.payload.dateFrom,"2026-01-01");
  assert.equal(request.payload.dateTo,"2026-10-26");
  assert.ok(viewport.all().some(x=>x.textContent==="فرد جديد مستقل"));
  assert.ok(viewport.all().some(x=>x.textContent==="مفصول سابق"));
  assert.ok(viewport.all().some(x=>x.textContent==="إجمالي التحضيرات المعتمدة: 2 — إجمالي الحركات: 1"));
  assert.equal(buttons("عرض سجل الفرد").length,2);
  buttons("عرض سجل الفرد")[1].handlers.click();
  await settle();await settle();
  assert.equal(calls.at(-1).payload.personId,"NEW-ID",
    "the new person must be loaded by immutable PersonId, never by 5151");
  assert.ok(viewport.all().some(x=>x.textContent==="الاسم: فرد جديد مستقل — الرقم (ع): 5151"));
  assert.ok(viewport.all().some(x=>x.textContent.includes("العودة إلى التقرير الجماعي")));
  console.log("UNIFIED REPORT UI PASS: person/group, historical vs new identity, exact PersonIds, date filter, drilldown");
}
main().catch(err=>{console.error(err);process.exitCode=1;});
