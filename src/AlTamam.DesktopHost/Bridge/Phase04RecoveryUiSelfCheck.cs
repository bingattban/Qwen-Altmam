using System.IO;
using System.Text.Json;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Contracts.Bridge;
using Microsoft.Web.WebView2.Core;

namespace AlTamam.DesktopHost.Bridge;

// Runs only after the original independent SQL assertions, on the disposable DB.
internal static class Phase04RecoveryUiSelfCheck
{
    public static async Task RunAsync(CoreWebView2 core, AlTamamDbContext database,
        string databaseKey, string output, string ownerPassword)
    {
        long Count(AlTamamDbContext db, string table)
        {
            using var c = db.CreateOpenConnection(); using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM " + table;
            return Convert.ToInt64(q.ExecuteScalar());
        }
        long people = Count(database, "Persons"), users = Count(database, "Users");
        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs> completed = (_, args) => navigation.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += completed;
        try
        {
            core.Navigate(LocalWebUiOrigin.EntryPointUrl);
            if (!await navigation.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true))
                throw new InvalidOperationException("Recovery UI root navigation failed");
        }
        finally { core.NavigationCompleted -= completed; }
        string javascript = """
            (()=>{
              window.__r01RecoveryUi={status:"running"};
              let stage="modules-ready",resetDispatches=0,logoutCompletions=0;
              const countReset=event=>{
                if(event.detail?.action==="CommitFullReset"&&event.detail.busy)resetDispatches++;
                if(event.detail?.action==="Logout"&&event.detail.busy===false)logoutCompletions++;
              };
              document.addEventListener("altamam:bridge-loading",countReset);
              const viewport=()=>document.getElementById("appViewport");
              const button=(root,text)=>[...(root?.querySelectorAll("button")||[])].find(b=>b.textContent.trim()===text);
              async function wait(read,label){
                for(let i=0;i<1200;i++){
                  const found=read();if(found)return found;
                  await new Promise(resolve=>setTimeout(resolve,50));
                }
                const current=viewport();
                const issue=current?.querySelector(".settings-feedback .admin-warning,.settings-feedback .ui-error,.auth-status .ui-error");
                throw Error("recovery UI did not reach "+label+
                  " [route="+location.hash+
                  ",settings="+!!current?.querySelector(".settings-heading")+
                  ",auth="+!!current?.querySelector(".auth-form")+
                  ",dashboard="+!!current?.querySelector(".daily-roster-panel")+
                  ",error="+(issue?.textContent||"none").trim().slice(0,180)+"]");
              }
              async function modal(text){
                const control=await wait(()=>button(document.getElementById("appModalBody"),text),text);
                control.click();
              }
              void(async()=>{
                const api=await wait(()=>
                  window.AltamamBridge?.hasBridge?.()&&
                  typeof window.AltamamBridge.invoke==="function"&&
                  typeof window.AltamamFunctional?.render==="function"&&
                  typeof window.AltamamAdminSettings?.renderSettings==="function"&&
                  typeof window.AltamamDialog?.confirm==="function"&&
                  typeof window.AltamamDialog?.detailsReview==="function"&&
                  typeof window.AltamamDialog?.alert==="function"&&viewport()
                    ?window.AltamamBridge:null,"shipped bridge and UI modules");
                stage="session-transition";
                // After a root reload, boot() may still be resolving the inherited
                // authenticated session. Do not race its GetCurrentSession or issue
                // a second native Logout directly from the acceptance script.
                const initial=await wait(()=>{
                  const sidebar=document.querySelector(".app-sidebar");
                  if(!sidebar)return null;
                  if(sidebar.hidden===true&&viewport().querySelector(".auth-form"))
                    return "login";
                  if(sidebar.hidden===false&&viewport().querySelector(".daily-roster-panel"))
                    return "authenticated";
                  return null;
                },"settled initial session view");
                if(initial==="authenticated"){
                  const logoutAction=document.getElementById("logoutAction");
                  if(!logoutAction||logoutAction.hidden||!logoutAction.isConnected)
                    throw Error("real logout control unavailable for recovered session");
                  logoutAction.click();
                }
                stage="login-form";
                const auth=await wait(()=>document.querySelector(".app-sidebar")?.hidden===true
                  ?viewport().querySelector(".auth-form"):null,"real login form");
                auth.querySelector('input[autocomplete="username"]').value="phase04-owner";
                auth.querySelector('input[type="password"]').value=__OWNER__;
                stage="founder-authentication";
                auth.requestSubmit();
                await wait(()=>viewport().querySelector(".daily-roster-panel")&&
                  document.querySelector(".app-sidebar")?.hidden===false,"real founder login");
                stage="founder-session-check";
                const authenticated=await api.invoke("GetCurrentSession",{});
                if(authenticated.data?.username!=="phase04-owner"||
                   !authenticated.data?.sessionNonce)
                  throw Error("real founder session not active before settings navigation");
                stage="configure-recovery-output";
                await api.invoke("SetOutputDirectory",{directory:__OUTPUT__});
                stage="settings-navigation";
                const settingsNav=document.getElementById("navBtn-settings");
                if(!settingsNav||settingsNav.hidden||!settingsNav.isConnected)
                  throw Error("real settings navigation control unavailable");
                settingsNav.click();
                await wait(()=>button(viewport(),"إنشاء نسخة احتياطية الآن"),"real settings");
                stage="backup";
                const save=button(viewport(),"إنشاء نسخة احتياطية الآن");
                if(!save||save.disabled)throw Error("native backup control unavailable");
                save.click();await modal("تأكيد العملية");
                await wait(()=>!save.disabled&&viewport().textContent.includes("تم إنشاء نسخة مشفرة متحقق منها"),"verified backup");
                stage="corrupt-inspection";
                const inspect=button(viewport(),"اختيار نسخة مشفرة وفحص سلامتها");
                const preview=button(viewport(),"معاينة الاستعادة دون تنفيذ");
                if(!inspect||!preview||!preview.disabled)throw Error("restore must start disabled");
                inspect.click();await wait(()=>!inspect.disabled,"corrupt inspection refusal");
                if(!preview.disabled)throw Error("corrupt backup enabled preview");
                stage="verified-inspection";
                inspect.click();await wait(()=>!inspect.disabled&&!preview.disabled,"verified inspection");
                preview.click();await modal("إغلاق المعاينة");
                await wait(()=>viewport().textContent.includes("اكتملت المقارنة دون تعديل البيانات"),"read-only preview receipt");
                if([...viewport().querySelectorAll("button")].some(b=>b.textContent.includes("تنفيذ الاستعادة")))
                  throw Error("disabled restore exposed destructive UI");
                stage="reset-preview";
                button(viewport(),"معاينة التصفير الكامل").click();
                await modal("الانتقال للتحقق بكلمة المرور");
                let approve=await wait(()=>button(viewport(),"تأكيد تصفير البرنامج نهائياً"),"reset verification");
                let form=approve.closest('[aria-label="التحقق النهائي من التصفير"]');
                let password=form.querySelector('input[type="password"]'),phrase=form.querySelector('input[type="text"]');
                if(!approve.disabled)throw Error("reset countdown was skipped");
                await wait(()=>!approve.disabled,"reset countdown");
                stage="abandoned-final-reset-confirmation";
                password.value=__OWNER__;phrase.value="تصفير جميع البيانات";
                approve.click();
                const abandonedConfirm=await wait(()=>button(document.getElementById("appModalBody"),"تأكيد العملية"),"pending final confirmation");
                window.AltamamFunctional.render("dashboard");abandonedConfirm.click();
                const stillProvisioned=await api.invoke("GetProvisioningState",{});
                if(resetDispatches!==0||stillProvisioned.data.provisioned!==true)
                  throw Error("abandoned reset confirmation dispatched a destructive command");
                window.AltamamFunctional.render("settings");
                await wait(()=>button(viewport(),"معاينة التصفير الكامل"),"reopened settings");
                button(viewport(),"معاينة التصفير الكامل").click();
                await modal("الانتقال للتحقق بكلمة المرور");
                approve=await wait(()=>button(viewport(),"تأكيد تصفير البرنامج نهائياً"),"fresh reset verification");
                form=approve.closest('[aria-label="التحقق النهائي من التصفير"]');
                password=form.querySelector('input[type="password"]');phrase=form.querySelector('input[type="text"]');
                await wait(()=>!approve.disabled,"fresh countdown");
                stage="wrong-reset-password";
                password.value="Wrong456";phrase.value="تصفير جميع البيانات";
                approve.click();await modal("تأكيد العملية");
                await wait(()=>!approve.disabled&&form.querySelector(".admin-warning"),"wrong password refusal");
                if(password.value!=="")throw Error("rejected password retained in UI");
                if(!(await api.invoke("GetProvisioningState",{})).data.provisioned)
                  throw Error("wrong reset password changed provisioning");
                stage="confirmed-reset";
                const beforeResetLogout=logoutCompletions;
                password.value=__OWNER__;phrase.value="تصفير جميع البيانات";
                approve.click();await modal("تأكيد العملية");
                await modal("حسناً");
                await wait(()=>logoutCompletions>beforeResetLogout&&viewport().querySelector(".auth-first-run"),"first setup after reset logout");
                const state=await api.invoke("GetProvisioningState",{});
                if(state.data.provisioned!==false)throw Error("reset UI reported success without first setup");
                if(resetDispatches!==2)throw Error("unexpected reset command count");
                window.__r01RecoveryUi={status:"pass"};
              })().catch(error=>{window.__r01RecoveryUi={status:"failed",reason:stage+": "+error.message};})
                .finally(()=>document.removeEventListener("altamam:bridge-loading",countReset));
            })();
            """;
        javascript = javascript.Replace("__OWNER__", JsonSerializer.Serialize(ownerPassword), StringComparison.Ordinal)
            .Replace("__OUTPUT__", JsonSerializer.Serialize(output), StringComparison.Ordinal);
        await core.ExecuteScriptAsync(javascript).ConfigureAwait(true);
        for (int i = 0; i < 1200; i++)
        {
            string? raw = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(
                "JSON.stringify(window.__r01RecoveryUi||null)").ConfigureAwait(true));
            if (raw is not null && raw != "null")
            {
                using var state = JsonDocument.Parse(raw);
                string? status = state.RootElement.GetProperty("status").GetString();
                if (status == "failed") throw new InvalidOperationException(state.RootElement.GetProperty("reason").GetString());
                if (status == "pass")
                {
                    if (Count(database, "Persons") != 0 || Count(database, "Users") != 0 || !database.HasValidIntegrity())
                        throw new InvalidOperationException("Reset UI did not leave an intact empty database");
                    string rescue = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "backups"),
                        "rescue-before-reset-*.dbbackup").Single();
                    using var copy = new AlTamamDbContext(rescue, databaseKey, useConnectionPooling: false);
                    if (!copy.HasValidIntegrity() || Count(copy, "Persons") != people || Count(copy, "Users") != users)
                        throw new InvalidOperationException("Reset UI lost the original encrypted rescue");
                    if (File.ReadAllText(Path.Combine(output, "invalid.dbbackup")) != "synthetic corrupt backup")
                        throw new InvalidOperationException("Reset removed an unregistered file");
                    MainWindow.TraceWebViewSelfCheck("PASS R01 real backup restore-preview reset UI and independent encrypted rescue");
                    return;
                }
            }
            await Task.Delay(150).ConfigureAwait(true);
        }
        throw new TimeoutException("Native recovery UI acceptance did not complete");
    }
}
