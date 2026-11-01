"use strict";
const assert=require("node:assert/strict"),fs=require("node:fs"),vm=require("node:vm"),path=require("node:path");
class Element {
 constructor(tag){this.tag=tag;this.textContent="";this.className="";this.children=[];
  this.handlers={};this.attrs={};this.value="";this.checked=false;this.disabled=false;
  this.isConnected=true;}
 append(...items){this.children.push(...items);}
 appendChild(item){this.children.push(item);return item;}
 replaceChildren(...items){this.children=items;}
 setAttribute(k,v){this.attrs[k]=v;}
 addEventListener(event,cb){this.handlers[event]=cb;}
 all(){return [this,...this.children.flatMap(x=>x instanceof Element?x.all():[])];}
}
const root=new Element("div"),invoked=[],committed=[],archiveRequests=[];
let allow=false;
let deferredArchive=null;
const modalReviews=[],detailReviews=[];
const responses={
 QuerySites:[{siteId:"SITE-SOURCE",name:"الموقع الأول",active:true},
  {siteId:"SITE-DEST",name:"الموقع الثاني",active:true}],
 QueryPersons:{total:1,hasNext:false,items:[{personId:"P1",serviceNumberA:"001",
   displayName:"فرد تجريبي",siteName:"الموقع الأول"}]},
 PreviewInternalTransfer:{previewToken:"SERVER-ONLY-TOKEN",count:1,
   businessDate:"2026-10-25",external:false,destinationName:"الموقع الثاني",
   persons:[{name:"فرد تجريبي",serviceNumberA:"001",sourceSiteName:"الموقع الأول"}]},
 CommitInternalTransfer:{count:1},
 QueryMovementArchive:{total:1,hasNext:false,items:[{movementId:"MOV-TST-1",
  serviceNumberA:"001",name:"فرد تجريبي",type:"داخلي",
  sourceSite:"الموقع الأول",destination:"الموقع الثاني",date:"2026-10-23"}]},
 GetMovementDetails:{movementId:"MOV-TST-1",serviceNumberA:"001",name:"فرد تجريبي",
   type:"داخلي",sourceSite:"الموقع الأول",destination:"الموقع الثاني",
   date:"2026-10-23",committedAtUtc:"2026-10-23T08:00:00Z",actorUsername:"test-admin"},
 ExportPersonLifetimeExcel:{filePath:"C:\\Reports\\نقل داخلي_2026-10-26.xlsx"}
};
const bridge={invoke:async(name,payload)=>{
 invoked.push(name);if(name==="CommitInternalTransfer")committed.push(payload);
 if(name==="QueryMovementArchive"||name==="ExportPersonLifetimeExcel")
    archiveRequests.push({name,payload});
 if(name==="QueryMovementArchive"&&deferredArchive)
   return new Promise((resolve,reject)=>deferredArchive.push({resolve,reject,payload}));
 if(!(name in responses))throw Error("Unexpected action "+name);
 return {data:responses[name]};
}};
const ctx={document:{createElement:tag=>new Element(tag)},
 window:{AltamamBridge:bridge,AltamamDialog:{
   transferReview:async(data)=>{modalReviews.push(data);return allow;},
   detailsReview:async(details)=>{detailReviews.push(details);return true;},
   confirm:async()=>true
 }},console,Date};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
 "../src/AlTamam.WebUI/assets/js/personnel-movements.js"),"utf8"),ctx);
const all=()=>root.all(),find=(tag,text)=>all().find(e=>e.tag===tag && e.textContent===text);
const flush=()=>new Promise(r=>setImmediate(r));
async function run(){
 await ctx.window.AltamamMovements.render(root,"transferInternal");
 const selects=all().filter(x=>x.tag==="select");
 assert.equal(selects.length,2);
 selects[0].value="SITE-SOURCE";selects[0].handlers.change();
 await flush();
 assert.ok(invoked.includes("QueryPersons"),"real paginated source roster");
 const cb=all().find(x=>x.tag==="input"&&x.type==="checkbox");
 assert.ok(cb,"active source person is selectable");
 cb.checked=true;cb.handlers.change();selects[1].value="SITE-DEST";
 await find("button","معاينة النقل والتأكيد").handlers.click();
 assert.equal(committed.length,0,"declining modal preview cannot move person");
 assert.equal(modalReviews.length,1,"the first preview opens a centered modal directly");
 assert.equal(modalReviews[0].businessDate,"2026-10-25");
 assert.equal(modalReviews[0].persons[0].sourceSiteName,"الموقع الأول");
 assert.equal(modalReviews[0].persons[0].serviceNumberA,"001");
 assert.ok(!find("button","تأكيد النقل النهائي"),
   "no duplicate inline preview or confirmation screen");
 allow=true;
 await find("button","معاينة النقل والتأكيد").handlers.click();
 assert.equal(committed.length,1);
 assert.equal(modalReviews.length,2,"final approval happens inside transfer modal");
 assert.equal(committed[0].previewToken,"SERVER-ONLY-TOKEN",
   "commit must send only the validated server preview token");
 const archiveRoot=new Element("div");
 await ctx.window.AltamamMovements.render(archiveRoot,"archiveInternal");
 assert.ok(invoked.includes("QueryMovementArchive"),"real movement archive read");
 assert.equal(archiveRequests.at(-1).payload.type,"internal",
   "internal archive MUST bind SQL type=internal, not rely on UI-only filtering");
 const movementDetails=archiveRoot.all().find(x=>x.tag==="button"&&x.textContent==="تفاصيل");
 assert.ok(movementDetails,"movement archive has a details button for each immutable row");
 await movementDetails.handlers.click();
 assert.ok(invoked.includes("GetMovementDetails"),"details must be read from the C# bridge");
 assert.equal(detailReviews.length,1,"movement details open inside the shared modal");
 assert.ok(detailReviews[0].rows.some(([key,value])=>
   key==="الرقم (ع) وقت النقل"&&value==="001"));
 assert.equal(committed.length,1,"read-only archive details must not commit another transfer");
 const internalExport=archiveRoot.all().find(x=>x.tag==="button"&&
  x.textContent.includes("تصدير أرشيف النقل الداخلي"));
 assert.ok(internalExport);
 await internalExport.handlers.click();await flush();
 assert.equal(archiveRequests.at(-1).payload.mode,"movementArchive");
 assert.equal(archiveRequests.at(-1).payload.type,"internal");
 const movementType=archiveRoot.all().find(x=>x.tag==="select"&&
  x.attrs["aria-label"]==="نوع السجل");
 assert.ok(movementType,"return history is selectable within the existing read-only archive");
 movementType.value="return";
 movementType.handlers.change();await flush();
 assert.equal(archiveRequests.at(-1).payload.type,"return",
  "return archive must query genuine immutable movement type 4");
 await internalExport.handlers.click();await flush();
 assert.equal(archiveRequests.at(-1).payload.type,"return",
  "Excel export must preserve the return archive filter");
 assert.ok(!archiveRoot.all().some(x=>x.textContent==="إضافة عودة"),
  "no second Return form may be added under transfers");
 const externalRoot=new Element("div");
 await ctx.window.AltamamMovements.render(externalRoot,"archiveExternal");
 assert.equal(archiveRequests.at(-1).payload.type,"external",
   "external archive MUST bind SQL type=external");
 assert.ok(externalRoot.all().some(x=>x.textContent==="أرشيف النقل الخارجي"));
 // A slow first archive query must not overwrite a newer site/date-filtered result.
 const raceRoot=new Element("div");
 deferredArchive=[];
 const firstRender=ctx.window.AltamamMovements.render(raceRoot,"archiveInternal");
 await flush();
 assert.equal(deferredArchive.length,1);
 const siteFilter=raceRoot.all().find(x=>x.tag==="select");
 siteFilter.value="SITE-DEST";
 siteFilter.handlers.change();
 await flush();
 assert.equal(deferredArchive.length,2);
 deferredArchive[1].resolve({data:{total:1,hasNext:false,items:[{
   movementId:"MOV-NEW",serviceNumberA:"002",name:"النتيجة الجديدة",
   type:"داخلي",sourceSite:"الموقع الأول",destination:"الموقع الثاني",
   date:"2026-10-29"
 }]}});
 await flush();
 deferredArchive[0].resolve({data:{total:1,hasNext:false,items:[{
   movementId:"MOV-OLD",serviceNumberA:"001",name:"نتيجة قديمة",
   type:"داخلي",sourceSite:"الموقع الأول",destination:"الموقع الثاني",
   date:"2026-10-28"
 }]}});
 await firstRender;await flush();
 assert.ok(raceRoot.all().some(x=>x.textContent==="النتيجة الجديدة"),
   "latest site-filtered archive response is displayed");
 assert.ok(!raceRoot.all().some(x=>x.textContent==="نتيجة قديمة"),
   "stale unfiltered archive response must never replace newer results");
 deferredArchive=null;
 console.log("MOVEMENTS UI PASS: source/target, review, cancel gate, token-only commit, archive, stale-filter race");
}
run().catch(e=>{console.error(e);process.exitCode=1;});
