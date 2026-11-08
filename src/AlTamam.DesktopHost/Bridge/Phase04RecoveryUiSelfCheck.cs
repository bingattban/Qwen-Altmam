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
        MainWindow.TraceWebViewSelfCheck("Recovery UI: entered native fixture");
        long Count(AlTamamDbContext db, string table)
        {
            using var c = db.CreateOpenConnection(); using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM " + table;
            return Convert.ToInt64(q.ExecuteScalar());
        }
        long people = Count(database, "Persons"), users = Count(database, "Users");
        MainWindow.TraceWebViewSelfCheck("Recovery UI: native person/user census complete");
        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs> completed = (_, args) => navigation.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += completed;
        try
        {
            MainWindow.TraceWebViewSelfCheck("Recovery UI: navigation requested");
            core.Navigate(LocalWebUiOrigin.EntryPointUrl);
            if (!await navigation.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true))
                throw new InvalidOperationException("Recovery UI root navigation failed");
            MainWindow.TraceWebViewSelfCheck("Recovery UI: root navigation completed");
        }
        finally { core.NavigationCompleted -= completed; }
        string javascript = """
            (()=>{
              window.__r01RecoveryUi={status:"running",stage:"modules-ready",uiState:"initializing"};
              let stage="modules-ready",resetDispatches=0,logoutCompletions=0;
              
              const countReset=event=>{
                if(event.detail?.action==="CommitFullReset"&&event.detail.busy)resetDispatches++;
                if(event.detail?.action==="Logout"&&event.detail.busy===false)logoutCompletions++;
              };
              document.addEventListener("altamam:bridge-loading",countReset);
              const viewport=()=>document.getElementById("appViewport");
              const button=(root,text)=>[...(root?.querySelectorAll("button")||[])].find(b=>b.textContent.trim()===text);
              // Real browser performance clock is monotonic even in the disposable
              // frozen-business-date fixture. A fixed number of 50-ms timers can
              // drift for minutes under WPF/WebView2 load, concealing the failure.
              const safeUiState=()=>{
                const current=viewport();
                const sidebar=document.querySelector(".app-sidebar");
                const logout=document.getElementById("logoutAction");
                const issue=current?.querySelector(".settings-feedback .admin-warning,.settings-feedback .ui-error,.auth-status .ui-error");
                return "[route="+location.hash+
                  ",documentState="+document.readyState+
                  ",sidebarHidden="+(sidebar?.hidden??"missing")+
                  ",logoutHidden="+(logout?.hidden??"missing")+
                  ",bridgeHasTransport="+!!window.AltamamBridge?.hasBridge?.()+
                  ",settings="+!!current?.querySelector(".settings-heading")+
                  ",auth="+!!current?.querySelector(".auth-form")+
                  ",authReady="+(current?.querySelector(".auth-form")?.dataset?.authReady??"missing")+
                  ",authSubmitDisabled="+(current?.querySelector('.auth-form button[type="submit"]')?.disabled??"missing")+
                  ",dashboard="+!!current?.querySelector(".daily-roster-panel")+
                  ",error="+(issue?.textContent||"none").trim().slice(0,180)+"]";
              };
              const setStage=next=>{
                stage=next;
                if(window.__r01RecoveryUi?.status==="running"){
                  window.__r01RecoveryUi.stage=next;
                  try {window.__r01RecoveryUi.uiState=safeUiState();}
                  catch {window.__r01RecoveryUi.uiState="[DOM transition]";}
                }
              };
              async function wait(read,label,timeoutMs=60000){
                const deadline=performance.now()+timeoutMs;
                while(performance.now()<deadline){
                  const found=read();if(found)return found;
                  await new Promise(resolve=>setTimeout(resolve,50));
                }
                throw Error("recovery UI did not reach "+label+" "+safeUiState());
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
                setStage("session-transition");
                // After a root reload, boot() may still be resolving the inherited
                // authenticated session. Do not race its GetCurrentSession or issue
                // a second native Logout directly from the acceptance script.
                const initial=await wait(()=>{
                  const sidebar=document.querySelector(".app-sidebar");
                  if(!sidebar)return null;
                  if(sidebar.hidden===true&&viewport().querySelector(".auth-form"))
                    return "login";
                  // The shipped UI may automatically display settings instead of
                  // the daily roster when its backup/output drive is unavailable.
                  // A visible native logout control is the actual authenticated
                  // UI signal, independently of which legitimate screen is open.
                  if(sidebar.hidden===false&&
                     document.getElementById("logoutAction")?.hidden===false)
                    return "authenticated";
                  return null;
                },"settled initial session view");
                if(initial==="authenticated"){
                  const logoutAction=document.getElementById("logoutAction");
                  if(!logoutAction||logoutAction.hidden||!logoutAction.isConnected)
                    throw Error("real logout control unavailable for recovered session");
                  logoutAction.click();
                }
                setStage("login-form");
                const auth=await wait(()=>{
                  if(document.querySelector(".app-sidebar")?.hidden!==true)return null;
                  const form=viewport()?.querySelector(".auth-form");
                  return form?.dataset?.authReady==="true"?form:null;
                },"real login form with bound submit handler");
                auth.querySelector('input[autocomplete="username"]').value="phase04-owner";
                auth.querySelector('input[type="password"]').value=__OWNER__;
                setStage("founder-authentication");
                auth.requestSubmit();
                await wait(()=>document.querySelector(".app-sidebar")?.hidden===false&&
                  document.getElementById("logoutAction")?.hidden===false&&
                  (viewport().querySelector(".daily-roster-panel")||
                   viewport().querySelector(".settings-heading")),"real founder login");
                setStage("founder-session-check");
                const authenticated=await api.invoke("GetCurrentSession",{});
                if(authenticated.data?.username!=="phase04-owner"||
                   !authenticated.data?.sessionNonce)
                  throw Error("real founder session not active before settings navigation");
                setStage("configure-recovery-output");
                await api.invoke("SetOutputDirectory",{directory:__OUTPUT__});
                setStage("settings-navigation");
                const settingsNav=document.getElementById("navBtn-settings");
                if(!settingsNav||settingsNav.hidden||!settingsNav.isConnected)
                  throw Error("real settings navigation control unavailable");
                settingsNav.click();
                await wait(()=>button(viewport(),"إنشاء نسخة احتياطية الآن"),"real settings");
                setStage("backup");
                const save=button(viewport(),"إنشاء نسخة احتياطية الآن");
                if(!save||save.disabled)throw Error("native backup control unavailable");
                save.click();await modal("تأكيد العملية");
                await wait(()=>!save.disabled&&viewport().textContent.includes("تم إنشاء نسخة مشفرة متحقق منها"),"verified backup");
                setStage("corrupt-inspection");
                const inspect=button(viewport(),"اختيار نسخة مشفرة وفحص سلامتها");
                const preview=button(viewport(),"معاينة الاستعادة دون تنفيذ");
                if(!inspect||!preview||!preview.disabled)throw Error("restore must start disabled");
                inspect.click();await wait(()=>!inspect.disabled,"corrupt inspection refusal");
                if(!preview.disabled)throw Error("corrupt backup enabled preview");
                setStage("verified-inspection");
                inspect.click();await wait(()=>!inspect.disabled&&!preview.disabled,"verified inspection");
                preview.click();await modal("إغلاق المعاينة");
                await wait(()=>viewport().textContent.includes("اكتملت المقارنة دون تعديل البيانات"),"read-only preview receipt");
                if([...viewport().querySelectorAll("button")].some(b=>b.textContent.includes("تنفيذ الاستعادة")))
                  throw Error("disabled restore exposed destructive UI");
                setStage("reset-preview");
                button(viewport(),"معاينة التصفير الكامل").click();
                await modal("الانتقال للتحقق بكلمة المرور");
                let approve=await wait(()=>button(viewport(),"تأكيد تصفير البرنامج نهائياً"),"reset verification");
                let form=approve.closest('[aria-label="التحقق النهائي من التصفير"]');
                let password=form.querySelector('input[type="password"]'),phrase=form.querySelector('input[type="text"]');
                if(!approve.disabled)throw Error("reset countdown was skipped");
                await wait(()=>!approve.disabled,"reset countdown");
                setStage("abandoned-final-reset-confirmation");
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
                setStage("wrong-reset-password");
                password.value="Wrong456";phrase.value="تصفير جميع البيانات";
                approve.click();await modal("تأكيد العملية");
                await wait(()=>!approve.disabled&&form.querySelector(".admin-warning"),"wrong password refusal");
                if(password.value!=="")throw Error("rejected password retained in UI");
                if(!(await api.invoke("GetProvisioningState",{})).data.provisioned)
                  throw Error("wrong reset password changed provisioning");
                setStage("confirmed-reset");
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
              return "R01_RECOVERY_INJECTED";
            })();
            """;
        javascript = javascript.Replace("__OWNER__", JsonSerializer.Serialize(ownerPassword), StringComparison.Ordinal)
            .Replace("__OUTPUT__", JsonSerializer.Serialize(output), StringComparison.Ordinal);
        string acknowledged = await core.ExecuteScriptAsync(javascript).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        if (acknowledged != JsonSerializer.Serialize("R01_RECOVERY_INJECTED"))
            throw new InvalidOperationException("Recovery UI script injection did not return its startup receipt: " + acknowledged);
        MainWindow.TraceWebViewSelfCheck("Recovery UI: browser probe injection completed");
        MainWindow.TraceWebViewSelfCheck("Recovery UI: browser probe startup acknowledged");
        string? lastStage = null;
        int missingStateCount = 0;
        for (int i = 0; i < 1200; i++)
        {
            string? raw = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(
                "JSON.stringify(window.__r01RecoveryUi||null)").WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true));
            if (raw is null || raw == "null")
            {
                if (++missingStateCount >= 12)
                    throw new InvalidOperationException("Recovery UI probe lost from browser document after acknowledged injection");
            }
            else
            {
                missingStateCount = 0;
                using var state = JsonDocument.Parse(raw);
                string? status = state.RootElement.GetProperty("status").GetString();
                if (status == "running" &&
                    state.RootElement.TryGetProperty("stage", out var stageProperty))
                {
                    string? stageName = stageProperty.GetString();
                    if (stageName != lastStage)
                    {
                        MainWindow.TraceWebViewSelfCheck("Recovery UI stage: " + stageName);
                        lastStage = stageName;
                    }
                    // This is only read-only UI metadata: never log the password,
                    // session nonce, bridge payload or recovery file contents.
                    if (i % 40 == 0 &&
                        state.RootElement.TryGetProperty("uiState", out var uiProperty))
                        MainWindow.TraceWebViewSelfCheck("Recovery UI state: " + uiProperty.GetString());
                }
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
