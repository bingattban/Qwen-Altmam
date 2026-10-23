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
 const css=fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/css/altamam.css"),"utf8");
 assert.ok(css.includes(".modal-backdrop{z-index:1000;align-items:center;justify-content:center}"),
   "dialogs are horizontally and vertically centered");
 console.log("APP DIALOGS PASS: centered theme, confirm/cancel, required reason, keyboard dismissal, focus restore");
}
run().catch(error=>{console.error(error);process.exitCode=1;});
