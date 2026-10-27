"use strict";
/* Administrative and settings screens. All writes require a real C# bridge action.
   Unsupported legacy routes are listed honestly, not simulated or auto-saved. */
(function () {
  const target = () => document.getElementById("appViewport");
  const api = () => window.AltamamBridge;
  // The real WebView always loads operation-dialogs.js. Native fallbacks are
  // reserved for legacy isolated Node UI stubs that do not load the HTML shell.
  const dialogs = {
    confirm: (message,options) => window.AltamamDialog?.confirm(message,options) ?? Promise.resolve(confirm(message)),
    prompt: (message,value="") => window.AltamamDialog?.prompt(message,value) ?? Promise.resolve(prompt(message,value)),
    alert: message => window.AltamamDialog?.alert(message) ?? Promise.resolve(alert(message))
  };
  let currentRequest = 0;
  let state = { session: null, active: "home", page: 1, term: "", siteId: "" };
  const sections = [
    {key:"force",title:"إدارة القوة",children:[
      ["attendance","التمام"],["persons","تعديل الأسماء"],["transfers","التنقلات"],
      ["separation","الفصل"],["personReport","التقرير"]]},
    {key:"config",title:"الحالات والمواقع",children:[["sites","المواقع"],["statuses","الحالات"],
      ["closedCorrection","التصحيح الاستثنائي لليوم المغلق"]]},
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
    if(/^العودة إلى|^رجوع/.test(String(label)))b.className+=" app-back-action";
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
      ["transferInternal","نقل داخلي"],
      ["transferExternal","نقل خارجي"],
      ["archiveInternal","أرشيف النقل الداخلي"],
      ["archiveExternal","أرشيف النقل الخارجي"]
    ],
    separation: [
      ["separatePerson","فصل فرد"],
      ["separatedArchive","أرشيف الفصل"]
    ]
  });
  const nestedParents = Object.freeze({
    transferInternal:"transfers",transferExternal:"transfers",
    archiveInternal:"transfers",archiveExternal:"transfers",
    separatedArchive:"separation",separatePerson:"separation"
  });
  function parentOf(screen) {
    return sections.find(section => section.children.some(child => child[0] === screen));
  }
  function start(title) {
    const page=target();page.replaceChildren();
    const parent=parentOf(state.active);
    const parentKey=nestedParents[state.active];
    const parentLabel=parentKey==="transfers"?"التنقلات":
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
      if(key==="closedCorrection" && !["GeneralAdministration","OrdinaryAdministration"].includes(state.session?.role))continue;
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
      if(key==="closedCorrection" && !["GeneralAdministration","OrdinaryAdministration"].includes(state.session?.role))continue;
      const tile=btn(label,()=>navigate(key),"admin-subsection-tile");
      tile.dataset.adminNested=key;
      grid.appendChild(tile);
    }
    page.appendChild(grid);
  }
  function renderDetail(screen) {
    if(screen==="attendance" && window.AltamamAttendance)
      return window.AltamamAttendance.render(target());
    if(["transferInternal","transferExternal","archiveInternal","archiveExternal"].includes(screen) && window.AltamamMovements){
      const page=start("الإدارة / التنقلات");
      return window.AltamamMovements.render(page,screen);
    }
    if(["separatePerson","separatedArchive"].includes(screen) && window.AltamamSeparation){
      const page=start("الإدارة / إدارة القوة / الفصل");
      return window.AltamamSeparation.render(page,screen);
    }
    if(screen==="personReport" && window.AltamamReports)
      return window.AltamamReports.render(target(),()=>renderGroup("force"));
    if(screen==="import")return importExcel();
    if(screen==="export" && window.AltamamFunctional)
      return window.AltamamFunctional.render("monthly");
    if(screen==="closedCorrection")return closedCorrection();
    if(screen==="accounts")return accounts();
    if(screen==="sites")return sites();
    if(screen==="statuses")return statuses();
    if(screen==="layout")return rosterLayout();
    if(screen==="persons")return persons();
    if(screen==="log")return audit();
    if(screen==="transfers")return renderNested(screen,"التنقلات");
     if(screen==="separation")return renderNested(screen,"الفصل");

    const labels={
      attendance:"التمام",transferInternal:"النقل الداخلي",
      transferExternal:"النقل الخارجي",archiveInternal:"أرشيف النقل الداخلي",
      archiveExternal:"أرشيف النقل الخارجي",personReport:"التقرير",
      separatePerson:"فصل فرد",separatedArchive:"أرشيف الفصل",
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
      "أعمدة الكشف: الاسم، الرقم (ع)، الموقع (اختياري)، والرقم العادي للتسلسل فقط. " +
      "الرقم (ع) لا يقل عن أربع خانات، ويختلف عن رقم ترتيب الصف. الحد 500 فرد و160 كيلوبايت. " +
      "سيُنشئ البرنامج المواقع غير الموجودة تلقائياً عند التأكيد، ولن يغير هوية أو تحضير فرد موجود."));
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
            const detailsReview=window.AltamamDialog?.detailsReview;
            if(typeof detailsReview!=="function")
              throw Error("نافذة معاينة الاستيراد غير متاحة؛ لم يُحفظ أي تغيير.");
            const details={rows:[
              ["أفراد جدد",plan.newCount],["صفوف مستبعدة",plan.issueCount],
              ["حماية السجلات","لن تتغير الهويات أو حالات التحضير أو السجلات القديمة."]
            ],columns:["الاسم الجديد","الرقم (ع)"],
              items:(plan.acceptedPreview||[]).map(p=>[p.name,p.serviceNumberA])};
            const accepted=await detailsReview(details,{
              title:"معاينة خطة إضافة الأفراد",
              message:"راجع الأسماء والأرقام (ع) والتعارضات قبل الاعتماد.",
              confirmLabel:"تأكيد استيراد الأفراد الجدد"
            });
            if(!accepted){previewToken="";return;}
            const token=previewToken;
            const commit=async()=>{
              try{
                const done=await api().invoke("CommitImport",{previewToken:token});
                if(!valid())return;
                if(!Number.isInteger(done.data?.createdCount))
                  throw Error("لم يُؤكد الخادم نجاح الاستيراد.");
                previewToken="";
                confirmBox.replaceChildren(node("p","settings-success",
                  "تم إنشاء "+done.data.createdCount+" فرداً جديداً وحُفظت العملية في سجل التدقيق."+
                  " معرف الدفعة: "+(done.data.importBatchId||"—")));
                workbookToken="";
                await loadHistory();
              }catch(e){
                errors(confirmBox,e);
                // Retry the exact same preview if the response was lost.
                confirmBox.appendChild(btn("إعادة معاينة واعتماد الخطة نفسها",async()=>{
                  if(await detailsReview(details,{
                    title:"إعادة مراجعة خطة الاستيراد",confirmLabel:"إعادة تأكيد الاستيراد"
                  }))await commit();
                }));
              }
            };
            await commit();
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
    if(!["GeneralAdministration","OrdinaryAdministration"].includes(state.session?.role)){
      start("الإدارة / التصحيح الاستثنائي");
      target().appendChild(warning("هذه العملية مخصصة للإدارة العامة والإدارة العادية فقط."));
      return;
    }
    const page=start("الإدارة / التقارير والفصل / التصحيح الاستثنائي");
    const box=card("تصحيح تحضير يوم مغلق — الإدارة العامة والإدارة العادية");
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
    // Keep the same operation ID for the same policy intent after a lost reply.
    // A changed target or reason is a fresh intent; success clears the retry key.
    let policyIntent=null;
    const setPolicy=enabled=>async()=>{
      clearPreview();
      const verb=enabled?"تفعيل":"إيقاف";
      if(!await dialogs.confirm("هل تؤكد "+verb+" إذن التصحيح الاستثنائي؟ سيتم تسجيل تغيير الإعداد في سجل العمليات، والسبب إلزامي عند تصحيح التحضير نفسه فقط."))return;
      const signature=JSON.stringify([enabled,reason.input.value]);
      if(policyIntent?.signature!==signature)
        policyIntent={signature,operationId:makeOperationId()};
      try{
        const result=await api().invoke("SetClosedDayCorrectionPolicy",{
          enabled,reason:reason.input.value,operationId:policyIntent.operationId});
        if(result.data?.enabled!==enabled)throw Error("لم يؤكد الخادم تعديل السياسة.");
        policyIntent=null;
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
        const detailsReview=window.AltamamDialog?.detailsReview;
        if(typeof detailsReview!=="function")
          throw Error("نافذة معاينة التصحيح غير متاحة؛ لم يُحفظ أي تغيير.");
        const details={rows:[
          ["الفرد",result.personName],["الرقم (ع)",result.serviceNumberA],
          ["الموقع",result.siteName],["التاريخ",result.businessDate],
          ["الحالة السابقة",result.beforeStatusName],["الحالة الجديدة",result.afterStatusName],
          ["سبب التصحيح",result.reason]
        ]};
        const operationId=makeOperationId();
        const accept=await detailsReview(details,{
          title:"معاينة تصحيح اليوم المغلق",
          message:"تصحيح استثنائي موثق في سجل العمليات؛ لا يُعاد فتح اليوم.",
          confirmLabel:"اعتماد التصحيح الاستثنائي"
        });
        if(!accept){clearPreview();return;}
        const commit=async()=>{
          try{
            const committed=await api().invoke("CommitClosedDayCorrection",{
              previewToken,operationId
            });
            if(!committed.data?.revisionNumber)throw Error("لم يؤكد الخادم اعتماد التصحيح.");
            previewToken=null;
            previewPanel.replaceChildren(node("p","settings-success",
              "اعتمد التصحيح بنجاح، ورقم المراجعة "+committed.data.revisionNumber+
              ". لم يُغيَّر تاريخ التحضير الأصلي."));
          }catch(error){
            errors(notice,error);
            previewPanel.replaceChildren(btn("إعادة مراجعة واعتماد التصحيح نفسه",async()=>{
              if(await detailsReview(details,{title:"معاينة التصحيح الاستثنائي",
                confirmLabel:"إعادة اعتماد التصحيح"}))await commit();
            }));
          }
        };
        await commit();
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
      const passwordConfirm=field("تأكيد كلمة المرور","","password");
      passwordConfirm.input.autocomplete="new-password";
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
      let accountPayload=null,accountOperation=null;
      const save=btn("تأكيد إضافة الحساب",async()=>{
        if(!personPick.value||!username.input.value.trim()||!password.input.value||
           !passwordConfirm.input.value)
          return errors(message,new Error("اختر فرداً وأدخل اسم المستخدم وكلمة المرور وتأكيدها."));
        if(password.input.value!==passwordConfirm.input.value)
          return errors(message,new Error("كلمة المرور وتأكيدها غير متطابقين."));
        if(!await dialogs.confirm("تأكيد إنشاء حساب «"+username.input.value.trim()+"» للفرد المحدد؟"))return;
        save.disabled=true;message.replaceChildren();
        try {
          const fields=[personPick.value,username.input.value.trim(),role.value,
            password.input.value,passwordConfirm.input.value];
          const signature=JSON.stringify(fields);
          if(accountPayload!==signature){accountPayload=signature;accountOperation=makeOperationId();}
          await api().invoke("CreateUserAccount",{
            personId:personPick.value,username:username.input.value.trim(),
            role:role.value,password:password.input.value,
            passwordConfirmation:passwordConfirm.input.value,operationId:accountOperation
          });
          accountPayload=null;accountOperation=null;
          password.input.value="";passwordConfirm.input.value="";
          form.remove();
          notice.replaceChildren(node("p","settings-success","تم إنشاء الحساب بعد حفظه بنجاح."));
          await load();
        }catch(e){password.input.value="";errors(message,e);}
        finally{save.disabled=false;}
      },"btn btn-primary");
      form.append(personSearch.wrap,searchButton,personPick,selected,
        username.wrap,password.wrap,passwordConfirm.wrap,node("p","section-description",
          "كلمة المرور: 8 خانات على الأقل، 3 أحرف إنجليزية، 3 أرقام ورمز خاص دون مسافات."),
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
          page:state.page,pageSize:100,term:state.term
        });
        if(serial!==currentRequest)return;
        const data=reply.data||{},listing=table(["م","الرقم (ع)","المستخدم","الاسم الكامل",
          "نوع الحساب","أضافه","الحالة","الإجراءات"]);
        (data.items||[]).forEach(account=>{
          const row=node("tr");
          const fields=[account.index,account.serviceNumberA,account.username,account.fullName,
            account.roleLabelAr,account.createdBy,account.active?"نشط":"معطّل"];
          fields.forEach(v=>row.appendChild(node("td","",v)));
          const cell=node("td","admin-row-buttons");
          if(account.canModify && account.active){
            cell.appendChild(btn("تغيير كلمة المرور",()=>showPassword(account),"btn btn-outline"));
            // One stable ID per requested disable, safe when a response is lost.
            const disableOperationId=makeOperationId();
            cell.appendChild(btn("تعطيل الحساب",async()=>{
              if(!await dialogs.confirm("تأكيد تعطيل حساب «"+account.username+
                "»؟ لن يتمكن من الدخول، وستلغى جلساته الحالية."))return;
              try {
                await api().invoke("DeactivateUserAccount",{
                  userId:account.userId,expectedRevision:account.rowVersion,
                  operationId:disableOperationId
                });
                await load();
              }catch(e){errors(notice,e);}
            },"btn btn-danger"));
          }else if(account.canModify && !account.active){
            let reactivationIntent=null;
            cell.appendChild(btn("إعادة تفعيل الحساب",async()=>{
              const reason=await dialogs.prompt(
                "سبب إعادة التفعيل: يُشترط أن يعود الفرد فعليًا إلى كشف القوة النشطة.");
              if(reason===null)return;
              if(reason.trim().length<5)return errors(notice,
                new Error("يجب كتابة سبب واضح من خمسة أحرف على الأقل."));
              if(!await dialogs.confirm("هل تؤكد إعادة تفعيل حساب «"+
                account.username+"»؟ سيتم تسجيل العملية وستبقى الجلسات القديمة ملغاة."))return;
              const intent=JSON.stringify([account.userId,account.rowVersion,reason.trim()]);
              if(reactivationIntent?.signature!==intent)
                reactivationIntent={signature:intent,operationId:makeOperationId()};
              try{
                await api().invoke("ReactivateUserAccount",{
                  userId:account.userId,expectedRevision:account.rowVersion,
                  reason:reason.trim(),operationId:reactivationIntent.operationId
                });
                reactivationIntent=null;
                await load();
              }catch(e){errors(notice,e);}
            },"btn btn-primary"));
          }else cell.textContent="—";
          row.appendChild(cell);listing.body.appendChild(row);
        });
        empty(listing.body,8,"لا توجد حسابات مطابقة للبحث.");
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
      // Do not cache the plaintext password in an additional JS signature.
      // The field owns its value; changing it starts a fresh operation ID.
      let passwordOperationId=null;
      pwd.input.addEventListener("input",()=>{passwordOperationId=null;});
      const save=btn("تأكيد التغيير",async()=>{
        if(!await dialogs.confirm("اعتماد كلمة المرور الجديدة وإلغاء جلسات هذا الحساب؟"))return;
        save.disabled=true;
        if(!passwordOperationId)passwordOperationId=makeOperationId();
        try {
          await api().invoke("ChangeUserPassword",{
            userId:account.userId,expectedRevision:account.rowVersion,
            password:pwd.input.value,operationId:passwordOperationId
          });
          pwd.input.value="";passwordOperationId=null;wrapper.remove();await load();
        }catch(e){
          // Do not persist passwords. Keep the input only within this dialog so
          // an interrupted successful update may be retried with the same ID.
          errors(result,e);
        }finally{save.disabled=false;}
      },"btn btn-primary");
      wrapper.append(pwd.wrap,save,btn("إلغاء",()=>{pwd.input.value="";wrapper.remove();}),
        result);
      page.appendChild(wrapper);
      wrapper.scrollIntoView?.({block:"nearest"});
    }
    await load();
  }

  // The one canonical roster-column editor. No presentation label is a data key.
  // Service returns immutable ColumnId, optimistic snapshot and stored-value count.
  async function rosterLayout() {
    const page=start("الإدارة / الكشوفات / تعديل هيكل الكشف");
    const panel=card("إدارة أعمدة الكشف"),feedback=node("div","admin-note");
    panel.appendChild(node("p","section-description",
      "الاسم والرقم (ع) والموقع ورقم التسلسل أعمدة أساسية محمية. "+
      "الأعمدة الإضافية ترتبط بالمعرف ColumnId الثابت، وليس بعنوان العمود. "+
      "إخفاء العمود أو تعطيله لا يحذف بيانات الأفراد القديمة."));
    page.appendChild(panel);
    const token=++currentRequest;
    const types=[["1","نص"],["2","رقم"],["3","تاريخ"],["4","نعم / لا"]];
    function kindPicker(value) {
      const picker=node("select","form-control");
      picker.setAttribute("aria-label","نوع العمود");
      for(const [type,title] of types){
        const opt=node("option","",title);opt.value=type;
        picker.appendChild(opt);
      }
      picker.value=String(value??1);return picker;
    }
    function typeLabel(code) {
      return types.find(x=>Number(x[0])===code)?.[1]||"غير معروف";
    }
    const add=card("إضافة عمود اختياري");
    const caption=field("عنوان العمود الجديد");
    caption.input.maxLength=80;
    const kind=kindPicker(1),label=node("label","admin-input");
    label.append(node("span","","نوع البيانات"),kind);
    const addButton=btn("إضافة العمود",async()=>{
      if(caption.input.value.trim().length<2)return errors(feedback,
        new Error("اكتب عنوانًا واضحًا لا يقل عن حرفين."));
      if(!await dialogs.confirm("إضافة عمود «"+caption.input.value.trim()+
         "» إلى هيكل الكشف؟"))return;
      addButton.disabled=true;feedback.replaceChildren();
      try{
        await api().invoke("CreateCustomFieldDefinition",{
          caption:caption.input.value.trim(),fieldType:Number(kind.value),
          operationId:makeOperationId()
        });
        await rosterLayout();
      }catch(e){errors(feedback,e);}
      finally{addButton.disabled=false;}
    },"btn btn-primary");
    add.append(caption.wrap,label,addButton);
    panel.append(add,feedback);
    try{
      const response=await api().invoke("GetCustomFieldDefinitions",{});
      if(token!==currentRequest)return;
      const items=Array.isArray(response.data?.items)?response.data.items:[];
      const active=items.filter(item=>item.active!==false);
      const custom=active.filter(item=>!item.isSystemProtected);
      const tableView=table(["عنوان العمود","نوع البيانات","الترتيب","الظهور","الإجراءات"]);
      const editor=node("div","admin-note");
      const explanation=node("p","section-description",
        "الأعمدة المحمية لا تُحذف. تغيير نوع عمود يحتوي قيمًا مسجلة يُرفض "+
        "حتى لا يُغيّر تفسير البيانات التاريخية دون ترحيل موثق.");
      const apply=async(item,changes)=>{
        const payload={
          columnId:item.columnId,expectedSnapshot:item.snapshot,
          caption:item.caption,fieldType:item.fieldType,
          displayOrder:item.displayOrder,visible:item.visible,
          ...changes,operationId:makeOperationId()
        };
        await api().invoke("UpdateCustomFieldMetadata",payload);
        await rosterLayout();
      };
      const showEditor=item=>{
        editor.replaceChildren();
        const box=card("تعديل العمود: "+item.caption);
        const name=field("عنوان العمود",item.caption);
        name.input.maxLength=80;
        const type=kindPicker(item.fieldType);
        const typeWrap=node("label","admin-input");
        typeWrap.append(node("span","","نوع البيانات"),type);
        const order=field("ترتيب العمود",String(item.displayOrder),"number");
        order.input.min=5;order.input.max=custom.length+4;
        const shown=node("label","admin-input"),check=node("input");
        check.type="checkbox";check.checked=item.visible;
        shown.append(node("span","","إظهار العمود في الكشف"),check);
        const save=btn("حفظ تغييرات العمود",async()=>{
          save.disabled=true;feedback.replaceChildren();
          try{
            await apply(item,{caption:name.input.value.trim(),
              fieldType:Number(type.value),displayOrder:Number(order.input.value),
              visible:check.checked});
          }catch(e){errors(feedback,e);}
          finally{save.disabled=false;}
        },"btn btn-primary");
        box.append(name.wrap,typeWrap,order.wrap,shown,save,
          btn("إلغاء التعديل",()=>editor.replaceChildren()));
        editor.replaceChildren(box);
      };
      for(const item of active){
        const row=node("tr");
        const text=node("td");
        text.append(node("strong","",item.caption),
          node("p","section-description","ColumnId: "+item.columnId));
        row.append(text,
          node("td","",typeLabel(item.fieldType)),
          node("td","",item.displayOrder),
          node("td","",item.visible?"ظاهر":"مخفي"));
        const action=node("td","admin-row-buttons");
        if(item.isSystemProtected)action.append(node("span","section-description","عمود محمي"));
        else{
          action.append(btn("تعديل",()=>showEditor(item)));
          action.append(btn(item.visible?"إخفاء":"إظهار",async()=>{
            try{await apply(item,{visible:!item.visible});}
            catch(e){errors(feedback,e);}
          }));
          if(item.displayOrder>5)action.append(btn("أعلى",async()=>{
            try{await apply(item,{displayOrder:item.displayOrder-1});}
            catch(e){errors(feedback,e);}
          }));
          if(item.displayOrder<custom.length+4)action.append(btn("أسفل",async()=>{
            try{await apply(item,{displayOrder:item.displayOrder+1});}
            catch(e){errors(feedback,e);}
          }));
          action.append(btn("تعطيل العمود",async()=>{
            const message="تعطيل عمود «"+item.caption+"»؟ عدد القيم المحفوظة: "+
              (item.storedValueCount||0)+". لن تُحذف قيم الأفراد أو السجلات القديمة.";
            if(!await dialogs.confirm(message))return;
            try{
              await api().invoke("DeactivateCustomField",{
                columnId:item.columnId,expectedSnapshot:item.snapshot,
                confirmed:true,operationId:makeOperationId()
              });
              await rosterLayout();
            }catch(e){errors(feedback,e);}
          }));
        }
        row.appendChild(action);
        tableView.body.appendChild(row);
      }
      empty(tableView.body,5,"لا توجد أعمدة في الكشف.");
      panel.append(explanation,tableView.wrap,editor);
    }catch(e){errors(feedback,e);}
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
      let sitePayload=null,siteOperation=null;
      const send=btn("إضافة الموقع",async()=>{
        if(!await dialogs.confirm("تأكيد إنشاء الموقع «"+name.input.value.trim()+"»؟"))return;
        send.disabled=true;note.replaceChildren();
        try {
          const current=JSON.stringify([name.input.value.trim()]);
          if(sitePayload!==current){sitePayload=current;siteOperation=makeOperationId();}
          await api().invoke("CreateSite",{name:name.input.value,operationId:siteOperation});
          sitePayload=null;siteOperation=null;
          name.input.value="";await sites();}
        catch(e){errors(note,e);}finally{send.disabled=false;}
      },"btn btn-primary");
      form.append(name.wrap,send,note);
      const t=table(["اسم الموقع","الحالة","الإجراء"]);
      list.forEach(item=>{
        const sys=item.siteId==="SITE-UNSPECIFIED";
        const row=node("tr"),actions=node("td");
        row.append(node("td","",item.name),node("td","",
          item.active?"نشط":"موقوف"),actions);
        if(sys)actions.append(node("span","section-description","موقع محمي"));
        else if(!item.active)actions.append(node("span","section-description","موقع محفوظ تاريخيًا"));
        else{
          actions.append(btn("إعادة تسمية",async()=>{
            const next=await dialogs.prompt("اسم الموقع الجديد",item.name);
            if(!next||next.trim()===item.name)return;
            if(!await dialogs.confirm("اعتماد تغيير اسم الموقع من «"+
                item.name+"» إلى «"+next+"»؟"))return;
            try{
              await api().invoke("RenameSite",{siteId:item.siteId,
                name:next.trim(),expectedRevision:item.rowVersion});
              await sites();
            }catch(e){void dialogs.alert(e.message||"تعذرت إعادة التسمية.");}
          }));
          const remove=btn("إزالة الموقع",async()=>{
            remove.disabled=true;
            try{
              const preview=(await api().invoke("QueryPersons",{
                siteId:item.siteId,organizationalStatus:1,page:1,pageSize:1
              })).data||{};
              const count=Number(preview.total);
              if(!Number.isSafeInteger(count)||count<0)
                throw Error("تعذرت قراءة عدد الأفراد؛ أُلغيت الإزالة.");
              const message="هل تؤكد إزالة موقع «"+item.name+"»؟ "+
                "الأفراد النشطون الذين سيُنقلون إلى «غير محدد»: "+count+
                ". ستبقى جميع التحضيرات والانتسابات والأسماء القديمة محفوظة، "+
                "ولن تتم العملية إذا سُجل تحضير أو نقل اليوم أو تغير الكشف.";
              if(!await dialogs.confirm(message,{title:"تأكيد إزالة الموقع"}))return;
              const result=await api().invoke("RemoveSiteAndReassignCurrentPersons",{
                siteId:item.siteId,expectedRevision:item.rowVersion,
                expectedActiveCount:count,confirmed:true,operationId:makeOperationId()
              });
              await dialogs.alert("أزيل الموقع من المواقع النشطة، ونُقل "+
                result.data.reassignedCount+" فردًا إلى غير محدد. السجلات السابقة محفوظة.");
              await sites();
            }catch(e){void dialogs.alert(e.message||"تعذرت إزالة الموقع؛ لم يُعتمد الحذف.");}
            finally{remove.disabled=false;}
          });
          actions.append(remove);
        }
        t.body.appendChild(row);
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
      let statusPayload=null,statusOperation=null;
      const save=btn("إضافة حالة",async()=>{
        if(!await dialogs.confirm("تأكيد إضافة حالة التحضير «"+name.input.value.trim()+"»؟"))return;
        note.replaceChildren();save.disabled=true;
        try {
          const identity=JSON.stringify([name.input.value.trim(),symbol.input.value.trim(),color.input.value]);
          if(statusPayload!==identity){statusPayload=identity;statusOperation=makeOperationId();}
          await api().invoke("CreateAttendanceStatus",{
            name:name.input.value,symbol:symbol.input.value,color:color.input.value,
            operationId:statusOperation});
          statusPayload=null;statusOperation=null;
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
          const newName=await dialogs.prompt("اسم الحالة",item.displayName);
          if(newName===null)return;
          const newSymbol=await dialogs.prompt("رمز الحالة",item.shortSymbol);
          if(newSymbol===null)return;
          const newColor=await dialogs.prompt("لون الحالة #RRGGBB",item.color);
          if(newColor===null)return;
          if(!await dialogs.confirm("تأكيد تعديل الحالة «"+item.displayName+"»؟"))return;
          try{await api().invoke("UpdateAttendanceStatus",{
            statusId:item.statusId,name:newName,symbol:newSymbol,color:newColor,
            expectedRevision:item.version});await statuses();}
          catch(e){void dialogs.alert(e.message);}
        }));
        td.appendChild(btn("تعطيل",async()=>{
          if(!await dialogs.confirm("تعطيل الحالة «"+item.displayName+"» دون حذف سجلات تحضيرها التاريخية؟"))return;
          try{await api().invoke("DeactivateOrDeleteStatus",{
            statusId:item.statusId,expectedRevision:item.version});await statuses();}
          catch(e){void dialogs.alert(e.message);}
        },"btn btn-danger"));
        tr.appendChild(td);t.body.appendChild(tr);
      }
      empty(t.body,4,"لا توجد حالات نشطة.");
      output.append(t.wrap,frm);
    }catch(e){errors(output,e);}
  }

  async function persons() {
    const page=start("الإدارة / إدارة القوة / تعديل الأسماء"), box=card("الأفراد المسجلون");
    // A simple number beside the screen title; keep the old table/pager layout.
    const activeTotalNumber=node("span","admin-force-inline-count","…");
    activeTotalNumber.setAttribute("aria-label","إجمالي القوة النشطة");
    page.querySelector(".section-title")?.appendChild(activeTotalNumber);
    const creation=node("details","admin-create-person card");
    const toggle=node("summary","btn btn-primary","إضافة فرد واحد للكشف");
    const creationFields=node("div","admin-site-form");
    const nameField=field("الاسم الكامل"),
      numberField=field("الرقم (ع) — أربع خانات فأكثر");
    const selectSite=node("select","form-control");
    selectSite.setAttribute("aria-label","الموقع المسجل");
    const fallback=node("option","","غير محدد");
    fallback.value="SITE-UNSPECIFIED";
    selectSite.appendChild(fallback);
    selectSite.value="SITE-UNSPECIFIED";
    const siteField={input:selectSite,wrap:node("label","admin-input")};
    siteField.wrap.append(node("span","","اختر الموقع من المواقع المسجلة"),selectSite);
    numberField.input.inputMode="numeric";
    numberField.input.minLength=4;
    numberField.input.maxLength=70;
    // Recognize Arabic-Indic and Persian numerals automatically while typing/pasting.
    numberField.input.addEventListener("input",()=>{
      numberField.input.value=numberField.input.value.replace(/[٠-٩۰-۹]/g,c=>
        String.fromCharCode(48+c.charCodeAt(0)-(c<="٩"?0x0660:0x06F0)));
    });
    const addMessage=node("div","admin-note");
    let lastPersonPayload=null,lastPersonOperation=null;
    const saveNew=btn("تأكيد وإضافة الفرد",async()=>{
      const name=nameField.input.value.trim(),number=numberField.input.value.trim(),
        siteId=siteField.input.value||"SITE-UNSPECIFIED",
        siteName=Array.from(selectSite.children).find(option=>option.value===siteId)?.textContent||"غير محدد";
      if(name.length<3 || !/^[0-9٠-٩۰-۹]{4,70}$/.test(number) || !siteId)
        return errors(addMessage,new Error("أدخل الاسم والرقم (ع) بأربع خانات رقمية على الأقل والموقع."));
      const canonicalNumber=number.replace(/[٠-٩۰-۹]/g,c=>
        String.fromCharCode(48+c.charCodeAt(0)-(c<="٩"?0x0660:0x06F0)));
      const identityKey=()=>JSON.stringify([nameField.input.value.trim(),
        numberField.input.value.trim().replace(/[٠-٩۰-۹]/g,c=>
          String.fromCharCode(48+c.charCodeAt(0)-(c<="٩"?0x0660:0x06F0))),
        siteField.input.value||"SITE-UNSPECIFIED"]);
      const proposed=JSON.stringify([name,canonicalNumber,siteId]);
      const unchanged=()=>identityKey()===proposed;
      const resetForm=async(message)=>{
        nameField.input.value="";numberField.input.value="";
        selectSite.value="SITE-UNSPECIFIED";
        lastPersonPayload=null;lastPersonOperation=null;
        addMessage.replaceChildren(node("p","settings-success",message));
        await load();
      };
      saveNew.disabled=true;
      addMessage.replaceChildren(warning("جارٍ فحص تطابق الاسم والرقم (ع) مع الأرشيف..."));
      try{
        const scanned=(await api().invoke("PreviewArchivedIdentity",{
          visibleName:name,serviceNumberA:canonicalNumber,siteId
        })).data||{};
        if(!unchanged())throw Error("تغيرت بيانات الفرد أثناء فحص الأرشيف؛ أعد التحقق.");
        const candidates=Array.isArray(scanned.candidates)?scanned.candidates:[];
        if(!candidates.length){
          if(!await dialogs.confirm("تأكيد إضافة «"+name+"» بالرقم (ع) «"+canonicalNumber+
            "» في الموقع «"+siteName+"»؟")){addMessage.replaceChildren();return;}
          const createKey=proposed;
          if(lastPersonPayload!==createKey){
            lastPersonPayload=createKey;lastPersonOperation=makeOperationId();
          }
          const response=await api().invoke("CreatePerson",{visibleName:name,
            serviceNumberA:canonicalNumber,siteId,operationId:lastPersonOperation});
          if(!response.data?.personId)throw Error("لم يتم تأكيد إنشاء السجل.");
          await resetForm("تمت إضافة الفرد بنجاح. رقم (ع): "+canonicalNumber+" — الموقع: "+siteName);
          return;
        }
        if(!scanned.previewToken)throw Error("تعذر توثيق معاينة هوية الأرشيف.");
        const review=card("تحقق من هوية محفوظة في الأرشيف");
        review.appendChild(warning("يوجد تطابق محتمل؛ راجع كل مرشح وحدد هل الفرد نفسه عاد للخدمة أم شخص مختلف. لن تُنشأ هوية جديدة تلقائياً."));
        const t=table(["الاسم المؤرشف","الرقم (ع)","نوع الخروج","تاريخ الخروج","سبب التطابق","قرار الهوية"]);
        const submitted=async(fn)=>{
          if(!unchanged())return errors(addMessage,new Error("تغيرت بيانات الفرد؛ أعد التحقق قبل الحفظ."));
          saveNew.disabled=true;
          try{await fn();}catch(e){errors(addMessage,e);}
          finally{saveNew.disabled=false;}
        };
        for(const candidate of candidates){
          const tr=node("tr");
          for(const value of [candidate.name,candidate.serviceNumberA,
            candidate.exitType,candidate.exitDate,
            candidate.numberMatch?"تطابق الرقم (ع)":"تطابق الاسم فقط"])
            tr.appendChild(node("td","",value));
          const action=node("td","admin-row-buttons");
          action.appendChild(btn("عاد نفس الفرد",()=>submitted(async()=>{
            const agreement="أؤكد أن «"+name+"» (ع: "+canonicalNumber+
              ") هو الشخص نفسه المسجل سابقاً باسم «"+candidate.name+
              "» (ع: "+candidate.serviceNumberA+")؛ خروج: "+candidate.exitType+
              " بتاريخ "+candidate.exitDate+". ستُحفظ هويته وتاريخه القديمان. متابعة العودة إلى «"+siteName+"»؟";
            if(!await dialogs.confirm(agreement))return;
            const result=await api().invoke("CommitArchivedReturn",{
              previewToken:scanned.previewToken,personId:candidate.personId,
              decision:"same",operationId:makeOperationId()
            });
            if(!result.data?.returned)throw Error("لم يتم توثيق عودة الفرد المؤرشف.");
            await resetForm("أُعيد تفعيل الفرد بالمعرف الأصلي، وحُفظ تاريخ خروجه وعودته. رقم (ع): "+canonicalNumber);
          })));
          tr.appendChild(action);t.body.appendChild(tr);
        }
        review.appendChild(t.wrap);
        const contested=candidates.some(x=>x.numberMatch);
        const dismissedNumberCandidates=candidates.filter(x=>x.numberMatch &&
          x.organizationalStatus===3 && x.exitType==="فصل");
        const numberCandidates=candidates.filter(x=>x.numberMatch);
        if(contested && numberCandidates.length===1 && dismissedNumberCandidates.length===1){
          const former=dismissedNumberCandidates[0];
          review.appendChild(btn("إضافة فرد جديد مستقل بهذا الرقم (ع)",()=>submitted(async()=>{
            const detail="الرقم (ع) «"+canonicalNumber+"» يخص المفصول «"+former.name+
              "» (تاريخ الفصل "+former.exitDate+").";
            const confirmed=await dialogs.confirm(detail+" هل تؤكد أن «"+name+
              "» شخص آخر مختلف تماماً؟ سيُنشأ له سجل جديد من الصفر ومعرّف داخلي مستقل؛ "+
              "لن يُنقل إليه أي تحضير أو حركة أو تاريخ من المفصول، "+
              "وستبقى الأدلة القديمة محفوظة في الأرشيف دون نسبتها إليه.");
            if(!confirmed)return;
            const response=await api().invoke("CreatePerson",{
              visibleName:name,serviceNumberA:canonicalNumber,siteId,
              identityResolutionToken:scanned.previewToken,
              identityDecision:"replaceDismissed",dismissedPersonId:former.personId,
              operationId:makeOperationId()
            });
            if(!response.data?.personId || response.data.personId===former.personId)
              throw Error("لم يتم تأكيد إنشاء هوية مستقلة جديدة.");
            await resetForm("أُنشئ للفرد الجديد سجل مستقل من الصفر دون سجل المفصول السابق.");
          }),"btn btn-primary"));
        }
        if(!contested){
          review.appendChild(btn("فرد آخر مختلف عن جميع المرشحين",()=>submitted(async()=>{
            const detail=candidates.map(x=>x.name+" (ع: "+x.serviceNumberA+
              "، "+x.exitType+" "+x.exitDate+")").join("؛ ");
            if(!await dialogs.confirm("تأكيد نهائي: «"+name+"» شخص مختلف عن جميع الهويات المؤرشفة التالية: "+
              detail+". إنشاء هوية جديدة مستقلة بهذا الرقم المختلف؟"))return;
            const response=await api().invoke("CreatePerson",{
              visibleName:name,serviceNumberA:canonicalNumber,siteId,
              identityResolutionToken:scanned.previewToken,identityDecision:"different",
              operationId:makeOperationId()
            });
            if(!response.data?.personId)throw Error("لم يتم تأكيد الهوية المستقلة الجديدة.");
            await resetForm("أُنشئت هوية مستقلة بعد توثيق أنها مختلفة عن مرشحي الأرشيف.");
          }),"btn btn-outline"));
        }else if(dismissedNumberCandidates.length!==1 || numberCandidates.length!==1){
          review.appendChild(warning("الرقم (ع) يتطابق مع سجل غير مؤهل للاستبدال أو عدة سجلات؛ يلزم تسوية التعارض. لا يمكن تجاوز سجل المنقول خارجياً."));
        }
        addMessage.replaceChildren(review);
      }catch(e){errors(addMessage,e);}
      finally{saveNew.disabled=false;}
    },"btn btn-primary");
    creationFields.append(nameField.wrap,numberField.wrap,siteField.wrap);
    const cancelCreation=btn("إلغاء",async()=>{
      if((nameField.input.value.trim()||numberField.input.value.trim()) &&
        !await dialogs.confirm("إلغاء بيانات الفرد قبل الحفظ؟"))return;
      nameField.input.value="";numberField.input.value="";
      selectSite.value="SITE-UNSPECIFIED";
      creation.open=false;
      addMessage.replaceChildren();
    });
    creation.append(toggle,creationFields,saveNew,cancelCreation,addMessage);
    page.append(creation,box);
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
        if(site.siteId!=="SITE-UNSPECIFIED"){
          const choice=node("option","",site.name);choice.value=site.siteId;
          selectSite.appendChild(choice);
        }
      }
      filter.value=state.siteId;
      filter.addEventListener("change",()=>{state.siteId=filter.value;state.page=1;load();});
    }catch(e){errors(content,e);}
    async function load(){
      const token=++currentRequest;
      content.replaceChildren(warning("جارٍ تحميل الأفراد..."));
      try {
        const filterQuery={term:state.term,siteId:state.siteId,organizationalStatus:1,
          page:state.page,pageSize:100};
        const allQuery={term:"",siteId:"",organizationalStatus:1,page:1,pageSize:1};
        const [response,all]=await Promise.all([
          api().invoke("QueryPersons",filterQuery),
          (!state.term&&!state.siteId)
            ? Promise.resolve(null)
            : api().invoke("QueryPersons",allQuery)
        ]);
        if(token!==currentRequest)return;
        const data=response.data||{};
        const activeTotal=all?.data?.total??data.total??0;
        activeTotalNumber.textContent="("+activeTotal+")";
        const t=table(["الرقم (ع)","الاسم","الموقع","إجراء"]);
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
      const visibleName=await dialogs.prompt("الاسم الكامل الجديد",person.displayName);
      if(visibleName===null)return;
      const serviceNumberA=await dialogs.prompt("الرقم (ع) الجديد",person.serviceNumberA);
      if(serviceNumberA===null)return;
      const now=new Date();
      const businessDate=[now.getFullYear(),String(now.getMonth()+1).padStart(2,"0"),
        String(now.getDate()).padStart(2,"0")].join("-");
      const payload={personId:person.personId,visibleName,serviceNumberA,
        baseRevision:person.revision,effectiveLocalDate:businessDate};
      try {
        const preview=(await api().invoke("PreviewPersonIdentityUpdate",payload)).data;
        if(!await dialogs.confirm("اعتماد تعديل «"+preview.oldName+"» (ع: "+preview.oldServiceNumberA+
          ") إلى «"+preview.newName+"» (ع: "+preview.newServiceNumberA+")؟"))return;
        await api().invoke("UpdatePersonIdentity",{...payload,
          expectedRevision:person.revision,reason:"تعديل من شاشة إدارة القوة"});
        await load();
      }catch(e){void dialogs.alert(e.message);}
    }
    await load();
  }

  async function audit() {
    const page=start("الإدارة / سجل العمليات"),box=card("سجل العمليات المحفوظ");
    page.appendChild(box);
    const general=field("بحث شامل في سجل العمليات","","search");
    general.input.placeholder="بحث بالاسم أو الرقم (ع) أو العملية أو الوصف";
    const actor=field("من قام بالتنفيذ"),targetField=field("المنفذ عليه"),
      op=field("نوع العملية (رمز)"),startDate=field("من تاريخ","","date"),
      endDate=field("إلى تاريخ","","date"),controls=node("div","functional-toolbar"),
      results=node("div","admin-note");
    const canEditAudit=["GeneralAdministration","OrdinaryAdministration"].includes(state.session?.role);
    let openDetail=null;
    const search=btn("بحث",()=>{state.page=1;load();},"btn btn-primary");
    general.input.addEventListener("keydown",event=>{
      if(event.key==="Enter"){event.preventDefault();state.page=1;void load();}
    });
    controls.append(general.wrap,actor.wrap,targetField.wrap,op.wrap,startDate.wrap,endDate.wrap,search);
    const showHidden=node("input");
    if(canEditAudit){
      showHidden.type="checkbox";
      const toggle=node("label","admin-input");
      toggle.append(showHidden,node("span","","إظهار القيود المخفية (للمسؤولين فقط)"));
      showHidden.addEventListener("change",()=>{state.page=1;load();});
      controls.appendChild(toggle);
    }
    box.append(controls,results);
    async function load() {
      const token=++currentRequest;
      try {
        const response=await api().invoke("QueryAuditLog",{
          term:general.input.value.trim(),actor:actor.input.value,target:targetField.input.value,
          operation:op.input.value,fromDate:startDate.input.value,toDate:endDate.input.value,
          includeTombstoned:canEditAudit&&showHidden.checked,
          page:state.page,pageSize:100});
        if(token!==currentRequest)return;
        const data=response.data||{},t=table(["العملية","الهدف","المنفذ","الوصف الظاهر","وقت التنفيذ"]);
        (data.items||[]).forEach(item=>addRow(t.body,[
          item.operationArabicName+(item.isTombstoned?" (مخفي)":""),
          item.targetDisplay,
          [item.actorUsername,item.actorRosterFullName].filter(Boolean).join(" - "),
          item.description||item.rawDescription,
          item.executedAtUtc
        ],"عرض التفاصيل",()=>showAuditDetails(item.auditEventId)));
        empty(t.body,5,"لا توجد عمليات مطابقة.");
        const pager=node("div","functional-pager");
        const prev=btn("السابق",()=>{state.page--;load();}),next=btn("التالي",()=>{state.page++;load();});
        prev.disabled=state.page<=1;next.disabled=!data.hasNext;
        pager.append(node("span","","إجمالي "+(data.total||0)),prev,next);
        results.replaceChildren(t.wrap,pager);
      }catch(e){if(token===currentRequest)errors(results,e);}
    }
    async function showAuditDetails(auditEventId) {
      openDetail?.parentElement?.remove();
      const detail=card("تفاصيل القيد وسلامة السجل");
      openDetail=detail;
      detail.classList.add("auto-attendance-dialog");
      const overlay=node("div","auto-attendance-overlay");
      overlay.setAttribute("role","dialog");overlay.setAttribute("aria-modal","true");
      overlay.setAttribute("aria-label","تفاصيل القيد");
      overlay.appendChild(detail);
      results.after(overlay);
      const content=node("div","admin-note");
      detail.append(content,btn("إغلاق التفاصيل",()=>overlay.remove()));
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
          ["الوصف الظاهر الحالي",item.overlays?.length
            ? (item.overlays[item.overlays.length-1].correctedDescription||
               (item.overlays[item.overlays.length-1].isTombstoned?"مخفي":item.rawDescription))
            : item.rawDescription],
          ["سلامة تسلسل السجل",item.chainValid?"موثوق":"تعذر إثبات سلامة التسلسل"]
        ]){
          const line=node("div","settings-property");
          line.append(node("span","settings-property-label",label),
            node("strong","settings-property-value",value||"—"));
          facts.appendChild(line);
        }
        content.replaceChildren(facts);
        const history=node("div","settings-properties");
        const overlays=Array.isArray(item.overlays)?item.overlays:[];
        const latest=overlays.at(-1);
        const revision=Number(latest?.overlayVersion)||0;
        if(overlays.length){
          history.appendChild(node("h3","section-title","تاريخ التعديلات والإخفاء (لا يُحذف)"));
          for(const entry of overlays){
            history.appendChild(node("p","section-description",
              "الإصدار "+entry.overlayVersion+" — "+
              (entry.isTombstoned?"إخفاء":"تعديل ظاهر")+
              " — "+entry.editedByDisplay+" — "+entry.editedAtUtc+
              " — السبب: "+entry.reason));
          }
          content.appendChild(history);
        }
        if(canEditAudit){
          const editBox=node("div","admin-note");
          const reason=field("سبب التعديل أو الإخفاء (إلزامي)");
          const corrected=field("الوصف الظاهر الجديد",
            latest?.correctedDescription||item.rawDescription);
          const form=node("div","functional-toolbar");
          let retry=null;
          async function commitOverlay(action){
            const reasonText=reason.input.value.trim();
            const description=corrected.input.value.trim();
            if(!reasonText || (action==="UpdateAuditPresentation"&&!description))
              return errors(editBox,new Error("اكتب سبب الإجراء والوصف البديل عند التعديل."));
            const signature=JSON.stringify([action,auditEventId,revision,reasonText,description]);
            if(retry?.signature!==signature)
              retry={signature,operationId:makeOperationId()};
            const label=action==="DeleteAuditPresentation"?"إخفاء القيد":"حفظ الوصف الظاهر";
            if(!await dialogs.confirm("تأكيد "+label+"؟ يبقى القيد الأصلي محفوظاً دون تغيير."))return;
            edit.disabled=true;hide.disabled=true;
            try{
              const payload={auditEventId,expectedOverlayVersion:revision,
                reason:reasonText,operationId:retry.operationId};
              if(action==="UpdateAuditPresentation")payload.correctedDescription=description;
              const reply=await api().invoke(action,payload);
              if(!reply.data?.rawEvidenceUntouched)
                throw Error("لم يؤكد الخادم الاحتفاظ بالقيد الأصلي.");
              retry=null;
              detail.remove();openDetail=null;
              await load();
            }catch(e){errors(editBox,e);}
            finally{edit.disabled=false;hide.disabled=false;}
          }
          const edit=btn(latest?.isTombstoned?"إظهار القيد بوصف معدل":"تعديل الوصف الظاهر",
            ()=>commitOverlay("UpdateAuditPresentation"),"btn btn-primary");
          const hide=btn("إخفاء القيد من العرض",
            ()=>commitOverlay("DeleteAuditPresentation"),"btn btn-outline");
          hide.disabled=latest?.isTombstoned===true;
          form.append(edit,hide);
          content.append(reason.wrap,corrected.wrap,form,editBox);
        }
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
  function makeOperationId(){
    if(window.crypto && typeof window.crypto.randomUUID==="function")
      return window.crypto.randomUUID().replace(/-/g,"");
    // Host-agnostic fallback is a correlation token, never a credential.
    return Date.now().toString(36)+Math.random().toString(36).slice(2)+Math.random().toString(36).slice(2);
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
      account.classList.add("settings-account-panel");
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

      if(["GeneralAdministration","OrdinaryAdministration"].includes(session.role)){
        const automatic=settingsCard("أوقات التحضير التلقائي وقفل التمام اليومي","◷");
        automatic.classList.add("settings-attendance-panel");
        automatic.appendChild(node("p","settings-copy",
          "يحدد وقت التحضير التلقائي موعد نسخ حالات أمس لغير المحضرين، ويحدد وقت القفل آخر وقت لإجراء التحضير والتعديل المعتاد خلال اليوم. لا يشمل الاعتماد التلقائي يوم النقل."));
        const label=node("label","admin-input"),picker=node("select","form-control");
        picker.setAttribute("aria-label","الحالة الافتراضية عند عدم وجود تحضير أمس");
        const empty=node("option","","اختر الحالة الافتراضية");empty.value="";
        picker.appendChild(empty);
        label.append(node("span","","الحالة الافتراضية عند عدم وجود تحضير أمس"),picker);
        const feedback=node("div","settings-feedback");
        automatic.append(label,feedback);
        grid.appendChild(automatic);
        try{
          const [policyResult,statusResult]=await Promise.all([
            api().invoke("GetAutoAttendancePolicy",{}),
            api().invoke("GetActiveAttendanceStatuses",{})
          ]);
          if(token!==currentRequest)return;
          const policy=policyResult.data||{};
          const choices=Array.isArray(statusResult.data)?statusResult.data:[];
          for(const entry of choices){
            const option=node("option","",entry.displayName);
            option.value=entry.statusId;picker.appendChild(option);
          }
          picker.value=policy.defaultStatusId||"";
          if(!choices.some(x=>x.statusId===picker.value))picker.value="";
          automatic.appendChild(node("p","section-description",
            policy.configured?"الحالة المعتمدة حالياً: "+policy.defaultStatusName:
              "لم تُحدد حالة افتراضية بعد؛ لن تُنشأ حالة دون مرجع حتى تختارها الإدارة."));
          const scheduledTime=node("input","form-control");
          scheduledTime.type="time";scheduledTime.required=true;
          scheduledTime.value=policy.dueTimeLocal||"08:00";
          scheduledTime.setAttribute("aria-label","وقت التحضير التلقائي");
          const lockTime=node("input","form-control");
          lockTime.type="time";lockTime.required=true;
          lockTime.value=policy.dailyLockTimeLocal||"20:00";
          lockTime.setAttribute("aria-label","وقت قفل التحضير اليومي");
          const scheduledLabel=node("label","admin-input");
          scheduledLabel.append(node("span","","وقت التحضير التلقائي"),scheduledTime);
          const lockLabel=node("label","admin-input");
          lockLabel.append(node("span","","وقت قفل التحضير اليومي"),lockTime);
          const saveTimes=btn("حفظ إعدادات التحضير التلقائي",async()=>{
            if(!picker.value)return errors(feedback,new Error("اختر حالة افتراضية نشطة أولاً."));
            if(!/^([01][0-9]|2[0-3]):[0-5][0-9]$/.test(scheduledTime.value)||
               !/^([01][0-9]|2[0-3]):[0-5][0-9]$/.test(lockTime.value))
              return errors(feedback,new Error("حدد وقتين صحيحين بصيغة HH:mm."));
            if(!await dialogs.confirm("اعتماد الحالة الافتراضية وأوقات التحضير والقفل معاً؟"))return;
            saveTimes.disabled=true;
            try{
              await api().invoke("SetAutoAttendanceDefaultStatus",{
                statusId:picker.value,operationId:makeOperationId()
              });
              const result=await api().invoke("SetAttendanceSchedule",{
                dueTimeLocal:scheduledTime.value,
                dailyLockTimeLocal:lockTime.value,operationId:makeOperationId()
              });
              if(token!==currentRequest)return;
              feedback.replaceChildren(node("p","settings-success",
                "تم حفظ الحالة الافتراضية وأوقات التحضير والقفل: "+
                result.data.dueTimeLocal+" / "+result.data.dailyLockTimeLocal));
            }catch(e){if(token===currentRequest)errors(feedback,e);}
            finally{saveTimes.disabled=false;}
          },"btn btn-primary settings-action");
          const timesRow=node("div","settings-attendance-fields");
          timesRow.append(scheduledLabel,lockLabel);
          automatic.append(timesRow,saveTimes);
        }catch(e){if(token===currentRequest)errors(feedback,e);}
      }

      // The daily schedule is executed by the native Windows host, never by JS timers.
      const backup=settingsCard("النسخ الاحتياطي","▣");
      backup.classList.add("settings-backup-panel");
      backup.append(
        node("p","settings-copy","اختر مجلداً مشتركاً للنسخ الاحتياطي المشفر وجميع ملفات Excel، مع التحقق من سلامة كل نسخة.")
      );
      const backupInfo=node("div","settings-properties");
      backupInfo.append(
        settingsValue("مجلد النسخ الاحتياطي والتصدير",info.backupDirectory),
        settingsValue("وقت النسخ التلقائي",info.backupTime||"16:00"),
        settingsValue("حالة المجلد",info.defaultDriveAvailable?"متاح":"غير متاح")
      );
      backup.appendChild(backupInfo);
      const folder=field("مسار مجلد الحفظ المشترك",info.backupDirectory);
      folder.input.setAttribute("aria-label","مجلد النسخ والتصدير");
      const folderNote=node("div","settings-feedback");
      folderNote.setAttribute("aria-live","polite");
      const pickOutputFolder=async()=>{
        choose.disabled=true;
        try{
          const reply=await api().invoke("SetOutputDirectory",{choose:true});
          if(token!==currentRequest)return;
          folder.input.value=reply.data?.directory||folder.input.value;
          folderNote.replaceChildren(node("p","settings-success",
            "تم اعتماد المجلد للنسخ والتصدير: "+folder.input.value));
          info.backupDirectory=folder.input.value;info.defaultDriveAvailable=true;
          save.disabled=false;
          backupInfo.firstChild?.remove?.();
          backupInfo.prepend(settingsValue("مجلد النسخ والتصدير",folder.input.value));
        }catch(e){if(token===currentRequest)errors(folderNote,e);}
        finally{choose.disabled=false;}
      };
      const choose=btn("اختيار مجلد من Windows",pickOutputFolder,"btn btn-outline settings-action");
      const updateFolder=btn("تأكيد تغيير مسار الحفظ",async()=>{
        if(!await dialogs.confirm("هل تؤكد استخدام هذا المجلد للنسخ الاحتياطي وكل ملفات Excel؟"))return;
        updateFolder.disabled=true;
        try{
          const reply=await api().invoke("SetOutputDirectory",{directory:folder.input.value});
          if(token!==currentRequest)return;
          folder.input.value=reply.data?.directory||folder.input.value;
          info.backupDirectory=folder.input.value;info.defaultDriveAvailable=true;
          save.disabled=false;
          folderNote.replaceChildren(node("p","settings-success","تم اعتماد مجلد الحفظ: "+folder.input.value));
          backupInfo.firstChild?.remove?.();
          backupInfo.prepend(settingsValue("مجلد النسخ والتصدير",folder.input.value));
        }catch(e){if(token===currentRequest)errors(folderNote,e);}
        finally{updateFolder.disabled=false;}
      },"btn btn-primary settings-action");
      backup.append(folder.wrap,choose,updateFolder,folderNote);
      if(!info.defaultDriveAvailable){
        void Promise.resolve().then(saveFolderNotice);
      }
      function saveFolderNotice(){
        const message="هذا المسار غير صالح أو غير متاح، يُرجى تحديد مسار سليم وصحيح: "+
          info.backupDirectory+"\nهل تريد تحديد مجلد جديد الآن؟";
        folderNote.replaceChildren(warning("مسار النسخ والتصدير غير متاح؛ لن تُحفظ الملفات فيه."));
        save.disabled=true;
        void dialogs.confirm(message,{title:"مسار النسخ الاحتياطي غير متاح",
          confirmLabel:"اختيار مسار جديد"}).then(confirmed=>{
          if(confirmed && token===currentRequest)void pickOutputFolder();
        });
      }
      const scheduleInfo=node("p","section-description",info.automaticBackupNote||
        "الجدولة غير مفعلة بعد.");
      backup.appendChild(scheduleInfo);
      if(["GeneralAdministration","OrdinaryAdministration"].includes(session.role)){
        const scheduleControls=node("div","functional-toolbar");
        const dailyTime=node("input","form-control");
        dailyTime.type="time";dailyTime.value=info.backupTime||"16:00";
        dailyTime.setAttribute("aria-label","وقت النسخ التلقائي اليومي");
        const checkbox=node("input");checkbox.type="checkbox";
        checkbox.checked=info.automaticBackupEnabled===true;
        const label=node("label","admin-input");
        label.append(node("span","","تفعيل النسخ التلقائي اليومي"),checkbox);
        const timeLabel=node("label","admin-input");
        timeLabel.append(node("span","","ساعة النسخ بتوقيت Windows المحلي"),dailyTime);
        const scheduleFeedback=node("div","settings-feedback");
        const apply=btn("حفظ جدول النسخ التلقائي",async()=>{
          if(!await dialogs.confirm("هل تؤكد "+(checkbox.checked?"تفعيل":"إيقاف")+
            " النسخ التلقائي في الساعة "+dailyTime.value+"؟"))return;
          apply.disabled=true;
          try{
            const answer=await api().invoke("UpdateBackupSchedule",{
              backupTime:dailyTime.value,enabled:checkbox.checked,
              operationId:makeOperationId()
            });
            if(token!==currentRequest)return;
            info.backupTime=answer.data.backupTime;
            info.automaticBackupEnabled=answer.data.enabled;
            scheduleInfo.textContent=answer.data.enabled
              ?"جدولة النسخ التلقائي مفعلة الساعة "+answer.data.backupTime+
                " أثناء تشغيل البرنامج، بعد التحقق من المجلد وسلامة النسخة."
              :"جدولة النسخ التلقائي متوقفة.";
            scheduleFeedback.replaceChildren(node("p","settings-success","حُفظ جدول النسخ في قاعدة البيانات."));
          }catch(e){if(token===currentRequest)errors(scheduleFeedback,e);}
          finally{apply.disabled=false;}
        },"btn btn-primary settings-action");
        scheduleControls.append(label,timeLabel,apply);
        backup.append(scheduleControls,scheduleFeedback);
      }
      const backupResult=node("div","settings-feedback");
      backupResult.setAttribute("aria-live","polite");
      const save=btn("إنشاء نسخة احتياطية الآن",async()=>{
        if(!await dialogs.confirm("إنشاء نسخة احتياطية مشفرة في المجلد المختار والتحقق من سلامتها؟"))return;
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
        "المجلد الحالي غير متاح؛ اختر مجلداً محلياً قابلاً للكتابة."));
      grid.appendChild(backup);

      const integrity=settingsCard("سلامة قاعدة البيانات","◇");
      integrity.classList.add("settings-integrity-panel");
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

      const planned=settingsCard("التحقق من النسخ والاستعادة","⚙");
      planned.classList.add("settings-restore-panel");
      planned.appendChild(node("p","settings-copy",
        "هذه الإجراءات تحتاج إلى اختبارات حماية البيانات قبل السماح بتشغيلها."));
      const forthcoming=node("div","settings-properties");
      for(const [name,detail] of [
        ["استيراد نسخة احتياطية","غير متاح حتى اكتمال اختبارات الاستعادة الآمنة"],
      ]) forthcoming.appendChild(settingsValue(name,detail));
      planned.appendChild(forthcoming);
      const inspectFeedback=node("div","settings-feedback");
      inspectFeedback.setAttribute("aria-live","polite");
      const inspect=btn("اختيار نسخة مشفرة وفحص سلامتها",async()=>{
        inspect.disabled=true;
        inspectFeedback.replaceChildren(warning("جارٍ اختيار الملف والتحقق من التشفير والبيانات..."));
        try{
          const result=await api().invoke("InspectBackup",{}, {timeoutMs:180000});
          if(token!==currentRequest)return;
          const data=result.data||{};
          if(data.verified!==true)throw Error("لم ينجح فحص النسخة.");
          const detail=node("div","settings-properties");
          for(const [label,value] of [
            ["اسم النسخة",data.fileName],
            ["الحجم بالبايت",data.fileSizeBytes],
            ["إصدار قاعدة البيانات",data.schemaVersion],
            ["الأفراد",data.people],
            ["التحضير المسجل",data.attendance],
            ["سجلات العمليات",data.auditEvents],
            ["الحسابات",data.users],
            ["بصمة SHA-256",data.sha256]
          ])detail.appendChild(settingsValue(label,value));
          inspectFeedback.replaceChildren(node("p","settings-success",data.message),detail);
        }catch(e){if(token===currentRequest)errors(inspectFeedback,e);}
        finally{inspect.disabled=false;}
      },"btn btn-outline settings-action");
      planned.append(inspect,inspectFeedback);
      if(session.role==="GeneralAdministration"){
        const resetBox=node("section","settings-danger-zone");
        const notice=node("p","settings-copy",
          "التصفير النهائي يمسح الحسابات والأفراد والتحضير والتاريخ وسجل العمليات. لا يمكن التراجع عنه.");
        const resetStatus=node("div","settings-feedback");
        const previewButton=btn("معاينة التصفير الكامل",async()=>{
          previewButton.disabled=true;
          resetStatus.replaceChildren(warning("جارٍ إعداد معاينة البيانات التي سيتم حذفها..."));
          try{
            const response=await api().invoke("PreviewFullReset",{});
            if(token!==currentRequest)return;
            const data=response.data||{};
            if(!data.previewToken || !data.requiredConfirmation)throw Error("معاينة التصفير غير موثقة.");
            const detailsReview=window.AltamamDialog?.detailsReview;
            if(typeof detailsReview!=="function")
              throw Error("نافذة معاينة التصفير غير متاحة؛ أُلغي الإجراء.");
            const approved=await detailsReview({rows:[
              ["الأفراد المعرّضون للحذف",data.people],
              ["سجلات التحضير",data.attendance],
              ["النسخ المسجلة",data.backupEntries],
              ["تحذير",data.warning||"لا يمكن التراجع عن تصفير البرنامج."]
            ]},{title:"معاينة التصفير النهائي",
              message:"ستُمحى جميع البيانات. راجع الأعداد بعناية قبل الانتقال للتحقق النهائي.",
              confirmLabel:"الانتقال للتحقق بكلمة المرور",danger:true});
            if(!approved){resetStatus.replaceChildren();previewButton.disabled=false;return;}
            const passwordWrap=field("كلمة المرور الحالية للإدارة العامة","","password");
            passwordWrap.input.autocomplete="current-password";
            const phraseWrap=field("اكتب عبارة التأكيد حرفياً","","text");
            phraseWrap.input.autocomplete="off";
            const approve=btn("تأكيد تصفير البرنامج نهائياً",async()=>{
              if(!passwordWrap.input.value || phraseWrap.input.value!==data.requiredConfirmation){
                resetStatus.appendChild(warning("كلمة المرور وعبارة التأكيد مطلوبة حرفياً."));
                return;
              }
              if(!await dialogs.confirm("هذه العملية نهائية وتمسح جميع الحسابات والتحضير وسجل العمليات. هل تؤكد؟"))
                return;
              approve.disabled=true;
              try{
                const result=await api().invoke("CommitFullReset",{
                  previewToken:data.previewToken,expectedRevision:data.expectedRevision,
                  confirmation:phraseWrap.input.value,password:passwordWrap.input.value
                },{timeoutMs:180000});
                passwordWrap.input.value="";
                if(!result.data?.reset)throw Error("لم يؤكد الخادم إتمام التصفير.");
                await dialogs.alert("تم تصفير قاعدة البيانات. أعد التهيئة الأولى."+
                  (result.data.backupFilesNotDeleted
                    ?" تنبيه: توجد ملفات نسخ لم يمكن حذفها؛ راجع مجلدات النسخ السابقة.":""));
                await logout();
              }catch(e){passwordWrap.input.value="";errors(resetStatus,e);approve.disabled=false;}
            },"btn btn-primary settings-action");
            const cancel=btn("إلغاء",()=>{passwordWrap.input.value="";resetStatus.replaceChildren();previewButton.disabled=false;});
            resetStatus.replaceChildren(node("p","settings-copy",
              "تمت مراجعة المعاينة. أدخل كلمة المرور وعبارة التأكيد لتنفيذ الإجراء النهائي."),
              passwordWrap.wrap,phraseWrap.wrap,approve,cancel);
          }catch(e){errors(resetStatus,e);previewButton.disabled=false;}
        },"btn btn-outline settings-action");
        resetBox.append(notice,previewButton,resetStatus);
        planned.appendChild(resetBox);
      }
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
