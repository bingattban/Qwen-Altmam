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
  search.placeholder="بحث بالاسم أو الرقم (ع)";
  const note=el("div"),list=el("div"),
      confirmation=el("div",undefined,"movement-retry-panel"),
      counter=el("p","الأفراد المحددون: 0","movement-selection-counter");
  const selected=new Set();let sites=[],page=1,serial=0;
  function reset(){selected.clear();counter.textContent="الأفراد المحددون: 0";confirmation.replaceChildren();}
  function destinations(){if(external)return;to.replaceChildren(opt("","اختر الموقع المنقول إليه"));sites.filter(s=>s.siteId!==from.value).forEach(s=>to.appendChild(opt(s.siteId,s.name)));}
  card.appendChild(el("h2",external?"النقل الخارجي":"النقل الداخلي","section-title"));
  card.appendChild(el("p",external
     ?"يخرج الفرد من كشوف القوة النشطة بعد النقل الخارجي، مع الاحتفاظ بتاريخه في الأرشيف. يسمح بالنقل حتى بعد تحضير اليوم أو قفله."
     :"يبقى المنقول داخلياً في القوة النشطة والتمام الشهري بموقعه الجديد. يسمح بالنقل بعد التحضير والقفل مع الاحتفاظ بالحالة السابقة؛ والتحضير الجديد يبدأ غداً.",
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
        term:search.value.trim(),page,pageSize:100})).data||{};
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
      note.replaceChildren(el("p","جارٍ إعداد معاينة النقل والتحقق من المواقع والأفراد...","section-description"));
      const data=(await api().invoke(external?"PreviewExternalTransfer":"PreviewInternalTransfer",
        payload,{timeoutMs:60000})).data||{};
      if(!card.isConnected)return;
      const expectedIds=new Set(payload.personIds);
      if(typeof data.previewToken!=="string"||data.previewToken.length<32||
         !Array.isArray(data.persons)||data.persons.length!==expectedIds.size||
         data.count!==expectedIds.size||typeof data.destinationName!=="string"||
         !data.persons.every(p=>p&&expectedIds.has(p.personId)&&
           p.sourceSiteId===payload.sourceSiteId)||
         new Set(data.persons.map(p=>p.personId)).size!==expectedIds.size)
        throw Error("لم تتطابق معاينة النقل من الخدمة مع الأفراد المحددين؛ لم تُنفذ العملية.");
      note.replaceChildren();
      // The complete server-verified movement preview opens immediately as a modal.
      // Keep the same token and operationId for a possible lost-response retry.
      const transferReview=window.AltamamDialog?.transferReview;
      if(typeof transferReview!=="function")
        throw Error("نافذة معاينة النقل غير متاحة؛ لم يُحفظ أي تغيير.");
      const operationId=window.crypto?.randomUUID?.()||
        ("MOV-"+Date.now()+"-"+Math.random().toString(36).slice(2)+
         Math.random().toString(36).slice(2));
      let submitting=false;
      async function commitSamePlan(){
        if(submitting||!card.isConnected)return;
        // The user may have changed the source or destination while a native
        // preview was in flight. Never commit a plan for a different choice.
        if(from.value!==payload.sourceSiteId ||
           (external ? outside.value.trim()!==payload.destinationExternalName :
            to.value!==payload.destinationSiteId) ||
           selected.size!==expectedIds.size ||
           !Array.from(selected).every(id=>expectedIds.has(id))){
          return err(note,new Error("تغيّر اختيار الأفراد أو مواقع النقل أثناء المعاينة؛ أعد معاينة الخطة."));
        }
        submitting=true;
        confirmation.replaceChildren();
        note.replaceChildren(el("p","جارٍ تثبيت النقل في قاعدة البيانات وتسجيل الحركة...","section-description"));
        try{
          const response=await api().invoke(
            external?"CommitExternalTransfer":"CommitInternalTransfer",
            {previewToken:data.previewToken,operationId},{timeoutMs:120000});
          if(!response?.data||response.data.count!==expectedIds.size||
             response.data.destinationName!==data.destinationName){
            const error=new Error("لم يتطابق عدد أو وجهة النقل المؤكد مع المعاينة. تحقّق من الأرشيف قبل إعادة المحاولة.");
            error.errorCode="BridgeResultMismatch";throw error;
          }
          if(!card.isConnected)return;
          reset();page=1;
          note.replaceChildren(el("p","تم حفظ نقل "+expectedIds.size+
            " فرداً إلى "+data.destinationName+" وتسجيل العملية.","settings-success"));
          // Refresh is a separate read. A failed refresh must not turn an
          // already committed, receipted transfer into a false retry prompt.
          await load();
        }catch(error){
          if(!card.isConnected)return;
          err(note,error);
          if(error?.errorCode==="BridgeTimeout"||error?.errorCode==="BridgeOffline"){
            // The write may have succeeded before its response was lost:
            // retry only with the exact same previewToken + operationId.
            const dismiss=btn("إغلاق نافذة إعادة المحاولة",()=>confirmation.replaceChildren());
            dismiss.className="btn btn-outline app-modal-close";
            confirmation.replaceChildren(dismiss,
              el("p","لم تتأكد نتيجة الحفظ بعد فقدان الاتصال بالخدمة؛ يمكن إعادة إرسال نفس العملية دون تكرارها.","section-description"),
              btn("إعادة مراجعة ومحاولة التأكيد",async()=>{
                if(submitting)return;
                if(await transferReview(data))await commitSamePlan();
              }));
          }else{
            confirmation.replaceChildren(el("p",
              "رفضت الخدمة الخطة أو تعذّر إثبات نتيجتها؛ تحقّق من الأرشيف ثم أعد معاينة النقل عند الحاجة.",
              "section-description"));
          }
        }finally{submitting=false;}
      }
      if(await transferReview(data))await commitSamePlan();
    }catch(e){err(note,e);}
    finally{previewButton.disabled=false;}
  });
  card.appendChild(previewButton);
}
async function archive(host,mode){
 const card=el("section",undefined,"card"),bar=el("div",undefined,"functional-toolbar");
 const site=el("select"),search=el("input"),
    dateFrom=el("input"),dateTo=el("input"),result=el("div"),notice=el("div");
  dateFrom.type="date";dateTo.type="date";
  dateFrom.setAttribute("aria-label","من تاريخ");
  dateTo.setAttribute("aria-label","إلى تاريخ");
  site.setAttribute("aria-label","فلتر الموقع");
  site.appendChild(opt("","جميع المواقع"));
 search.type="search";search.placeholder="بحث بالاسم أو الرقم (ع)";
 const archiveType=mode==="all"?"all":mode==="external"?"external":"internal";
 const allMovements=archiveType==="all";
 const title=allMovements?"سجل التنقلات والعودة":
   archiveType==="external"?"أرشيف النقل الخارجي":"أرشيف النقل الداخلي";
 card.appendChild(el("h2",title,"section-title"));
 card.appendChild(el("p",allMovements?
   "سجل للقراءة فقط: النقل الداخلي والخارجي وحركات العودة المعتمدة من إضافة فرد. الفصل في أرشيف الفصل المستقل، ولا توجد استمارة عودة هنا.":
   archiveType==="external"?"يعرض النقل الخارجي فقط ويحتفظ بتاريخ كل فرد مستقلاً.":
   "يعرض النقل الداخلي فقط مع الموقع السابق والوجهة والتاريخ.","section-description"));
 let page=1,archiveRequest=0;
 bar.append(site,dateFrom,dateTo,search,btn("بحث",()=>{page=1;load();}));
 if(!allMovements)bar.appendChild(btn("تصدير "+title+" إلى Excel",async()=>{
   const payload={mode:"movementArchive",type:archiveType,
     term:search.value.trim(),siteId:site.value,
     dateFrom:dateFrom.value,dateTo:dateTo.value};
   if(!await modalConfirm("تصدير "+title+" حسب البحث والفلاتر المعروضة؟ "+
     "سيُحفظ Excel في مجلد التقارير المحدد بالإعدادات."))return;
   notice.replaceChildren(el("p","جارٍ تجهيز ملف Excel..."));
   try{
     const response=await api().invoke("ExportPersonLifetimeExcel",payload);
     if(!card.isConnected)return;
     notice.replaceChildren(el("p","تم إنشاء الملف: "+response.data.filePath,"settings-success"));
   }catch(e){if(card.isConnected)err(notice,e);}
 }));
  try {
    const response=await api().invoke("QuerySites",{});
    for(const entry of response.data||[])site.appendChild(opt(entry.siteId,entry.name));
  }catch(e){err(result,e);}
 for(const selector of [site,dateFrom,dateTo])
    selector.addEventListener("change",()=>{page=1;load();});
 card.append(bar,notice,result);host.appendChild(card);
 async function load(){
   const request=++archiveRequest;
   try{
     const data=(await api().invoke("QueryMovementArchive",{page,pageSize:100,
       type:archiveType,term:search.value.trim(),siteId:site.value,
        dateFrom:dateFrom.value,dateTo:dateTo.value})).data||{};
     if(request!==archiveRequest||!card.isConnected)return;
     const t=table(["الرقم (ع)","الاسم","النوع","الموقع السابق","الجهة الجديدة","التاريخ","التفاصيل"]);
     for(const m of data.items||[]){
       const tr=row(t.body,[m.serviceNumberA,m.name,m.type,m.sourceSite,m.destination,m.date]);
       const cell=el("td");
       cell.appendChild(btn("تفاصيل",async()=>{
         notice.replaceChildren();
         try{
           const response=await api().invoke("GetMovementDetails",{movementId:m.movementId});
           const detail=response.data||{};
           if(detail.movementId!==m.movementId)
             throw Error("لم تتطابق بيانات الحركة مع الأرشيف.");
           const show=window.AltamamDialog?.detailsReview;
           if(typeof show!=="function")
             throw Error("نافذة عرض تفاصيل النقل غير متاحة.");
           await show({rows:[
             ["الاسم وقت النقل",detail.name],
             ["الرقم (ع) وقت النقل",detail.serviceNumberA],
             ["نوع النقل",detail.type],
             ["الموقع السابق",detail.sourceSite],
             ["الجهة الجديدة",detail.destination],
             [detail.type==="عودة"?"تاريخ العودة":"تاريخ النقل",detail.date],
             ["وقت تسجيل العملية",detail.committedAtUtc],
             ["منفذ العملية",detail.actorUsername]
           ]},{title:"تفاصيل النقل المحفوظة في الأرشيف",
             message:"بيانات تاريخية للقراءة فقط؛ لا تُعدّل حركة النقل أو كشوف القوة.",
             confirmLabel:"إغلاق التفاصيل"});
         }catch(e){err(notice,e);}
       }));
       tr.appendChild(cell);
     }
     if(!t.body.children.length){const tr=row(t.body,["لا توجد حركات مطابقة"]);tr.children[0].colSpan=7;}
     const pager=el("div",undefined,"functional-pager"),prev=btn("السابق",()=>{page--;load();}),next=btn("التالي",()=>{page++;load();});
     prev.disabled=page<=1;next.disabled=!data.hasNext;
     pager.append(el("span","إجمالي الحركات: "+(data.total||0)),prev,next);
     result.replaceChildren(t.wrap,pager);
   }catch(e){if(request===archiveRequest&&card.isConnected)err(result,e);}
 }await load();
}
window.AltamamMovements=Object.freeze({render:(host,screen)=>
  screen==="archiveInternal"?archive(host,"internal"):
  screen==="archiveExternal"?archive(host,"external"):
  screen==="archiveAll"?archive(host,"all"):
  screen==="transferInternal"?transfer(host,false):
  screen==="transferExternal"?transfer(host,true):
  host.appendChild(el("p","شاشة النقل غير معروفة.","admin-warning"))});
})();
