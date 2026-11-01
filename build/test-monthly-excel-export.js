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
  getAttribute(key){return this.attrs[key]||null;}
  focus(){document.activeElement=this;}
  querySelectorAll(){return this.all().filter(x=>["button","input","select","textarea"].includes(x.tag)&&!x.disabled&&!x.hidden);}
  removeAttribute(key){delete this.attrs[key];}
  all(){return [this,...this.children.flatMap(x=>x instanceof El?x.all():[])];}
}
const viewport=new El("main"),logoutAction=new El("button"),sidebar=new El("aside"),
  badge=new El("span"),events={},calls=[];
let deferMonthlyPreview=false,resolveMonthlyPreview=null;
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
    if(action==="PreviewMonthlyExport"){
      const reply={data:{personCount:1,year,month,siteId:payload.siteId,format:"xlsx"}};
      if(deferMonthlyPreview)
        return new Promise(resolve=>{resolveMonthlyPreview=()=>resolve(reply);});
      return reply;
    }
    if(action==="SetOutputDirectory")return{data:{directory:"E:\\\\MonthlyExports"}};
    if(action==="ExportMonthlyWorkbookSet")return{data:{
      filePath:"C:\\Users\\Test\\Documents\\AlTamam\\Exports\\sample.xlsx",
      format:"xlsx",personCount:1,fileCount:2,files:[
        {siteId:"",filePath:"C:\\Reports\\sample.xlsx",personCount:1},
        {siteId:"SITE-UNSPECIFIED",filePath:"C:\\Reports\\site.xlsx",personCount:1}
      ]}};
    if(action==="GetMonthlyCellDetails")return{data:{personId:payload.personId,businessDate:payload.businessDate,
      personName:"شخص اختباري",serviceNumberA:"X1",siteName:"غير محدد",
      attendance:{statusName:"غياب",symbol:"غ",committedAtUtc:"2026-10-19T07:00:00Z",
        actorUserId:"TST-U",revisionNumber:2,siteName:"غير محدد"},movement:null,
      revisions:[{beforeStatus:"موجود",afterStatus:"غياب",reason:"سبب اختبار",
        actorUserId:"TST-U",executedAtUtc:"2026-10-19T07:30:00Z"}]}};
    throw Error("Unexpected bridge action "+action);
  }
};
const document={activeElement:null,createElement:tag=>new El(tag),
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
  const dayCount=new Date(year,month,0).getDate();
  const firstVisible=today.getDate();
  const visibleCount=Math.min(12,dayCount-firstVisible+1);
  assert.equal(headers.length,4+visibleCount,
    "four identity columns and only the requested bounded calendar day window");
  for(let index=0;index<visibleCount;index++)
    assert.equal(Number.parseInt(String(headers[4+index].textContent),10),firstVisible+index,
      "day cells preserve chronological order inside the visible window");
  assert.ok(viewport.all().some(x=>x.className.includes("monthly-day-window-nav")),
    "other days are reachable without requesting or painting the full month");
  assert.ok(viewport.all().some(x=>x.className.includes("functional-paged-area")),
    "pager lives in fixed table panel rather than below the whole screen");
  const monthPanel=viewport.all().find(x=>x.className.includes("monthly-grid-panel"));
  assert.ok(viewport.className.includes("monthly-dashboard")&&monthPanel,
    "monthly page must use the full-height grid experience");
  assert.ok(monthPanel.children.some(x=>x.className.includes("functional-pager")),
    "previous/next remains visible outside the scrolling monthly table");
  assert.ok(viewport.all().some(x=>x.className.includes("monthly-archive-controls")),
    "compact month/year archive selector remains available below grid");
  const styles=fs.readFileSync(path.join(__dirname,
    "../src/AlTamam.WebUI/assets/css/altamam.css"),"utf8");
  assert.ok(styles.includes(".monthly-scroll .current-day-column"),
    "today's non-sticky chronological column remains highlighted");
  const search=viewport.all().find(x=>x.tag==="input"&&x.type==="search");
  assert.ok(search,"monthly screen has a real historical name/number search");
  search.value="EX٠٠١";
  const searchButton=viewport.all().find(x=>x.tag==="button"&&x.textContent==="بحث في الكشف");
  assert.ok(searchButton);
  searchButton.handlers.click();await tick();await tick();
  const lastFilteredGrid=calls.filter(x=>x.action==="QueryMonthlyGrid").at(-1);
  assert.equal(lastFilteredGrid.payload.pageSize,100,"monthly grid must use full 100-person pages");
  assert.equal(lastFilteredGrid.payload.term,"EX٠٠١");
  assert.equal(lastFilteredGrid.payload.page,1);
  assert.equal(lastFilteredGrid.payload.year,year);
  assert.equal(lastFilteredGrid.payload.month,month);
  const previewButton=viewport.all().find(x=>x.tag==="button"&&x.textContent==="معاينة تصدير Excel");
  assert.ok(previewButton,"monthly Excel preview is visible");
  const todayHeading=viewport.all().find(x=>x.tag==="th"&&
    x.className.includes("current-day-column"));
  assert.ok(todayHeading,"today stays at its actual month position");
  assert.ok(!todayHeading.className.includes("pinned"),
    "today is not fixed and must scroll both directions");
  assert.ok(todayHeading.textContent.includes("اليوم"),
    "current-day header must be clearly labeled");
  const visibleStatus=viewport.all().find(x=>x.tag==="td"&&
    x.style.backgroundColor==="#EF4444");
  assert.ok(visibleStatus,"committed attendance is color coded");
  assert.equal(visibleStatus.style.color,"#000000",
    "colored cells choose contrasting text instead of possibly invisible default ink");
  const css=fs.readFileSync(path.join(__dirname,
    "../src/AlTamam.WebUI/assets/css/altamam.css"),"utf8");
  assert.ok(css.includes(".monthly-scroll td.current-day-column"),
    "today is prominent without a sticky position");
  assert.ok(css.includes(".monthly-scroll .fixed-three{"),
    "identity text is allowed to wrap instead of being clipped");
  assert.equal(calls.some(x=>x.action==="ExportMonthlyWorkbookSet"),false,
    "initial display must not export");
  // Test-first: the pending preview must be dismissible before the Bridge
  // replies. A late result must not recreate the form or initiate an export.
  deferMonthlyPreview=true;
  previewButton.focus();
  const pendingPreview=previewButton.handlers.click();await tick();
  const loadingPanel=viewport.all().find(x=>x.className.includes("functional-export-preview"));
  assert.equal(loadingPanel.hidden,false,"preview becomes a real modal during loading");
  const loadingClose=loadingPanel.all().find(x=>x.tag==="button"&&
    x.textContent==="إغلاق المعاينة");
  assert.ok(loadingClose,"preview close is available before the server replies");
  assert.equal(document.activeElement,loadingClose,"loading modal moves focus to its close control");
  loadingClose.handlers.click();
  assert.equal(loadingPanel.hidden,true,"user can dismiss a pending Excel preview");
  assert.equal(document.activeElement,previewButton,"closing preview restores focus to its opener");
  resolveMonthlyPreview();await pendingPreview;await tick();
  assert.equal(loadingPanel.hidden,true,"late preview response cannot reopen dismissed modal");
  assert.ok(!loadingPanel.all().some(x=>x.textContent==="حفظ ملف Excel"),
    "late response cannot render saving controls after dismissal");
  assert.equal(calls.filter(x=>x.action==="ExportMonthlyWorkbookSet").length,0,
    "dismissing a pending preview never triggers a native export");
  deferMonthlyPreview=false;

  // A fresh deliberate click still loads the genuine preview and export.
  await previewButton.handlers.click();await tick();
  assert.equal(calls.filter(x=>x.action==="PreviewMonthlyExport").length,2);
  assert.equal(calls.filter(x=>x.action==="PreviewMonthlyExport").at(-1).payload.term,"EX٠٠١");
  assert.equal(calls.filter(x=>x.action==="ExportMonthlyWorkbookSet").length,0,
    "preview never writes file");
  const save=viewport.all().find(x=>x.tag==="button"&&x.textContent==="حفظ ملف Excel");
  assert.ok(save,"save is shown only after approved preview");
  const openPreviewPanel=viewport.all().find(x=>x.className.includes("functional-export-preview"));
  assert.ok(openPreviewPanel.handlers.keydown,"monthly export modal handles keyboard Escape");
  let trappedEscape=false;
  openPreviewPanel.handlers.keydown({key:"Escape",
    preventDefault(){trappedEscape=true;}});
  assert.equal(trappedEscape,true,"Escape is handled inside the preview");
  assert.equal(openPreviewPanel.hidden,true,"Escape dismisses an uncommitted preview");
  assert.equal(document.activeElement,previewButton,"Escape restores the preview opener focus");
  assert.equal(calls.filter(x=>x.action==="ExportMonthlyWorkbookSet").length,0);
  await previewButton.handlers.click();await tick();
  const saveAgain=viewport.all().find(x=>x.tag==="button"&&x.textContent==="حفظ ملف Excel");
  assert.ok(saveAgain,"reopening after keyboard dismissal starts a fresh preview");
  const pathPicker=viewport.all().find(x=>x.tag==="button"&&
    x.textContent==="اختيار مجلد الحفظ من Windows");
  assert.ok(pathPicker,"monthly export has a native Windows directory picker");
  await pathPicker.handlers.click();await tick();
  const chosenName=viewport.all().find(x=>x.tag==="input"&&
    x.attrs["aria-label"]==="اسم ملف التصدير الشهري");
  assert.ok(chosenName,"operator can propose the XLSX filename");
  chosenName.value="كشف مختار.xlsx";
    await saveAgain.handlers.click();await tick();
  const exported=calls.filter(x=>x.action==="ExportMonthlyWorkbookSet");
  assert.equal(exported.length,1,"save invokes real native Excel export exactly once");
  assert.equal(exported[0].payload.year,year);
  assert.equal(exported[0].payload.month,month);
  assert.equal(exported[0].payload.term,"EX٠٠١");
  assert.equal(exported[0].payload.siteId,lastFilteredGrid.payload.siteId);
  assert.equal(exported[0].payload.fileName,"كشف مختار.xlsx");
  assert.equal(exported[0].payload.directory,"E:\\\\MonthlyExports");
  const confirmation=viewport.all().map(x=>String(x.textContent)).join(" | ");
  assert.ok(confirmation.includes("sample.xlsx"),"completed export path is rendered");
  const exportDialog=viewport.all().find(x=>x.className.includes("functional-export-preview"));
  assert.equal(exportDialog.attrs["aria-modal"],"true","Excel preview is an actual modal");
  const dismiss=exportDialog.all().find(x=>x.tag==="button"&&x.textContent==="إغلاق المعاينة");
  assert.ok(dismiss,"successful export must always offer a close action");
  dismiss.handlers.click();
  assert.equal(exportDialog.hidden,true,"dismiss keeps the monthly grid in place");
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
  const overlay=viewport.all().find(x=>x.className.includes("auto-attendance-overlay"));
  assert.ok(overlay&&overlay.attrs["aria-modal"]==="true");
  assert.equal(details.hidden,false);
  assert.equal(overlay.hidden,false);
  const close=viewport.all().find(x=>x.tag==="button"&&x.textContent==="إغلاق التفاصيل");
  close.handlers.click();
  assert.equal(overlay.hidden,true,"close hides modal without touching attendance");
  const clear=viewport.all().find(x=>x.tag==="button"&&x.textContent==="مسح بحث الشهر");
  assert.ok(clear);
  clear.handlers.click();await tick();await tick();
  assert.equal(calls.filter(x=>x.action==="QueryMonthlyGrid").at(-1).payload.term,"");
  assert.equal(calls.filter(x=>x.action==="SetOutputDirectory").length,1,
    "folder selection occurs only on the explicit Windows-picker action");
  assert.equal(calls.filter(x=>!["GetCurrentSession","QuerySites","GetAvailableArchivePeriods",
    "QueryMonthlyGrid","GetMonthlyCellDetails","PreviewMonthlyExport","ExportMonthlyWorkbookSet",
    "GetSettingsOverview","GetDashboardSummary","SetOutputDirectory"].includes(x.action)).length,0,
    "read-only click must never commit attendance or modify history");
  console.log("MONTHLY EXCEL + MONTHLY CELL UI PASS: guarded preview, native export response, date-bound read-only details");
}
main().catch(e=>{console.error(e);process.exitCode=1;});
