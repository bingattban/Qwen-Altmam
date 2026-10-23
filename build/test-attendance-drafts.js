"use strict";
const assert=require("node:assert/strict"),fs=require("node:fs"),vm=require("node:vm"),path=require("node:path");
class El{
 constructor(tag){this.tag=tag;this.children=[];this.textContent="";this.className="";
   this.handlers={};this.attrs={};this.disabled=false;this.value="";this.checked=false;this.isConnected=true;}
 append(...items){this.children.push(...items);}
 appendChild(child){this.children.push(child);return child;}
 replaceChildren(...items){this.children=items;}
 insertBefore(child,before){const index=this.children.indexOf(before);this.children.splice(index<0?0:index,0,child);}
 setAttribute(key,value){this.attrs[key]=value;}
 addEventListener(event,callback){this.handlers[event]=callback;}
 all(){return [this,...this.children.flatMap(child=>child instanceof El?child.all():[])];}
}
const root=new El("main"),invocations=[],saves=[],deletions=[],records=[];
let confirmation=false;
const bridge={invoke:async(action,payload)=>{
 invocations.push(action);
 if(action==="QuerySites")return{data:[{siteId:"SITE-UNSPECIFIED",name:"غير محدد",active:true}]};
 if(action==="GetActiveAttendanceStatuses")return{data:[{statusId:"STS-PRESENT",displayName:"موجود"}]};
 if(action==="QueryDailyRoster")return{data:{total:2,hasNext:false,items:[
   {personId:"P1",name:"شخص أول",serviceNumberA:"1",siteName:"غير محدد",statusName:"غير محضر"},
   {personId:"P2",name:"شخص ثان",serviceNumberA:"2",siteName:"غير محدد",statusName:"غير محضر"}]}};
 if(action==="ListAttendanceDrafts")return{data:records.map(r=>({...r}))};
 if(action==="SaveAttendanceDraft"){
   saves.push(payload);
   const draftId=payload.draftId||"DRF-TEST";
   const existing=records.findIndex(r=>r.draftId===draftId);
   const value={...payload,draftId,updatedAtUtc:"2026-10-23T12:00:00Z"};
   if(existing<0)records.push(value);else records[existing]=value;
   return{data:{draftId}};
 }
 if(action==="DeleteAttendanceDraft"){
   deletions.push(payload.draftId);
   const index=records.findIndex(r=>r.draftId===payload.draftId);
   if(index>=0)records.splice(index,1);
   return{data:{draftId:payload.draftId}};
 }
 if(action==="PreviewAttendanceChange")return{data:{previewToken:"TST-TOKEN",mode:"initial",count:1,
   statusName:"موجود",persons:[{personId:"P1",name:"شخص أول",serviceNumberA:"1",previousStatusName:"غير محضر"}]}};
 throw Error("Unknown bridge action "+action);
}};
const context={document:{createElement:tag=>new El(tag)},window:{AltamamBridge:bridge,
  AltamamAdminSettings:{renderGroup:()=>{}}},confirm:()=>confirmation,Date,console};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,"../src/AlTamam.WebUI/assets/js/attendance-ui.js"),"utf8"),context);
const findButton=text=>root.all().find(x=>x.tag==="button"&&x.textContent===text);
const flush=()=>new Promise(done=>setImmediate(done));
async function run(){
 await context.window.AltamamAttendance.render(root);
 assert.ok(invocations.includes("ListAttendanceDrafts"));
 assert.equal(saves.length,0,"opening page must not write draft");
 const status=root.all().filter(x=>x.tag==="select")[1];
 status.value="STS-PRESENT";
 const selected=root.all().find(x=>x.tag==="input"&&x.type==="checkbox");
 selected.checked=true;selected.handlers.change();
 await findButton("حفظ مسودة").handlers.click();
 await flush();
 assert.equal(saves.length,1);
 assert.equal(saves[0].selectedPersonIds.length,1);
 assert.ok(!invocations.includes("SubmitAttendance"),"draft must never commit attendance");
 const savedDate=records[0].businessDate;
 await findButton("تحديث").handlers.click();await flush();
 await findButton("استعادة المسودة").handlers.click();await flush();
 assert.ok(root.all().some(x=>String(x.textContent).includes("استعيدت المسودة")));
 assert.equal(root.all().filter(x=>x.tag==="input"&&x.type==="checkbox"&&x.checked).length,1);
 records[0].businessDate="2020-01-01";
 await findButton("تحديث المسودات").handlers.click();await flush();
 await findButton("استعادة المسودة").handlers.click();await flush();
 assert.ok(root.all().some(x=>String(x.textContent).includes("لا يمكن استعادة مسودة من يوم سابق")));
 records[0].businessDate=savedDate;
 await findButton("تحديث المسودات").handlers.click();await flush();
 await findButton("حذف المسودة").handlers.click();await flush();
 assert.equal(deletions.length,0,"denied confirmation cannot delete draft");
 confirmation=true;
 await findButton("حذف المسودة").handlers.click();await flush();
 assert.equal(deletions.length,1);
 assert.equal(records.length,0);
 assert.ok(!invocations.includes("SubmitAttendance"));
 console.log("ATTENDANCE DRAFTS UI PASS: save, restore, stale-day refusal, guarded delete, no implicit attendance commit");
}
run().catch(e=>{console.error(e);process.exitCode=1;});
