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
    if(action==="QuerySites")
      return {data:[{siteId:"SITE-OLD",name:"الموقع السابق"},
        {siteId:"SITE-NEW",name:"الموقع الحالي"}]};
    if(action==="GetActiveAttendanceStatuses")
      return {data:[{statusId:"STS-ABSENT",name:"غياب"},
        {statusId:"STS-PRESENT",name:"موجود"}]};
    if(action==="BuildPersonLifetimeReport"&&payload.exportPreview===true)
      return {data:{fileName:"تقرير فرد_2026-10-26_aabbcc.xlsx",
       exportId:"feca3d1a-0123-4567-89ab-cdef98765432",directory:"C:\\Reports",personCount:1,
       worksheets:["ملخص التقرير","سجل التنقلات","تغييرات العمليات"]}};
    if(action==="ExportPersonLifetimeExcel")
      return {data:{filePath:"C:\\Reports\\تقرير فرد_2026-10-26_aabbcc.xlsx",format:"xlsx"}};
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
        attendanceTotal:0,attendanceHasNext:false,
        siteHistory:[{siteId:"SITE-NEW",siteName:"الموقع الحالي",
          from:"2026-10-26",to:null}],siteTotal:1,sitePage:1,siteHasNext:false,
        identityHistory:[],identityPage:1,
        identityTotal:0,identityHasNext:false,movements:[],movementPage:1,movementTotal:0,
        movementHasNext:false,audit:[],auditPage:1,auditTotal:0,auditHasNext:false}};
    throw Error("Unexpected report request "+action+" "+JSON.stringify(payload));
  }
};
const context={window:{AltamamBridge:bridge,
   AltamamDialog:{confirm:async()=>true}},document:{createElement:tag=>new El(tag)},console};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/js/personnel-report.js"),"utf8"),context);
const settle=()=>new Promise(resolve=>setImmediate(resolve));
const buttons=label=>viewport.all().filter(x=>x.tag==="button"&&x.textContent===label);
async function main(){
  context.window.AltamamReports.render(viewport,()=>{});
  await settle();await settle();
  const dropdowns=viewport.all().filter(x=>x.tag==="select");
  assert.equal(dropdowns.length,3,
    "site/status/movement dropdowns must be inside the one report screen");
  assert.ok(dropdowns[0].all().some(x=>x.textContent==="الموقع السابق"));
  assert.ok(dropdowns[1].all().some(x=>x.textContent==="غياب"));
  dropdowns[0].value="SITE-OLD";
  dropdowns[1].value="STS-ABSENT";
  dropdowns[2].value="3";
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
  const popup=viewport.all().find(x=>x.className==="person-report-modal");
  assert.ok(popup&&!popup.hidden,"group report must open in a modal, not inline");
  const request=calls.find(x=>x.action==="BuildPersonLifetimeReport"&&x.payload.personIds);
  assert.ok(request,"C# report service must be invoked for a group");
  assert.equal(request.payload.personIds.length,2);
  assert.equal(request.payload.personIds[0],"OLD-ID");
  assert.equal(request.payload.personIds[1],"NEW-ID");
  assert.equal(request.payload.dateFrom,"2026-01-01");
  assert.equal(request.payload.dateTo,"2026-10-26");
  assert.equal(request.payload.siteId,"SITE-OLD");
  assert.equal(request.payload.statusId,"STS-ABSENT");
  assert.equal(request.payload.movementType,3);
  assert.ok(viewport.all().some(x=>x.textContent==="فرد جديد مستقل"));
  assert.ok(viewport.all().some(x=>x.textContent==="مفصول سابق"));
  assert.ok(viewport.all().some(x=>x.textContent==="إجمالي التحضيرات المعتمدة: 2 — إجمالي الحركات: 1"));
  assert.equal(buttons("عرض سجل الفرد").length,2);
  buttons("عرض سجل الفرد")[1].handlers.click();
  await settle();await settle();
  assert.equal(calls.at(-1).payload.personId,"NEW-ID",
    "the new person must be loaded by immutable PersonId, never by 5151");
  assert.equal(calls.at(-1).payload.siteId,"SITE-OLD");
  assert.equal(calls.at(-1).payload.statusId,"STS-ABSENT");
  assert.equal(calls.at(-1).payload.movementType,3);
  assert.ok(viewport.all().some(x=>x.textContent==="الاسم: فرد جديد مستقل — الرقم (ع): 5151"));
  assert.ok(viewport.all().some(x=>x.textContent.includes("العودة إلى التقرير الجماعي")));
  assert.ok(viewport.all().some(x=>x.textContent==="تاريخ المواقع والفترات الفعلية"));
  const exportButton=buttons("تصدير التقرير إلى Excel")[0];
  assert.ok(exportButton,"modal must offer real XLSX export");
  exportButton.handlers.click();await settle();await settle();
  assert.ok(calls.some(x=>x.action==="BuildPersonLifetimeReport"&&x.payload.exportPreview),
    "preview must be fetched before export");
  const exported=calls.find(x=>x.action==="ExportPersonLifetimeExcel");
  assert.ok(exported&&exported.payload.personId==="NEW-ID");
  assert.equal(exported.payload.mode,"report");
  assert.equal(exported.payload.exportId,"feca3d1a-0123-4567-89ab-cdef98765432",
    "save must use the SAME server-proposed filename identifier");
  buttons("إغلاق التقرير")[0].handlers.click();
  assert.equal(popup.hidden,true,"close returns to existing multi-selection");
  console.log("UNIFIED REPORT UI PASS: person/group, historical vs new identity, exact PersonIds, date filter, drilldown");
}
main().catch(err=>{console.error(err);process.exitCode=1;});
