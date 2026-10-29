"use strict";
(function(){
 const api=()=>window.AltamamBridge;
 const el=(tag,text,cls)=>{const n=document.createElement(tag);
   if(text!==undefined)n.textContent=String(text);
   if(cls)n.className=cls;return n;};
 const btn=(text,action)=>{const b=el("button",text,"btn btn-outline");
   if(/^العودة إلى|^رجوع/.test(String(text)))b.className+=" app-back-action";
   b.type="button";b.addEventListener("click",action);return b;};
 const alert=(box,error)=>box.replaceChildren(el("p",
   error.message||"تعذرت قراءة التقرير.","admin-warning"));
 function table(columns){
   const wrap=el("div",undefined,"functional-table-wrap"),
     tab=el("table",undefined,"data-table functional-table"),
     head=el("thead"),body=el("tbody"),tr=el("tr");
   columns.forEach(c=>tr.appendChild(el("th",c)));
   head.appendChild(tr);tab.append(head,body);wrap.appendChild(tab);
   return {wrap,body};
 }
 function rows(root,columns,records,project,emptyText){
   const t=table(columns);
   for(const item of records||[]){
     const tr=el("tr");
     project(item).forEach(value=>tr.appendChild(el("td",value??"—")));
     t.body.appendChild(tr);
   }
   if(!t.body.children.length){
     const tr=el("tr"),cell=el("td",emptyText||"لا توجد سجلات");
     cell.colSpan=columns.length;tr.appendChild(cell);t.body.appendChild(tr);
   }
   root.appendChild(t.wrap);
 }
 function card(title){const x=el("section",undefined,"card");
   x.appendChild(el("h3",title,"section-title"));return x;}
 function render(container,onBack){
   const root=el("div",undefined,"person-report-page");
   container.replaceChildren(root);
   root.appendChild(btn("العودة إلى إدارة القوة",onBack));
   const searchCard=card("التقرير — فرد أو مجموعة");
   searchCard.append(el("p","بحث بالاسم أو الرقم (ع) في بيانات البرنامج؛ التقرير للقراءة فقط.",
     "section-description"));
   const term=el("input");
   term.type="search";term.placeholder="بحث بالاسم أو الرقم (ع)";
   term.setAttribute("aria-label","البحث عن فرد");
   const controls=el("div",undefined,"functional-toolbar");
   const results=el("div"),report=el("div",undefined,"person-report-content"),status=el("div"),selectedPanel=el("div");
   const popup=el("section",undefined,"person-report-modal");
   popup.hidden=true;popup.setAttribute("role","dialog");
   popup.setAttribute("aria-modal","true");
   popup.setAttribute("aria-label","معاينة تقرير التمام");
   const modalBox=el("div",undefined,"person-report-dialog");
   const modalHeading=el("div",undefined,"person-report-actions");
   const modalTitle=el("h2","معاينة تقرير التمام","section-title");
   const exportFeedback=el("div",undefined,"functional-feedback");
   let reportMode="person";
   const closePopup=()=>{popup.hidden=true;report.replaceChildren();};
   const closeButton=btn("إغلاق التقرير",closePopup);
   function reportPayload(){
     return reportMode==="group"?
       {mode:"report",personIds:Array.from(selected.keys()),...filters()}:
       {mode:"report",personId:chosen,...filters()};
   }
   async function exportExcel(){
     const payload=reportPayload();
     if(reportMode==="group"&&!payload.personIds.length)
       return alert(exportFeedback,new Error("اختر أفراد المجموعة أولًا."));
     exportFeedback.replaceChildren(el("p","جارٍ إعداد معاينة ملف Excel..."));
     try{
       const preview=(await api().invoke("BuildPersonLifetimeReport",
         {...payload,exportPreview:true})).data||{};
       if(popup.hidden)return;
       if(!preview.fileName||!preview.directory)
         throw Error("لم تصل بيانات معاينة التصدير.");
       const description="الملف: "+preview.fileName+" — المجلد: "+
         preview.directory+" — عدد الأفراد: "+preview.personCount+
         " — الأوراق: "+(preview.worksheets||[]).join("، ");
       const confirm=window.AltamamDialog?.confirm?
         await window.AltamamDialog.confirm(description+" — هل تؤكد التصدير؟"):
         await Promise.resolve(window.confirm?.(description)||false);
       if(!confirm)return exportFeedback.replaceChildren();
       const progressText=el("p","جارٍ إنشاء ملف Excel والتأكد من اكتمال الصفوف...");
       let monitoring=true,pollBusy=false;
       const cancelExport=btn("إيقاف التصدير الجاري",async()=>{
         cancelExport.disabled=true;
         try{
           await api().invoke("CancelExport",{});
           progressText.textContent="طُلب إلغاء التصدير؛ جارٍ إيقاف الكتابة...";
         }catch(e){if(!popup.hidden)alert(exportFeedback,e);}
       });
       exportFeedback.replaceChildren(progressText,cancelExport);
       const poll=(typeof setInterval === "function")?setInterval(async()=>{
         if(!monitoring||pollBusy||popup.hidden)return;
         pollBusy=true;
         try{
           const info=(await api().invoke("GetExportProgress",{},
             {timeoutMs:10000})).data||{};
           if(!monitoring||popup.hidden)return;
           const ratio=Number.isInteger(info.percentage)?" — "+info.percentage+"%":"";
           const rows=info.total?" ("+info.processed+" من "+info.total+" صف)":"";
           progressText.textContent=info.cancelRequested?
             "جارٍ إيقاف التصدير والتحقق من حذف الملف المؤقت...":
             "جارٍ كتابة Excel"+ratio+rows;
         }catch(e){
           if(e?.errorCode!=="MissingData"&&monitoring)
             progressText.textContent="جارٍ معالجة ملف Excel...";
         }finally{pollBusy=false;}
       },1250):null;
       try{
         const reply=await api().invoke("ExportPersonLifetimeExcel",
           {...payload,exportId:preview.exportId},{timeoutMs:600000});
         if(!popup.hidden)exportFeedback.replaceChildren(el("p",
           "تم حفظ Excel: "+reply.data.filePath,"settings-success"));
       }finally{monitoring=false;if(poll!==null && typeof clearInterval === "function")clearInterval(poll);}
     }catch(e){if(!popup.hidden)alert(exportFeedback,e);}
   }
   const exportButton=btn("تصدير التقرير إلى Excel",()=>{void exportExcel();});
   modalHeading.append(modalTitle,exportButton,closeButton);
   modalBox.append(modalHeading,exportFeedback,report);
   popup.appendChild(modalBox);
   popup.addEventListener("keydown",e=>{
     if(e.key==="Escape"){e.preventDefault?.();closePopup();}
   });
   popup.addEventListener("click",e=>{if(e.target===popup)closePopup();});
   const showPopup=mode=>{
     reportMode=mode;popup.hidden=false;exportFeedback.replaceChildren();
   };
   const dateFrom=el("input"),dateTo=el("input");
   dateFrom.type="date";dateTo.type="date";
   dateFrom.setAttribute("aria-label","بداية فترة التقرير");
   dateTo.setAttribute("aria-label","نهاية فترة التقرير");
   const site=el("select"),statusFilter=el("select"),movement=el("select");
   site.setAttribute("aria-label","فلتر الموقع التاريخي");
   statusFilter.setAttribute("aria-label","فلتر حالة التحضير التاريخية");
   movement.setAttribute("aria-label","فلتر نوع الحركة التاريخية");
   const addOption=(select,value,label)=>{
     const option=el("option",label);option.value=value;
     select.appendChild(option);
   };
   addOption(site,"","جميع المواقع التاريخية");
   addOption(statusFilter,"","كل حالات التحضير");
   [["0","كل الحركات"],["1","النقل الداخلي"],["2","النقل الخارجي"],
    ["3","الفصل"],["4","العودة للخدمة"]].forEach(([value,label])=>
      addOption(movement,value,label));
   movement.value="0";
   async function loadHistoricalFilters(){
     try{
       const [sites,statuses]=await Promise.all([
         api().invoke("QuerySites",{}),api().invoke("GetActiveAttendanceStatuses",{})
       ]);
       if(!root.isConnected)return;
       for(const entry of sites.data||[]){
         if(entry.siteId)addOption(site,entry.siteId,entry.name||entry.siteId);
       }
       for(const entry of statuses.data||[]){
         const id=entry.statusId||entry.id;
         if(id)addOption(statusFilter,id,entry.name||entry.displayName||id);
       }
     }catch(e){alert(status,e);}
   }
   const filters=()=>({
     dateFrom:dateFrom.value,dateTo:dateTo.value,
     siteId:site.value,statusId:statusFilter.value,
     movementType:Number(movement.value||0)
   });
   const fromLabel=el("label",undefined,"من تاريخ"),toLabel=el("label",undefined,"إلى تاريخ");
   fromLabel.appendChild(dateFrom);toLabel.appendChild(dateTo);
   const selected=new Map();
   controls.append(term,btn("بحث",search),
     btn("عرض التقرير الجماعي",()=>{void loadGroupReport();}));
   const siteLabel=el("label",undefined,"الموقع التاريخي");
   siteLabel.appendChild(site);
   const statusLabel=el("label",undefined,"حالة التحضير");
   statusLabel.appendChild(statusFilter);
   const movementLabel=el("label",undefined,"نوع الحركة");
   movementLabel.appendChild(movement);
   searchCard.append(controls,el("p","حدد الأشخاص من نتائج البحث؛ يمكن الاختيار عبر أكثر من عملية بحث، بحد أقصى 100 فرد.","section-description"),
     selectedPanel,fromLabel,toLabel,siteLabel,statusLabel,movementLabel,status,results);
   root.append(searchCard,popup);
   void loadHistoricalFilters();
   let chosen=null,serial=0,attendancePage=1,auditPage=1,identityPage=1,
     movementPage=1,sitePage=1,revisionPage=1;
   function renderSelected(){
     const box=card("الأفراد المحددون للمجموعة: "+selected.size);
     if(!selected.size)box.appendChild(el("p","لم تحدد أفراداً بعد."));
     for(const person of selected.values()){
       const line=el("div",undefined,"functional-toolbar");
       line.append(el("span",person.displayName+" — الرقم (ع): "+person.serviceNumberA+
         (person.active?" — نشط":" — سجل مؤرشف")),
         btn("إزالة",()=>{selected.delete(person.personId);renderSelected();}));
       box.appendChild(line);
     }
     selectedPanel.replaceChildren(box);
   }
   renderSelected();
   async function loadGroupReport(){
     if(!selected.size)return alert(status,new Error("حدد فرداً أو مجموعة من نتائج البحث أولاً."));
     const ids=Array.from(selected.keys());
     const ticket=++serial;
     showPopup("group");
     status.replaceChildren();
     report.replaceChildren(el("p","جارٍ إعداد التقرير الجماعي من السجلات المعتمدة..."));
     try{
       const reply=await api().invoke("BuildPersonLifetimeReport",{
         personIds:ids,...filters()
       });
       if(ticket!==serial||!root.isConnected)return;
       const data=reply.data||{};
       if(data.mode!=="group"||!Array.isArray(data.persons)||data.persons.length!==ids.length)
         throw Error("لم يتم اعتماد تقرير المجموعة من قاعدة البيانات.");
       report.replaceChildren();
       const summary=card("ملخص التقرير الجماعي — "+data.personCount+" فرد");
       summary.append(el("p","فترة التقرير: "+(data.dateFrom||"من بداية السجل")+" إلى "+
         (data.dateTo||"آخر سجل محفوظ")),
         el("p","إجمالي التحضيرات المعتمدة: "+data.attendanceTotal+
           " — إجمالي الحركات: "+data.movementTotal));
       rows(summary,["الحالة","عدد الأيام المعتمدة"],data.statusTotals,
         x=>[x.statusName,x.count],"لا توجد حالات تحضير معتمدة في هذه الفترة.");
       report.appendChild(summary);
       const perPerson=card("تفصيل كل فرد في المجموعة — دون دمج السجلات المتشابهة");
       const t=table(["الرقم (ع)","اسم الفرد","نوع السجل","أيام التحضير","التنقلات","الحالات","التفاصيل"]);
       for(const person of data.persons){
         const tr=el("tr");
         const kind=person.active?"نشط":person.organizationalStatus===3?
           "مفصول":person.organizationalStatus===2?"منقول خارجيًا":"مؤرشف";
         const states=(person.statusTotals||[])
           .map(x=>x.statusName+" ("+x.count+")").join("، ")||"لا توجد";
         [person.serviceNumberA,person.name,kind,person.attendanceTotal,
           person.movementTotal,states]
           .forEach(x=>tr.appendChild(el("td",x)));
         const action=el("td");
         action.appendChild(btn("عرض سجل الفرد",()=>{
           chosen=person.personId;
           attendancePage=auditPage=identityPage=movementPage=sitePage=revisionPage=1;
           void loadReport();
         }));
         tr.appendChild(action);t.body.appendChild(tr);
       }
       perPerson.appendChild(t.wrap);
       report.appendChild(perPerson);
     }catch(e){if(ticket===serial&&root.isConnected)alert(report,e);}
   }
   async function search(){
     const value=term.value.trim();
     if(!value)return alert(status,new Error("أدخل اسم الفرد أو الرقم (ع)."));
     chosen=null;closePopup();
     const ticket=++serial;
     results.replaceChildren(el("p","جارٍ البحث عن الأفراد..."));
     try{
       const reply=await api().invoke("QueryPersons",{
         term:value,page:1,pageSize:100,includeArchived:true
       });
       if(ticket!==serial||!root.isConnected)return;
       const items=reply.data?.items||[];
       const t=table(["اختيار","الرقم (ع)","الاسم","حالة السجل","الموقع","التقرير"]);
       for(const person of items){
         const tr=el("tr");
         const selectCell=el("td");
         const toggle=btn(selected.has(person.personId)?"إلغاء الاختيار":"إضافة للمجموعة",()=>{
           if(selected.has(person.personId))selected.delete(person.personId);
           else {
             if(selected.size>=100)return alert(status,new Error("الحد الأقصى 100 فرد لكل تقرير."));
             selected.set(person.personId,person);
           }
           toggle.textContent=selected.has(person.personId)?"إلغاء الاختيار":"إضافة للمجموعة";
           renderSelected();
         });
         selectCell.appendChild(toggle);tr.appendChild(selectCell);
         const lifecycle=person.active?"نشط":person.organizationalStatus===3?
           "مفصول — سجل تاريخي":person.organizationalStatus===2?
           "منقول خارجيًا — سجل تاريخي":"مؤرشف — سجل تاريخي";
         [person.serviceNumberA,person.displayName,lifecycle,person.siteName]
           .forEach(v=>tr.appendChild(el("td",v)));
         const cell=el("td");
         cell.appendChild(btn("عرض التقرير",()=>{
           chosen=person.personId;attendancePage=1;auditPage=1;identityPage=1;movementPage=1;sitePage=1;revisionPage=1;
           void loadReport();
         }));
         tr.appendChild(cell);t.body.appendChild(tr);
       }
       if(!items.length){
         const tr=el("tr"),cell=el("td","لا يوجد فرد مطابق للبحث");
         cell.colSpan=6;tr.appendChild(cell);t.body.appendChild(tr);
       }
       results.replaceChildren(t.wrap);
     }catch(e){if(ticket===serial&&root.isConnected)alert(results,e);}
   }
   function pageBar(box,label,pageNo,total,hasNext,change){
     const bar=el("div",undefined,"functional-pager");
     const previous=btn("السابق",()=>change(pageNo-1));
     const next=btn("التالي",()=>change(pageNo+1));
     previous.disabled=pageNo<=1;next.disabled=!hasNext;
     bar.append(el("span",label+" — الإجمالي: "+total+" — الصفحة: "+pageNo),
       previous,next);box.appendChild(bar);
   }
   async function loadReport(){
     if(!chosen)return;
     const ticket=++serial;
     showPopup("person");
     status.replaceChildren();
     report.replaceChildren(el("p","جارٍ قراءة تاريخ الفرد من قاعدة البيانات المشفرة..."));
     try{
       const reply=await api().invoke("BuildPersonLifetimeReport",{
         personId:chosen,attendancePage,auditPage,identityPage,movementPage,sitePage,
         revisionPage,
         ...filters()
       });
       if(ticket!==serial||!root.isConnected)return;
       const d=reply.data||{},person=d.person||{};
       report.replaceChildren();
       if(selected.size)report.appendChild(btn("العودة إلى التقرير الجماعي",()=>{void loadGroupReport();}));
       const identity=card("بيانات الفرد");
       identity.append(
         el("p","الاسم: "+person.name+" — الرقم (ع): "+person.serviceNumberA),
         el("p","تاريخ الإدراج: "+person.firstRecordedDate),
         el("p","حالة الفرد: "+(person.active?"ضمن القوة النشطة":"غير نشط أو مؤرشف"))
       );report.appendChild(identity);
       const totals=card("إجمالي أيام الحالات المسجلة");
       rows(totals,["الحالة","عدد الأيام","أول يوم","آخر يوم"],d.statusTotals,
         x=>[x.statusName,x.count,x.firstDate,x.lastDate],
         "لا توجد حالات تحضير مسجلة لهذا الفرد.");
       report.appendChild(totals);
       const monthly=card("تفصيل الحالات حسب الشهر");
       rows(monthly,["الشهر","الحالة","عدد الأيام"],d.monthlyTotals,
         x=>[x.period,x.statusName,x.count]);
       report.appendChild(monthly);
       const daily=card("سجل التحضير اليومي");
       rows(daily,["التاريخ","الحالة","الموقع","مصدر التحضير","عدد التعديلات"],
         d.attendance,x=>[x.date,x.statusName,x.site,x.source,x.revision]);
       pageBar(daily,"أيام التحضير",d.attendancePage,d.attendanceTotal,d.attendanceHasNext,
         p=>{attendancePage=p;void loadReport();});
       report.appendChild(daily);
       const memberships=card("تاريخ المواقع والفترات الفعلية");
       rows(memberships,["الموقع التاريخي","بداية الانتساب","نهاية الانتساب"],
         d.siteHistory,x=>[x.siteName,x.from,x.to]);
       pageBar(memberships,"فترات المواقع",d.sitePage||1,d.siteTotal||0,d.siteHasNext,
         p=>{sitePage=p;void loadReport();});
       report.appendChild(memberships);
       const history=card("تاريخ الاسم والرقم (ع)");
       rows(history,["الاسم السابق أو الحالي","الرقم (ع)","بداية النفاذ","نهاية النفاذ",
         "سبب التعديل"],d.identityHistory,
         x=>[x.name,x.serviceNumberA,x.from,x.to,x.reason]);
       pageBar(history,"تغييرات الهوية",d.identityPage,d.identityTotal,d.identityHasNext,
         p=>{identityPage=p;void loadReport();});
       report.appendChild(history);
       const moves=card("التنقلات والفصل");
       rows(moves,["النوع","الموقع السابق","الجهة الجديدة","التاريخ","سبب الفصل"],
         d.movements,x=>[x.type,x.source,x.destination,x.date,x.reason]);
       pageBar(moves,"التنقلات والفصل",d.movementPage,d.movementTotal,d.movementHasNext,
         p=>{movementPage=p;void loadReport();});
       report.appendChild(moves);
       const revisions=card("جميع تصحيحات التحضير المحفوظة تاريخياً");
       rows(revisions,["تاريخ التحضير","الحالة السابقة","الحالة الجديدة",
         "السبب","المنفّذ (المستخدم - الاسم المحفوظ)","وقت التصحيح"],
         d.revisions,x=>[x.date,x.before,x.after,x.reason,x.executor,x.executedAtUtc]);
       pageBar(revisions,"تصحيحات التحضير",d.revisionPage||1,d.revisionTotal||0,
         d.revisionHasNext,p=>{revisionPage=p;void loadReport();});
       report.appendChild(revisions);
       const audit=card("سجل العمليات المرتبطة بالفرد");
       rows(audit,["العملية","المنفّذ (المستخدم - الاسم المحفوظ)","التاريخ",
          "الوصف","القيمة السابقة","القيمة الجديدة","السبب"],
         d.audit,x=>[x.operation,x.executor||x.username+" - "+x.fullName,
           x.executedAtUtc,x.description,x.oldValueJson,x.newValueJson,x.reason]);
       pageBar(audit,"سجل العمليات",d.auditPage,d.auditTotal,d.auditHasNext,
         p=>{auditPage=p;void loadReport();});
       report.appendChild(audit);
       const note=el("p","التقرير للقراءة فقط؛ المرشحات تعتمد على تاريخ التحضير "+
         "وموقعه المؤرخ، وتبقى فترات الهوية والمواقع كاملة. "+
         "وتتوفر تصحيحات التحضير في التقرير والتصدير من الخدمة نفسها.","section-description");
       report.appendChild(note);
     }catch(e){if(ticket===serial&&root.isConnected)alert(report,e);}
   }
 }
 window.AltamamReports=Object.freeze({render});
})();
