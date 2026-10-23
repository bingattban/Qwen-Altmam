"use strict";
// Actual internal/external transfers and read-only movement history.
// No writes occur until C# returns a one-use preview and user confirms it.
(function () {
  const api=()=>window.AltamamBridge;
  const element=(tag,text,cls)=>{
    const x=document.createElement(tag);
    if(text!==undefined)x.textContent=String(text);
    if(cls)x.className=cls;
    return x;
  };
  const button=(text,cb,cls)=>{
    const b=element("button",text,cls||"btn btn-outline");
    b.type="button";b.addEventListener("click",cb);return b;
  };
  const option=(id,name)=>{const o=element("option",name);o.value=id;return o;};
  const error=(target,e)=>target.replaceChildren(element("p",
    e&&e.message||"تعذر إكمال العملية؛ لم تُحفظ أي تغييرات.","admin-warning"));
  const today=()=>{
    const now=new Date();
    return [now.getFullYear(),String(now.getMonth()+1).padStart(2,"0"),
      String(now.getDate()).padStart(2,"0")].join("-");
  };
  const table=(cols)=>{
    const container=element("div",undefined,"functional-table-wrap");
    const t=element("table",undefined,"data-table functional-table");
    const tr=element("tr");cols.forEach(c=>tr.appendChild(element("th",c)));
    const head=element("thead"),body=element("tbody");
    head.appendChild(tr);t.append(head,body);container.appendChild(t);
    return {container,body};
  };
  function render(root,kind,onBack) {
    const external=kind==="external",archive=kind==="archive";
    let alive=true,serial=0,activePreview=0,pageNo=1,fetching=false;
    const selected=new Map();
    const host=element("div",undefined,"movement-page");
    root.replaceChildren(host);
    const top=element("section",undefined,"card");
    top.append(button("العودة إلى التنقلات",()=>{alive=false;serial++;onBack();}),
      element("h2",archive?"أرشيف التنقلات":
        external?"النقل الخارجي":"النقل الداخلي","section-title"));
    host.appendChild(top);
    const card=element("section",undefined,"card"),
      controls=element("div",undefined,"functional-toolbar"),
      feedback=element("div",undefined,"admin-note"),
      listing=element("div"),review=element("div");
    card.append(controls,feedback,listing,review);host.appendChild(card);
    function clearPreview(){activePreview++;review.replaceChildren();}
    const isCurrent=()=>alive&&host.isConnected;
    if(archive) {
      const type=element("select"),search=element("input");
      type.setAttribute("aria-label","نوع النقل");
      type.append(option("","جميع التنقلات"),option("internal","داخلي"),
        option("external","خارجي"));
      search.placeholder="بحث بالاسم أو الرقم (ع)";
      search.setAttribute("aria-label","بحث بالأفراد المنقولين");
      controls.append(type,search,button("بحث",()=>{pageNo=1;loadArchive();},
        "btn btn-primary"));
      async function loadArchive(){
        const ticket=++serial;
        listing.replaceChildren(element("p","جارٍ قراءة أرشيف التنقلات..."));
        try{
          const data=(await api().invoke("QueryMovementArchive",{
            type:type.value,term:search.value.trim(),page:pageNo,pageSize:50
          })).data||{};
          if(!isCurrent()||ticket!==serial)return;
          const grid=table(["الرقم (ع)","الاسم","الموقع السابق","نوع النقل",
            "الموقع المنقول إليه","تاريخ النقل"]);
          for(const person of data.items||[]){
            const tr=element("tr");
            [person.serviceNumberA,person.name,person.sourceSite,person.type,
              person.destination,person.date].forEach(x=>tr.appendChild(element("td",x||"—")));
            grid.body.appendChild(tr);
          }
          if(!grid.body.children.length){
            const tr=element("tr"),td=element("td","لا يوجد أرشيف مطابق للبحث");
            td.colSpan=6;tr.appendChild(td);grid.body.appendChild(tr);
          }
          const pager=element("div",undefined,"functional-pager");
          const prev=button("السابق",()=>{pageNo--;loadArchive();});
          const next=button("التالي",()=>{pageNo++;loadArchive();});
          prev.disabled=pageNo<=1;next.disabled=!data.hasNext;
          pager.append(element("span","الإجمالي: "+(data.total||0)),prev,next);
          listing.replaceChildren(grid.container,pager);
        }catch(e){if(isCurrent()&&ticket===serial)error(listing,e);}
      }
      void loadArchive();
      return;
    }
    const date=today(),source=element("select"),
      dest=external?element("input"):element("select"),
      count=element("span","تم تحديد 0 فرد"),
      destLabel=element("span",external?"ينقل خارج البرنامج إلى":"ينقل إلى"),
      cancel=()=>{selected.clear();count.textContent="تم تحديد 0 فرد";clearPreview();};
    source.setAttribute("aria-label","الموقع الحالي");
    dest.setAttribute("aria-label",external?"الموقع الخارجي الإجباري":"الموقع المنقول إليه");
    if(external)dest.placeholder="اكتب اسم الجهة الخارجية التي سينتقل إليها الفرد";
    source.appendChild(option("","اختر الموقع الحالي"));
    if(!external)dest.appendChild(option("","اختر الموقع المنقول إليه"));
    controls.append(source,element("span","⇠"),destLabel,dest,
      button("تحديث الكشف",()=>{cancel();loadRoster();}),
      button("معاينة النقل",preview,"btn btn-primary"));
    card.insertBefore(count,listing);
    const filterDest=()=>{
      if(external)return;
      const previous=dest.value;
      dest.replaceChildren(option("","اختر الموقع المنقول إليه"));
      for(const s of sites)
        if(s.siteId!==source.value&&s.active!==false)
          dest.appendChild(option(s.siteId,s.name));
      if(Array.from(dest.children).some(o=>o.value===previous))dest.value=previous;
      else dest.value="";
    };
    let sites=[];
    const showCount=()=>count.textContent="تم تحديد "+selected.size+" فرد (الحد 100)";
    source.addEventListener("change",()=>{pageNo=1;cancel();filterDest();loadRoster();});
    dest.addEventListener("change",clearPreview);
    if(external)dest.addEventListener("input",clearPreview);
    let initialized=false;
    async function loadRoster(){
      const ticket=++serial;
      if(!source.value){
        listing.replaceChildren(element("p","اختر الموقع الحالي لعرض الأفراد."));
        return;
      }
      listing.replaceChildren(element("p","جارٍ تحميل أفراد الموقع..."));
      try{
        const data=(await api().invoke("QueryDailyRoster",{
          businessDate:date,siteId:source.value,statusId:"",
          page:pageNo,pageSize:50
        })).data||{};
        if(ticket!==serial||!isCurrent())return;
        const grid=table(["تحديد","الرقم (ع)","الاسم","الموقع","الحالة"]);
        for(const person of data.items||[]){
          const tr=element("tr"),cell=element("td"),
            check=element("input");
          check.type="checkbox";check.checked=selected.has(person.personId);
          check.setAttribute("aria-label","تحديد "+person.name);
          check.addEventListener("change",()=>{
            if(check.checked&&selected.size>=100){
              check.checked=false;return error(feedback,
                new Error("الحد الأقصى للعملية الواحدة هو 100 فرد."));
            }
            if(check.checked)selected.set(person.personId,person);
            else selected.delete(person.personId);
            showCount();clearPreview();
          });
          cell.appendChild(check);tr.appendChild(cell);
          [person.serviceNumberA,person.name,person.siteName,person.statusName]
            .forEach(value=>tr.appendChild(element("td",value)));
          grid.body.appendChild(tr);
        }
        if(!grid.body.children.length){
          const tr=element("tr"),td=element("td","لا يوجد أفراد في هذا الموقع.");
          td.colSpan=5;tr.appendChild(td);grid.body.appendChild(tr);
        }
        const pager=element("div",undefined,"functional-pager");
        const prev=button("السابق",()=>{pageNo--;loadRoster();});
        const next=button("التالي",()=>{pageNo++;loadRoster();});
        prev.disabled=pageNo<=1;next.disabled=!data.hasNext;
        pager.append(element("span","الإجمالي: "+(data.total||0)),prev,next);
        listing.replaceChildren(grid.container,pager);
      }catch(e){if(isCurrent()&&ticket===serial)error(listing,e);}
    }
    async function preview(){
      const where=source.value,externalName=external?dest.value.trim():"";
      if(!where||selected.size===0||(external?externalName.length<2:!dest.value))
        return error(feedback,new Error("حدد المصدر والوجهة والأفراد قبل معاينة النقل."));
      if(!external&&where===dest.value)
        return error(feedback,new Error("يجب اختلاف موقع المصدر والوجهة."));
      feedback.replaceChildren();clearPreview();
      const version=activePreview;
      try{
        const data=(await api().invoke(external?"PreviewExternalTransfer":"PreviewInternalTransfer",{
          businessDate:date,sourceSiteId:where,
          destinationSiteId:external?null:dest.value,
          destinationExternalName:external?externalName:null,
          personIds:Array.from(selected.keys()).sort()
        })).data||{};
        if(!isCurrent()||version!==activePreview)return;
        if(!data.previewToken||!Array.isArray(data.persons))
          throw new Error("لم يقبل الخادم معاينة النقل.");
        const box=element("section",undefined,"card");
        box.append(element("h3","مراجعة النقل قبل الاعتماد","section-title"),
          element("p","العدد: "+data.count+" — من "+sites.find(s=>s.siteId===where)?.name+
            " إلى "+data.destinationName));
        const grid=table(["الرقم (ع)","الاسم","الموقع الحالي","ينقل إلى"]);
        for(const person of data.persons){
          const tr=element("tr");
          [person.serviceNumberA,person.name,person.sourceSiteName,data.destinationName]
            .forEach(v=>tr.appendChild(element("td",v)));
          grid.body.appendChild(tr);
        }
        box.appendChild(grid.container);
        const confirmButton=button("تأكيد وتنفيذ النقل",async()=>{
          if(!confirm("تنفيذ النقل وتسجيله في الأرشيف وسجل العمليات؟"))return;
          confirmButton.disabled=true;
          try{
            const result=(await api().invoke(external?"CommitExternalTransfer":
              "CommitInternalTransfer",{previewToken:data.previewToken})).data||{};
            if(!isCurrent())return;
            cancel();pageNo=1;await loadRoster();
            feedback.replaceChildren(element("p",
              "تم نقل "+(result.count||0)+" فرد وتسجيل العملية بنجاح.","settings-success"));
          }catch(e){if(isCurrent())error(feedback,e);clearPreview();}
          finally{confirmButton.disabled=false;}
        });
        box.append(confirmButton,button("إلغاء",()=>{
          if(confirm("إلغاء معاينة النقل دون حفظ؟"))clearPreview();
        }));
        review.appendChild(box);
      }catch(e){if(isCurrent())error(feedback,e);}
    }
    (async()=>{
      try{
        const data=(await api().invoke("QuerySites",{})).data;
        if(!isCurrent())return;
        sites=Array.isArray(data)?data.filter(s=>s.active!==false):[];
        for(const site of sites)source.appendChild(option(site.siteId,site.name));
        initialized=true;filterDest();
        listing.replaceChildren(element("p","اختر الموقع الحالي لعرض الأفراد."));
      }catch(e){if(isCurrent())error(feedback,e);}
    })();
  }
  window.AltamamMovement=Object.freeze({render});
})();
