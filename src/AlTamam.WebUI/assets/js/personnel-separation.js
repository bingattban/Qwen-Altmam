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
    if(screen==="separatePerson")return form(wrapper);
    if(screen==="separatedArchive")return archive(wrapper);
    wrapper.appendChild(el("p","هذه الشاشة غير متاحة.","admin-warning"));
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
    const results=el("div"),selected=el("div"),feedback=el("div"),preview=el("div");
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
        const sheet=el("section",undefined,"card");
        sheet.append(el("h3","تأكيد نهائي للفصل","section-title"),
          el("p","الفرد: "+data.name+" — الرقم (ع): "+data.serviceNumberA),
          el("p","الموقع: "+data.siteName+" — التاريخ: "+fullDate(data.businessDate)),
          el("p","سبب الفصل: "+data.reason),
          el("p","سيختفي من كشف القوة النشطة وتظل بياناته التاريخية محفوظة.","admin-warning"));
        const info=el("p","مهلة التأكيد: 5 ثوانٍ");
        // Once the five-second delay expires, this exact preview has one
        // stable retry identity even if the successful response is lost.
        const operationId=window.crypto?.randomUUID?.()||
          ("SEP-"+Date.now()+"-"+Math.random().toString(36).slice(2)+
           Math.random().toString(36).slice(2));
        const save=button("تأكيد الفصل",async()=>{
          if(!await dialogs.confirm("تأكيد فصل «"+data.name+"»؟ السبب: "+data.reason+" — ستبقى السجلات التاريخية محفوظة."))return;
          save.disabled=true;
          try{
            await api().invoke("CommitSeparation",{
              previewToken:data.previewToken,operationId
            });
            if(!root.isConnected)return;
            clear();selected.replaceChildren();person=null;page=1;
            feedback.replaceChildren(el("p","تم تنفيذ الفصل وتسجيله في الأرشيف وسجل العمليات.","settings-success"));
            await search();
          }catch(e){
            // A network error may happen after C# has committed successfully.
            // Keep this preview and operationId for a safe exact retry.
            error(feedback,e);
            save.disabled=false;
          }
        },"btn btn-danger");
        save.disabled=true;
        const cancel=button("إلغاء الطلب",()=>{
          if(confirm("إلغاء طلب الفصل دون حفظ أي تغيير؟"))clear();
        });
        sheet.append(info,save,cancel);
        preview.replaceChildren(sheet);
        let remaining=Math.max(5,Number(data.minimumConfirmationSeconds)||5);
        const timer=setInterval(()=>{
          if(!root.isConnected||expected!==previewCounter){
            clearInterval(timer);return;
          }
          remaining--;
          info.textContent=remaining>0?"مهلة التأكيد: "+remaining+" ثوانٍ":"يمكنك تأكيد الفصل الآن.";
          if(remaining<=0){save.disabled=false;clearInterval(timer);}
        },1000);
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
        const box=el("section",undefined,"card");
        box.append(el("h3","تفاصيل الفصل","section-title"),
          el("p",data.name+" — ع: "+data.serviceNumberA),
          el("p","الموقع قبل الفصل: "+data.siteName),
          el("p","التاريخ: "+fullDate(data.date)),
          el("p","سبب الفصل: "+data.reason),
          el("p","منفذ العملية (معرّف): "+data.actorUserId),
          button("إغلاق التفاصيل",()=>box.remove()));
        out.appendChild(box);
      }catch(e){error(out,e);}
    }
    await load();
  }
  window.AltamamSeparation=Object.freeze({render});
})();
