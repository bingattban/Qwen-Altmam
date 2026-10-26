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
   root.appendChild(btn("العودة إلى التقارير والفصل",onBack));
   const searchCard=card("التقرير التاريخي للفرد");
   searchCard.append(el("p","بحث بالاسم أو الرقم (ع) في بيانات البرنامج؛ التقرير للقراءة فقط.",
     "section-description"));
   const term=el("input");
   term.type="search";term.placeholder="بحث بالاسم أو الرقم (ع)";
   term.setAttribute("aria-label","البحث عن فرد");
   const controls=el("div",undefined,"functional-toolbar");
   const results=el("div"),report=el("div"),status=el("div");
   controls.append(term,btn("بحث",search));
   searchCard.append(controls,status,results);root.append(searchCard,report);
   let chosen=null,serial=0,attendancePage=1,auditPage=1,identityPage=1,movementPage=1;
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
       const t=table(["الرقم (ع)","الاسم","حالة السجل","الموقع","التقرير"]);
       for(const person of items){
         const tr=el("tr");
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
         cell.colSpan=5;tr.appendChild(cell);t.body.appendChild(tr);
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
