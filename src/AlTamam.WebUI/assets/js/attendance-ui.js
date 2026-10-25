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
  const modalConfirm=text=>window.AltamamDialog?.confirm(text,{title:"تأكيد التمام"}) ?? Promise.resolve(confirm(text));
   const day=localDate();
   const host=make("div",undefined,"admin-attendance-host");
   page.replaceChildren(host);
   const header=make("section",undefined,"card");
   header.append(make("h2","التمام اليومي","section-title"),
       make("p","التحضير الجديد متاح إلى الساعة ٨ مساءً، وبعدها يعتمد النظام حالة الأمس لمن لم يُحضَّر. يوم النقل مستثنى.","section-description"));
   const filters=make("div",undefined,"functional-toolbar");
   const site=make("select"),status=make("select"),mode=make("select"),
     search=make("input");search.type="search";
   search.placeholder="بحث بالاسم أو الرقم (ع)";
   search.setAttribute("aria-label","بحث في أسماء وأرقام القوة");
   site.setAttribute("aria-label","الموقع");
   status.setAttribute("aria-label","الحالة الجديدة");
   mode.setAttribute("aria-label","نوع العملية");
   site.appendChild(option("","جميع المواقع"));
   status.appendChild(option("","اختر الحالة"));
   mode.append(option("initial","تحضير جديد"),option("revise","تغيير حالة التحضير"));
   const list=make("div"),info=make("div");
   const choice=make("span","لم يتم تحديد أسماء");
   const draftPanel=make("section",undefined,"card attendance-drafts");
   const draftResults=make("div");
   draftPanel.append(make("h3","مسودات التمام المحفوظة","section-title"),draftResults);
   const selected=new Set();
   let activeDraftId=null;
   let pageNo=1,loading=false,nonce=0,previewVersion=0;
   const summary=()=>choice.textContent="الأفراد المحددون: "+selected.size+" من 100";
   const clearReview=()=>{previewVersion++;};
   const reset=()=>{selected.clear();activeDraftId=null;clearReview();summary();};
   mode.addEventListener("change",()=>{
     clearReview();
     previewButton.textContent=mode.value==="revise"?"معاينة تغيير الحالة":"معاينة التحضير";
   });
   status.addEventListener("change",clearReview);
   site.addEventListener("change",()=>{pageNo=1;reset();load();});
   search.addEventListener("input",clearReview);
   search.addEventListener("keydown",e=>{
     if(e.key==="Enter"){e.preventDefault();pageNo=1;load();}
   });
   filters.append(site,search,button("بحث",()=>{pageNo=1;load();}),status,mode);
   header.append(filters,choice,info,list);
   host.appendChild(draftPanel);
   host.insertBefore(header,draftPanel);
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
         businessDate:day,siteId:site.value,statusId:"__ELIGIBLE__",
         term:search.value.trim(),page:pageNo,pageSize:50
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
   async function refreshDrafts(){
     draftResults.replaceChildren(make("p","جارٍ تحميل المسودات..."));
     try{
       const result=await bridge.invoke("ListAttendanceDrafts",{});
       if(!host.isConnected)return;
       if(!Array.isArray(result.data))throw Error("صيغة المسودات غير صالحة.");
       const records=result.data;
       const content=make("div",undefined,"attendance-draft-list");
       if(!records.length)content.appendChild(make("p","لا توجد مسودات محفوظة."));
       for(const draft of records){
         const row=make("div",undefined,"attendance-draft-row");
         const total=Array.isArray(draft.selectedPersonIds)?draft.selectedPersonIds.length:0;
         row.appendChild(make("span",(draft.businessDate||"؟")+" — "+total+" فرد"));
         const restore=button("استعادة المسودة",async()=>{
           if(draft.businessDate!==day)
             return showError(info,new Error("لا يمكن استعادة مسودة من يوم سابق إلى تحضير اليوم."));
           if(!Array.isArray(draft.selectedPersonIds)||!total||total>100||
             draft.selectedPersonIds.some(id=>typeof id!=="string"||!id))
             return showError(info,new Error("بيانات المسودة غير صالحة؛ لم تُستعد."));
           const availableStatus=Array.from(status.children).some(o=>o.value===draft.targetStatusId);
           const availableSite=!draft.siteId||Array.from(site.children).some(o=>o.value===draft.siteId);
           if(!availableStatus||!availableSite)
             return showError(info,new Error("حالة المسودة أو موقعها لم يعودا متاحين."));
           site.value=draft.siteId||"";status.value=draft.targetStatusId;
           mode.value="initial";previewButton.textContent="معاينة التحضير";
           selected.clear();draft.selectedPersonIds.forEach(id=>selected.add(id));
           activeDraftId=draft.draftId;pageNo=1;clearReview();summary();
           await load();
           info.replaceChildren(make("p","استعيدت المسودة للمراجعة فقط؛ لم يُحفظ أي تحضير.","settings-success"));
         });
         const remove=button("حذف المسودة",async()=>{
           if(!await modalConfirm("تأكيد حذف المسودة المحفوظة دون حذف سجلات التحضير؟"))return;
           remove.disabled=true;
           try{
             await bridge.invoke("DeleteAttendanceDraft",{draftId:draft.draftId});
             if(activeDraftId===draft.draftId)activeDraftId=null;
             await refreshDrafts();
           }catch(e){showError(info,e);remove.disabled=false;}
         });
         row.append(restore,remove);content.appendChild(row);
       }
       draftResults.replaceChildren(content);
     }catch(e){showError(draftResults,e);}
   }
   async function saveDraft(){
     if(!status.value||selected.size<1||selected.size>100)
       return showError(info,new Error("اختر حالة وواحداً إلى 100 فرد لحفظ المسودة."));
     saveDraftButton.disabled=true;
     try{
       const payload={businessDate:day,siteId:site.value||"",targetStatusId:status.value,
         selectedPersonIds:Array.from(selected).sort()};
       if(activeDraftId)payload.draftId=activeDraftId;
       const result=await bridge.invoke("SaveAttendanceDraft",payload);
       if(!result.data||!result.data.draftId)throw Error("لم يؤكد الخادم حفظ المسودة.");
       activeDraftId=result.data.draftId;
       info.replaceChildren(make("p","حُفظت المسودة محلياً دون تنفيذ أي تحضير.","settings-success"));
       await refreshDrafts();
     }catch(e){showError(info,e);}
     finally{saveDraftButton.disabled=false;}
   }
   const saveDraftButton=button("حفظ مسودة",saveDraft);
   draftPanel.insertBefore(button("تحديث المسودات",refreshDrafts),draftResults);
   header.insertBefore(saveDraftButton,choice);
   async function preview(){
     if(!status.value||selected.size===0)
       return showError(info,new Error("اختر الحالة والأفراد."));
     clearReview();info.replaceChildren();
     const expectedPreviewVersion=previewVersion;
     previewButton.disabled=true;
     try{
       const r=await bridge.invoke("PreviewAttendanceChange",{
         businessDate:day,statusId:status.value,siteId:site.value,
         personIds:Array.from(selected).sort(),mode:mode.value
       });
       if(!host.isConnected||previewVersion!==expectedPreviewVersion)return;
       const data=r.data||{};
       if(!data.previewToken||!Array.isArray(data.persons))
         throw Error("تعذر اعتماد المعاينة.");
       const review=window.AltamamDialog?.attendanceReview;
       if(typeof review!=="function")
         throw Error("تعذر فتح نافذة معاينة التمام؛ لا يمكن اعتماد التحضير دون مراجعتها.");
       const changing=data.mode==="revise";
       const accepted=await review(data,{
         title:changing?"معاينة تغيير حالة التحضير":"معاينة التحضير",
         message:changing?
           "راجع الحالة السابقة والجديدة لكل فرد قبل تأكيد التغيير.":
           "راجع الأسماء والأرقام (ع) والحالات قبل اعتماد التحضير.",
         confirmLabel:changing?"تأكيد تغيير الحالة":"تأكيد وحفظ التحضير"
       });
       if(!accepted||!host.isConnected||previewVersion!==expectedPreviewVersion)return;
       const action=changing?"ReviseTodayAttendance":"SubmitAttendance";
       const saved=await bridge.invoke(action,{previewToken:data.previewToken});
       if(!saved.data||!Number.isInteger(saved.data.count))
         throw Error("لم يؤكد الخادم نجاح عملية التحضير.");
       reset();pageNo=1;await load();
       info.replaceChildren(make("p",changing?
         "تم تأكيد تغيير حالة "+saved.data.count+" فرداً.":
         "تم حفظ "+saved.data.count+" فرداً.","settings-success"));
     }catch(e){
       if(host.isConnected&&previewVersion===expectedPreviewVersion)
         showError(info,e);
     }finally{previewButton.disabled=false;}
   }
   filters.appendChild(button("تحديث",()=>{reset();load();}));
   const previewButton=button("معاينة التحضير",preview);
   previewButton.className="btn btn-primary";
   header.insertBefore(previewButton,choice);
   header.insertBefore(button("العودة إلى إدارة القوة",()=>{
     window.AltamamAdminSettings.renderGroup("force");
   }),filters);
   try{
     const [sites,statuses]=await Promise.all([
       bridge.invoke("QuerySites",{}),bridge.invoke("GetActiveAttendanceStatuses",{})
     ]);
     for(const s of sites.data||[])if(s.active!==false)site.appendChild(option(s.siteId,s.name));
     for(const x of statuses.data||[])status.appendChild(option(x.statusId,x.displayName));
     await Promise.all([load(),refreshDrafts()]);
   }catch(e){showError(info,e);}
 }
 window.AltamamAttendance=Object.freeze({render});
})();
