"use strict";
const assert=require("node:assert/strict"),fs=require("node:fs"),
  vm=require("node:vm"),path=require("node:path");
class Element{
 constructor(tag){this.tag=tag;this.children=[];this.textContent="";this.value="";
 this.handlers={};this.className="";this.attrs={};this.type="";this.disabled=false;
 this.style={};this.classList={open:false,add:x=>{if(x==="open")this.classList.open=true},
 remove:x=>{if(x==="open")this.classList.open=false}};}
 append(...items){this.children.push(...items);}
 appendChild(item){this.children.push(item);return item;}
 replaceChildren(...items){this.children=[...items];}
 setAttribute(k,v){this.attrs[k]=v;}
 addEventListener(k,fn){this.handlers[k]=fn;}
 focus(){document.activeElement=this;}
 click(){this.handlers.click?.({preventDefault(){},stopPropagation(){},target:this});}
 all(){return [this,...this.children.flatMap(x=>x instanceof Element?x.all():[])];}
 querySelectorAll(){return this.all().filter(x=>x.tag==="button"||x.tag==="input"||x.tag==="textarea");}
}
const overlay=new Element("div"),title=new Element("h2"),body=new Element("section"),
  previous=new Element("button"),listeners=new Map();
const document={activeElement:previous,getElementById:id=>
  id==="appModalBackdrop"?overlay:id==="appModalTitle"?title:id==="appModalBody"?body:null,
  createElement:tag=>new Element(tag),
  addEventListener:(kind,fn)=>listeners.set(kind,fn),
  removeEventListener:kind=>listeners.delete(kind)};
const context={document,window:{}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/js/operation-dialogs.js"),"utf8"),context);
const dialog=context.window.AltamamDialog;
assert.ok(dialog,"global dialogs registered in local app");
const button=label=>body.all().find(x=>x.tag==="button"&&x.textContent===label);
async function run(){
 const yes=dialog.confirm("تأكيد عملية حساسة",{title:"التمام اليومي"});
 assert.equal(overlay.classList.open,true);
 assert.equal(title.textContent,"التمام اليومي");
 assert.equal(body.all().filter(x=>x.tag==="input").length,0);
 button("تأكيد العملية").click();
 assert.equal(await yes,true);
 assert.equal(overlay.classList.open,false);
 assert.equal(document.activeElement,previous,"focus must return to original control");
 const no=dialog.confirm("إلغاء عملية");
 button("إلغاء").click();
 assert.equal(await no,false);
 const headerClose=dialog.confirm("إلغاء من زر إغلاق النافذة");
 assert.equal(overlay.classList.open,true);
 assert.equal(dialog.dismiss(),true,"header close uses dialog engine, not CSS hide");
 assert.equal(await headerClose,false,"header close resolves the pending confirmation as rejected");
 assert.equal(overlay.classList.open,false,"dismiss resets the shared backdrop");
 assert.equal(document.activeElement,previous,"header close restores the previous focused control");
 assert.equal(dialog.dismiss(),false,"repeat dismissal is safe when already closed");
 const reason=dialog.reason("سبب الفصل",{minLength:5,danger:true});
 const box=body.all().find(x=>x.tag==="textarea");
 assert.ok(box,"reason entered in centered app modal");
 box.value="أقل";
 button("معاينة العملية").click();
 assert.equal(overlay.classList.open,true,"invalid reason does not close modal");
 box.value="سبب الفصل موثق";
 button("معاينة العملية").click();
 assert.equal(await reason,"سبب الفصل موثق");
 const canceled=dialog.reason("عدم اعتماد العملية");
 listeners.get("keydown")({key:"Escape",preventDefault(){},shiftKey:false});
 assert.equal(await canceled,null);
 assert.equal(overlay.classList.open,false);
 const reviewData={businessDate:"2026-10-24",statusName:"موجود",
   persons:[{name:"محمد صالح الحضرمي",serviceNumberA:"12345",
     previousStatusName:"غير محضر"},
     {name:"أحمد سالم باصالح",serviceNumberA:"67890",
       previousStatusName:"غياب"}]};
 const canceledReview=dialog.attendanceReview(reviewData,
   {confirmLabel:"تأكيد وحفظ التحضير"});
 assert.equal(overlay.classList.open,true,"review is shown inside app modal");
 assert.equal(body.all().filter(x=>x.tag==="table").length,1,
   "modal contains the entire server-verified attendance list");
 assert.equal(body.all().filter(x=>x.className==="app-preview-property").length,4,
   "review summary must show date, mode, target status and selected count in four cards");
 assert.ok(body.all().some(x=>x.textContent==="محمد صالح الحضرمي"));
 assert.ok(body.all().some(x=>x.textContent==="12345"));
 assert.ok(body.all().some(x=>x.textContent==="أحمد سالم باصالح"));
 assert.ok(body.all().some(x=>x.textContent==="67890"));
 assert.ok(body.all().some(x=>x.textContent==="غير محضر"));
 assert.ok(body.all().some(x=>x.textContent==="غياب"));
 assert.ok(body.all().some(x=>x.textContent==="موجود"));
 button("إلغاء").click();
 assert.equal(await canceledReview,false,"cancel closes without approval");
 const acceptedReview=dialog.attendanceReview(reviewData,
   {title:"معاينة التحضير",confirmLabel:"تأكيد وحفظ التحضير"});
 assert.equal(title.textContent,"معاينة التحضير");
 button("تأكيد وحفظ التحضير").click();
 assert.equal(await acceptedReview,true,"review itself owns the approval action");
 assert.equal(overlay.classList.open,false);
 const invalidReview=await dialog.attendanceReview({...reviewData,persons:[]});
 assert.equal(invalidReview,false,"never approve an empty or missing server preview");
 const perPerson=dialog.attendanceReview({
   businessDate:"2026-10-26",statusName:"الحالة حسب أمس",
   persons:[{name:"فرد اختباري",serviceNumberA:"1234",siteName:"الموقع",
     previousStatusName:"غير محضر",proposedStatusName:"إجازة"}]
 });
 assert.ok(body.all().some(x=>x.textContent==="إجازة"),
   "manual carry-forward preview must display each person's own proposed status");
 button("إلغاء").click();
 assert.equal(await perPerson,false);
 const selection=dialog.unpreparedSelection(async page=>({
   items:[{personId:"P1",name:"فرد",serviceNumberA:"1234",siteName:"الموقع"}],
   total:1
 }));
 await new Promise(resolve=>setImmediate(resolve));
 assert.equal(overlay.classList.open,true);
 const check=body.all().find(x=>x.tag==="input"&&x.type==="checkbox");
 assert.ok(check,"unprepared modal has an individual checkbox");
 check.checked=true;check.handlers.change();
 button("معاينة حالة الأمس").click();
 assert.deepEqual([...await selection],["P1"]);
 const transferPlan={businessDate:"2026-10-25",destinationName:"موقع حضرموت",
   external:false,persons:[
     {name:"محمد صالح",serviceNumberA:"12345",sourceSiteName:"الموقع الأول"},
     {name:"أحمد سالم",serviceNumberA:"67890",sourceSiteName:"الموقع الثاني"}
   ]};
 const abortedTransfer=dialog.transferReview(transferPlan);
 assert.equal(overlay.classList.open,true,"transfer must use the centered modal");
 assert.equal(body.all().filter(x=>x.tag==="table").length,1,
   "the full transfer list is rendered in the review");
 assert.ok(body.all().some(x=>x.textContent==="2026-10-25"));
 assert.ok(body.all().some(x=>x.textContent==="موقع حضرموت"));
 assert.ok(body.all().some(x=>x.textContent==="محمد صالح"));
 assert.ok(body.all().some(x=>x.textContent==="67890"));
 button("إلغاء").click();
 assert.equal(await abortedTransfer,false,"rejecting transfer review forbids commit");
 const confirmedTransfer=dialog.transferReview(transferPlan);
 button("تأكيد النقل النهائي").click();
 assert.equal(await confirmedTransfer,true,"only explicit approval completes modal");
 assert.equal(await dialog.transferReview({...transferPlan,persons:[]}),false,
   "empty transfer selection cannot bypass the modal");
 const formDialog=dialog.form([{name:"reason",label:"سبب الإجراء",type:"text",
   required:true,minLength:3}],{title:"بيانات العملية"});
 assert.equal(overlay.classList.open,true,"shared multi-field dialog opens");
 const formField=body.all().find(x=>x.tag==="input");
 formField.value="تفاصيل معتمدة";
 button("تأكيد العملية").click();
 assert.equal((await formDialog).reason,"تفاصيل معتمدة",
   "multi-field forms collect typed user data without changing legacy dialogs");
 const css=fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/css/altamam.css"),"utf8");
 assert.ok(css.includes(".modal-backdrop{z-index:1000;align-items:center;justify-content:center}"),
   "dialogs are horizontally and vertically centered");
 console.log("APP DIALOGS PASS: centered theme, full attendance preview, no-write cancel, one-step approval, reason validation and focus restore");
}
run().catch(error=>{console.error(error);process.exitCode=1;});
