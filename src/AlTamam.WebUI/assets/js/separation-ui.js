"use strict";
// Separation and immutable separation archive, backed by C# SQLCipher policies.
(function(){
 const api=()=>window.AltamamBridge;
 const e=(tag,text,css)=>{const x=document.createElement(tag);
   if(text!==undefined)x.textContent=String(text);
   if(css)x.className=css;
   return x;};
 const btn=(text,cb)=>{const b=e("button",text,"btn btn-outline");
   b.type="button";b.addEventListener("click",cb);return b;};
 const error=(container,ex)=>container.replaceChildren(e("p",
   ex.message||"تعذر إتمام العملية، ولم يتم تغيير السجلات.","admin-warning"));
 function table(cols){
   const wrap=e("div",undefined,"functional-table-wrap"),
     t=e("table",undefined,"data-table functional-table"),
     tr=e("tr"),thead=e("thead"),body=e("tbody");
   cols.forEach(v=>tr.appendChild(e("th",v)));
   thead.appendChild(tr);t.append(thead,body);wrap.appendChild(t);
   return {wrap,body};
 }
 function today(){
   const d=new Date();return [d.getFullYear(),String(d.getMonth()+1).padStart(2,"0"),
     String(d.getDate()).padStart(2,"0")].join("-");
 }
 function render(root,kind,onBack){
   const archive=kind==="archive";let alive=true,request=0,page=1,revision=0;
   let delay=null;
   const host=e("div",undefined,"separation-page");
   root.replaceChildren(host);
   const head=e("section",undefined,"card"),
     title=e("h2",archive?"أرشيف المفصولين":"فصل فرد","section-title"),
     form=e("section",undefined,"card"),
     note=e("div",undefined,"admin-note"),
     results=e("div"),preview=e("div");
   head.append(btn("العودة إلى الفصل",()=>{
     alive=false;request++;clear();onBack();
   }),title);
   form.append(note,results,preview);host.append(head,form);
   const active=()=>alive&&host.isConnected;
   const clear=()=>{revision++;preview.replaceChildren();
     if(delay!==null){clearTimeout(delay);delay=null;}};
   if(archive){
     const row=e("div",undefined,"functional-toolbar"),term=e("input");
     term.placeholder="بحث بالاسم أو الرقم (ع)";
     term.setAttribute("aria-label","بحث في أرشيف المفصولين");
     row.append(term,btn("بحث",()=>{page=1;load();}));
     form.insertBefore(row,note);
     async function load(){
       const token=++request;
       results.replaceChildren(e("p","جارٍ قراءة أرشيف الفصل..."));
       try{
         const data=(await api().invoke("QuerySeparationArchive",{
           term:term.value.trim(),page,pageSize:50
         })).data||{};
         if(!active()||token!==request)return;
         const grid=table(["الرقم (ع)","الاسم","الموقع","سبب الفصل",
           "تاريخ الفصل","التفاصيل"]);
         for(const person of data.items||[]){
           const tr=e("tr");
           [person.serviceNumberA,person.name,person.siteName,
             person.reason,person.date].forEach(v=>tr.appendChild(e("td",v||"—")));
           const td=e("td");
           td.appendChild(btn("عرض",async()=>{
             clear();try{
               const details=(await api().invoke("GetSeparationDetails",{
                 movementId:person.movementId
               })).data||{};
               if(!active())return;
               const card=e("section",undefined,"card");
               card.append(e("h3","بيانات الفصل المحفوظة","section-title"),
                 e("p","الفرد: "+details.name+" — الرقم (ع): "+details.serviceNumberA),
                 e("p","الموقع: "+details.siteName),
                 e("p","سبب الفصل: "+details.reason),
                 e("p","تاريخ الفصل: "+details.date),
                 e("p","منفذ الإجراء: "+details.actorUserId),
                 btn("إغلاق",clear));
               preview.appendChild(card);
             }catch(err){if(active())error(note,err);}
           }));
           tr.appendChild(td);grid.body.appendChild(tr);
         }
         if(!grid.body.children.length){
           const tr=e("tr"),td=e("td","لا توجد أسماء في أرشيف الفصل");
           td.colSpan=6;tr.appendChild(td);grid.body.appendChild(tr);
         }
         const pager=e("div",undefined,"functional-pager");
         const prev=btn("السابق",()=>{page--;load();});
         const next=btn("التالي",()=>{page++;load();});
         prev.disabled=page<=1;next.disabled=!data.hasNext;
         pager.append(e("span","الإجمالي: "+(data.total||0)),prev,next);
         results.replaceChildren(grid.wrap,pager);
       }catch(ex){if(active()&&token===request)error(results,ex);}
     }
     void load();return;
   }
   const query=e("input"),reason=e("textarea");
   query.setAttribute("aria-label","بحث عن فرد");
   query.placeholder="الاسم أو الرقم (ع)";
   reason.setAttribute("aria-label","سبب الفصل الإجباري");
   reason.placeholder="سبب الفصل (خمسة أحرف على الأقل)";
   reason.rows=3;
   const control=e("div",undefined,"functional-toolbar");
   control.append(query,btn("البحث عن فرد",search));
   form.insertBefore(control,note);
   form.insertBefore(reason,note);
   const picked=e("select");picked.setAttribute("aria-label","الفرد المحدد");
   form.insertBefore(picked,note);
   picked.appendChild(Object.assign(e("option","اختر فرداً من الكشف"),{value:""}));
   let lastSearch=[];
   reason.addEventListener("input",clear);
   picked.addEventListener("change",clear);
   async function search(){
     clear();results.replaceChildren();const term=query.value.trim();
     if(!term)return error(note,new Error("أدخل اسماً أو الرقم (ع) للبحث."));
     const ticket=++request;
     note.replaceChildren(e("p","جارٍ البحث عن الفرد..."));
     try{
       const data=(await api().invoke("SearchActivePersonsForAccount",{
         term,take:30
       })).data||{};
       if(ticket!==request||!active())return;
       lastSearch=Array.isArray(data.items)?data.items:[];
       picked.replaceChildren(Object.assign(e("option","اختر الفرد"),{value:""}));
       for(const p of lastSearch){
         const option=e("option",p.displayName+" — ع: "+p.serviceNumberA);
         option.value=p.personId;picked.appendChild(option);
       }
       note.replaceChildren(e("p",lastSearch.length?
         "اختر الفرد المطلوب ثم اكتب السبب.":"لا توجد أسماء مطابقة للبحث."));
     }catch(ex){if(ticket===request&&active())error(note,ex);}
   }
   form.insertBefore(btn("معاينة الفصل",previewSeparation),results);
   async function previewSeparation(){
     if(!picked.value||reason.value.trim().length<5)
       return error(note,new Error("اختر الفرد واكتب سبب فصل لا يقل عن خمسة أحرف."));
     clear();const rev=revision;
     try{
       const data=(await api().invoke("PreviewSeparation",{
         personId:picked.value,reason:reason.value.trim(),businessDate:today()
       })).data||{};
       if(!active()||revision!==rev)return;
       if(!data.previewToken)throw new Error("لم يعتمد الخادم معاينة الفصل.");
       const card=e("section",undefined,"card"),
         safe=btn("تأكيد الفصل (بعد 5 ثوانٍ)",async()=>{
           if(!confirm("هذا الإجراء سيفصل الفرد ويحفظ الأرشيف. تأكيد نهائي؟"))return;
           safe.disabled=true;
           try{
             const result=(await api().invoke("CommitSeparation",{
               previewToken:data.previewToken
             })).data||{};
             if(!active())return;
             clear();picked.value="";reason.value="";
             results.replaceChildren(e("p","تم فصل "+result.name+
               " وحفظ سجل الفصل بنجاح.","settings-success"));
           }catch(ex){if(active())error(note,ex);clear();}
           finally{safe.disabled=false;}
         });
       safe.disabled=true;
       card.append(e("h3","مراجعة بيانات الفصل","section-title"),
         e("p","الاسم: "+data.name),
         e("p","الرقم (ع): "+data.serviceNumberA),
         e("p","الموقع الحالي: "+data.siteName),
         e("p","سبب الفصل: "+data.reason),
         e("p","تاريخ الفصل: "+data.businessDate),
         e("p","تأكيد الفصل لن يُتاح إلا بعد مرور خمس ثوانٍ، ويفرض الخادم المهلة أيضًا."),
         safe,btn("إلغاء",()=>{if(confirm("إلغاء المعاينة دون فصل؟"))clear();}));
       preview.appendChild(card);
       delay=setTimeout(()=>{
         delay=null;
         if(active()&&revision===rev){
           safe.textContent="تأكيد الفصل النهائي";safe.disabled=false;
         }
       },Math.max(5,Number(data.minimumConfirmationSeconds)||5)*1000);
     }catch(ex){if(active())error(note,ex);}
   }
 }
 window.AltamamSeparation=Object.freeze({render});
})();
