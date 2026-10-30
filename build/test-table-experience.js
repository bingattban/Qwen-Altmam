"use strict";
const assert=require("node:assert/strict");
const fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
class E{
  constructor(tag){
    this.tag=tag;this.parentElement=null;this.children=[];
    this.className="";this._text="";this.type="";this.placeholder="";
    this.hidden=false;this.value="";this.colSpan=1;this.handlers={};
    this.savedStyles={};this.style={setProperty:(name,value)=>this.savedStyles[name]=value};
  }
  get textContent(){return this._text+this.children.map(x=>x.textContent).join("");}
  set textContent(v){this._text=String(v);this.children=[];}
  get classList(){return{
    add:name=>{if(!this.className.split(/\s+/).includes(name))this.className+=" "+name;},
    contains:name=>this.className.split(/\s+/).includes(name)
  };}
  get tBodies(){return this.all().filter(e=>e.tag==="tbody");}
  get rows(){return this.children.filter(e=>e.tag==="tr");}
  append(...items){for(const item of items){item.parentElement=this;this.children.push(item);}}
  appendChild(item){this.append(item);return item;}
  insertBefore(item,before){const i=this.children.indexOf(before);
    if(i<0)throw Error("invalid insertion point");
    item.parentElement=this;this.children.splice(i,0,item);
  }
  setAttribute(key,v){this[key]=v;}
  getAttribute(key){return this[key]||null;}
  replaceChildren(...items){this.children=[];this.append(...items);}
  get isConnected(){return true;}
  addEventListener(key,fn){this.handlers[key]=fn;}
  getBoundingClientRect(){return {top:210,left:0,right:1000,height:500};}
  all(){return [this,...this.children.flatMap(x=>x.all())];}
  querySelectorAll(selector){
    if(selector==="input")return this.all().filter(x=>x!==this&&x.tag==="input");
    if(selector==="label")return this.all().filter(x=>x!==this&&x.tag==="label");
    if(selector.includes("input[type"))return this.all().filter(x=>x!==this&&x.tag==="input"&&x.type==="search");
    if(selector===":scope > .functional-pager")
      return this.children.filter(x=>x.classList.contains("functional-pager"));
    if(selector.startsWith("."))return this.all().filter(x=>x!==this&&x.classList.contains(selector.slice(1)));
    return [];
  }
  querySelector(selector){return this.querySelectorAll(selector)[0]||null;}
  closest(selector){
    const options=selector.split(",").map(x=>x.trim());
    for(let e=this;e;e=e.parentElement){
      if(options.some(s=>s.startsWith(".")&&e.classList.contains(s.slice(1)) ||
        s.startsWith("#")&&e.id===s.slice(1)))return e;
    }
    return null;
  }
}
const main=new E("main");main.id="appViewport";
const modal=new E("section");modal.id="appModalBody";
const document={
  readyState:"loading",
  createElement:tag=>new E(tag),
  querySelectorAll:selector=>selector==="#appViewport table, #appModalBody table"
    ?[...main.all(),...modal.all()].filter(x=>x.tag==="table"):
    selector==="input"?[...main.all(),...modal.all()].filter(x=>x.tag==="input"):[],
  getElementById:id=>id==="appViewport"?main:id==="appModalBody"?modal:null,
  addEventListener:()=>{}
};
const window={innerHeight:900,addEventListener:()=>{}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/js/table-experience.js"),"utf8"),
  {window,document,Promise,console},{filename:"table-experience.js"});
const helper=window.AltamamTables;
function build(owner,{backendSearch=false,paged=false,names=[]}={}){
  const card=new E("section");card.className="card";owner.append(card);
  if(backendSearch){
    const input=new E("input");input.type="search";input.placeholder="بحث بالاسم";
    card.append(input);
  }
  const wrap=new E("div");wrap.className="functional-table-wrap";
  const table=new E("table"),body=new E("tbody");
  table.append(body);
  for(const name of names){const row=new E("tr"),cell=new E("td");
    cell.textContent=name;row.append(cell);body.append(row);}
  wrap.append(table);card.append(wrap);
  if(paged){const pager=new E("div");pager.className="functional-pager";card.append(pager);}
  return {card,wrap,table,body};
}
assert.equal(helper.normalize("  مُحَمَّد  ١٢٣  "),"محمد 123");
const first=build(main,{paged:true,names:["محمد ١٢٣","سالم 456","خالد 789"]});
helper.enhance(first.table);
assert.ok(first.table.classList.contains("unified-app-table"),"every table gets the common grid");
assert.ok(first.wrap.classList.contains("table-expanded-scroll"),"full available table height");
assert.ok(first.card.classList.contains("table-expanded-paged"),"pager outside row scroller");
assert.ok(first.wrap.savedStyles["--usable-table-height"].endsWith("px"),"dynamic height applied");
assert.ok(first.wrap.classList.contains("has-local-table-search"),"missing search auto-created");
const local=first.wrap.children[0],input=local.children.find(x=>x.tag==="input");
assert.equal(input.type,"search");
assert.ok(input.classList.contains("app-unified-search"),
  "locally generated table searches receive the compact dimensions immediately");
input.value="123";input.handlers.input();
assert.deepEqual(first.body.rows.map(x=>x.hidden),[false,true,true]);
input.value="سالم";input.handlers.input();
assert.deepEqual(first.body.rows.map(x=>x.hidden),[true,false,true]);
input.value="";input.handlers.input();
assert.deepEqual(first.body.rows.map(x=>x.hidden),[false,false,false]);
helper.enhance(first.table);
assert.equal(first.wrap.children.filter(x=>x.className==="table-local-search").length,1,
  "repeated DOM scans may not duplicate controls");
const native=build(main,{backendSearch:true,names:["أحمد","حسن"]});
helper.enhance(native.table);
assert.equal(native.wrap.children.length,1,
  "screen-wide backend search must not be duplicated or replaced");
const second=build(main,{names:["فاطمة","إبراهيم"]});
helper.enhance(second.table);
assert.equal(second.wrap.children[0].className,"table-local-search",
  "search for first table must not suppress search in a different card");
const review=build(modal,{names:["١٢٣","٤٥٦"]});
helper.enhance(review.table);
assert.ok(review.wrap.classList.contains("has-local-table-search"),
  "modal tables also receive scoped search");
const css=fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/css/altamam.css"),"utf8");
assert.match(css,/\.table-expanded-paged>\.table-expanded-scroll/);
assert.match(css,/\.table-local-search/);
assert.match(css,/height:38px;min-height:38px/,"search height must accommodate Arabic text");
assert.match(css,/width:100%;min-width:200px;max-width:360px/,"search is flexible, not fixed");
assert.match(css,/label\.app-unified-search-wrap\{\s*display:flex;flex:1 1 240px;min-width:200px;max-width:360px/);
assert.doesNotMatch(css,/170px!important/,"old narrow search footprint must not regress");
const html=fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/index.html"),"utf8");
assert.match(html,/assets\/js\/table-experience\.js/);
console.log("ALL TABLE EXPERIENCE PASS: shared styles, per-table search, Arabic digit normalization, modal tables, no duplicate server search, dynamic height and pager");

async function verifySharedInputCompletion(){
  let lookups=0;
  window.AltamamBridge={invoke:async(operation,payload)=>{
    assert.equal(operation,"QueryPersons");
    assert.equal(payload.term,"محمد");
    assert.equal(payload.pageSize,16);
    lookups++;
    return{data:{items:[{displayName:"محمد سالم",serviceNumberA:"12345"}]}};
  }};
  const person=new E("input");person.type="search";person.placeholder="بحث بالاسم أو الرقم (ع)";
  main.append(person);
  const labeled=new E("label"),caption=new E("span");
  caption.textContent="بحث عن فرد بالاسم أو الرقم (ع)";
  const named=new E("input");named.type="text";
  labeled.append(caption,named);main.append(labeled);
  const audit=new E("input");audit.type="search";
  audit.placeholder="بحث في سجل العمليات";main.append(audit);
  const general=new E("input");general.type="text";main.append(general);
  const secret=new E("input");secret.type="password";main.append(secret);
  helper.scan();
  assert.ok(person.className.includes("app-smart-search"));
  assert.ok(person.classList.contains("app-unified-search"));
  assert.equal(person.placeholder,"بحث بالاسم أو الرقم (ع)");
  assert.ok(named.classList.contains("app-unified-search"),
    "admin text search receives the same compact dimensions");
  assert.equal(named.placeholder,"بحث بالاسم أو الرقم (ع)",
    "person-search hint moves inside the input");
  assert.ok(labeled.classList.contains("app-unified-search-wrap"));
  assert.ok(caption.classList.contains("app-unified-search-caption"),
    "visible search caption is visually hidden but remains accessible");
  assert.ok(audit.classList.contains("app-unified-search"),
    "general non-person search inputs have the same dimensions");
  assert.equal(audit.placeholder,"بحث في سجل العمليات",
    "audit-specific search semantics must remain visible");
  assert.ok(person.list?.startsWith("person-suggestions-"));
  assert.equal(general.autocomplete,"on","ordinary text fields retain safe native completion");
  assert.notEqual(secret.autocomplete,"on","passwords never receive completion");
  person.value="محمد";
  person.handlers.input();
  await new Promise(resolve=>setImmediate(resolve));
  const datalist=main.all().find(x=>x.tag==="datalist"&&x.id===person.list);
  assert.ok(datalist);
  assert.ok(datalist.all().some(x=>x.tag==="option"&&x.value==="محمد سالم"));
  assert.ok(datalist.all().some(x=>x.tag==="option"&&x.value==="12345"));
  assert.equal(lookups,1,"one bounded backend lookup for the typed phrase");
  const originalLists=main.all().filter(x=>x.tag==="datalist").length;
  helper.scan();assert.equal(main.all().filter(x=>x.tag==="datalist").length,originalLists,
    "rescan cannot duplicate name-suggestion lists");
  console.log("SHARED AUTOCOMPLETE PASS: person names/numbers, ordinary inputs, password exclusion");
}
verifySharedInputCompletion().catch(error=>{console.error(error);process.exitCode=1;});
