"use strict";
(function(){
 const make=(tag,text,cls)=>{
   const e=document.createElement(tag);
   if(text!==undefined)e.textContent=String(text);
   if(cls)e.className=cls;
   return e;
 };
 const button=(text,click)=>{
   const b=make("button",text,"btn btn-outline");
   b.type="button";b.addEventListener("click",click);return b;
 };
 const option=(value,title)=>{
   const o=make("option",title);o.value=value;return o;
 };
 const showError=(box,error)=>{
   box.replaceChildren(make("p",error.message||"تعذرت العملية","admin-warning"));
 };
 function localDate(){
   const now=new Date();
   return [now.getFullYear(),String(now.getMonth()+1).padStart(2,"0"),
       String(now.getDate()).padStart(2,"0")].join("-");
 }
 async function render(page){
   const bridge=window.AltamamBridge;
   const day=localDate();
   const host=make("div",undefined,"admin-attendance-host");
   page.replaceChildren(host);
   const header=make("section",undefined,"card");
   header.append(make("h2","التمام اليومي","section-title"),
       make("p","تحضير فردي أو جماعي لليوم الحالي. لا يحفظ أي تعديل قبل المراجعة.","section-description"));
   const filters=make("div",undefined,"functional-toolbar");
   const site=make("select"),status=make("select"),mode=make("select");
   site.setAttribute("aria-label","الموقع");
   status.setAttribute("aria-label","الحالة الجديدة");
   mode.setAttribute("aria-label","نوع العملية");
   site.appendChild(option("","جميع المواقع"));
   status.appendChild(option("","اختر الحالة"));
   mode.append(option("initial","تحضير جديد"),option("revise","تعديل تحضير اليوم"));
   const reason=make("input");reason.placeholder="سبب تغيير التحضير (5 أحرف على الأقل)";
   reason.setAttribute("aria-label","سبب تغيير التحضير");
   reason.hidden=true;
   const list=make("div"),info=make("div"),review=make("div");
   const choice=make("span","لم يتم تحديد أسماء");
   const selected=new Set();
   let pageNo=1,loading=false,nonce=0;
   const summary=()=>choice.textContent="الأفراد المحددون: "+selected.size+" من 100";
   const clearReview=()=>review.replaceChildren();
   const reset=()=>{selected.clear();clearReview();summary();};
   mode.addEventListener("change",()=>{
     reason.hidden=mode.value!=="revise";clearReview();
   });
   status.addEventListener("change",clearReview);
   site.addEventListener("change",()=>{pageNo=1;reset();load();});
   filters.append(site,status,mode,reason);
   header.append(filters,choice,info,list,review);
   host.appendChild(header);
   const fields=["تحديد","الرقم (ع)","الاسم","الموقع","الحالة الحالية"];
   function table(headers){
     const outer=make("div",undefined,"functional-table-wrap");
     const t=make("table",undefined,"data-table functional-table");
     const tr=make("tr");
     headers.forEach(h=>tr.appendChild(make("th",h)));
     const head=make("thead"),body=make("tbody");
     head.appendChild(tr);t.append(head,body);outer.appendChild(t);
     return {outer,body};
   }
   async function load(){
     if(loading)return;
     loading=true;const current=++nonce;
     list.replaceChildren(make("p","جارٍ تحميل كشف القوة..."));
     try{
       const response=await bridge.invoke("QueryDailyRoster",{
         businessDate:day,siteId:site.value,statusId:"",
         page:pageNo,pageSize:50
       });
       if(current!==nonce||!host.isConnected)return;
       const d=response.data||{},t=table(fields);
       for(const p of d.items||[]){
         const tr=make("tr"),td=make("td"),cb=make("input");
         cb.type="checkbox";cb.checked=selected.has(p.personId);
         cb.setAttribute("aria-label","تحديد "+p.name);
         cb.addEventListener("change",()=>{
           if(cb.checked&&selected.size>=100){
             cb.checked=false;return showError(info,new Error("الحد 100 فرد لكل عملية."));
           }
           if(cb.checked)selected.add(p.personId);else selected.delete(p.personId);
           summary();clearReview();
         });
         td.appendChild(cb);tr.appendChild(td);
         [p.serviceNumberA,p.name,p.siteName,p.statusName].forEach(v=>tr.appendChild(make("td",v)));
         t.body.appendChild(tr);
       }
       if(!t.body.children.length){
         const tr=make("tr"),td=make("td","لا يوجد أفراد");td.colSpan=5;
         tr.appendChild(td);t.body.appendChild(tr);
       }
       const pages=make("div",undefined,"functional-pager");
       const prev=button("السابق",()=>{pageNo--;load();});
       const next=button("التالي",()=>{pageNo++;load();});
       prev.disabled=pageNo<=1;next.disabled=!d.hasNext;
       pages.append(make("span","الإجمالي: "+(d.total||0)),prev,next);
       list.replaceChildren(t.outer,pages);
     }catch(e){if(current===nonce)showError(list,e);}
     finally{loading=false;}
   }
   async function preview(){
     if(!status.value||selected.size===0)
       return showError(info,new Error("اختر الحالة والأفراد."));
     if(mode.value==="revise"&&reason.value.trim().length<5)
       return showError(info,new Error("أدخل سبب تغيير التحضير."));
     clearReview();info.replaceChildren();
     try{
       const r=await bridge.invoke("PreviewAttendanceChange",{
         businessDate:day,statusId:status.value,siteId:site.value,
         personIds:Array.from(selected).sort(),mode:mode.value,
         reason:mode.value==="revise"?reason.value.trim():""
       });
       if(!host.isConnected)return;
       const data=r.data||{};
       if(!data.previewToken||!Array.isArray(data.persons))
         throw Error("تعذر اعتماد المعاينة.");
       const c=make("section",undefined,"card");
       c.appendChild(make("h3","مراجعة التحضير قبل التأكيد","section-title"));
       const t=table(["الرقم (ع)","الاسم","الحالة السابقة","الحالة الجديدة"]);
       for(const p of data.persons){
         const row=make("tr");
         [p.serviceNumberA,p.name,p.previousStatusName,data.statusName]
           .forEach(v=>row.appendChild(make("td",v)));
         t.body.appendChild(row);
       }
       c.appendChild(t.outer);
       const save=button("تأكيد وحفظ التحضير",async()=>{
         if(!confirm("اعتماد "+data.count+" فرداً في حالة "+data.statusName+"؟"))return;
         save.disabled=true;
         try{
           const action=data.mode==="revise"?"ReviseTodayAttendance":"SubmitAttendance";
           const result=await bridge.invoke(action,{previewToken:data.previewToken});
           reset();pageNo=1;await load();
           info.replaceChildren(make("p","تم حفظ "+result.data.count+" فرداً.","settings-success"));
         }catch(e){showError(info,e);clearReview();}
         finally{save.disabled=false;}
       });
       c.append(save,button("إلغاء",clearReview));
       review.appendChild(c);
     }catch(e){showError(info,e);}
   }
   filters.appendChild(button("تحديث",()=>{reset();load();}));
   header.insertBefore(button("معاينة التحضير",preview),choice);
   header.insertBefore(button("العودة إلى إدارة القوة",()=>{
     window.AltamamAdminSettings.renderGroup("force");
   }),filters);
   try{
     const [sites,statuses]=await Promise.all([
       bridge.invoke("QuerySites",{}),bridge.invoke("GetActiveAttendanceStatuses",{})
     ]);
     for(const s of sites.data||[])if(s.active!==false)site.appendChild(option(s.siteId,s.name));
     for(const x of statuses.data||[])status.appendChild(option(x.statusId,x.displayName));
     await load();
   }catch(e){showError(info,e);}
 }
 window.AltamamAttendance=Object.freeze({render});
})();
