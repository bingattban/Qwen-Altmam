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
  addEventListener(key,fn){this.handlers[key]=fn;}
  all(){return [this,...this.children.flatMap(c=>c instanceof Element?c.all():[])];}
}
const viewport=new Element("main"),calls=[];
const responses={
  SelectImportWorkbook:{workbookToken:"A".repeat(64),rowCount:3,maxRows:500},
  AnalyzeImport:{totalRows:3,newCount:2,issueCount:1,
    issues:[{row:4,name:"مكرر",serviceNumberA:"001",reason:"الرقم موجود"}],
    acceptedPreview:[{name:"أحمد صالح",serviceNumberA:"002"}]},
  PreviewImportPlan:{previewToken:"B".repeat(64),newCount:2,issueCount:1,
    requiresExplicitConfirmation:true,issues:[],
    acceptedPreview:[{name:"أحمد صالح",serviceNumberA:"002"},
      {name:"حسن سعيد",serviceNumberA:"003"}]},
  CommitImport:{createdCount:2}
};
const bridge={invoke:async (name,body)=>{
  calls.push({name,body});
  if(!(name in responses)) throw Error("Unexpected bridge call "+name);
  return {data:responses[name]};
}};
class FakeFileReader{
  readAsDataURL(file){
    this.result="data:application/vnd.openxmlformats-officedocument.spreadsheetml.sheet;base64,"+file.base64;
    queueMicrotask(()=>this.onload());
  }
}
const context={window:{AltamamBridge:bridge},
  document:{getElementById:x=>x==="appViewport"?viewport:null,
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
  assert.equal(calls.length,0,"opening screen never imports a person");
  const picker=all().find(x=>x.tag==="input"&&x.type==="file");
  assert.ok(picker.accept.includes(".xlsx"));
  picker.files=[{name:"كشف_القوة.xlsx",size:80,base64:"UEsDBBQ"}];
  await button("تحليل الملف").handlers.click();
  assert.deepEqual(calls.map(x=>x.name),["SelectImportWorkbook","AnalyzeImport"]);
  assert.ok(all().some(x=>x.textContent.includes("الصفوف المستبعدة")));
  await button("معاينة خطة الإضافة").handlers.click();
  assert.equal(calls.filter(x=>x.name==="CommitImport").length,0,
    "server-side preview must not persist rows");
  assert.ok(button("تأكيد استيراد الأفراد الجدد"));
  await button("تأكيد استيراد الأفراد الجدد").handlers.click();
  assert.equal(calls.filter(x=>x.name==="CommitImport").length,1);
  assert.equal(calls.at(-1).body.previewToken,"B".repeat(64));
  assert.ok(all().some(x=>x.textContent.includes("تم إنشاء 2 فرداً جديداً")));
  picker.files=[{name:"too-large.xlsx",size:163_841,base64:"A"}];
  const before=calls.length;
  await button("تحليل الملف").handlers.click();
  assert.equal(calls.length,before,"oversize file must never enter the bridge");
  console.log("EXCEL IMPORT UI PASS: no implicit write, select/analyze/preview/commit, server token, size guard");
}
run().catch(e=>{console.error(e);process.exitCode=1;});
