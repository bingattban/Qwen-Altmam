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
const root=new El("main"),calls=[],writes=[];
const bridge={invoke:async(name,payload)=>{
 calls.push(name);
 const data={
   QuerySites:[{siteId:"SITE-UNSPECIFIED",name:"غير محدد",active:true}],
   GetActiveAttendanceStatuses:[
      {statusId:"STS-PRESENT",displayName:"موجود"},
      {statusId:"STS-ABSENT",displayName:"غياب"}],
   QueryDailyRoster:{total:2,hasNext:false,items:[
      {personId:"P1",name:"شخص تجريبي",serviceNumberA:"A1",siteName:"غير محدد",
       statusName:"غير محضر"},
      {personId:"P2",name:"شخص تجريبي آخر",serviceNumberA:"A2",siteName:"غير محدد",
       statusName:"غير محضر"}]},
   PreviewAttendanceChange:{previewToken:"SERVER_ONE_TIME_TOKEN",
      mode:payload?.mode||"initial",count:1,
      statusName:payload?.mode==="revise"?"غياب":"موجود",
      persons:[{personId:"P1",name:"شخص تجريبي",
        serviceNumberA:"A1",previousStatusName:payload?.mode==="revise"?"موجود":"غير محضر"}]},
   SubmitAttendance:{count:1},
   ReviseTodayAttendance:{count:1}
 };
 if(name==="SubmitAttendance"||name==="ReviseTodayAttendance"){writes.push({name,payload});}
 if(!Object.hasOwn(data,name))throw Error("Unexpected action: "+name);
 return {data:data[name]};
}};
let confirmed=false;
const context={
 document:{createElement:tag=>new El(tag)},window:{AltamamBridge:bridge,
   AltamamAdminSettings:{renderGroup:()=>{}}},
 confirm:()=>confirmed,console,Date
};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
 "../src/AlTamam.WebUI/assets/js/attendance-ui.js"),"utf8"),context);
const one=text=>root.all().find(x=>x.tag==="button"&&x.textContent===text);
const selects=()=>root.all().filter(x=>x.tag==="select");
async function flush(){await new Promise(resolve=>setImmediate(resolve));}
async function main(){
 await context.window.AltamamAttendance.render(root);
 assert.ok(one("معاينة التحضير"),"attendance real page");
 assert.equal(root.all().filter(x=>x.tag==="input"&&x.type==="checkbox").length,2);
 assert.equal(writes.length,0,"no write on page load");
 const choices=selects();
 choices[1].value="STS-PRESENT";
 const checkbox=root.all().find(x=>x.tag==="input"&&x.type==="checkbox");
 checkbox.checked=true;checkbox.handlers.change();
 await one("معاينة التحضير").handlers.click();
 await flush();
 assert.ok(one("تأكيد وحفظ التحضير"),"review first, no automatic save");
 assert.equal(writes.length,0,"preview must not write attendance");
 await one("تأكيد وحفظ التحضير").handlers.click();
 assert.equal(writes.length,0,"declined confirmation must not write");
 confirmed=true;
 await one("تأكيد وحفظ التحضير").handlers.click();
 await flush();
 assert.equal(writes.length,1,"one confirmed write");
 assert.equal(writes[0].payload.previewToken,"SERVER_ONE_TIME_TOKEN",
   "commit must send ONLY server-issued preview token");
 await context.window.AltamamAttendance.render(root);
 const changed=selects();
 changed[1].value="STS-ABSENT";
 changed[2].value="revise";
 changed[2].handlers.change();
 assert.ok(one("معاينة تغيير الحالة"),"change mode exposes a clear preview action");
 assert.equal(root.all().filter(x=>x.tag==="input"&&x.type!=="checkbox").length,0,
   "routine status revision must not request a reason");
 const reviseCheckbox=root.all().find(x=>x.tag==="input"&&x.type==="checkbox");
 reviseCheckbox.checked=true;reviseCheckbox.handlers.change();
 await one("معاينة تغيير الحالة").handlers.click();
 await flush();
 assert.ok(one("تأكيد تغيير الحالة"),"explicit change-status approval after review");
 assert.equal(calls.filter(x=>x==="ReviseTodayAttendance").length,0);
 await one("تأكيد تغيير الحالة").handlers.click();await flush();
 assert.equal(calls.filter(x=>x==="ReviseTodayAttendance").length,1);
 assert.equal(writes[1].payload.previewToken,"SERVER_ONE_TIME_TOKEN");
 console.log("ATTENDANCE UI PASS: paged roster, explicit confirmation and reason-free same-day state revision");
}
main().catch(e=>{console.error(e);process.exitCode=1;});
