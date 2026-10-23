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
    {key:"users",title:"المستخدمون",children:[]},
    {key:"audit",title:"سجل العمليات",children:[]}
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
  const nestedOptions = Object.freeze({
    transfers: [
      ["transferInternal","النقل الداخلي"],
      ["transferExternal","النقل الخارجي"],
      ["transferArchive","أرشيف التنقلات"]
    ],
    reports: [
      ["personReport","التقارير الفردية"],
      ["separation","الفصل"],
      ["closedCorrection","التصحيح الاستثنائي لليوم المغلق"]
    ],
    separation: [
      ["separatePerson","فصل فرد"],
      ["separatedArchive","أرشيف المفصولين"]
    ]
  });
  const nestedParents = Object.freeze({
    transferInternal:"transfers",transferExternal:"transfers",transferArchive:"transfers",
    personReport:"reports",separation:"reports",
    separatePerson:"separation",separatedArchive:"separation",
    closedCorrection:"reports"
  });
  function parentOf(screen) {
    return sections.find(section => section.children.some(child => child[0] === screen));
  }
  function start(title) {
    const page=target();page.replaceChildren();
    const parent=parentOf(state.active);
    const parentKey=nestedParents[state.active];
    const parentLabel=parentKey==="transfers"?"التنقلات":
      parentKey==="reports"?"التقارير والفصل":
      parentKey==="separation"?"الفصل":null;
    const head=card(title);
    head.classList.add("admin-subpage-heading");
    const back=btn(parentLabel?"العودة إلى "+parentLabel:
      parent?"العودة إلى "+parent.title:"العودة إلى الإدارة",
      () => parentKey ? navigate(parentKey) :
        parent ? renderGroup(parent.key) : renderAdmin(state.session),
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
      const tile=btn(section.title,()=>{
        if(section.key==="users")return navigate("accounts");
        if(section.key==="audit")return navigate("log");
        renderGroup(section.key);
      },"admin-primary-tile");
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
  function renderNested(section,title) {
    const page=start("الإدارة / "+title);
    const grid=node("div","admin-subsection-grid");
    grid.setAttribute("aria-label","خيارات "+title);
    for(const [key,label] of nestedOptions[section]) {
      if(key==="closedCorrection" && state.session?.role!=="GeneralAdministration")continue;
      const tile=btn(label,()=>navigate(key),"admin-subsection-tile");
      tile.dataset.adminNested=key;
      grid.appendChild(tile);
    }
    page.appendChild(grid);
  }
  function renderDetail(screen) {
    if(screen==="attendance" && window.AltamamAttendance)
      return window.AltamamAttendance.render(target());
    if(["transferInternal","transferExternal","transferArchive"].includes(screen) && window.AltamamMovements){
      const page=start("الإدارة / التنقلات");
      return window.AltamamMovements.render(page,screen);
    }
    if(["separatePerson","separatedArchive"].includes(screen) && window.AltamamSeparation){
      const page=start("الإدارة / التقارير والفصل / الفصل");
      return window.AltamamSeparation.render(page,screen);
    }
    if(screen==="personReport" && window.AltamamReports)
      return window.AltamamReports.render(target(),()=>navigate("reports"));
    if(screen==="import")return importExcel();
    if(screen==="export" && window.AltamamFunctional)
      return window.AltamamFunctional.render("monthly");
    if(screen==="closedCorrection")return closedCorrection();
    if(screen==="accounts")return accounts();
    if(screen==="sites")return sites();
    if(screen==="statuses")return statuses();
    if(screen==="persons")return persons();
    if(screen==="log")return audit();
    if(screen==="transfers")return renderNested(screen,"التنقلات");
    if(screen==="reports")return renderNested(screen,"التقارير والفصل");
    if(screen==="separation")return renderNested(screen,"الفصل");

    const labels={
      attendance:"التمام",transferInternal:"النقل الداخلي",
      transferExternal:"النقل الخارجي",transferArchive:"أرشيف التنقلات",
      personReport:"التقارير الفردية",separatePerson:"فصل فرد",
      separatedArchive:"أرشيف المفصولين",
      import:"استيراد Excel",export:"تصدير Excel",
      layout:"تعديل هيكل الكشف"
    };
    const page=start("الإدارة / "+(labels[screen]||"الشاشة المطلوبة"));
    const shell=card("حالة الربط الوظيفي");
    shell.appendChild(warning(
      "وظائف هذه الشاشة لم تُعتمد بعد للاستخدام. لن تُنفّذ عمليات تعديل " +
      "أو نقل أو فصل أو استيراد دون التأكيد والتحقق من الصلاحيات وحفظ سجل العمليات."
    ));
    page.appendChild(shell);
  }

  // Safe two-step XLSX import: do not mutate existing people, assignments or attendance.
  function importExcel(){
    const page=start("الإدارة / الكشوفات / استيراد Excel");
    const shell=card("استيراد أسماء جديدة من ملف Excel");
    shell.appendChild(node("p","section-description",
      "الأعمدة المطلوبة: الاسم | الرقم (ع). العدد الأقصى 500 فرد وحجم الملف 160 كيلوبايت. " +
      "سيُضاف الأفراد الجدد فقط بتاريخ اليوم؛ الأرقام الموجودة أو المكررة لا تُستبدل. " +
      "اكتب الرقم (ع) كرقم صحيح في Excel مباشرة، دون الحاجة لتحويل العمود إلى نص."));
    const choose=node("input","form-control");choose.type="file";
    choose.accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    const picker=node("label","admin-input");
    picker.append(node("span","","اختر الملف من الجهاز"),choose);
    const status=node("div","admin-note");
    const analysis=node("div","admin-note");
    const confirmBox=node("div","admin-note");
    const history=card("سجل دفعات الاستيراد المعتمدة");
    const historyContent=node("div","admin-note");
    const historyDetails=node("div","admin-note");
    let detailsSerial=0;
    const refreshHistory=btn("تحديث سجل الاستيراد",loadHistory,"btn btn-outline");
    history.append(refreshHistory,historyContent,historyDetails);
    let workbookToken="",previewToken="",serial=0;

    async function loadHistory(){
      const request=++detailsSerial;
      historyContent.replaceChildren(warning("جارٍ قراءة سجل الاستيراد..."));
      historyDetails.replaceChildren();
      try{
        const response=await api().invoke("QueryImportOutcome",{});
        if(!valid()||request!==detailsSerial)return;
        const batches=Array.isArray(response.data?.batches)?response.data.batches:[];
        if(!batches.length){
          historyContent.replaceChildren(node("p","section-description",
            "لا توجد دفعات استيراد محفوظة لهذا الحساب بعد."));
          return;
        }
        const report=table(["تاريخ الاعتماد UTC","الملف","الإجمالي","أضيف","استُبعد","التفاصيل"]);
        for(const batch of batches){
          const row=node("tr");
          for(const value of [batch.committedAtUtc,batch.fileName,batch.totalRows,
            batch.acceptedRows,batch.rejectedRows])
            row.appendChild(node("td","",value==null?"—":value));
          const actions=node("td");
          actions.appendChild(btn("عرض الدفعة",()=>showOutcome(batch.importBatchId)));
          row.appendChild(actions);report.body.appendChild(row);
        }
        historyContent.replaceChildren(report.wrap);
      }catch(e){if(valid()&&request===detailsSerial)errors(historyContent,e);}
    }

    async function showOutcome(batchId){
      const request=++detailsSerial;
      historyDetails.replaceChildren(warning("جارٍ قراءة تفاصيل الدفعة..."));
      try{
        const response=await api().invoke("QueryImportOutcome",{importBatchId:batchId});
        if(!valid()||request!==detailsSerial)return;
        const record=response.data?.batch;
        if(!record||record.importBatchId!==batchId)
          throw Error("تعذر مطابقة بيانات الدفعة المحفوظة.");
        const panel=card("تفاصيل الدفعة — "+record.fileName);
        const summary=node("div","settings-properties");
        for(const [label,value] of [["الإجمالي",record.totalRows],
          ["أفراد أُضيفوا",record.acceptedRows],["صفوف استُبعدت",record.rejectedRows],
          ["وقت الاعتماد UTC",record.committedAtUtc]])
          summary.appendChild(settingsValue(label,value));
        panel.appendChild(summary);
        const issues=Array.isArray(response.data?.issues)?response.data.issues:[];
        if(issues.length){
          panel.appendChild(node("h3","section-title","أسباب استبعاد الصفوف"));
          const issueTable=table(["الصف","الاسم","الرقم (ع)","السبب"]);
          for(const issue of issues)
            addRow(issueTable.body,[issue.row,issue.name,issue.serviceNumberA,issue.reason]);
          panel.appendChild(issueTable.wrap);
          const exportButton=btn("حفظ تقرير الأخطاء Excel",async()=>{
            exportButton.disabled=true;
            try{
              const result=await api().invoke("ExportImportIssues",{importBatchId:batchId});
              if(!valid()||request!==detailsSerial)return;
              if(result.data?.format!=="xlsx"||!result.data?.filePath)
                throw Error("لم يؤكد الخادم إنشاء ملف Excel.");
              historyDetails.appendChild(node("p","settings-success",
                "تم حفظ تقرير الأخطاء: "+result.data.filePath));
            }catch(e){if(valid())errors(historyDetails,e);}
            finally{exportButton.disabled=false;}
          },"btn btn-primary");
          panel.appendChild(exportButton);
        }else{
          panel.appendChild(node("p","section-description",
            "لا توجد صفوف مستبعدة لهذه الدفعة."));
        }
        historyDetails.replaceChildren(panel);
      }catch(e){if(valid()&&request===detailsSerial)errors(historyDetails,e);}
    }
    const valid=()=>state.active==="import"&&page.isConnected!==false;
    function reset(){
      serial++;workbookToken="";previewToken="";
      analysis.replaceChildren();confirmBox.replaceChildren();status.replaceChildren();
    }
    choose.addEventListener("change",reset);
    async function dataUrl(file){
      return await new Promise((resolve,reject)=>{
        const reader=new FileReader();
        reader.onerror=()=>reject(new Error("تعذر فتح الملف المختار."));
        reader.onload=()=>resolve(String(reader.result||"").split(",",2)[1]||"");
        reader.readAsDataURL(file);
      });
    }
    const examine=btn("تحليل الملف",async()=>{
      reset();
      const file=choose.files?.[0];
      if(!file||!file.name.toLowerCase().endsWith(".xlsx")||
          file.size>163840||file.size<=0)
        return errors(status,new Error("اختر ملف xlsx لا يتجاوز حجمه 160 كيلوبايت."));
      const current=serial;examine.disabled=true;
      try{
        status.replaceChildren(warning("جارٍ فحص ملف Excel واكتشاف التعارضات..."));
        const selected=await api().invoke("SelectImportWorkbook",{
          fileName:file.name,base64:await dataUrl(file)
        });
        if(current!==serial||!valid())return;
        workbookToken=selected.data?.workbookToken||"";
        if(!workbookToken)throw Error("لم تُنشأ تذكرة استيراد صالحة.");
        const analyzed=await api().invoke("AnalyzeImport",{workbookToken});
        if(current!==serial||!valid())return;
        const report=analyzed.data||{};
        const summary=node("div","settings-properties");
        for(const [key,value] of [["إجمالي الصفوف",report.totalRows],
          ["أفراد جدد",report.newCount],["صفوف متعارضة أو غير صالحة",report.issueCount]])
          summary.appendChild(settingsValue(key,value));
        const issues=table(["الصف","الاسم","الرقم (ع)","سبب الاستبعاد"]);
        for(const item of (report.issues||[]).slice(0,500))
          addRow(issues.body,[item.row,item.name,item.serviceNumberA,item.reason]);
        if((report.issues||[]).length)
          summary.append(node("h3","section-title","الصفوف المستبعدة"),issues.wrap);
        const preview=btn("معاينة خطة الإضافة",async()=>{
          preview.disabled=true;confirmBox.replaceChildren();previewToken="";
          try{
            const result=await api().invoke("PreviewImportPlan",{workbookToken});
            if(current!==serial||!valid())return;
            const plan=result.data||{};
            if(!plan.previewToken||!plan.requiresExplicitConfirmation)
              throw Error("معاينة الاستيراد لم تُعتمد على الخادم.");
            previewToken=plan.previewToken;
            const out=card("خطة الإضافة النهائية");
            out.append(node("p","section-description",
              "سيتم إنشاء "+plan.newCount+" فرد جديد. " +
              "لن تتغير أي هوية أو حالة تحضير أو تاريخ قديم."),
              node("p","section-description","صفوف مستبعدة: "+plan.issueCount));
            const names=table(["الاسم الجديد","الرقم (ع)"]);
            for(const person of plan.acceptedPreview||[])
              addRow(names.body,[person.name,person.serviceNumberA]);
            out.append(names.wrap);
            const commit=btn("تأكيد استيراد الأفراد الجدد",async()=>{
              if(!previewToken)return;
              if(!confirm("تأكيد إضافة "+plan.newCount+" فرداً جديداً بعد مراجعة التعارضات؟"))return;
              const token=previewToken;previewToken="";
              commit.disabled=true;
              try{
                const done=await api().invoke("CommitImport",{previewToken:token});
                if(!valid())return;
                if(!Number.isInteger(done.data?.createdCount))
                  throw Error("لم يُؤكد الخادم نجاح الاستيراد.");
                confirmBox.replaceChildren(node("p","settings-success",
                  "تم إنشاء "+done.data.createdCount+" فرداً جديداً، وحُفظت العملية في سجل التدقيق."+
                  " معرف الدفعة: "+(done.data.importBatchId||"—")));
                workbookToken="";
                await loadHistory();
              }catch(e){errors(confirmBox,e);}
            },"btn btn-primary");
            out.append(commit,btn("إلغاء المعاينة",()=>{
              previewToken="";confirmBox.replaceChildren();
            }));
            confirmBox.appendChild(out);
          }catch(e){errors(confirmBox,e);}
          finally{preview.disabled=false;}
        },"btn btn-primary");
        analysis.replaceChildren(summary);
        if(Number(report.newCount)>0)analysis.appendChild(preview);
        status.replaceChildren(node("p","settings-success","اكتمل التحليل؛ راجع الصفوف قبل اعتماد الخطة."));
      }catch(e){errors(status,e);}
      finally{examine.disabled=false;}
    },"btn btn-primary");
    shell.append(picker,examine,status,analysis,confirmBox);
    page.append(shell,history);
    void loadHistory();
  }

  // Emergency historical changes are always gated in C#, never merely by hiding UI.
  async function closedCorrection(){
    if(state.session?.role!=="GeneralAdministration"){
      start("الإدارة / التصحيح الاستثنائي");
      target().appendChild(warning("هذه العملية مخصصة للإدارة العامة فقط."));
      return;
    }
    const page=start("الإدارة / التقارير والفصل / التصحيح الاستثنائي");
    const box=card("تصحيح تحضير يوم مغلق — الإدارة العامة");
    const notice=node("div","admin-note");
    box.appendChild(warning("عملية استثنائية وخاضعة للتدقيق. يجب تفعيل السياسة صراحة، ومعاينة حالة قديمة محفوظة، ولا يمكن تصحيح يوم نقل أو فصل. لا يُفتح اليوم ولا يُغيَّر وقت التحضير الأصلي."));
    const date=field("التاريخ المغلق","","date");
    const personSearch=field("بحث عن فرد بالاسم أو الرقم (ع)");
    const personList=node("select","form-control");
    const searchResults=node("label","admin-input");
    searchResults.append(node("span","","اختر الفرد من نتائج البحث"),personList);
    const statusList=node("select","form-control");
    const statuses=node("label","admin-input");
    statuses.append(node("span","","الحالة المصححة"),statusList);
    const reason=field("السبب التفصيلي (10 إلى 400 حرف)");
    const controls=node("div","functional-toolbar");
    const previewPanel=node("div","admin-note");
    let previewToken=null;
    function clearPreview(){previewToken=null;previewPanel.replaceChildren();}
    [date.input,personList,statusList,reason.input].forEach(input=>
      input.addEventListener("change",clearPreview));
    const search=btn("بحث عن فرد",async()=>{
      clearPreview();search.disabled=true;
      try{
        const response=await api().invoke("SearchPersons",{term:personSearch.input.value, take:50});
        const data=response.data||{};
        personList.replaceChildren();
        for(const item of data.items||[]){
          const option=node("option","",item.displayName+" — "+item.serviceNumberA);
          option.value=item.personId;personList.appendChild(option);
        }
        if(!(data.items||[]).length)throw Error("لا يوجد أفراد مطابقون للبحث.");
        notice.replaceChildren(node("p","","تم اختيار الفرد؛ راجع اليوم والحالة والسبب قبل المعاينة."));
      }catch(e){errors(notice,e);}
      finally{search.disabled=false;}
    });
    const setPolicy=enabled=>async()=>{
      clearPreview();
      const verb=enabled?"تفعيل":"إيقاف";
      if(!confirm("هل تؤكد "+verb+" التصحيح الاستثنائي وتسجيل السبب في سجل العمليات؟"))return;
      try{
        const result=await api().invoke("SetClosedDayCorrectionPolicy",{
          enabled,reason:reason.input.value});
        if(result.data?.enabled!==enabled)throw Error("لم يؤكد الخادم تعديل السياسة.");
        notice.replaceChildren(node("p","", "تم "+verb+" التصحيح الاستثنائي وتسجيل العملية."));
      }catch(e){errors(notice,e);}
    };
    const preview=btn("معاينة التصحيح",async()=>{
      preview.disabled=true;clearPreview();
      try{
        const response=await api().invoke("PreviewClosedDayCorrection",{
          personId:personList.value,businessDate:date.input.value,
          statusId:statusList.value,reason:reason.input.value});
        const result=response.data||{};
        if(!result.previewToken||result.requiresExplicitConfirmation!==true)
          throw Error("لم تُرجع الخدمة معاينة موثّقة.");
        previewToken=result.previewToken;
        const summary=node("div","settings-properties");
        for(const [label,value] of [
          ["الفرد",result.personName],["الرقم (ع)",result.serviceNumberA],
          ["الموقع",result.siteName],["التاريخ",result.businessDate],
          ["من الحالة",result.beforeStatusName],["إلى الحالة",result.afterStatusName],
          ["السبب",result.reason]
        ])summary.appendChild(settingsValue(label,value));
        const submit=btn("اعتماد التصحيح الاستثنائي",async()=>{
          if(!previewToken)return;
          if(!confirm("اعتماد التصحيح النهائي لليوم المغلق؟ ستُحفظ الحالة السابقة والتعديل والسبب في سجل العمليات."))return;
          const chosen=previewToken;previewToken=null;submit.disabled=true;
          try{
            const committed=await api().invoke("CommitClosedDayCorrection",{previewToken:chosen});
            if(!committed.data?.revisionNumber)throw Error("لم يؤكد الخادم اعتماد التصحيح.");
            previewPanel.replaceChildren(node("p","",
              "اعتمد التصحيح بنجاح، ورقم المراجعة "+committed.data.revisionNumber+
              ". لم يُغيَّر تاريخ التحضير الأصلي."));
          }catch(e){errors(previewPanel,e);}
        },"btn btn-primary");
        previewPanel.replaceChildren(summary,submit,btn("إلغاء",clearPreview));
      }catch(e){errors(previewPanel,e);}
      finally{preview.disabled=false;}
    },"btn btn-primary");
    controls.append(search,btn("تفعيل الإذن الاستثنائي",setPolicy(true)),
      btn("إيقاف الإذن الاستثنائي",setPolicy(false)),preview);
    box.append(date.wrap,personSearch.wrap,searchResults,statuses,reason.wrap,controls,notice,previewPanel);
    page.appendChild(box);
    try{
      const response=await api().invoke("GetActiveAttendanceStatuses",{});
      const data=Array.isArray(response.data)?response.data:[];
      for(const item of data){
        const option=node("option","",item.name||item.displayName||item.statusId);
        option.value=item.statusId;statusList.appendChild(option);
      }
    }catch(e){errors(notice,e);}
  }

  // Users open directly from the administration home. No extra "إدارة الحسابات"
  // intermediate screen. All mutations are performed by server-authorized C#.
  async function accounts() {
    const page=start("الإدارة / المستخدمون"),container=card("الحسابات المسجلة");
    page.appendChild(container);
    const notice=node("div","admin-note");
    const tools=node("div","functional-toolbar");
    const filter=field("بحث باسم المستخدم أو الاسم أو الرقم (ع)",state.term);
    tools.append(filter.wrap,btn("بحث",()=>{
      state.term=filter.input.value.trim();state.page=1;load();
    },"btn btn-primary"),btn("إضافة حساب",()=>showAdd(),"btn btn-outline"));
    container.append(tools,notice);
    const rows=node("div","admin-users-list");container.appendChild(rows);
    const token=++currentRequest;
    let allowedRoles=[];
    try {
      const result=await api().invoke("GetAllowedRoles",{});
      if(token!==currentRequest)return;
      allowedRoles=Array.isArray(result.data)?result.data:[];
    }catch(e){if(token===currentRequest)errors(notice,e);return;}

    function showAdd() {
      if(state.active!=="accounts")return;
      const form=card("إضافة حساب مرتبط بفرد من القوة");
      form.classList.add("admin-user-form");
      const personSearch=field("بحث عن فرد بالاسم أو الرقم (ع)");
      const personPick=node("select","form-control");
      personPick.setAttribute("aria-label","اختر فرداً من الكشف");
      const initial=node("option","","ابحث أولاً ثم اختر الفرد");
      initial.value="";personPick.appendChild(initial);
      const selected=node("p","section-description","الرقم (ع): —");
      const username=field("اسم المستخدم");
      const password=field("كلمة المرور الجديدة","","password");
      password.input.autocomplete="new-password";
      const role=node("select","form-control");
      role.setAttribute("aria-label","نوع الحساب");
      for(const allowed of allowedRoles){
        const opt=node("option","",allowed.name);opt.value=allowed.role;role.appendChild(opt);
      }
      const searchButton=btn("البحث في كشف القوة",async()=>{
        const term=personSearch.input.value.trim();
        if(!term){errors(message,new Error("أدخل الاسم أو الرقم (ع) للبحث."));return;}
        searchButton.disabled=true;message.replaceChildren();
        try {
          const result=await api().invoke("SearchActivePersonsForAccount",{term,take:30});
          const people=(result.data?.items||[]).filter(p=>p.organizationalStatus===1);
          personPick.replaceChildren();
          const head=node("option","","اختر الفرد");head.value="";personPick.appendChild(head);
          for(const person of people){
            const opt=node("option","",person.displayName+" — ع: "+person.serviceNumberA);
            opt.value=person.personId;opt.dataset.serviceNumberA=person.serviceNumberA;
            personPick.appendChild(opt);
          }
          if(people.length===0)errors(message,new Error("لم يوجد فرد نشط مطابق."));
        }catch(e){errors(message,e);}finally{searchButton.disabled=false;}
      });
      personPick.addEventListener("change",()=>{
        const option=Array.from(personPick.children).find(o=>o.value===personPick.value);
        selected.textContent="الرقم (ع): "+(option?.dataset.serviceNumberA||"—");
      });
      const message=node("div","admin-note");
      const save=btn("تأكيد إضافة الحساب",async()=>{
        if(!personPick.value||!username.input.value.trim()||!password.input.value)
          return errors(message,new Error("اختر فرداً وأدخل اسم المستخدم وكلمة المرور."));
        if(!confirm("تأكيد إنشاء حساب «"+username.input.value.trim()+"» للفرد المحدد؟"))return;
        save.disabled=true;message.replaceChildren();
        try {
          await api().invoke("CreateUserAccount",{
            personId:personPick.value,username:username.input.value.trim(),
            role:role.value,password:password.input.value
          });
          password.input.value="";
          form.remove();
          notice.replaceChildren(node("p","settings-success","تم إنشاء الحساب بعد حفظه بنجاح."));
          await load();
        }catch(e){password.input.value="";errors(message,e);}
        finally{save.disabled=false;}
      },"btn btn-primary");
      form.append(personSearch.wrap,searchButton,personPick,selected,
        username.wrap,password.wrap,node("p","section-description",
          "كلمة المرور: 10 خانات على الأقل، 3 أحرف إنجليزية، 3 أرقام ورمز خاص دون مسافات."),
        node("label","","نوع الحساب"),role,save,
        btn("إلغاء",()=>form.remove()),message);
      page.appendChild(form);
      form.scrollIntoView?.({block:"nearest"});
    }

    async function load() {
      const serial=++currentRequest;
      rows.replaceChildren(warning("جارٍ تحميل حسابات المستخدمين..."));
      try {
        const reply=await api().invoke("ListUserAccounts",{
          page:state.page,pageSize:30,term:state.term
        });
        if(serial!==currentRequest)return;
        const data=reply.data||{},listing=table(["الرقم (ع)","المستخدم","الاسم الكامل",
          "نوع الحساب","أضافه","الحالة","الإجراءات"]);
        (data.items||[]).forEach(account=>{
          const row=node("tr");
          const fields=[account.serviceNumberA,account.username,account.fullName,
            account.roleLabelAr,account.createdBy,account.active?"نشط":"معطّل"];
          fields.forEach(v=>row.appendChild(node("td","",v)));
          const cell=node("td","admin-row-buttons");
          if(account.canModify && account.active){
            cell.appendChild(btn("تغيير كلمة المرور",()=>showPassword(account),"btn btn-outline"));
            cell.appendChild(btn("تعطيل الحساب",async()=>{
              if(!confirm("تأكيد تعطيل حساب «"+account.username+
                "»؟ لن يتمكن من الدخول، وستلغى جلساته الحالية."))return;
              try {
                await api().invoke("DeactivateUserAccount",{
                  userId:account.userId,expectedRevision:account.rowVersion
                });
                await load();
              }catch(e){errors(notice,e);}
            },"btn btn-danger"));
          }else cell.textContent="—";
          row.appendChild(cell);listing.body.appendChild(row);
        });
        empty(listing.body,7,"لا توجد حسابات مطابقة للبحث.");
        const pager=node("div","functional-pager");
        const prev=btn("السابق",()=>{state.page--;load();});
        const next=btn("التالي",()=>{state.page++;load();});
        prev.disabled=state.page<=1;next.disabled=!data.hasNext;
        pager.append(node("span","","الإجمالي: "+(data.total||0)),prev,next);
        rows.replaceChildren(listing.wrap,pager);
      }catch(e){if(serial===currentRequest)errors(rows,e);}
    }

    function showPassword(account) {
      const wrapper=card("تغيير كلمة مرور «"+account.username+"»");
      const pwd=field("كلمة المرور الجديدة","","password");
      pwd.input.autocomplete="new-password";
      const result=node("div","admin-note");
      const save=btn("تأكيد التغيير",async()=>{
        if(!confirm("اعتماد كلمة المرور الجديدة وإلغاء جلسات هذا الحساب؟"))return;
        save.disabled=true;
        try {
          await api().invoke("ChangeUserPassword",{
            userId:account.userId,expectedRevision:account.rowVersion,password:pwd.input.value
          });
          pwd.input.value="";wrapper.remove();await load();
        }catch(e){pwd.input.value="";errors(result,e);}
        finally{save.disabled=false;}
      },"btn btn-primary");
      wrapper.append(pwd.wrap,save,btn("إلغاء",()=>{pwd.input.value="";wrapper.remove();}),
        result);
      page.appendChild(wrapper);
      wrapper.scrollIntoView?.({block:"nearest"});
    }
    await load();
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
        ],"عرض التفاصيل",()=>showAuditDetails(item.auditEventId)));
        empty(t.body,4,"لا توجد عمليات مطابقة.");
        const pager=node("div","functional-pager");
        const prev=btn("السابق",()=>{state.page--;load();}),next=btn("التالي",()=>{state.page++;load();});
        prev.disabled=state.page<=1;next.disabled=!data.hasNext;
        pager.append(node("span","","إجمالي "+(data.total||0)),prev,next);
        results.replaceChildren(t.wrap,pager);
      }catch(e){if(token===currentRequest)errors(results,e);}
    }
    async function showAuditDetails(auditEventId) {
      const detail=card("تفاصيل القيد وسلامة السجل");
      results.after(detail);
      const content=node("div","admin-note");
      detail.append(content,btn("إغلاق التفاصيل",()=>detail.remove()));
      content.replaceChildren(warning("جارٍ التحقق من تفاصيل القيد..."));
      try {
        const reply=await api().invoke("GetAuditEntryDetails",{auditEventId});
        if(state.active!=="log"){detail.remove();return;}
        const item=reply.data||{};
        const facts=node("div","settings-properties");
        for(const [label,value] of [
          ["العملية",item.operationArabicName],
          ["المستهدف",item.targetDisplay],
          ["اسم المستخدم",item.actorUsername],
          ["الاسم الكامل",item.actorRosterFullName],
          ["وقت التنفيذ",item.executedAtUtc],
          ["الوصف الأصلي",item.rawDescription],
          ["سلامة تسلسل السجل",item.chainValid?"موثوق":"تعذر إثبات سلامة التسلسل"]
        ]){
          const line=node("div","settings-property");
          line.append(node("span","settings-property-label",label),
            node("strong","settings-property-value",value||"—"));
          facts.appendChild(line);
        }
        content.replaceChildren(facts);
      }catch(e){errors(content,e);}
    }
    await load();
  }

  function settingsValue(label,value) {
    const row=node("div","settings-property");
    row.append(node("span","settings-property-label",label),
      node("strong","settings-property-value",value==null || value==="" ? "غير محدد" : value));
    return row;
  }
  function settingsCard(title,mark) {
    const box=card(title);
    box.classList.add("settings-panel");
    const heading=box.querySelector(".section-title");
    heading.classList.add("settings-panel-title");
    heading.prepend(node("span","settings-panel-icon",mark));
    return box;
  }
  async function renderSettings(session,logout) {
    state.session=session;
    const token=++currentRequest;
    const page=target();page.replaceChildren();
    const hero=card("الإعدادات");
    hero.classList.add("settings-heading");
    hero.appendChild(node("p","section-description",
      "إدارة الحساب والنسخ الاحتياطية وسلامة البيانات على هذا الجهاز دون إنترنت."));
    const notices=node("div","settings-feedback");
    const grid=node("div","settings-grid");
    grid.replaceChildren(warning("جارٍ قراءة إعدادات البرنامج المحفوظة..."));
    page.append(hero,notices,grid);
    try {
      const [overview,versionReply]=await Promise.all([
        api().invoke("GetSettingsOverview",{}),
        api().invoke("GetApplicationVersion",{})
      ]);
      if(token!==currentRequest)return;
      const info=overview.data||{},version=versionReply.data||{};
      grid.replaceChildren();
      // Identity and runtime version are read from the authenticated bridge.
      const account=settingsCard("الحساب والبرنامج","◉");
      const accountData=node("div","settings-properties");
      accountData.append(
        settingsValue("اسم البرنامج",version.productNameAr||"التمام اليومي"),
        settingsValue("المستخدم الحالي",info.username||session.username),
        settingsValue("نوع الحساب",info.roleLabelAr||session.roleLabelAr||info.role),
        settingsValue("إصدار خدمات التطبيق",version.version)
      );
      const logoutButton=btn("تسجيل الخروج",logout,"btn btn-outline settings-action");
      account.append(accountData,logoutButton);
      grid.appendChild(account);

      // The stored 16:00 schedule is explicitly NOT an operational scheduler.
      const backup=settingsCard("النسخ الاحتياطي","▣");
      backup.append(
        node("p","settings-copy","حفظ نسخة مشفرة من قاعدة البيانات على القرص E مع فحص سلامتها قبل اعتمادها.")
      );
      const backupInfo=node("div","settings-properties");
      backupInfo.append(
        settingsValue("مسار الحفظ",info.backupDirectory),
        settingsValue("الوقت المسجل للنسخ التلقائي",info.backupTime||"16:00"),
        settingsValue("حالة القرص E",info.defaultDriveAvailable?"متصل":"غير متصل")
      );
      backup.appendChild(backupInfo);
      const scheduleNote=warning(info.automaticBackupNote||
        "النسخ الاحتياطي التلقائي غير مفعل بعد، حتى لو كان وقت الجدولة محفوظاً.");
      backup.appendChild(scheduleNote);
      const backupResult=node("div","settings-feedback");
      backupResult.setAttribute("aria-live","polite");
      const save=btn("إنشاء نسخة احتياطية الآن",async()=>{
        if(!confirm("إنشاء نسخة احتياطية مشفرة على القرص E والتحقق من سلامتها؟"))return;
        save.disabled=true;
        backupResult.replaceChildren(warning("جارٍ إنشاء النسخة والتحقق من سلامتها..."));
        try {
          const response=await api().invoke("CreateVerifiedBackup",{}, {timeoutMs:180000});
          if(token!==currentRequest)return;
          const data=response.data||{};
          if(!data.verificationPassed)throw new Error("لم يتم تأكيد سلامة النسخة الاحتياطية.");
          backupResult.replaceChildren(node("p","settings-success",
            "تم إنشاء نسخة مشفرة متحقق منها: "+data.path));
        } catch(e) {
          if(token===currentRequest)errors(backupResult,e);
        } finally {
          if(token===currentRequest)save.disabled=!info.defaultDriveAvailable;
        }
      },"btn btn-primary settings-action");
      save.disabled=!info.defaultDriveAvailable;
      backup.append(save,backupResult);
      if(!info.defaultDriveAvailable)backup.appendChild(warning(
        "القرص E غير متصل، لذا لا يمكن إنشاء النسخة الاحتياطية الآن."));
      grid.appendChild(backup);

      const integrity=settingsCard("سلامة قاعدة البيانات","◇");
      integrity.appendChild(node("p","settings-copy",
        "تحقق من سلامة البيانات المشفرة داخل البرنامج دون تعديل أي سجل."));
      const result=node("div","settings-feedback");
      result.setAttribute("aria-live","polite");
      const checkButton=btn("فحص سلامة البيانات",async()=>{
        checkButton.disabled=true;
        result.replaceChildren(warning("جارٍ التحقق من قاعدة البيانات..."));
        try {
          const response=await api().invoke("RunDatabaseIntegrityCheck",{});
          if(token!==currentRequest)return;
          const data=response.data||{};
          if(data.integrity!=="ok")throw new Error("لم يؤكد الفحص سلامة قاعدة البيانات.");
          result.replaceChildren(node("p","settings-success",
            "اكتمل الفحص: قاعدة البيانات المشفرة سليمة."));
        }catch(e){
          if(token===currentRequest)errors(result,e);
        }finally{
          if(token===currentRequest)checkButton.disabled=false;
        }
      },"btn btn-outline settings-action");
      integrity.append(checkButton,result);
      grid.appendChild(integrity);

      const planned=settingsCard("الاستعادة والإعدادات المتقدمة","⚙");
      planned.appendChild(node("p","settings-copy",
        "هذه الإجراءات تحتاج إلى اختبارات حماية البيانات قبل السماح بتشغيلها."));
      const forthcoming=node("div","settings-properties");
      for(const [name,detail] of [
        ["استيراد نسخة احتياطية","غير متاح حتى اكتمال فحص الاستعادة"],
        ["النسخ التلقائي اليومي","الجدولة غير نشطة حالياً"],
        ["تغيير وقت النسخ","غير متاح حتى تشغيل الجدولة"],
        ["حذف جميع البيانات","موقوف لحماية قاعدة البيانات والنسخ"]
      ]) forthcoming.appendChild(settingsValue(name,detail));
      planned.appendChild(forthcoming);
      grid.appendChild(planned);
    }catch(e) {
      if(token!==currentRequest)return;
      grid.replaceChildren();
      errors(notices,e);
      notices.appendChild(btn("إعادة تحميل الإعدادات",
        ()=>renderSettings(session,logout),"btn btn-outline"));
    }
  }
  window.AltamamAdminSettings=Object.freeze({renderAdmin,renderGroup,renderSettings});
})();
