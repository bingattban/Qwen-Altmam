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
    for(const size of ["sm","md","lg","xl"])
      backdrop.classList.remove("dialog-"+size);
    const previous=current;current=null;
    backdrop.classList.remove("open");
    if(previous.timer && typeof window.clearInterval==="function")window.clearInterval(previous.timer);
    body.replaceChildren();
    document.removeEventListener("keydown",previous.key);
    previous.resolve(result);
    previous.focus?.focus?.();
  }
  function show(options){
    if(current)return Promise.resolve(null);
    return new Promise(resolve=>{
      const previous=document.activeElement;
      const size=["sm","md","lg","xl"].includes(options.size)?options.size:
        (options.unprepared||options.transferReview?"lg":
          (options.review?.persons?.length>10?"xl":"md"));
      backdrop.classList.add("dialog-"+size);
      title.textContent=options.title||"تأكيد العملية";
      body.replaceChildren();
      const text=make("p","app-dialog-copy",options.message||"");
      body.appendChild(text);
      // Individual/bulk picker, entirely separate from the dashboard counter.
      // Selection is only a draft: no write occurs until the server-side
      // carry-forward preview is displayed and explicitly confirmed.
      let selectedPeople=null;
      if(options.unprepared){
        selectedPeople=new Map();
        const pick=make("section","attendance-review");
        const summary=make("p","attendance-review-summary","اختر فرداً أو مجموعة لاعتماد حالة أمس؛ الحد الأقصى 100 فرد لكل دفعة.");
        const loading=make("p","app-dialog-copy");
        const scrolling=make("div","attendance-review-scroll");
        const controls=make("div","app-dialog-actions");
        const prev=make("button","btn btn-outline","السابق");
        const next=make("button","btn btn-outline","التالي");
        const pageText=make("span","");
        prev.type=next.type="button";
        controls.append(prev,pageText,next);
        pick.append(summary,loading,scrolling,controls);
        body.appendChild(pick);
        let page=1, requestSerial=0;
        async function loadPage(){
          const serial=++requestSerial;
          scrolling.replaceChildren();
          loading.textContent="جارٍ قراءة غير المحضرين من قاعدة البيانات…";
          prev.disabled=next.disabled=true;
          try{
            const data=await options.unprepared.loadPage(page);
            if(!current||serial!==requestSerial)return;
            const people=Array.isArray(data.items)?data.items:[];
            const count=Number(data.total)||0;
            const table=make("table","data-table attendance-review-table");
            const head=make("thead"),tr=make("tr");
            for(const label of ["تحديد","الرقم (ع)","اسم الفرد","الموقع"])
              tr.appendChild(make("th","",label));
            head.appendChild(tr);
            const rows=make("tbody");
            for(const person of people){
              const line=make("tr"),cell=make("td");
              const checkbox=make("input");checkbox.type="checkbox";
              checkbox.checked=selectedPeople.has(person.personId);
              checkbox.setAttribute("aria-label","تحديد "+person.name);
              checkbox.addEventListener("change",()=>{
                if(checkbox.checked&&selectedPeople.size>=100){
                  checkbox.checked=false;
                  loading.textContent="الحد الأقصى 100 فرد لكل دفعة.";
                  return;
                }
                if(checkbox.checked)selectedPeople.set(person.personId,person);
                else selectedPeople.delete(person.personId);
                summary.textContent="المحددون: "+selectedPeople.size+" — اعتمد حالة كل فرد من أمس بعد المعاينة.";
              });
              cell.appendChild(checkbox);line.appendChild(cell);
              for(const value of [person.serviceNumberA,person.name,person.siteName])
                line.appendChild(make("td","",value));
              rows.appendChild(line);
            }
            table.append(head,rows);scrolling.appendChild(table);
            loading.textContent=count?"إجمالي غير المحضرين: "+count:"لا يوجد أفراد غير محضرين.";
            pageText.textContent="صفحة "+page+" من "+Math.max(1,Math.ceil(count/100));
            prev.disabled=page<=1;next.disabled=page*100>=count;
          }catch(error){
            loading.textContent=error?.message||"تعذرت قراءة القائمة؛ أعد المحاولة.";
          }
        }
        prev.addEventListener("click",()=>{if(page>1){page--;void loadPage();}});
        next.addEventListener("click",()=>{page++;void loadPage();});
        void loadPage();
      }
      if(options.review){
        const detail=make("section","attendance-review");
        const overview=make("dl","app-preview-properties attendance-review-stats");
        [
          ["تاريخ التحضير",options.review.date],
          ["نوع العملية",options.review.mode==="revise"?"تغيير الحالة":"تحضير أو اعتماد"],
          ["الحالة المستهدفة",options.review.status||"بحسب الفرد"],
          ["عدد المحددين",options.review.persons.length]
        ].forEach(([label,value])=>{
          const property=make("div","app-preview-property");
          property.append(make("dt","",label),make("dd","",value));
          overview.appendChild(property);
        });
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
            person.previousStatusName,person.proposedStatusName||options.review.status])
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
        const facts=make("dl","app-preview-properties transfer-review-summary");
        for(const [label,value] of [
          ["تاريخ الحركة",review.businessDate],
          ["نوع الحركة",review.external?"نقل خارجي":"نقل داخلي"],
          ["الجهة الجديدة",review.destinationName],
          ["عدد الأفراد",review.persons.length]
        ]){
          const property=make("div","app-preview-property");
          property.append(make("dt","",label),make("dd","",value));
          facts.appendChild(property);
        }
        detail.appendChild(facts);
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
      if(options.details){
        const detail=make("section","attendance-review");
        const properties=make("dl","app-preview-properties");
        for(const [label,value] of options.details.rows){
          const pair=make("div","app-preview-property");
          pair.append(make("dt","",label),make("dd","",value==null?"—":value));
          properties.appendChild(pair);
        }
        detail.appendChild(properties);
        if(options.details.columns?.length && options.details.items?.length){
          const scrolling=make("div","attendance-review-scroll");
          const grid=make("table","data-table attendance-review-table");
          const head=make("thead"),heading=make("tr");
          for(const colTitle of options.details.columns)
            heading.appendChild(make("th","",colTitle));
          head.appendChild(heading);
          const tbody=make("tbody");
          for(const line of options.details.items.slice(0,100)){
            const tr=make("tr");
            for(const value of line)tr.appendChild(make("td","",value==null?"—":value));
            tbody.appendChild(tr);
          }
          grid.append(head,tbody);scrolling.appendChild(grid);detail.appendChild(scrolling);
        }
        body.appendChild(detail);
      }
      // Multi-field dialog API for a single accessible, structured form.
      // Existing confirm / prompt / reason / preview contracts remain unchanged.
      const multiFields=[];
      let field=null,feedback=null;
      if(Array.isArray(options.fields)&&options.fields.length){
        const form=make("div","app-dialog-form");
        for(const spec of options.fields){
          if(!spec||typeof spec.name!=="string"||!spec.name)continue;
          const label=make("label","app-dialog-field");
          label.appendChild(make("span","",spec.label||spec.name));
          let control;
          if(spec.type==="select"){
            control=make("select","form-control");
            for(const item of spec.options||[]){
              const option=make("option","",item.label||item.value);
              option.value=String(item.value??"");control.appendChild(option);
            }
          }else if(spec.type==="textarea"){
            control=make("textarea","form-control");control.rows=4;
          }else{
            control=make("input","form-control");
            control.type=["text","password","number","date","color"].includes(spec.type)?
              spec.type:"text";
          }
          control.value=String(spec.value??"");
          control.setAttribute("name",spec.name);
          if(spec.required)control.required=true;
          if(spec.minLength)control.minLength=spec.minLength;
          if(spec.maxLength)control.maxLength=spec.maxLength;
          label.appendChild(control);form.appendChild(label);
          multiFields.push({spec,control});
        }
        body.appendChild(form);
        feedback=make("p","app-dialog-error");
        feedback.setAttribute("role","alert");
        body.appendChild(feedback);
      }
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
        }else if(multiFields.length){
          const values={};
          for(const {spec,control} of multiFields){
            const value=String(control.value||"").trim();
            const min=Number(spec.minLength)||0;
            if((spec.required&&!value)||(min&&value.length<min)){
              feedback.textContent="أكمل الحقل المطلوب: "+(spec.label||spec.name);
              control.focus();return;
            }
            values[spec.name]=value;
          }
          close(values);
        }else if(selectedPeople){
          if(selectedPeople.size===0){
            const notice=make("p","app-dialog-error","اختر فرداً واحداً على الأقل قبل المتابعة.");
            body.appendChild(notice);
            return;
          }
          close(Array.from(selectedPeople.keys()));
        }else close(true);
      });
      actions.append(cancel,accept);body.appendChild(actions);
      let timer=null;
      if(Number(options.delaySeconds)>0 && typeof window.setInterval==="function"){
        let remaining=Math.max(1,Math.ceil(options.delaySeconds));
        const countdown=make("p","app-dialog-countdown","مهلة التأكيد: "+remaining+" ثوانٍ");
        body.insertBefore(countdown,actions);
        accept.disabled=true;
        timer=window.setInterval(()=>{
          remaining--;
          countdown.textContent=remaining>0?"مهلة التأكيد: "+remaining+" ثوانٍ":
            "انتهت المهلة؛ يمكنك التأكيد الآن.";
          if(remaining<=0){accept.disabled=false;window.clearInterval(timer);timer=null;}
        },1000);
      }
      const key=ev=>{
        if(ev.key==="Escape"){ev.preventDefault();close(null);}
        // Native buttons own Enter/Space. Do not turn Enter on Cancel or
        // inside a multi-field textarea into an unintended confirmation.
        if(ev.key==="Enter" && ev.target?.tagName!=="BUTTON" &&
           ev.target?.tag!=="button" && !ev.target?.isContentEditable &&
           !(ev.target?.tagName==="TEXTAREA"||ev.target?.tag==="textarea") &&
           (!field||!options.input.multiline)){
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
      current={resolve,focus:previous,key,timer};
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
    dismiss:()=>{if(!current)return false;close(null);return true;},
    confirm:(message,opts={})=>show({...opts,message}).then(v=>v===true),
    form:(fields,opts={})=>show({...opts,title:opts.title||"بيانات العملية",
      fields:Array.isArray(fields)?fields:[]}),
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
    detailsReview:(details,opts={})=>{
      if(!details||!Array.isArray(details.rows)||!details.rows.length||
         !details.rows.every(pair=>Array.isArray(pair)&&pair.length===2))
        return Promise.resolve(false);
      const size=opts.size||(
        (details.items?.length||0)>30?"xl":details.items?.length?"lg":"md");
      return show({title:opts.title||"معاينة العملية",message:opts.message||"",
        confirmLabel:opts.confirmLabel||"تأكيد",danger:opts.danger===true,
        delaySeconds:opts.delaySeconds||0,details,size
      }).then(result=>result===true);
    },
    transferReview:(review,opts={})=>{
      if(!review||!Array.isArray(review.persons)||!review.persons.length||
         review.persons.length>100||typeof review.businessDate!=="string"||
         typeof review.destinationName!=="string"||
         !review.persons.every(p=>typeof p.name==="string"&&
           typeof p.serviceNumberA==="string"&&typeof p.sourceSiteName==="string"))
        return Promise.resolve(false);
      return show({title:opts.title||"معاينة النقل النهائي",
        message:"راجع أسماء الأفراد وأرقامهم والمواقع وتاريخ الحركة قبل الحفظ.",
        confirmLabel:"تأكيد النقل النهائي",transferReview:review,
        size:review.persons.length>25?"xl":"lg"
      }).then(result=>result===true);
    },
    unpreparedSelection:loadPage=>show({
      title:"اعتماد حالة أمس لغير المحضرين",
      message:"اختر فردياً أو جماعياً، ثم راجع الحالة التي ستُعتمد. من لم تحضره يدوياً سيعالجه النظام الساعة 8 صباحاً.",
      confirmLabel:"معاينة حالة الأمس",size:"xl",
      unprepared:{loadPage}
    }),
    attendanceReview:(review,opts={})=>{
      if(!review||!Array.isArray(review.persons)||!review.persons.length||
         review.persons.length>100||!review.persons.every(p=>
         typeof p.name==="string"&&typeof p.serviceNumberA==="string"))
        return Promise.resolve(false);
      return show({title:opts.title||"معاينة التحضير وتأكيده",
        message:opts.message||"راجع الأفراد وحالاتهم قبل الاعتماد.",
        confirmLabel:opts.confirmLabel||"تأكيد وحفظ التحضير",
        review:{date:review.businessDate||"",status:review.statusName||"",
          mode:review.mode||"",persons:review.persons}}).then(result=>result===true);
    },
    alert:(message,opts={})=>show({title:opts.title||"تنبيه",message,confirmLabel:"حسناً"})
  });
})();
