"use strict";
(function(){
 const api=()=>window.AltamamBridge;
 const el=(tag,text,cls)=>{const n=document.createElement(tag);
   if(text!==undefined)n.textContent=String(text);
   if(cls)n.className=cls;return n;};
 const btn=(text,action)=>{const b=el("button",text,"btn btn-outline");
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
   const results=el("div"),report=el("div"),status=el("div"),selectedPanel=el("div");
   const dateFrom=el("input"),dateTo=el("input");
   dateFrom.type="date";dateTo.type="date";
   dateFrom.setAttribute("aria-label","بداية فترة التقرير الجماعي");
   dateTo.setAttribute("aria-label","نهاية فترة التقرير الجماعي");
   const fromLabel=el("label",undefined,"من تاريخ"),toLabel=el("label",undefined,"إلى تاريخ");
   fromLabel.appendChild(dateFrom);toLabel.appendChild(dateTo);
   const selected=new Map();
   controls.append(term,btn("بحث",search),
     btn("عرض التقرير الجماعي",()=>{void loadGroupReport();}));
   searchCard.append(controls,el("p","حدد الأشخاص من نتائج البحث؛ يمكن الاختيار عبر أكثر من عملية بحث، بحد أقصى 100 فرد.","section-description"),
     selectedPanel,fromLabel,toLabel,status,results);
   root.append(searchCard,report);
   let chosen=null,serial=0,attendancePage=1,auditPage=1,identityPage=1,movementPage=1;
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
     status.replaceChildren();
     report.replaceChildren(el("p","جارٍ إعداد التقرير الجماعي من السجلات المعتمدة..."));
     try{
       const reply=await api().invoke("BuildPersonLifetimeReport",{
         personIds:ids,dateFrom:dateFrom.value,dateTo:dateTo.value
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
           attendancePage=auditPage=identityPage=movementPage=1;
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
     chosen=null;report.replaceChildren();
     const ticket=++serial;
     results.replaceChildren(el("p","جارٍ البحث عن الأفراد..."));
     try{
       const reply=await api().invoke("QueryPersons",{
         term:value,page:1,pageSize:50,includeArchived:true
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
           chosen=person.personId;attendancePage=1;auditPage=1;identityPage=1;movementPage=1;
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
     status.replaceChildren();
     report.replaceChildren(el("p","جارٍ قراءة تاريخ الفرد من قاعدة البيانات المشفرة..."));
     try{
       const reply=await api().invoke("BuildPersonLifetimeReport",{
         personId:chosen,attendancePage,auditPage,identityPage,movementPage
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
       const audit=card("سجل العمليات المرتبطة بالفرد");
       rows(audit,["العملية","من قام بالتنفيذ","الاسم الكامل","التاريخ","الوصف"],
         d.audit,x=>[x.operation,x.username,x.fullName,x.executedAtUtc,x.description]);
       pageBar(audit,"سجل العمليات",d.auditPage,d.auditTotal,d.auditHasNext,
         p=>{auditPage=p;void loadReport();});
       report.appendChild(audit);
       const note=el("p","هذا التقرير للقراءة فقط. تصديره بصيغة Excel "+
         "يحتاج إلى خدمة التصدير المعتمدة ولا يُعتبر متاحاً حاليًا.","section-description");
       report.appendChild(note);
     }catch(e){if(ticket===serial&&root.isConnected)alert(report,e);}
   }
 }
 window.AltamamReports=Object.freeze({render});
})();
