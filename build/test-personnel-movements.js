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
const root=new Element("div"),invoked=[],committed=[];
let allow=false;
const modalReviews=[];
const responses={
 QuerySites:[{siteId:"SITE-SOURCE",name:"الموقع الأول",active:true},
  {siteId:"SITE-DEST",name:"الموقع الثاني",active:true}],
 QueryPersons:{total:1,hasNext:false,items:[{personId:"P1",serviceNumberA:"001",
   displayName:"فرد تجريبي",siteName:"الموقع الأول"}]},
 PreviewInternalTransfer:{previewToken:"SERVER-ONLY-TOKEN",count:1,
   businessDate:"2026-10-25",external:false,destinationName:"الموقع الثاني",
   persons:[{name:"فرد تجريبي",serviceNumberA:"001",sourceSiteName:"الموقع الأول"}]},
 CommitInternalTransfer:{count:1},
 QueryMovementArchive:{total:1,hasNext:false,items:[{serviceNumberA:"001",name:"فرد تجريبي",
  type:"داخلي",sourceSite:"الموقع الأول",destination:"الموقع الثاني",date:"2026-10-23"}]}
};
const bridge={invoke:async(name,payload)=>{
 invoked.push(name);if(name==="CommitInternalTransfer")committed.push(payload);
 if(!(name in responses))throw Error("Unexpected action "+name);
 return {data:responses[name]};
}};
const ctx={document:{createElement:tag=>new Element(tag)},
 window:{AltamamBridge:bridge,AltamamDialog:{
   transferReview:async(data)=>{modalReviews.push(data);return allow;},
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
 assert.ok(find("button","تأكيد النقل النهائي"),"preview visible before mutation");
 assert.equal(committed.length,0,"no commit on preview");
 await find("button","تأكيد النقل النهائي").handlers.click();
 assert.equal(committed.length,0,"declined confirmation cannot move person");
 assert.equal(modalReviews.length,1,"one full transfer review must precede any mutation");
 assert.equal(modalReviews[0].businessDate,"2026-10-25");
 assert.equal(modalReviews[0].persons[0].sourceSiteName,"الموقع الأول");
 assert.equal(modalReviews[0].persons[0].serviceNumberA,"001");
 allow=true;
 await find("button","تأكيد النقل النهائي").handlers.click();
 assert.equal(committed.length,1);
 assert.equal(modalReviews.length,2,"final approval must pass through transfer dialog");
 assert.equal(committed[0].previewToken,"SERVER-ONLY-TOKEN",
   "commit must send only the validated server preview token");
 const archiveRoot=new Element("div");
 await ctx.window.AltamamMovements.render(archiveRoot,"transferArchive");
 assert.ok(invoked.includes("QueryMovementArchive"),"real movement archive read");
 console.log("MOVEMENTS UI PASS: source/target, review, cancel gate, token-only commit, archive");
}
run().catch(e=>{console.error(e);process.exitCode=1;});
