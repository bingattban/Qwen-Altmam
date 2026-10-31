"use strict";
/* Separation and historic archive. Never delete a person/attendance record.
   The server verifies actor, preview, five-second delay and write transaction. */
(function(){
  const api=()=>window.AltamamBridge;
  const dialogs={
    confirm:text=>window.AltamamDialog?.confirm(text,{danger:true,title:"تأكيد الفصل"}) ??
      Promise.resolve(confirm(text)),
    reason:text=>window.AltamamDialog?.reason(text,{danger:true,
      minLength:5,title:"سبب الفصل"}) ?? Promise.resolve(prompt(text))
  };
  const el=(tag,text,cls)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=String(text);if(cls)e.className=cls;return e;};
  const button=(text,handler,cls)=>{const b=el("button",text,cls||"btn btn-outline");b.type="button";b.addEventListener("click",handler);return b;};
  const option=(value,name)=>{const e=el("option",name);e.value=value;return e;};
  const error=(to,e)=>{to.replaceChildren(el("p",e?.message||"فشل الإجراء.","admin-warning"));};
  const date=()=>{const n=new Date();return [n.getFullYear(),String(n.getMonth()+1).padStart(2,"0"),String(n.getDate()).padStart(2,"0")].join("-");};
  const fullDate=s=>{const d=new Date(s+"T12:00:00");return Number.isNaN(d.valueOf())?s:
    new Intl.DateTimeFormat("ar",{weekday:"long",day:"numeric",month:"long",year:"numeric"}).format(d);};
  function table(headers){
    const wrap=el("div",undefined,"functional-table-wrap"),tab=el("table",undefined,"functional-table data-table"),
          tr=el("tr"),head=el("thead"),body=el("tbody");
    headers.forEach(t=>tr.appendChild(el("th",t)));
    head.appendChild(tr);tab.append(head,body);wrap.appendChild(tab);
    return {wrap,body};
  }
  function row(body,values){
    const r=el("tr");
    values.forEach(x=>r.appendChild(el("td",x??"")));
    body.appendChild(r);return r;
  }
  function render(host,screen){
    const wrapper=el("section",undefined,"card");
    wrapper.classList.add("separation-screen");
    host.appendChild(wrapper);
    // One owning screen: the historical register is an internal tab rather
    // than a second management tile. Legacy deep links choose the same tab.
    const tabs=el("div",undefined,"separation-tabs");
    tabs.setAttribute("role","tablist");
    tabs.setAttribute("aria-label","الفصل والأرشيف");
    const content=el("div",undefined,"separation-content");
    const separateTab=button("فصل فرد",()=>activate("separatePerson"));
    const archiveTab=button("أرشيف الفصل",()=>activate("separatedArchive"));
    for(const tab of [separateTab,archiveTab])tab.setAttribute("role","tab");
    tabs.append(separateTab,archiveTab);
    wrapper.append(tabs,content);
    function activate(selected){
      const isArchive=selected==="separatedArchive";
      separateTab.className=isArchive?"btn btn-outline":"btn btn-primary";
      archiveTab.className=isArchive?"btn btn-primary":"btn btn-outline";
      separateTab.setAttribute("aria-selected",String(!isArchive));
      archiveTab.setAttribute("aria-selected",String(isArchive));
      separateTab.setAttribute("tabindex",isArchive?"-1":"0");
      archiveTab.setAttribute("tabindex",isArchive?"0":"-1");
      const pane=el("div",undefined,"separation-pane");
      content.replaceChildren(pane);
      return isArchive?archive(pane):form(pane);
    }
    return activate(screen==="separatedArchive"?"separatedArchive":"separatePerson");
  }
  async function form(root) {
    root.replaceChildren(el("h2","فصل فرد من القوة","section-title"),
      el("p","تُحفظ هوية الفرد وتاريخه وتحضيراته السابقة. الفصل لا يحذف أي سجل.","section-description"));
    const today=date();
    const input=el("input");input.type="search";input.placeholder="بحث بالاسم أو الرقم (ع)";
    input.setAttribute("aria-label","البحث عن فرد نشط");
    const find=button("بحث",search,"btn btn-outline");
    const bar=el("div",undefined,"functional-toolbar");
    bar.append(input,find);
    const results=el("div"),selected=el("div",undefined,"separation-selected"),
      feedback=el("div"),preview=el("div",undefined,"separation-retry");
    selected.setAttribute("role","dialog");
    selected.setAttribute("aria-label","الفرد المحدد وسبب الفصل");
    preview.setAttribute("role","dialog");
    preview.setAttribute("aria-label","إعادة مراجعة الفصل");
    root.append(bar,results,selected,feedback,preview);
    let person=null,page=1,seq=0,previewCounter=0;
    const clear=()=>{previewCounter++;preview.replaceChildren();};
    async function search(){
      person=null;selected.replaceChildren();clear();
      const token=++seq;
      results.replaceChildren(el("p","جارٍ البحث في القوة..."));
      try{
        const response=await api().invoke("QueryPersons",{
          term:input.value.trim(),organizationalStatus:1,includeArchived:false,
          page,pageSize:100
        });
        if(token!==seq||!root.isConnected)return;
        const data=response.data||{},t=table(["الرقم (ع)","الاسم","الموقع","اختيار"]);
        for(const entry of data.items||[]){
          if(entry.active===false)continue;
          const tr=row(t.body,[entry.serviceNumberA,entry.displayName,entry.siteName]);
          const td=el("td");
          td.appendChild(button("اختيار",()=>{
            person=entry;clear();feedback.replaceChildren();
            selected.replaceChildren(el("h3","الفرد المحدد","section-title"),
              el("p",entry.displayName+" — الرقم (ع): "+entry.serviceNumberA+" — "+entry.siteName));
            selected.appendChild(button("✕ إغلاق",()=>{person=null;selected.replaceChildren();clear();},
              "btn btn-outline app-modal-close"));
            selected.appendChild(button("تحديد سبب الفصل والمعاينة",async()=>{
              const reason=await dialogs.reason("بيانات الفصل: "+entry.displayName+
                " — الرقم (ع): "+entry.serviceNumberA+" — الموقع: "+entry.siteName);
              if(reason===null)return;
              await requestPreview(entry,reason);
            },"btn btn-primary"));
          },"btn btn-outline"));
          tr.appendChild(td);
        }
        if(!t.body.children.length){
          const tr=el("tr"),cell=el("td","لا يوجد أفراد مطابقون للبحث.");
          cell.colSpan=4;tr.appendChild(cell);t.body.appendChild(tr);
        }
        const pager=el("div",undefined,"functional-pager");
        const previous=button("السابق",()=>{page--;search();});
        const next=button("التالي",()=>{page++;search();});
        previous.disabled=page<=1;next.disabled=!data.hasNext;
        pager.append(el("span","العدد: "+(data.total||0)),previous,next);
        results.replaceChildren(t.wrap,pager);
      }catch(e){if(token===seq)error(results,e);}
    }
    async function requestPreview(entry,reason) {
      const text=reason.trim();
      if(text.length<5)return error(feedback,new Error("اكتب سبب فصل لا يقل عن خمسة أحرف."));
      clear();feedback.replaceChildren();
      const expected=previewCounter;
      try{
        const response=await api().invoke("PreviewSeparation",{
          personId:entry.personId,reason:text,businessDate:today
        });
        if(expected!==previewCounter||!root.isConnected||person?.personId!==entry.personId)return;
        const data=response.data||{};
        if(!data.previewToken)throw Error("لم تصدر الخدمة معاينة صالحة.");
        // The full separation preview (including the five-second safety delay)
        // is a modal, not an embedded card requiring a second navigation.
        const detailsReview=window.AltamamDialog?.detailsReview;
        if(typeof detailsReview!=="function")
          throw Error("نافذة معاينة الفصل غير متاحة؛ أُلغي الاعتماد.");
        const operationId=window.crypto?.randomUUID?.()||
          ("SEP-"+Date.now()+"-"+Math.random().toString(36).slice(2)+
           Math.random().toString(36).slice(2));
        const summary={rows:[
          ["اسم الفرد",data.name],["الرقم (ع)",data.serviceNumberA],
          ["الموقع",data.siteName],["التاريخ",fullDate(data.businessDate)],
          ["سبب الفصل",data.reason],
          ["الأثر","يخرج الفرد من كشف القوة النشطة وتظل بياناته التاريخية محفوظة."]
        ]};
        async function commitSamePlan(){
          try{
            await api().invoke("CommitSeparation",{previewToken:data.previewToken,operationId});
            if(!root.isConnected)return;
            clear();selected.replaceChildren();person=null;page=1;
            feedback.replaceChildren(el("p","تم تنفيذ الفصل وتسجيله في الأرشيف وسجل العمليات.","settings-success"));
            await search();
          }catch(error){
            errorToUser(error);
            // Recover from a lost response without changing the preview identity.
            preview.replaceChildren(
              button("✕ إغلاق",()=>preview.replaceChildren(),"btn btn-outline app-modal-close"),
              button("إعادة مراجعة وتأكيد الفصل",async()=>{
              if(await detailsReview(summary,{title:"معاينة فصل الفرد",
                message:"المعاينة نفسها محفوظة للمحاولة الآمنة.",
                confirmLabel:"تأكيد الفصل",danger:true}))
                await commitSamePlan();
            }));
          }
        }
        function errorToUser(err){error(feedback,err);}
        if(await detailsReview(summary,{title:"معاينة فصل الفرد",
          message:"راجع جميع التفاصيل. لن يُنفذ الفصل قبل انتهاء مهلة التأكيد.",
          confirmLabel:"تأكيد الفصل",danger:true,
          delaySeconds:Math.max(5,Number(data.minimumConfirmationSeconds)||5)})){
          if(expected!==previewCounter||!root.isConnected||person?.personId!==entry.personId)return;
          await commitSamePlan();
        }
      }catch(e){error(feedback,e);}
    }
    await search();
  }
  async function archive(root) {
    root.replaceChildren(el("h2","أرشيف الفصل","section-title"),
      el("p","الأرشيف للقراءة فقط، ولا يحذف سجلات الأفراد أو تحضيراتهم السابقة.","section-description"));
    const query=el("input");query.type="search";query.placeholder="بحث بالاسم أو الرقم (ع)";
    const sites=el("select");sites.appendChild(option("","جميع المواقع"));
    const bar=el("div",undefined,"functional-toolbar");
    const out=el("div"),feedback=el("div");
    root.append(bar,feedback,out);
    bar.append(query,sites,button("بحث",()=>{page=1;load();},"btn btn-primary"),
      button("تصدير أرشيف الفصل إلى Excel",async()=>{
        const confirm=await dialogs.confirm("هل تؤكد تصدير أرشيف الفصل حسب الاسم والموقع المحددين؟ "+
          "سيُحفظ الملف في مجلد التصدير المعرّف بالإعدادات.");
        if(!confirm)return;
        feedback.replaceChildren(el("p","جارٍ إعداد ملف Excel..."));
        try{
          const result=await api().invoke("ExportPersonLifetimeExcel",{
            mode:"separationArchive",term:query.value.trim(),siteId:sites.value
          });
          if(root.isConnected)feedback.replaceChildren(el("p",
            "تم حفظ أرشيف الفصل: "+result.data.filePath,"settings-success"));
        }catch(e){if(root.isConnected)error(feedback,e);}
      },"btn btn-outline"));
    let page=1,sequence=0;
    try{
      const response=await api().invoke("QuerySites",{});
      for(const s of response.data||[])sites.appendChild(option(s.siteId,s.name));
    }catch(e){error(out,e);}
    sites.addEventListener("change",()=>{page=1;load();});
    async function load(){
      const request=++sequence;
      out.replaceChildren(el("p","جارٍ تحميل أرشيف الفصل..."));
      try{
        const response=await api().invoke("QuerySeparationArchive",{
          term:query.value.trim(),siteId:sites.value,page,pageSize:100
        });
        if(request!==sequence||!root.isConnected)return;
        const data=response.data||{},t=table(["الرقم (ع)","الاسم","الموقع","تاريخ الفصل","التفاصيل"]);
        for(const entry of data.items||[]){
          const tr=row(t.body,[entry.serviceNumberA,entry.name,entry.siteName,fullDate(entry.date)]);
          const td=el("td");
          td.appendChild(button("عرض",()=>details(entry.movementId)));
          tr.appendChild(td);
        }
        if(!t.body.children.length){
          const tr=el("tr"),td=el("td","لا توجد حالات فصل مسجلة مطابقة.");
          td.colSpan=5;tr.appendChild(td);t.body.appendChild(tr);
        }
        const paginator=el("div",undefined,"functional-pager");
        const prev=button("السابق",()=>{page--;load();});
        const next=button("التالي",()=>{page++;load();});
        prev.disabled=page<=1;next.disabled=!data.hasNext;
        paginator.append(el("span","إجمالي المفصولين: "+(data.total||0)),prev,next);
        out.replaceChildren(t.wrap,paginator);
      }catch(e){if(request===sequence)error(out,e);}
    }
    async function details(id) {
      try{
        const response=await api().invoke("GetSeparationDetails",{movementId:id});
        const data=response.data||{};
        const show=window.AltamamDialog?.detailsReview;
        if(typeof show!=="function")
          throw Error("نافذة تفاصيل الأرشيف غير متاحة؛ لم يتغير أي سجل.");
        await show({rows:[
          ["الاسم عند الفصل",data.name],
          ["الرقم (ع) عند الفصل",data.serviceNumberA],
          ["الموقع قبل الفصل",data.siteName],
          ["تاريخ الفصل",fullDate(data.date)],
          ["سبب الفصل",data.reason],
          ["منفذ العملية (معرّف)",data.actorUserId]
        ]},{title:"تفاصيل الفصل المحفوظة في الأرشيف",
          message:"هذه بيانات تاريخية للقراءة فقط؛ لا تعيد الفصل ولا تغيّر السجل.",
          confirmLabel:"إغلاق التفاصيل"});
      }catch(e){error(out,e);}
    }
    await load();
  }
  window.AltamamSeparation=Object.freeze({render});
})();
