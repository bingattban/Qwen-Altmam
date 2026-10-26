"use strict";
(function(){
const api=()=>window.AltamamBridge;
 const modalConfirm=text=>window.AltamamDialog?.confirm(text,{title:"تأكيد النقل"}) ??
   Promise.resolve(confirm(text));
function el(tag,text,cls){const e=document.createElement(tag);if(text!==undefined)e.textContent=String(text);if(cls)e.className=cls;return e;}
function btn(label,fn){const b=el("button",label,"btn btn-outline");b.type="button";b.addEventListener("click",fn);return b;}
function opt(value,label){const x=el("option",label);x.value=value;return x;}
function err(host,e){host.replaceChildren(el("p",e.message||"تعذر تنفيذ العملية","admin-warning"));}
function today(){const d=new Date();return [d.getFullYear(),String(d.getMonth()+1).padStart(2,"0"),String(d.getDate()).padStart(2,"0")].join("-");}
function table(headers){const wrap=el("div",undefined,"functional-table-wrap"),t=el("table",undefined,"data-table functional-table"),head=el("thead"),tr=el("tr"),body=el("tbody");headers.forEach(x=>tr.appendChild(el("th",x)));head.appendChild(tr);t.append(head,body);wrap.appendChild(t);return {wrap,body};}
function row(body,values){const tr=el("tr");values.forEach(x=>tr.appendChild(el("td",x)));body.appendChild(tr);return tr;}
async function transfer(host,external){
  const card=el("section",undefined,"card"),bar=el("div",undefined,"functional-toolbar");
  const from=el("select"),to=el("select"),outside=el("input"),search=el("input");
  from.setAttribute("aria-label","الموقع الحالي");to.setAttribute("aria-label","الموقع المنقول إليه");
  outside.setAttribute("aria-label","الجهة الخارجية");outside.placeholder="اسم الجهة الخارجية (إجباري)";
  search.placeholder="بحث بالاسم أو الرقم (ع)";search.className="form-input boxed-search";
  const note=el("div"),list=el("div"),confirmation=el("div"),counter=el("p","الأفراد المحددون: 0");
  const selected=new Set();let sites=[],page=1,serial=0;
  function reset(){selected.clear();counter.textContent="الأفراد المحددون: 0";confirmation.replaceChildren();}
  function destinations(){if(external)return;to.replaceChildren(opt("","اختر الموقع المنقول إليه"));sites.filter(s=>s.siteId!==from.value).forEach(s=>to.appendChild(opt(s.siteId,s.name)));}
  card.appendChild(el("h2",external?"النقل الخارجي":"النقل الداخلي","section-title"));
  card.appendChild(el("p",external
     ?"النقل الخارجي يستبعد الفرد من القوة مع الاحتفاظ بتاريخ خدمته."
     :"تظهر حركة النقل في يوم التنفيذ بالموقعين، ويبدأ التحضير بالموقع الجديد في اليوم التالي.",
     "section-description"));
  bar.append(from,external?outside:to,search,btn("بحث",()=>{page=1;reset();load();}));
  card.append(bar,counter,note,list,confirmation);host.appendChild(card);
  try{
    const data=(await api().invoke("QuerySites",{})).data||[];
    if(!card.isConnected)return;
    sites=data.filter(x=>x.active!==false);
    from.appendChild(opt("","اختر الموقع الحالي"));sites.forEach(s=>from.appendChild(opt(s.siteId,s.name)));
    destinations();
  }catch(e){err(note,e);return;}
  from.addEventListener("change",()=>{page=1;reset();destinations();load();});
  to.addEventListener("change",()=>confirmation.replaceChildren());
  outside.addEventListener("input",()=>confirmation.replaceChildren());
  async function load(){
    const current=++serial;
    if(!from.value){list.replaceChildren(el("p","اختر الموقع الحالي"));return;}
    list.replaceChildren(el("p","جارٍ قراءة الكشف..."));
    try{
      const data=(await api().invoke("QueryPersons",{siteId:from.value,organizationalStatus:1,
        term:search.value.trim(),page,pageSize:50})).data||{};
      if(current!==serial||!card.isConnected)return;
      const t=table(["اختيار","الرقم (ع)","الاسم","الموقع"]);
      for(const p of data.items||[]){
        const tr=row(t.body,["",p.serviceNumberA,p.displayName,p.siteName]);
        const cb=el("input");cb.type="checkbox";cb.checked=selected.has(p.personId);
        cb.setAttribute("aria-label","تحديد "+p.displayName);
        cb.addEventListener("change",()=>{
          if(cb.checked&&selected.size>=100){cb.checked=false;return err(note,new Error("الحد الأقصى 100 فرد."));}
          if(cb.checked)selected.add(p.personId);else selected.delete(p.personId);
          counter.textContent="الأفراد المحددون: "+selected.size;confirmation.replaceChildren();
        });tr.children[0].appendChild(cb);
      }
      if(!t.body.children.length){const tr=row(t.body,["لا توجد أسماء"]);tr.children[0].colSpan=4;}
      const nav=el("div",undefined,"functional-pager"),prev=btn("السابق",()=>{page--;load();}),next=btn("التالي",()=>{page++;load();});
      prev.disabled=page<=1;next.disabled=!data.hasNext;
      nav.append(el("span","الإجمالي: "+(data.total||0)),prev,next);
      list.replaceChildren(t.wrap,nav);
    }catch(e){if(current===serial)err(list,e);}
  }
  const previewButton=btn("معاينة النقل والتأكيد",async()=>{
    confirmation.replaceChildren();note.replaceChildren();
    if(!from.value||selected.size===0)return err(note,new Error("اختر الموقع المصدر والأفراد."));
    if(!external&&!to.value)return err(note,new Error("اختر الموقع المنقول إليه."));
    if(external&&outside.value.trim().length<2)return err(note,new Error("حدد الجهة الخارجية."));
    const payload={businessDate:today(),sourceSiteId:from.value,personIds:Array.from(selected).sort()};
    if(external)payload.destinationExternalName=outside.value.trim();
    else payload.destinationSiteId=to.value;
    previewButton.disabled=true;
    try{
      const data=(await api().invoke(external?"PreviewExternalTransfer":"PreviewInternalTransfer",payload)).data||{};
      if(!card.isConnected)return;
      if(!data.previewToken||!Array.isArray(data.persons))throw Error("فشلت المعاينة.");
      const review=el("section",undefined,"card");
      review.appendChild(el("h3","مراجعة نقل "+data.count+" فرداً بتاريخ "+data.businessDate,"section-title"));
      const t=table(["الرقم (ع)","الاسم","الموقع السابق","الجهة الجديدة"]);
      data.persons.forEach(p=>row(t.body,[p.serviceNumberA,p.name,p.sourceSiteName,data.destinationName]));
      review.appendChild(t.wrap);
      // Keep one operation ID for the lifetime of this exact preview. If the
      // response is lost, re-clicking confirms the same intent exactly once.
      const operationId=window.crypto?.randomUUID?.()||
        ("MOV-"+Date.now()+"-"+Math.random().toString(36).slice(2)+
         Math.random().toString(36).slice(2));
      const confirmButton=btn("تأكيد النقل النهائي",async()=>{
        const transferReview=window.AltamamDialog?.transferReview;
         if(typeof transferReview!=="function")
           return err(note,new Error("نافذة مراجعة النقل غير متاحة؛ أُلغي الحفظ."));
         if(!await transferReview(data))return;
        confirmButton.disabled=true;
        try{
          const result=await api().invoke(external?"CommitExternalTransfer":"CommitInternalTransfer",
            {previewToken:data.previewToken,operationId});
          reset();page=1;await load();
          note.replaceChildren(el("p","تم حفظ نقل "+result.data.count+" فرداً وسجل العملية.","settings-success"));
        }catch(e){
          // Retain the same preview and operationId for an exact network retry.
          // A genuine stale/expired preview still gets rejected by C#.
          err(note,e);
        }
        finally{confirmButton.disabled=false;}
      });
      review.append(confirmButton,btn("إلغاء",async()=>{if(await modalConfirm("إلغاء المعاينة دون نقل؟"))confirmation.replaceChildren();}));
      confirmation.replaceChildren(review);
    }catch(e){err(note,e);}
    finally{previewButton.disabled=false;}
  });
  card.appendChild(previewButton);
}
async function archive(host){
 const card=el("section",undefined,"card"),bar=el("div",undefined,"functional-toolbar");
 const kind=el("select"),site=el("select"),search=el("input"),
    dateFrom=el("input"),dateTo=el("input"),result=el("div");
  dateFrom.type="date";dateTo.type="date";
  dateFrom.setAttribute("aria-label","من تاريخ");
  dateTo.setAttribute("aria-label","إلى تاريخ");
  site.setAttribute("aria-label","فلتر الموقع");
  site.appendChild(opt("","جميع المواقع"));
 kind.setAttribute("aria-label","نوع النقل");
 kind.append(opt("","الكل"),opt("internal","داخلي"),opt("external","خارجي"));
 search.placeholder="بحث بالاسم أو الرقم (ع)";
 card.appendChild(el("h2","أرشيف التنقلات","section-title"));
 let page=1;
 bar.append(kind,site,dateFrom,dateTo,search,btn("بحث",()=>{page=1;load();}));
  try {
    const response=await api().invoke("QuerySites",{});
    for(const entry of response.data||[])site.appendChild(opt(entry.siteId,entry.name));
  }catch(e){err(result,e);}
 for(const selector of [kind,site,dateFrom,dateTo])
    selector.addEventListener("change",()=>{page=1;load();});
 card.append(bar,result);host.appendChild(card);
 async function load(){
   try{
     const data=(await api().invoke("QueryMovementArchive",{page,pageSize:50,
       type:kind.value,term:search.value.trim(),siteId:site.value,
        dateFrom:dateFrom.value,dateTo:dateTo.value})).data||{};
     if(!card.isConnected)return;
     const t=table(["الرقم (ع)","الاسم","النوع","الموقع السابق","الجهة الجديدة","التاريخ"]);
     for(const m of data.items||[])row(t.body,[m.serviceNumberA,m.name,m.type,m.sourceSite,m.destination,m.date]);
     if(!t.body.children.length){const tr=row(t.body,["لا توجد حركات مطابقة"]);tr.children[0].colSpan=6;}
     const pager=el("div",undefined,"functional-pager"),prev=btn("السابق",()=>{page--;load();}),next=btn("التالي",()=>{page++;load();});
     prev.disabled=page<=1;next.disabled=!data.hasNext;
     pager.append(el("span","إجمالي الحركات: "+(data.total||0)),prev,next);
     result.replaceChildren(t.wrap,pager);
   }catch(e){err(result,e);}
 }await load();
}
window.AltamamMovements=Object.freeze({render:(host,screen)=>screen==="transferArchive"?archive(host):
 transfer(host,screen==="transferExternal")});
})();
