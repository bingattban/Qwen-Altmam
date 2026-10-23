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
    if(action==="QuerySites")return{data:[{siteId:"SITE-UNSPECIFIED",name:"غير محدد",active:true}]};
    if(action==="GetAvailableArchivePeriods")return{data:[firstDay.slice(0,7)]};
    if(action==="QueryMonthlyGrid")return{data:{total:1,page:1,items:[
      {personId:"TST-P",serviceNumberA:"X1",name:"شخص اختباري",siteName:"غير محدد",
       days:{[firstDay]:{symbol:"غ",name:"غياب",color:"#EF4444"}}}]}};
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
    "QueryMonthlyGrid","GetMonthlyCellDetails"].includes(x.action)).length,0,
    "read-only click must never commit attendance or modify history");
  console.log("MONTHLY CELL DETAILS UI PASS: date-bound click, saved snapshot and revision, close, read-only");
}
main().catch(e=>{console.error(e);process.exitCode=1;});
