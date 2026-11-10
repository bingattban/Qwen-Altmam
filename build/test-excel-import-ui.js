"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs"),vm=require("node:vm"),path=require("node:path");
class Element {
  constructor(tag) { this.tag=tag;this.children=[];this.className="";this.textContent="";
    this.dataset={};this.attrs={};this.handlers={};this.disabled=false;this.style={};this.value="";}
  get classList() { return {add:(...a)=>{this.className+=" "+a.join(" ")},
    contains:x=>this.className.split(/\s+/).includes(x)}; }
  append(...parts){this.children.push(...parts);}
  appendChild(x){this.children.push(x);return x;}
  prepend(x){this.children.unshift(x);}
  replaceChildren(...parts){this.children=[...parts];}
  setAttribute(key,value){this.attrs[key]=value;}
  focus(){context.document.activeElement=this;}
  addEventListener(key,fn){this.handlers[key]=fn;}
  all(){return [this,...this.children.flatMap(c=>c instanceof Element?c.all():[])];}
}
const viewport=new Element("main"),calls=[];
let committed=false,delayOutcome=false,finishOutcome=null;
let nextImportCommitError=null,overrideImportReceipt=null;
const sampleBatch={importBatchId:"IMP-TEST-1",fileName:"كشف_القوة.xlsx",
  totalRows:3,acceptedRows:2,rejectedRows:1,status:"COMMITTED",
  committedAtUtc:"2026-10-23T20:00:00Z"};
const responses={
  SelectImportWorkbook:{workbookToken:"A".repeat(64),rowCount:3,maxRows:500},
  AnalyzeImport:{totalRows:3,newCount:2,issueCount:1,
    issues:[{row:4,name:"مكرر",serviceNumberA:"001",reason:"الرقم موجود"}],
    acceptedPreview:[{worksheet:"الورقة الأولى",row:2,name:"أحمد صالح",serviceNumberA:"002",siteName:"غير محدد"}],
    newSites:[{name:"موقع مستورد جديد",personCount:1}],
    similarNames:[{worksheet:"الورقة الأولى",row:2,name:"أحمد صالح",serviceNumberA:"002",
      matches:[{personId:"OLD-ID",name:"أحمد صالح",serviceNumberA:"0001",active:false}]}]},
  PreviewImportPlan:{previewToken:"B".repeat(64),newCount:2,issueCount:1,
    requiresExplicitConfirmation:true,issues:[],
    newSites:[{name:"موقع مستورد جديد",personCount:1}],
    acceptedPreview:[{name:"أحمد صالح",serviceNumberA:"002"},
      {name:"حسن سعيد",serviceNumberA:"003"}]},
  CommitImport:{createdCount:2,createdSitesCount:1,importBatchId:"IMP-TEST-1",rejectedCount:1},
  ExportImportIssues:{format:"xlsx",filePath:"C:/Users/Test/Documents/AlTamam/Exports/issues.xlsx"}
};
const bridge={invoke:async (name,body)=>{
  calls.push({name,body});
  if(name==="QueryImportOutcome"){
    const data=body?.importBatchId?
      {batch:sampleBatch,issues:[{row:4,name:"مكرر",serviceNumberA:"001",
        reason:"الرقم موجود"}]}:
      {batches:committed?[sampleBatch]:[]};
    if(body?.importBatchId&&delayOutcome)
      return new Promise(resolve=>{finishOutcome=()=>resolve({data});});
    return {data};
  }
  if(!(name in responses)) throw Error("Unexpected bridge call "+name);
  if(name==="CommitImport"){
    if(nextImportCommitError){const e=nextImportCommitError;nextImportCommitError=null;throw e;}
    committed=true;
    if(overrideImportReceipt){const value=overrideImportReceipt;overrideImportReceipt=null;return {data:value};}
  }
  return {data:responses[name]};
}};
class FakeFileReader{
  readAsDataURL(file){
    this.result="data:application/vnd.openxmlformats-officedocument.spreadsheetml.sheet;base64,"+file.base64;
    queueMicrotask(()=>this.onload());
  }
}
let allowImport=false;
const importReviews=[];
const context={window:{AltamamBridge:bridge,AltamamDialog:{
  detailsReview:async(data,opts)=>{importReviews.push({data,opts});return allowImport;}
}},
  document:{activeElement:null,getElementById:x=>x==="appViewport"?viewport:null,
    createElement:tag=>new Element(tag)},
  FileReader:FakeFileReader,console,confirm:()=>true,
  alert:()=>{throw Error("unexpected alert")},
  prompt:()=>{throw Error("unexpected prompt")}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/js/admin-settings.js"),"utf8"),context);
const all=()=>viewport.all(), button=name=>all().find(x=>x.tag==="button"&&x.textContent===name);
async function run(){
  const admin={role:"GeneralAdministration",username:"test"};
  context.window.AltamamAdminSettings.renderAdmin(admin);
  all().find(x=>x.dataset.adminSection==="sheets").handlers.click();
  all().find(x=>x.dataset.adminScreen==="import").handlers.click();
  assert.ok(button("تحليل الملف"),"real XLSX importer should replace placeholder");
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(calls.filter(c=>c.name==="QueryImportOutcome").length,1,
    "opening screen only requests a read-only import history");
  assert.equal(calls.some(c=>c.name==="CommitImport"),false,
    "opening screen never imports a person");
  const picker=all().find(x=>x.tag==="input"&&x.type==="file");
  assert.ok(picker.accept.includes(".xlsx"));
  picker.files=[{name:"كشف_القوة.xlsx",size:80,base64:"UEsDBBQ"}];
  await button("تحليل الملف").handlers.click();
  assert.deepEqual(calls.filter(c=>c.name!=="QueryImportOutcome").map(x=>x.name),
    ["SelectImportWorkbook","AnalyzeImport"]);
  assert.ok(all().some(x=>x.textContent.includes("الصفوف المستبعدة")));
  assert.ok(all().some(x=>x.textContent.includes("مواقع جديدة")));
  assert.ok(all().some(x=>x.textContent.includes("تشابه أسماء")));
  assert.ok(all().some(x=>x.textContent.includes("الأفراد المقبولون")));
  await button("معاينة خطة الإضافة").handlers.click();
  assert.equal(calls.filter(x=>x.name==="CommitImport").length,0,
    "server-side preview must not persist rows");
  assert.equal(importReviews.length,1,"only the final batch confirmation is required");
  assert.equal(importReviews[0].data.items.length,2,"modal shows both accepted identities");
  assert.ok(importReviews[0].data.rows.some(row=>row[0]==="مواقع جديدة ستُنشأ تلقائيا"&&row[1]===1));
  assert.ok(importReviews[0].data.rows.some(row=>row[0]==="أسماء المواقع الجديدة"&&
    row[1]==="موقع مستورد جديد"));
  allowImport=true;
  await button("معاينة خطة الإضافة").handlers.click();
  assert.equal(calls.filter(x=>x.name==="CommitImport").length,1);
  assert.equal(calls.find(x=>x.name==="CommitImport").body.previewToken,"B".repeat(64));
  assert.equal(calls.find(x=>x.name==="CommitImport").body.confirmNewSites,undefined,
    "signed approved import plan automatically creates sites");
  assert.equal(calls.find(x=>x.name==="CommitImport").body.approvedSiteNames,undefined);
  assert.ok(all().some(x=>x.textContent.includes("تم إنشاء 2 فرداً جديداً")));
  assert.ok(all().some(x=>x.textContent.includes("وإنشاء 1 موقع جديد")));
  assert.ok(button("عرض الدفعة"),"saved history includes the new batch");
  await button("عرض الدفعة").handlers.click();
  assert.ok(button("حفظ تقرير الأخطاء Excel"),"rejected row report is available");
  assert.ok(all().some(x=>x.textContent.includes("الرقم موجود")));
  await button("حفظ تقرير الأخطاء Excel").handlers.click();
  assert.equal(calls.filter(x=>x.name==="ExportImportIssues").length,1);
  assert.equal(calls.find(x=>x.name==="ExportImportIssues").body.importBatchId,"IMP-TEST-1");
  assert.ok(all().some(x=>x.textContent.includes("issues.xlsx")));

  // A pending batch-details read must provide immediate close, allow Escape,
  // restore the original action focus, and never revive stale results after close.
  const outcome=all().find(x=>x.attrs["aria-label"]==="تفاصيل الدفعة المستوردة");
  assert.equal(outcome.attrs["aria-modal"],"true","batch details is an accessible modal");
  const dismiss=outcome.all().find(x=>x.tag==="button"&&x.textContent==="✕ إغلاق");
  assert.ok(dismiss);
  outcome.handlers.keydown({key:"Escape",preventDefault(){}});
  assert.equal(outcome.children.length,0,"Escape dismisses batch details");
  delayOutcome=true;
  const openOutcome=button("عرض الدفعة");
  openOutcome.focus();openOutcome.handlers.click();
  assert.ok(finishOutcome,"server outcome read is still pending");
  const pendingClose=outcome.all().find(x=>x.tag==="button"&&x.textContent==="✕ إغلاق");
  assert.ok(pendingClose,"the close button exists before the server replies");
  pendingClose.handlers.click();
  assert.equal(outcome.children.length,0,"close works during pending response");
  assert.equal(context.document.activeElement,openOutcome,"focus restored to batch row action");
  finishOutcome();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(outcome.children.length,0,"late response does not redraw a dismissed batch");
  delayOutcome=false;
  picker.files=[{name:"too-large.xlsx",size:163_841,base64:"A"}];
  const before=calls.length;
  await button("تحليل الملف").handlers.click();
  assert.equal(calls.length,before,"oversize file must never enter the bridge");
  // Rejected-only XLSX is analyzable and may be archived with explicit
  // approval, without creating or returning any person in SQLCipher.
  responses.AnalyzeImport={totalRows:2,newCount:0,issueCount:2,
    issues:[{row:2,name:"مرفوض",serviceNumberA:"12",reason:"رقم غير صالح"}],
    acceptedPreview:[],newSites:[],similarNames:[]};
  responses.PreviewImportPlan={previewToken:"C".repeat(64),newCount:0,
    issueCount:2,requiresExplicitConfirmation:true,newSites:[],
    issues:responses.AnalyzeImport.issues,acceptedPreview:[]};
  responses.CommitImport={createdCount:0,createdSitesCount:0,rejectedCount:2,importBatchId:"IMP-REJECT-ONLY"};
  picker.files=[{name:"مرفوض_كله.xlsx",size:80,base64:"UEsDBBQ"}];
  await button("تحليل الملف").handlers.click();
  assert.ok(button("معاينة اعتماد صفوف الرفض فقط"));
  await button("معاينة اعتماد صفوف الرفض فقط").handlers.click();
  assert.equal(calls.filter(x=>x.name==="CommitImport").at(-1).body.previewToken,
    "C".repeat(64));
  assert.ok(all().some(x=>x.textContent.includes("تم حفظ صفوف الرفض فقط")));
  // Definitive stale plans may not be replayed after a native rejection.
 await button("تحليل الملف").handlers.click();
 nextImportCommitError=Object.assign(new Error("تغيرت خطة الاستيراد."),
   {errorCode:"ImportPlanStale"});
 await button("معاينة اعتماد صفوف الرفض فقط").handlers.click();
 assert.ok(all().some(x=>x.textContent.includes("تغيرت خطة الاستيراد")));
 assert.equal(button("إعادة معاينة واعتماد الخطة نفسها"),undefined,
   "definitively rejected plan cannot be retried as a valid token");

 // A successful transport response without a batch receipt is uncertain.
 overrideImportReceipt={createdCount:0,importBatchId:""};
 await button("معاينة اعتماد صفوف الرفض فقط").handlers.click();
 assert.equal(button("إعادة معاينة واعتماد الخطة نفسها"),undefined);
 assert.ok(all().some(x=>x.textContent.includes("لا يمكن تأكيد نتيجة الاستيراد")),
   "missing receipt is never displayed as a successful committed batch");

 // Ambiguous timeout may replay ONLY the originally confirmed intent.
 nextImportCommitError=Object.assign(new Error("انتهت مهلة الاستجابة."),
   {errorCode:"BridgeTimeout"});
 await button("معاينة اعتماد صفوف الرفض فقط").handlers.click();
 const retry=button("إعادة معاينة واعتماد الخطة نفسها");
 assert.ok(retry,"lost response permits an idempotent retry");
 const attempts=calls.filter(x=>x.name==="CommitImport");
 const original=attempts.at(-1).body;
 await retry.handlers.click();
 const after=calls.filter(x=>x.name==="CommitImport");
 assert.equal(after.length,attempts.length+1);
 assert.equal(after.at(-1).body.previewToken,original.previewToken);
 assert.equal(after.at(-1).body.operationId,original.operationId,
   "retry keeps the original immutable operation ID");
 assert.ok(all().some(x=>x.textContent.includes("تم حفظ صفوف الرفض فقط")));
 console.log("EXCEL IMPORT UI PASS: safe history, stale rejection, real receipt, idempotent retry and size guard");
}
run().catch(e=>{console.error(e);process.exitCode=1;});
