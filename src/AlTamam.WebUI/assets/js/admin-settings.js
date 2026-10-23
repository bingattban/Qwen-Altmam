"use strict";
/* Administrative and settings screens. All writes require a real C# bridge action.
   Unsupported legacy routes are listed honestly, not simulated or auto-saved. */
(function () {
  const target = () => document.getElementById("appViewport");
  const api = () => window.AltamamBridge;
  let currentRequest = 0;
  let state = { session: null, active: "home", page: 1, term: "", siteId: "" };
  const sections = [
    {key:"force",title:"إدارة القوة",children:[
      ["attendance","التمام"],["persons","تعديل الأسماء"],["transfers","التنقلات"],
      ["reports","التقارير والفصل"]]},
    {key:"config",title:"الحالات والمواقع",children:[["sites","المواقع"],["statuses","الحالات"]]},
    {key:"sheets",title:"الكشوفات",children:[["import","استيراد Excel"],
      ["export","تصدير Excel"],["layout","تعديل هيكل الكشف"]]},
    {key:"users",title:"المستخدمون",children:[["accounts","إدارة الحسابات"]]},
    {key:"audit",title:"سجل العمليات",children:[["log","العمليات المسجلة"]]}
  ];
  function node(tag,klass,value) {
    const x=document.createElement(tag);
    if(klass)x.className=klass;
    if(value!==undefined)x.textContent=String(value);
    return x;
  }
  function btn(label,callback,klass) {
    const b=node("button",klass||"btn btn-outline",label);b.type="button";
    b.addEventListener("click",callback);return b;
  }
  function card(title) {
    const c=node("section","card admin-card");
    c.appendChild(node("h2","section-title",title));return c;
  }
  function field(label,value,type) {
    const wrap=node("label","admin-input");
    const name=node("span","",label);
    const input=node("input","form-control");
    input.type=type||"text";input.value=value||"";
    wrap.append(name,input);return {wrap,input};
  }
  function warning(text) {
    const el=node("p","admin-warning",text);el.setAttribute("role","status");return el;
  }
  function errors(box,err) {box.replaceChildren(warning(err.message||"تعذر إتمام الطلب."));}
  function safeColor(value) {return /^#[0-9a-fA-F]{6}$/.test(value||"")?value:"#64748B";}
  function table(columns) {
    const wrap=node("div","functional-table-wrap"),
      tab=node("table","data-table functional-table"),
      head=node("thead"), row=node("tr"),body=node("tbody");
    columns.forEach(t=>row.appendChild(node("th","",t)));
    head.appendChild(row);tab.append(head,body);wrap.appendChild(tab);
    return {wrap,body};
  }
  function addRow(body,values,handlerLabel,handler) {
    const r=node("tr");
    values.forEach(v=>r.appendChild(node("td","",v==null?"":v)));
    if(handlerLabel) {
      const td=node("td");td.appendChild(btn(handlerLabel,handler));r.appendChild(td);
    }
    body.appendChild(r);
  }
  function empty(body,count,text) {
    if(body.children.length)return;
    const tr=node("tr"),td=node("td","",text);td.colSpan=count;
    tr.appendChild(td);body.appendChild(tr);
  }
  function navigate(screen) {
    state.active=screen;state.page=1;state.term="";currentRequest++;
    renderDetail(screen);
  }
  function parentOf(screen) {
    return sections.find(section => section.children.some(child => child[0] === screen));
  }
  function start(title) {
    const page=target();page.replaceChildren();
    const parent=parentOf(state.active);
    const head=card(title);
    head.classList.add("admin-subpage-heading");
    const back=btn(parent ? "العودة إلى "+parent.title : "العودة إلى الإدارة",
      () => parent ? renderGroup(parent.key) : renderAdmin(state.session),
      "btn btn-outline");
    head.appendChild(back);
    page.appendChild(head);
    return page;
  }
  function renderAdmin(session) {
    state.session=session;
    state.active="home";
    currentRequest++;
    const page=target();page.replaceChildren();
    const heading=card("الإدارة");
    heading.classList.add("admin-section-heading");
    heading.appendChild(node("p","section-description",
      "اختر القسم المطلوب لعرض شاشاته. لن تظهر الخيارات الفرعية إلا بعد الدخول إليه."));
    page.appendChild(heading);
    const grid=node("div","admin-home-grid");
    grid.setAttribute("aria-label","أقسام الإدارة");
    const icons={force:"♙",config:"◈",sheets:"▤",users:"♧",audit:"◷"};
    for(const section of sections) {
      if(section.key==="users" && session.role==="Supervisor")continue;
      const tile=btn(section.title,()=>renderGroup(section.key),"admin-primary-tile");
      tile.textContent="";
      tile.dataset.adminSection=section.key;
      tile.setAttribute("aria-label","فتح "+section.title);
      tile.append(
        node("span","admin-primary-icon",icons[section.key]),
        node("span","admin-primary-title",section.title),
        node("span","admin-primary-chevron","فتح القسم ←")
      );
      grid.appendChild(tile);
    }
    page.appendChild(grid);
  }
  function renderGroup(sectionKey) {
    const section=sections.find(item=>item.key===sectionKey);
    if(!section || (section.key==="users" && state.session.role==="Supervisor")) {
      renderAdmin(state.session);return;
    }
    state.active="group:"+section.key;
    currentRequest++;
    const page=target();page.replaceChildren();
    const heading=card(section.title);
    heading.classList.add("admin-subpage-heading");
    heading.append(
      node("p","section-description","اختر الشاشة التي تريد فتحها من هذا القسم."),
      btn("العودة إلى الإدارة",()=>renderAdmin(state.session),"btn btn-outline")
    );
    page.appendChild(heading);
    const grid=node("div","admin-subsection-grid");
    grid.setAttribute("aria-label","شاشات "+section.title);
    for(const [key,title] of section.children) {
      const item=btn(title,()=>navigate(key),"admin-subsection-tile");
      item.dataset.adminScreen=key;
      item.setAttribute("aria-label","فتح شاشة "+title);
      grid.appendChild(item);
    }
    page.appendChild(grid);
  }
  function renderDetail(screen) {
    if(screen==="sites")return sites();
    if(screen==="statuses")return statuses();
    if(screen==="persons")return persons();
    if(screen==="log")return audit();
    const labels={attendance:"التمام",transfers:"التنقلات",reports:"التقارير والفصل",
      import:"استيراد Excel",export:"تصدير Excel",layout:"هيكل الكشف",accounts:"المستخدمون"};
    const page=start(labels[screen]||"الإدارة");
    const shell=card("الوظائف المطلوبة");
    const sub={
      attendance:["كشف القوة","تحديد الأفراد","معاينة تغيير الحالات","التأكيد الجماعي"],
      transfers:["النقل الداخلي","النقل الخارجي","أرشيف التنقلات"],
      reports:["التقارير الفردية","الفصل","أرشيف المفصولين"],
      import:["اختيار ملف Excel","فحص التكرارات","معاينة ودمج الأفراد"],
      export:["الموقع والشهر والسنة","معاينة التصدير","حفظ ملف Excel"],
      layout:["إضافة عمود عرض","تعديل اسم عرض","ترتيب الأعمدة"],
      accounts:["إضافة حساب","تغيير كلمة المرور","تعطيل الحساب"]
    }[screen]||[];
    const grid=node("div","admin-action-grid");
    sub.forEach(name=>{
      const element=btn(name,()=>alert("هذه الوظيفة قيد التنفيذ والفحص، ولم تُحفظ أي تغييرات."),
        "btn btn-outline");
      element.title="قيد التنفيذ — غير متصل بعملية حفظ";
      grid.appendChild(element);
    });
    shell.append(grid,warning("هذه الشاشة لم تُربط بعد بخدمة معتمدة؛ لن يُعلن نجاح أي عملية غير منفّذة."));
    page.appendChild(shell);
  }

  async function sites() {
    const page=start("الإدارة / الحالات والمواقع / المواقع"), output=card("المواقع المسجلة");
    page.appendChild(output);
    const token=++currentRequest;
    try {
      const response=await api().invoke("QuerySites",{});
      if(token!==currentRequest)return;
      const list=Array.isArray(response.data)?response.data:[];
      const form=card("إنشاء موقع جديد");
      const name=field("اسم الموقع");
      const note=node("div","admin-note");
      const send=btn("إضافة الموقع",async()=>{
        send.disabled=true;note.replaceChildren();
        try {await api().invoke("CreateSite",{name:name.input.value});
          name.input.value="";await sites();}
        catch(e){errors(note,e);}finally{send.disabled=false;}
      },"btn btn-primary");
      form.append(name.wrap,send,note);
      const t=table(["اسم الموقع","الحالة","الإجراء"]);
      list.forEach(item=>{
        const sys=item.siteId==="SITE-UNSPECIFIED";
        addRow(t.body,[item.name,item.active?"نشط":"موقوف"],
          sys?"موقع محمي":"إعادة تسمية",()=>{
            if(sys){alert("موقع غير محدد محمي من التعديل والحذف.");return;}
            const next=prompt("اسم الموقع الجديد",item.name);
            if(!next||next.trim()===item.name)return;
            if(!confirm("اعتماد تغيير اسم الموقع من «"+item.name+"» إلى «"+next+"»؟"))return;
            api().invoke("RenameSite",{siteId:item.siteId,name:next.trim(),
              expectedRevision:item.rowVersion}).then(sites).catch(e=>alert(e.message));
          });
      });
      empty(t.body,3,"لا توجد مواقع مسجلة.");
      output.append(t.wrap,form);
    }catch(e){errors(output,e);}
  }

  async function statuses() {
    const page=start("الإدارة / الحالات والمواقع / الحالات"),output=card("الحالات المسجلة");
    page.appendChild(output);
    const token=++currentRequest;
    try {
      const response=await api().invoke("GetActiveAttendanceStatuses",{});
      if(token!==currentRequest)return;
      const items=Array.isArray(response.data)?response.data:[];
      const frm=card("إضافة حالة جديدة");
      const name=field("اسم الحالة"), symbol=field("الرمز"),color=field("اللون","#38BDF8","color");
      const note=node("div","admin-note");
      const save=btn("إضافة حالة",async()=>{
        note.replaceChildren();save.disabled=true;
        try {await api().invoke("CreateAttendanceStatus",{
          name:name.input.value,symbol:symbol.input.value,color:color.input.value});
          await statuses();}
        catch(e){errors(note,e);}finally{save.disabled=false;}
      },"btn btn-primary");
      frm.append(name.wrap,symbol.wrap,color.wrap,save,note);
      const t=table(["الحالة","الرمز","اللون","الإجراءات"]);
      for(const item of items){
        const tr=node("tr"), colorBox=node("span","admin-color",item.color);
        colorBox.style.borderRight="5px solid "+safeColor(item.color);
        [item.displayName,item.shortSymbol].forEach(x=>tr.appendChild(node("td","",x)));
        const tdColor=node("td");tdColor.appendChild(colorBox);tr.appendChild(tdColor);
        const td=node("td","admin-row-buttons");
        td.appendChild(btn("تعديل",async()=>{
          const newName=prompt("اسم الحالة",item.displayName);
          if(newName===null)return;
          const newSymbol=prompt("رمز الحالة",item.shortSymbol);
          if(newSymbol===null)return;
          const newColor=prompt("لون الحالة #RRGGBB",item.color);
          if(newColor===null)return;
          if(!confirm("تأكيد تعديل الحالة «"+item.displayName+"»؟"))return;
          try{await api().invoke("UpdateAttendanceStatus",{
            statusId:item.statusId,name:newName,symbol:newSymbol,color:newColor,
            expectedRevision:item.version});await statuses();}
          catch(e){alert(e.message);}
        }));
        td.appendChild(btn("تعطيل",async()=>{
          if(!confirm("تعطيل الحالة «"+item.displayName+"» دون حذف سجلات تحضيرها التاريخية؟"))return;
          try{await api().invoke("DeactivateOrDeleteStatus",{
            statusId:item.statusId,expectedRevision:item.version});await statuses();}
          catch(e){alert(e.message);}
        },"btn btn-danger"));
        tr.appendChild(td);t.body.appendChild(tr);
      }
      empty(t.body,4,"لا توجد حالات نشطة.");
      output.append(t.wrap,frm);
    }catch(e){errors(output,e);}
  }

  async function persons() {
    const page=start("الإدارة / إدارة القوة / تعديل الأسماء"), box=card("الأفراد المسجلون");
    page.appendChild(box);
    const controls=node("div","functional-toolbar"),search=field("بحث بالاسم أو الرقم (ع)",state.term);
    const filter=node("select","form-select");
    filter.appendChild(node("option","","جميع المواقع"));filter.firstChild.value="";
    const go=btn("بحث",()=>{state.term=search.input.value.trim();state.page=1;load();},"btn btn-primary");
    controls.append(search.wrap,filter,go);box.appendChild(controls);
    const content=node("div","admin-note");box.appendChild(content);
    try {
      const response=await api().invoke("QuerySites",{});
      for(const site of response.data||[]){
        if(!site.active)continue;
        const option=node("option","",site.name);option.value=site.siteId;
        filter.appendChild(option);
      }
      filter.value=state.siteId;
      filter.addEventListener("change",()=>{state.siteId=filter.value;state.page=1;load();});
    }catch(e){errors(content,e);}
    async function load(){
      const token=++currentRequest;
      content.replaceChildren(warning("جارٍ تحميل الأفراد..."));
      try {
        const response=await api().invoke("QueryPersons",{
          term:state.term,siteId:state.siteId,page:state.page,pageSize:50});
        if(token!==currentRequest)return;
        const data=response.data||{},t=table(["الرقم (ع)","الاسم","الموقع","إجراء"]);
        for(const person of data.items||[]){
          addRow(t.body,[person.serviceNumberA,person.displayName,person.siteName],
            "تعديل الاسم والرقم",()=>editPerson(person));
        }
        empty(t.body,4,"لا توجد أسماء مطابقة.");
        const pager=node("div","functional-pager");
        const prev=btn("السابق",()=>{state.page--;load();}),next=btn("التالي",()=>{state.page++;load();});
        prev.disabled=state.page<=1;next.disabled=!data.hasNext;
        pager.append(node("span","","إجمالي "+(data.total||0)),prev,next);
        content.replaceChildren(t.wrap,pager);
      }catch(e){if(token===currentRequest)errors(content,e);}
    }
    async function editPerson(person) {
      const visibleName=prompt("الاسم الكامل الجديد",person.displayName);
      if(visibleName===null)return;
      const serviceNumberA=prompt("الرقم (ع) الجديد",person.serviceNumberA);
      if(serviceNumberA===null)return;
      const now=new Date();
      const businessDate=[now.getFullYear(),String(now.getMonth()+1).padStart(2,"0"),
        String(now.getDate()).padStart(2,"0")].join("-");
      const payload={personId:person.personId,visibleName,serviceNumberA,
        baseRevision:person.revision,effectiveLocalDate:businessDate};
      try {
        const preview=(await api().invoke("PreviewPersonIdentityUpdate",payload)).data;
        if(!confirm("اعتماد تعديل «"+preview.oldName+"» (ع: "+preview.oldServiceNumberA+
          ") إلى «"+preview.newName+"» (ع: "+preview.newServiceNumberA+")؟"))return;
        await api().invoke("UpdatePersonIdentity",{...payload,
          expectedRevision:person.revision,reason:"تعديل من شاشة إدارة القوة"});
        await load();
      }catch(e){alert(e.message);}
    }
    await load();
  }

  async function audit() {
    const page=start("الإدارة / سجل العمليات"),box=card("سجل العمليات المحفوظ");
    page.appendChild(box);
    const actor=field("من قام بالتنفيذ"),targetField=field("المنفذ عليه"),
      op=field("نوع العملية (رمز)"),startDate=field("من تاريخ","","date"),
      endDate=field("إلى تاريخ","","date"),controls=node("div","functional-toolbar"),
      results=node("div","admin-note");
    const search=btn("بحث",()=>{state.page=1;load();},"btn btn-primary");
    controls.append(actor.wrap,targetField.wrap,op.wrap,startDate.wrap,endDate.wrap,search);
    box.append(controls,results);
    async function load() {
      const token=++currentRequest;
      try {
        const response=await api().invoke("QueryAuditLog",{
          actor:actor.input.value,target:targetField.input.value,
          operation:op.input.value,fromDate:startDate.input.value,toDate:endDate.input.value,
          page:state.page,pageSize:50});
        if(token!==currentRequest)return;
        const data=response.data||{},t=table(["العملية","الهدف","المنفذ","وقت التنفيذ"]);
        (data.items||[]).forEach(item=>addRow(t.body,[
          item.operationArabicName,item.targetDisplay,
          [item.actorUsername,item.actorRosterFullName].filter(Boolean).join(" — "),
          item.executedAtUtc
        ]));
        empty(t.body,4,"لا توجد عمليات مطابقة.");
        const pager=node("div","functional-pager");
        const prev=btn("السابق",()=>{state.page--;load();}),next=btn("التالي",()=>{state.page++;load();});
        prev.disabled=state.page<=1;next.disabled=!data.hasNext;
        pager.append(node("span","","إجمالي "+(data.total||0)),prev,next);
        results.replaceChildren(t.wrap,pager);
      }catch(e){if(token===currentRequest)errors(results,e);}
    }
    await load();
  }

  async function renderSettings(session,logout) {
    state.session=session;currentRequest++;
    const page=target();page.replaceChildren();
    const overview=card("الإعدادات"),messages=node("div","admin-note");
    overview.append(node("p","section-description",
      "المعلومات والنسخ الاحتياطية تُقرأ وتُنفذ عبر خدمات الجهاز المحلية فقط."),messages);
    page.appendChild(overview);
    const actions=node("div","admin-groups");page.appendChild(actions);
    try {
      const [infoResponse,versionResponse]=await Promise.all([
        api().invoke("GetSettingsOverview",{}),
        api().invoke("GetApplicationVersion",{})
      ]);
      const info=infoResponse.data||{},version=versionResponse.data||{};
      const id=card("البرنامج والحساب");
      id.append(node("p","","اسم البرنامج: التمام اليومي"),
        node("p","","الإصدار: "+(version.version||"غير محدد")),
        node("p","","اسم المستخدم: "+info.username),
        node("p","","نوع الحساب: "+info.roleLabelAr));
      id.append(btn("تسجيل الخروج",logout,"btn btn-outline"));actions.appendChild(id);
      const backup=card("النسخ الاحتياطي المشفر");
      backup.append(node("p","","مكان النسخ: "+info.backupDirectory),
        node("p","","موعد النسخ المحفوظ: "+info.backupTime),
        warning(info.automaticBackupNote||"النسخ التلقائي غير جاهز."));
      const result=node("div","admin-note");
      const b=btn("إنشاء نسخة مشفرة والتحقق منها",async()=>{
        if(!confirm("حفظ نسخة احتياطية مشفرة إلى القرص E الآن؟"))return;
        b.disabled=true;result.replaceChildren(warning("جارٍ نسخ قاعدة البيانات والتحقق من سلامتها..."));
        try{
          const data=(await api().invoke("CreateVerifiedBackup",{}, {timeoutMs:180000})).data;
          result.replaceChildren(node("p","",data.verificationPassed?
            "تمت كتابة نسخة مشفرة متحقق منها: "+data.path:"لم يتم تأكيد النسخة"));
        }catch(e){errors(result,e);}finally{b.disabled=false;}
      },"btn btn-primary");
      backup.append(b,result);actions.appendChild(backup);
      const integrity=card("فحص قاعدة البيانات");
      const stateText=node("div","admin-note");
      integrity.append(btn("فحص سلامة قاعدة البيانات",async()=>{
        try{
          const check=(await api().invoke("RunDatabaseIntegrityCheck",{})).data;
          stateText.replaceChildren(node("p","",check.integrity==="ok"?
            "قاعدة البيانات المشفرة سليمة.":"لم يتم تأكيد سلامة قاعدة البيانات"));
        }catch(e){errors(stateText,e);}
      }),stateText);actions.appendChild(integrity);
      const pending=card("الاستعادة والجدولة والتصفير");
      pending.append(warning("استيراد النسخة الاحتياطية وتعديل وقت الجدولة والحذف الشامل لم تُفعّل بعد. تتطلب اختبارات الاستعادة والصلاحيات والتأكيد، ولن يتم تعديل البيانات من هذه الشاشة حالياً."));
      actions.appendChild(pending);
    }catch(e){errors(messages,e);}
  }
  window.AltamamAdminSettings=Object.freeze({renderAdmin,renderSettings});
})();
