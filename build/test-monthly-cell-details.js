"use strict";
const assert=require("node:assert/strict"),fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
class El{
  constructor(tag){this.tag=tag;this.children=[];this.handlers={};this.attrs={};
    this.className="";this.textContent="";this.style={};this.value="";this.hidden=false;
    this.dataset={};this.disabled=false;this.isConnected=true;this.lastScroll=null;}
  get classList(){return{add:(...names)=>{this.className+=" "+names.join(" ")},
    toggle:(name,on)=>{const parts=this.className.split(/\s+/).filter(Boolean).filter(x=>x!==name);
      if(on)parts.push(name);this.className=parts.join(" ");}};}
  get tBodies(){return this.children.filter(x=>x.tag==="tbody");}
  getBoundingClientRect(){
    if(this.className.includes("monthly-scroll"))return{left:0,right:1180,width:1180};
    if(this.className.includes("pinned"))return{left:0,right:0,width:120};
    if(this.className.includes("current-day-column"))return{left:140,right:200,width:60};
    return{left:0,right:60,width:60};
  }
  querySelector(selector){return this.all().find(x=>x!==this &&
    x.className.split(/\s+/).includes(selector.slice(1)))||null;}
  querySelectorAll(selector){
    if(selector==="thead .pinned")return this.all().filter(x=>x.tag==="th"&&
      x.className.split(/\s+/).includes("pinned"));
    return [];
  }
  scrollBy(options){this.lastScroll=options;}
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
const older=new Date(year,month-2,1),oldYear=older.getFullYear(),
  oldMonth=older.getMonth()+1;
const oldPeriod=oldYear+"-"+String(oldMonth).padStart(2,"0");
const bridge={hasBridge:()=>true,onInvalidation:()=>{},setSessionNonce:()=>{},
  invoke:async(action,payload)=>{
    calls.push({action,payload});
    if(action==="GetCurrentSession")return{data:{sessionNonce:"A".repeat(32),username:"test",role:"Supervisor"}};
    if(action==="QuerySites")return{data:[{siteId:"SITE-UNSPECIFIED",name:"غير محدد",active:true}]};
    if(action==="GetAvailableArchivePeriods")return{data:[firstDay.slice(0,7),oldPeriod]};
    if(action==="GetDashboardSummary")return{data:{totalForce:7}};
    if(action==="QueryMonthlyGrid"){
      const chosen=payload.year+"-"+String(payload.month).padStart(2,"0")+"-01";
      return{data:{total:1,page:payload.page,items:[
        {personId:"TST-P",serviceNumberA:"X1",name:"شخص اختباري",siteName:"غير محدد",
         days:{[chosen]:{symbol:"غ",name:"غياب",color:"#EF4444"}}}]}};
    }
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
const context={window:{AltamamBridge:bridge,AltamamNavigation:{navigateTop:()=>{}},
  requestAnimationFrame:fn=>fn()},
  document,location:{hash:"#monthly"},history:{replaceState:()=>{}},Date,Intl,console};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/js/functional-screens.js"),"utf8"),context);
const tick=()=>new Promise(resolve=>setImmediate(resolve));
async function main(){
  await events.DOMContentLoaded();await tick();await tick();
  const scroller=viewport.all().find(x=>x.className.includes("monthly-scroll"));
  assert.ok(scroller,"monthly scroll region must exist");
  assert.ok(viewport.className.includes("monthly-dashboard"),
    "monthly roster must activate the full-screen layout");
  const monthPanel=viewport.all().find(x=>x.className.includes("monthly-grid-panel"));
  assert.ok(monthPanel&&monthPanel.className.includes("functional-paged-area"),
    "monthly grid uses the remaining viewport and keeps pagination attached");
  assert.equal(monthPanel.children[0],scroller,"calendar scroll stays inside the monthly grid panel");
  assert.ok(monthPanel.children.some(x=>x.className.includes("functional-pager")),
    "month page buttons stay outside the vertically/horizontally scrollable table");
  assert.ok(viewport.all().some(x=>x.className.includes("monthly-archive-controls")),
    "month archive controls retain their own accessible compact footer");
  const dayHeaders=viewport.all().filter(x=>x.tag==="th" &&
    x.className.split(/\s+/).includes("day-column"));
  const numberOfDays=new Date(year,month,0).getDate();
  assert.equal(dayHeaders.length,numberOfDays);
  dayHeaders.forEach((head,i)=>assert.equal(parseInt(head.textContent,10),i+1,
    "day columns must stay in actual chronological order"));
  const marked=dayHeaders.filter(x=>x.className.includes("current-day-column"));
  assert.equal(marked.length,1,"exactly one column marks today");
  assert.equal(parseInt(marked[0].textContent,10),today.getDate());
  assert.ok(!marked[0].className.includes("pinned"),
    "today must travel with its chronological day, not be fixed");
  assert.ok(scroller.lastScroll && Number.isFinite(scroller.lastScroll.left),
    "today must be adjacent to the last pinned core column at initial render");
  assert.equal(scroller.lastScroll.left,-500,
    "today must align beside fixed columns (right 1180 - fixed 480 - half day 30), not centered");
  const monthlyCss=fs.readFileSync(path.join(__dirname,
    "../src/AlTamam.WebUI/assets/css/altamam.css"),"utf8");
  assert.match(monthlyCss,/#appViewport\.monthly-dashboard>\.monthly-grid-panel\s*\{/,
    "monthly panel must fill the available screen instead of a 41-58vh cap");
  assert.match(monthlyCss,/#appViewport\.monthly-dashboard>\.monthly-grid-panel>\.monthly-scroll\s*\{/,
    "month table must own the scrolling area, keeping month-day alignment");
  assert.match(monthlyCss,/\.monthly-scroll \.functional-table thead th\.day-column\s*\{/,
    "readable calendar headers preserve weekday/newline layout");
  assert.match(monthlyCss,/#appViewport\.monthly-dashboard \.monthly-scroll \.functional-table thead th\.pinned\s*\{/,
    "fixed identity headers keep strong contrast and color");
  const activeCount=viewport.all().find(x=>x.textContent==="إجمالي القوة النشطة: 7");
  const monthCount=viewport.all().find(x=>x.textContent==="أفراد كشف الشهر الظاهرون: 1");
  assert.ok(activeCount&&monthCount,
    "monthly current period must disclose active force and visible roster separately");
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
  assert.ok(overlay&&overlay.attrs["aria-modal"]==="true","details open in an accessible modal");
  assert.equal(overlay.hidden,false);
  assert.equal(details.hidden,false);
  const close=viewport.all().find(x=>x.tag==="button"&&x.textContent==="إغلاق التفاصيل");
  close.handlers.click();
  assert.equal(overlay.hidden,true,"close hides detail modal without touching attendance");
  // Browse the previous month without losing the fixed columns, footer, or
  // chronological day order. The archive may have a roster but no preparations.
  const previousMonth=viewport.all().find(x=>x.tag==="button"&&x.textContent==="الشهر السابق");
  const followingMonth=viewport.all().find(x=>x.tag==="button"&&x.textContent==="الشهر التالي");
  assert.ok(previousMonth&&!previousMonth.disabled);
  assert.ok(followingMonth&&followingMonth.disabled,"future months disabled from current month");
  previousMonth.handlers.click();await tick();await tick();
  const earlierCall=calls.filter(x=>x.action==="QueryMonthlyGrid").at(-1);
  assert.equal(earlierCall.payload.pageSize,100,"monthly history must load 100 names per page");
  assert.equal(earlierCall.payload.year,oldYear);
  assert.equal(earlierCall.payload.month,oldMonth);
  assert.equal(earlierCall.payload.page,1);
  const earlierDays=viewport.all().filter(x=>x.tag==="th"&&
    x.className.split(/\s+/).includes("day-column"));
  assert.equal(earlierDays.length,new Date(oldYear,oldMonth,0).getDate());
  assert.equal(earlierDays.filter(x=>x.className.includes("current-day-column")).length,0,
    "historic calendar must not mark a fake current day");
  const visiblePager=viewport.all().find(x=>x.className.includes("functional-pager"));
  assert.ok(visiblePager,"monthly prev/next pages must remain visible outside table scrolling");
  assert.ok(visiblePager.all().some(x=>x.textContent==="السابق"));
  assert.ok(visiblePager.all().some(x=>x.textContent==="التالي"));
  assert.ok(visiblePager.all().some(x=>String(x.textContent).includes("صفحة 1 من 1")),
    "month pagination shows current page and total pages in the fixed footer");
  const sizeSelect=visiblePager.all().find(x=>x.tag==="select"&&
    x.attrs["aria-label"]==="عدد الأفراد في الصفحة");
  assert.ok(sizeSelect,"monthly size selector stays with always-visible page controls");
  assert.equal(sizeSelect.value,"100");
  sizeSelect.value="25";sizeSelect.handlers.change();await tick();await tick();
  const smallerPage=calls.filter(x=>x.action==="QueryMonthlyGrid").at(-1);
  assert.equal(smallerPage.payload.pageSize,25,
    "monthly page-size change is applied to the actual C# data query");
  assert.equal(smallerPage.payload.page,1,
    "page-size change resets pagination safely to page one");
  const afterSizePager=viewport.all().find(x=>x.className.includes("functional-pager"));
  assert.ok(afterSizePager.all().some(x=>x.tag==="select"&&x.value==="25"),
    "the selected page size survives monthly rerender");
  const goForward=viewport.all().find(x=>x.tag==="button"&&x.textContent==="الشهر التالي");
  assert.ok(goForward&&!goForward.disabled);
  goForward.handlers.click();await tick();await tick();
  const latest=calls.filter(x=>x.action==="QueryMonthlyGrid").at(-1);
  assert.equal(latest.payload.year,year);
  assert.equal(latest.payload.month,month);
  assert.equal(viewport.all().filter(x=>x.tag==="th"&&
    x.className.includes("current-day-column")).length,1);
  assert.equal(calls.filter(x=>!["GetCurrentSession","QuerySites","GetAvailableArchivePeriods",
    "QueryMonthlyGrid","GetMonthlyCellDetails","GetDashboardSummary"].includes(x.action)).length,0,
    "read-only navigation must never commit attendance or modify history");
  console.log("MONTHLY CELL DETAILS UI PASS: date-bound click, saved snapshot and revision, close, read-only");
}
main().catch(e=>{console.error(e);process.exitCode=1;});
