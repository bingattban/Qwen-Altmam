"use strict";
/* Accessible, in-app centered confirmation and reason dialogs.
   No window.prompt/confirm/alert in the real application path. */
(function(){
  const backdrop=document.getElementById("appModalBackdrop");
  const title=document.getElementById("appModalTitle");
  const body=document.getElementById("appModalBody");
  if(!backdrop||!title||!body)return;
  let current=null;
  const make=(tag,klass,txt)=>{
    const item=document.createElement(tag);
    if(klass)item.className=klass;
    if(txt!==undefined)item.textContent=String(txt);
    return item;
  };
  function close(result){
    if(!current)return;
    const previous=current;current=null;
    backdrop.classList.remove("open");
    body.replaceChildren();
    document.removeEventListener("keydown",previous.key);
    previous.resolve(result);
    previous.focus?.focus?.();
  }
  function show(options){
    if(current)return Promise.resolve(null);
    return new Promise(resolve=>{
      const previous=document.activeElement;
      title.textContent=options.title||"تأكيد العملية";
      body.replaceChildren();
      const text=make("p","app-dialog-copy",options.message||"");
      body.appendChild(text);
      if(options.review){
        const detail=make("section","attendance-review");
        const overview=make("p","attendance-review-summary",
          "التاريخ: "+options.review.date+" — الحالة الجديدة: "+options.review.status+
          " — إجمالي الأفراد: "+options.review.persons.length);
        detail.appendChild(overview);
        const scrolling=make("div","attendance-review-scroll");
        const grid=make("table","data-table attendance-review-table");
        const head=make("thead"),header=make("tr");
        for(const column of ["الرقم (ع)","اسم الفرد","الموقع","الحالة السابقة","الحالة الجديدة"]){
          const th=make("th","",column);th.scope="col";header.appendChild(th);
        }
        head.appendChild(header);
        const rows=make("tbody");
        for(const person of options.review.persons){
          const tr=make("tr");
          for(const value of [person.serviceNumberA,person.name,person.siteName,
            person.previousStatusName,options.review.status])
            tr.appendChild(make("td","",value||"غير محضر"));
          rows.appendChild(tr);
        }
        grid.append(head,rows);scrolling.appendChild(grid);
        detail.appendChild(scrolling);body.appendChild(detail);
        body.appendChild(make("p","attendance-review-warning",
          "يراجع البرنامج حالة كل فرد مرة أخرى قبل الحفظ. لن يُعتمد أي تغيير قبل الضغط على زر التأكيد."));
      }
      if(options.transferReview){
        const review=options.transferReview;
        const detail=make("section","attendance-review");
        detail.appendChild(make("p","attendance-review-summary",
          "تاريخ الحركة: "+review.businessDate+" — نوع النقل: "+
          (review.external?"خارجي":"داخلي")+" — إجمالي الأفراد: "+review.persons.length));
        const scrolling=make("div","attendance-review-scroll");
        const grid=make("table","data-table attendance-review-table");
        const head=make("thead"),header=make("tr");
        for(const column of ["الرقم (ع)","اسم الفرد","الموقع المصدر","الجهة المستقبلة","تاريخ الحركة"]){
          const th=make("th","",column);th.scope="col";header.appendChild(th);
        }
        head.appendChild(header);
        const rows=make("tbody");
        for(const person of review.persons){
          const tr=make("tr");
          for(const value of [person.serviceNumberA,person.name,
            person.sourceSiteName,review.destinationName,review.businessDate])
            tr.appendChild(make("td","",value));
          rows.appendChild(tr);
        }
        grid.append(head,rows);scrolling.appendChild(grid);
        detail.appendChild(scrolling);
        detail.appendChild(make("p","attendance-review-warning",
          review.external
            ?"سيُستبعد الأفراد من القوة الفعلية مع حفظ سجلاتهم التاريخية."
            :"تظهر حركة النقل اليوم في الموقعين، ويبدأ التحضير العادي في الموقع الجديد غداً."));
        body.appendChild(detail);
      }
      let field=null,feedback=null;
      if(options.input){
        const wrapper=make("label","app-dialog-field");
        wrapper.appendChild(make("span","",options.input.label||"القيمة المطلوبة"));
        field=options.input.multiline?make("textarea","form-control"):make("input","form-control");
        if(!options.input.multiline)field.type=options.input.type||"text";
        field.value=options.input.value||"";
        if(options.input.minLength)field.minLength=options.input.minLength;
        if(options.input.maxLength)field.maxLength=options.input.maxLength;
        if(options.input.multiline)field.rows=4;
        wrapper.appendChild(field);
        body.appendChild(wrapper);
        feedback=make("p","app-dialog-error");
        feedback.setAttribute("role","alert");
        body.appendChild(feedback);
      }
      const actions=make("div","app-dialog-actions");
      const cancel=make("button","btn btn-outline","إلغاء");
      const accept=make("button",options.danger?"btn btn-danger":"btn btn-primary",
        options.confirmLabel||"تأكيد العملية");
      cancel.type=accept.type="button";
      cancel.addEventListener("click",()=>close(null));
      accept.addEventListener("click",()=>{
        if(field){
          const value=field.value.trim();
          const min=options.input.minLength||0;
          if(value.length<min){
            feedback.textContent="أدخل "+(options.input.label||"القيمة")+" (على الأقل "+min+" أحرف).";
            field.focus();return;
          }
          close(value);
        }else close(true);
      });
      actions.append(cancel,accept);body.appendChild(actions);
      const key=ev=>{
        if(ev.key==="Escape"){ev.preventDefault();close(null);}
        if(ev.key==="Enter" && (!field || !options.input.multiline)){
          ev.preventDefault();accept.click();
        }
        if(ev.key==="Tab"){
          const controls=Array.from(backdrop.querySelectorAll(
            'button:not([disabled]),input:not([disabled]),textarea:not([disabled])'));
          const first=controls[0],last=controls[controls.length-1];
          if(ev.shiftKey&&document.activeElement===first){ev.preventDefault();last?.focus();}
          else if(!ev.shiftKey&&document.activeElement===last){ev.preventDefault();first?.focus();}
        }
      };
      current={resolve,focus:previous,key};
      document.addEventListener("keydown",key);
      backdrop.classList.add("open");
      (field||accept).focus();
    });
  }
  backdrop.addEventListener("click",event=>{
    if(!current)return;
    if(event.target===backdrop||event.target.closest?.('[data-action="closeModal"]')){
      event.preventDefault();event.stopPropagation();close(null);
    }
  },true);
  window.AltamamDialog=Object.freeze({
    confirm:(message,opts={})=>show({...opts,message}).then(v=>v===true),
    prompt:(message,value="",opts={})=>show({
      title:opts.title||"إدخال البيانات",message,
      confirmLabel:opts.confirmLabel||"اعتماد",input:{
        label:opts.label||message,value,minLength:opts.minLength||1,
        maxLength:opts.maxLength||255
      }}),
    reason:(message,opts={})=>show({
      title:opts.title||"سبب الإجراء والتأكيد",message,danger:opts.danger!==false,
      confirmLabel:opts.confirmLabel||"معاينة العملية",input:{
        label:"سبب العملية",multiline:true,minLength:opts.minLength||5,maxLength:400
      }}),
    transferReview:(review,opts={})=>{
      if(!review||!Array.isArray(review.persons)||!review.persons.length||
         review.persons.length>100||typeof review.businessDate!=="string"||
         typeof review.destinationName!=="string"||
         !review.persons.every(p=>typeof p.name==="string"&&
           typeof p.serviceNumberA==="string"&&typeof p.sourceSiteName==="string"))
        return Promise.resolve(false);
      return show({title:opts.title||"معاينة النقل النهائي",
        message:"راجع أسماء الأفراد وأرقامهم والمواقع وتاريخ الحركة قبل الحفظ.",
        confirmLabel:"تأكيد النقل النهائي",transferReview:review
      }).then(result=>result===true);
    },
    attendanceReview:(review,opts={})=>{
      if(!review||!Array.isArray(review.persons)||!review.persons.length||
         review.persons.length>100||!review.persons.every(p=>
         typeof p.name==="string"&&typeof p.serviceNumberA==="string"))
        return Promise.resolve(false);
      return show({title:opts.title||"معاينة التحضير وتأكيده",
        message:opts.message||"راجع الأفراد وحالاتهم قبل الاعتماد.",
        confirmLabel:opts.confirmLabel||"تأكيد وحفظ التحضير",
        review:{date:review.businessDate||"",status:review.statusName||"",
          persons:review.persons}}).then(result=>result===true);
    },
    alert:(message,opts={})=>show({title:opts.title||"تنبيه",message,confirmLabel:"حسناً"})
  });
})();
