"use strict";
const assert=require("node:assert/strict"),fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
class El{
  constructor(tag){this.tag=tag;this.children=[];this.handlers={};this.attrs={};
    this.className="";this.textContent="";this.style={};this.value="";this.hidden=false;
    this.dataset={};this.disabled=false;}
  get classList(){return{add:(...names)=>{this.className+=" "+names.join(" ")},
    toggle:(name,on)=>{const parts=this.className.split(/\s+/).filter(Boolean).filter(x=>x!==name);
      if(on)parts.push(name);this.className=parts.join(" ");}};}
  get tBodies(){return this.children.filter(x=>x.tag==="tbody");}
  append(...nodes){this.children.push(...nodes);}
  appendChild(node){this.children.push(node);return node;}
  replaceChildren(...nodes){this.children=nodes;}
  addEventListener(name,fn){this.handlers[name]=fn;}
  setAttribute(key,value){this.attrs[key]=value;}
  removeAttribute(key){delete this.attrs[key];}
  all(){return [this,...this.children.flatMap(x=>x instanceof El?x.all():[])];}
}
const viewport=new El("main"),logoutAction=new El("button"),sidebar=new El("aside"),
  badge=new El("span"),events={},calls=[];
const today=new Date(),year=today.getFullYear(),month=today.getMonth()+1;
const firstDay=year+"-"+String(month).padStart(2,"0")+"-01";
const bridge={hasBridge:()=>true,onInvalidation:()=>{},setSessionNonce:()=>{},
  invoke:async(action,payload)=>{
    calls.push({action,payload});
    if(action==="GetCurrentSession")return{data:{sessionNonce:"A".repeat(32),username:"test",role:"Supervisor"}};
    if(action==="GetSettingsOverview")return{data:{backupDirectory:"E:\\\\AlTamamAlYawmi",defaultDriveAvailable:true}};
    if(action==="QuerySites")return{data:[{siteId:"SITE-UNSPECIFIED",name:"غير محدد",active:true}]};
    if(action==="GetAvailableArchivePeriods")return{data:[firstDay.slice(0,7)]};
    if(action==="QueryMonthlyGrid")return{data:{total:1,page:1,items:[
      {personId:"TST-P",serviceNumberA:"X1",name:"شخص اختباري",siteName:"غير محدد",
       days:{[firstDay]:{symbol:"غ",name:"غياب",color:"#EF4444"}}}]}};
    if(action==="PreviewMonthlyExport")return{data:{personCount:1,year,month,siteId:payload.siteId,format:"xlsx"}};
    if(action==="ExportMonthlyWorkbookSet")return{data:{filePath:"C:\\Users\\Test\\Documents\\AlTamam\\Exports\\sample.xlsx",format:"xlsx",personCount:1}};
    if(action==="GetMonthlyCellDetails")return{data:{personId:payload.personId,businessDate:payload.businessDate,
      personName:"شخص اختباري",serviceNumberA:"X1",siteName:"غير محدد",
      attendance:{statusName:"غياب",symbol:"غ",committedAtUtc:"2026-10-19T07:00:00Z",
        actorUserId:"TST-U",revisionNumber:2,siteName:"غير محدد"},movement:null,
      revisions:[{beforeStatus:"موجود",afterStatus:"غياب",reason:"سبب اختبار",
        actorUserId:"TST-U",executedAtUtc:"2026-10-19T07:30:00Z"}]}};
    throw Error("Unexpected bridge action "+action);
  }
};
const document={createElement:tag=>new El(tag),
  getElementById:id=>({appViewport:viewport,headerRoleBadge:badge,logoutAction,
    "navBtn-dashboard":new El("button"),"navBtn-monthly":new El("button"),
    "navBtn-admin":new El("button"),"navBtn-settings":new El("button")})[id]||null,
  querySelector:selector=>selector===".app-sidebar"?sidebar:null,
  addEventListener:(name,fn)=>{events[name]=fn;}};
const context={window:{AltamamBridge:bridge,AltamamNavigation:{navigateTop:()=>{}}},
  document,location:{hash:"#monthly"},history:{replaceState:()=>{}},Date,Intl,console};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/js/functional-screens.js"),"utf8"),context);
const tick=()=>new Promise(resolve=>setImmediate(resolve));
async function main(){
  await events.DOMContentLoaded();await tick();await tick();
  const headers=viewport.all().filter(x=>x.tag==="th");
  assert.ok(headers.length>5,"monthly roster has fixed columns and calendar days");
  assert.equal(Number.parseInt(String(headers[4].textContent),10),
    new Date().getDate(),"today appears immediately after 4 fixed columns");
  assert.ok(headers[4].className.includes("fixed-today"),
    "today stays pinned while the other days scroll horizontally");
  assert.ok(viewport.all().some(x=>x.className.includes("functional-paged-area")),
    "pager lives in fixed table panel rather than below the whole screen");
  const styles=fs.readFileSync(path.join(__dirname,
    "../src/AlTamam.WebUI/assets/css/altamam.css"),"utf8");
  assert.ok(styles.includes(".monthly-scroll .fixed-today"),
    "today's column has an explicit sticky position");
  const previewButton=viewport.all().find(x=>x.tag==="button"&&x.textContent==="معاينة تصدير Excel");
  assert.ok(previewButton,"monthly Excel preview is visible");
  const todayHeading=viewport.all().find(x=>x.tag==="th"&&
    x.className.includes("fixed-today"));
  assert.ok(todayHeading,"today is pinned beside the static identity/site columns");
  assert.ok(todayHeading.textContent.includes("اليوم"),
    "current-day header must be clearly labeled");
  const visibleStatus=viewport.all().find(x=>x.tag==="td"&&
    x.style.backgroundColor==="#EF4444");
  assert.ok(visibleStatus,"committed attendance is color coded");
  assert.equal(visibleStatus.style.color,"#17212B",
    "colored cells choose contrasting text instead of possibly invisible default ink");
  const css=fs.readFileSync(path.join(__dirname,
    "../src/AlTamam.WebUI/assets/css/altamam.css"),"utf8");
  assert.ok(css.includes(".monthly-scroll td.fixed-today{font-size:17px"),
    "pinned today has readable, enlarged attendance text");
  assert.ok(css.includes(".monthly-scroll .fixed-three{"),
    "identity text is allowed to wrap instead of being clipped");
  assert.equal(calls.some(x=>x.action==="ExportMonthlyWorkbookSet"),false,
    "initial display must not export");
  await previewButton.handlers.click();await tick();
  assert.equal(calls.filter(x=>x.action==="PreviewMonthlyExport").length,1);
  assert.equal(calls.filter(x=>x.action==="ExportMonthlyWorkbookSet").length,0,
    "preview never writes file");
  const save=viewport.all().find(x=>x.tag==="button"&&x.textContent==="حفظ ملف Excel");
  assert.ok(save,"save is shown only after approved preview");
  await save.handlers.click();await tick();
  const exported=calls.filter(x=>x.action==="ExportMonthlyWorkbookSet");
  assert.equal(exported.length,1,"save invokes real native Excel export exactly once");
  assert.equal(exported[0].payload.year,year);
  assert.equal(exported[0].payload.month,month);
  const confirmation=viewport.all().map(x=>String(x.textContent)).join(" | ");
  assert.ok(confirmation.includes("sample.xlsx"),"completed export path is rendered");
  const open=viewport.all().find(x=>x.tag==="button"&&x.className.includes("monthly-day-detail"));
  assert.ok(open,"one committed cell has a detail button");
  assert.ok(open.attrs["aria-label"]?.includes(firstDay),"accessible date label");
  assert.equal(calls.filter(x=>x.action==="GetMonthlyCellDetails").length,0,
    "open page must not request details until click");
  await open.handlers.click();await tick();
  const call=calls.find(x=>x.action==="GetMonthlyCellDetails");
  assert.equal(call.payload.personId,"TST-P");
  assert.equal(call.payload.businessDate,firstDay);
  assert.equal(call.payload.siteId,"");
  const output=viewport.all().map(x=>String(x.textContent)).join(" | ");
  for(const text of ["التحضير المعتمد", "غياب", "تاريخ تعديل التحضير", "سبب اختبار"])
    assert.ok(output.includes(text),"must display authoritative "+text);
  const details=viewport.all().find(x=>x.className.includes("monthly-cell-details"));
  assert.equal(details.hidden,false);
  const close=viewport.all().find(x=>x.tag==="button"&&x.textContent==="إغلاق التفاصيل");
  close.handlers.click();
  assert.equal(details.hidden,true,"close hides details without touching attendance");
  assert.equal(calls.filter(x=>!["GetCurrentSession","QuerySites","GetAvailableArchivePeriods",
    "QueryMonthlyGrid","GetMonthlyCellDetails","PreviewMonthlyExport","ExportMonthlyWorkbookSet",
    "GetSettingsOverview"].includes(x.action)).length,0,
    "read-only click must never commit attendance or modify history");
  console.log("MONTHLY EXCEL + MONTHLY CELL UI PASS: guarded preview, native export response, date-bound read-only details");
}
main().catch(e=>{console.error(e);process.exitCode=1;});
