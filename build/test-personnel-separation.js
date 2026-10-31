"use strict";
const assert=require("node:assert/strict"),fs=require("node:fs"),
    vm=require("node:vm"),path=require("node:path");
class Node {
 constructor(tag){this.tag=tag;this.textContent="";this.className="";
 this.children=[];this.handlers={};this.disabled=false;this.checked=false;
 this.attrs={};this.isConnected=true;this.value="";this.style={};}
 append(...v){this.children.push(...v);}
 appendChild(v){this.children.push(v);return v;}
 replaceChildren(...v){this.children=v;}
 setAttribute(k,v){this.attrs[k]=v;}
 addEventListener(k,fn){this.handlers[k]=fn;}
 all(){return [this,...this.children.flatMap(v=>v instanceof Node?v.all():[])];}
 remove(){this.isConnected=false;}
 get classList(){return {add:x=>this.className+=" "+x};}
}
const root=new Node("div"),calls=[],commits=[],timers=[];
let confirmDecision=false,reasonDialogCalls=0;
const previews=[];
const data={
 QueryPersons:{items:[{personId:"PERSON-1",displayName:"فرد من الكشف",serviceNumberA:"999",
    siteName:"موقع مختبر",active:true}],total:1,hasNext:false},
 PreviewSeparation:{previewToken:"ONLY_SERVER_TOKEN",name:"فرد من الكشف",
    serviceNumberA:"999",siteName:"موقع مختبر",businessDate:"2026-10-23",
    reason:"سبب فصل مسجل",minimumConfirmationSeconds:5},
 CommitSeparation:{movementId:"MOV-1",separated:true},
 QuerySites:[],
 QuerySeparationArchive:{items:[{movementId:"MOV-1",serviceNumberA:"999",
    name:"فرد مفصول",siteName:"موقع مختبر",date:"2026-10-23"}],total:1,hasNext:false},
 GetSeparationDetails:{name:"فرد مفصول",serviceNumberA:"999",siteName:"موقع مختبر",
   reason:"سبب فصل مسجل",date:"2026-10-23",actorUserId:"USR-1"}
};
const api={invoke:async(name,payload)=>{
 calls.push(name);
 if(name==="CommitSeparation")commits.push(payload);
 if(!(name in data))throw Error("Unexpected bridge action "+name);
 return {data:data[name]};
}};
const context={window:{AltamamBridge:api,AltamamDialog:{
 reason:async()=>{reasonDialogCalls++;return "سبب فصل مسجل";},
 detailsReview:async(details,options)=>{previews.push({details,options});return confirmDecision;}
}},document:{createElement:tag=>new Node(tag)},
 setInterval:(fn)=>{timers.push(fn);return timers.length;},clearInterval:()=>{},
 Intl,Date,console,confirm:()=>confirmDecision};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
 "../src/AlTamam.WebUI/assets/js/personnel-separation.js"),"utf8"),context);
const ui=context.window.AltamamSeparation;
const byText=text=>root.all().find(x=>x.tag==="button"&&x.textContent===text);
async function tick(){await new Promise(resolve=>setImmediate(resolve));}
async function run(){
 await ui.render(root,"separatePerson");
 assert.ok(byText("بحث"));
 assert.equal(commits.length,0);
 byText("اختيار").handlers.click();
 assert.equal(root.all().filter(x=>x.tag==="textarea").length,0,
   "reason must be collected in a centered popup, not an inline textarea");
 await byText("تحديد سبب الفصل والمعاينة").handlers.click();
 assert.equal(reasonDialogCalls,1,"separation asks for reason in modal");
 assert.equal(commits.length,0,"declining a preview must not commit");
 assert.equal(previews.length,1,"the entire separation preview is modal");
 assert.ok(previews[0].details.rows.some(pair=>pair[0]==="سبب الفصل"));
 assert.ok(previews[0].options.delaySeconds>=5,"modal enforces five-second safety gate");
 confirmDecision=true;
 await byText("تحديد سبب الفصل والمعاينة").handlers.click();
 assert.equal(commits.length,1,"explicitly accepting the verified modal commits once");
 assert.equal(previews.length,2);
 assert.equal(commits[0].previewToken,"ONLY_SERVER_TOKEN");
 assert.ok(byText("أرشيف الفصل"),"archive belongs on the separation screen");
 await byText("أرشيف الفصل").handlers.click();
 assert.ok(root.all().some(x=>x.textContent.includes("فرد مفصول")),
   "archive content appears inside the same separation screen");
 await byText("فصل فرد").handlers.click();
 assert.ok(byText("بحث"),"returning to separation keeps person search");
 const archive=new Node("div");
 await ui.render(archive,"separatedArchive");
 assert.ok(archive.all().some(x=>x.textContent.includes("فرد مفصول")));
 assert.ok(calls.includes("QuerySeparationArchive"));
 assert.ok(calls.includes("CommitSeparation"));
 const view=archive.all().find(x=>x.tag==="button"&&x.textContent==="عرض");
 assert.ok(view,"separated archive entry must expose a details action");
 await view.handlers.click();
 assert.ok(calls.includes("GetSeparationDetails"),"details come from the encrypted archive");
 assert.equal(previews.length,3,"archive details must open in the shared modal");
 assert.ok(previews[2].details.rows.some(x=>x[0]==="سبب الفصل"&&
   x[1]==="سبب فصل مسجل"));
 assert.equal(previews[2].options.confirmLabel,"إغلاق التفاصيل");
 assert.equal(commits.length,1,"opening or dismissing archived details never separates again");
 console.log("SEPARATION UI PASS: timed preview, secure commit, read-only archive details popup");
}
run().catch(err=>{console.error(err);process.exitCode=1;});
