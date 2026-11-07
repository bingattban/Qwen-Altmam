using System.Text.Json;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Web.WebView2.Core;

namespace AlTamam.DesktopHost.Bridge;

/// <summary>
/// A real Windows browser -> native dispatcher -> encrypted SQLCipher -> HTML
/// round-trip. The host creates a fresh random-key temporary database and
/// isolated WebView2 profile before calling this. This test never touches the
/// user's persisted AppData or permits test credentials on production data.
/// </summary>
internal static class Phase04BridgeRoundTripSelfCheck
{
    public static async Task RunAsync(CoreWebView2 core, AlTamamDbContext database)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(database);

        // Unique, ephemeral test-only credentials. Neither value is a reusable
        // application password, database key, default account, or recovery code.
        string ownerPassword = "Abc123!" + Guid.NewGuid().ToString("N");
        string supervisorPassword = "Abc123!" + Guid.NewGuid().ToString("N");
        string adminPassword = "Abc123!" + Guid.NewGuid().ToString("N");

        // This separate movable fixture starts YESTERDAY. A person added
        // today through CreatePerson cannot have an assignment closed today
        // without violating the immutable effective-date SQLCipher constraint.
        // Do not modify the user's data or weaken production storage policy.
        string yesterday = TimeProvider.System.GetLocalNow().AddDays(-1).ToString("yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);
        // Create one active site in the disposable encrypted SQLCipher fixture.
        // The actual user's AppData and configuration are never accessed here.
        using (var fixtureConnection = database.CreateOpenConnection())
        using (var createSite = fixtureConnection.CreateCommand())
        {
            createSite.CommandText = """
                INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,
                    ActiveFlag,CreatedAtUtc,RowVersion)
                VALUES('SITE-PHASE15-E2E-DEST','وجهة نقل اختبارية',
                    'وجهة نقل اختبارية',0,1,'2026-01-01T00:00:00Z',1);
                INSERT INTO SiteNameHistory(SiteId,VisibleNameSnapshot,
                    ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc)
                VALUES('SITE-PHASE15-E2E-DEST','وجهة نقل اختبارية',
                    '2026-01-01',NULL,'USR-SYSTEM','2026-01-01T00:00:00Z');
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
                    FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES('TST-PHASE15-MOVABLE','فرد نقل قائم','فرد نقل قائم',
                    '00007194','00007194',$yesterday,$recorded,1,1,1);
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                    ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                VALUES('TST-PHASE15-MOVABLE','فرد نقل قائم','00007194',
                    $yesterday,NULL,'USR-SYSTEM',$recorded,'synthetic preexisting mover');
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                    ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES('TST-PHASE15-MOVABLE-A','TST-PHASE15-MOVABLE',
                    'SITE-UNSPECIFIED',$yesterday,NULL,NULL,$recorded);
                """;
            createSite.Parameters.AddWithValue("$yesterday", yesterday);
            createSite.Parameters.AddWithValue("$recorded", TimeProvider.System.GetUtcNow().ToString("O"));
            createSite.ExecuteNonQuery();
        }

        string javascript = """
            (() => {
              const api = window.AltamamBridge;
              if (!api?.hasBridge?.()) throw new Error("real host bridge missing");
              const target = document.createElement("output");
              target.id = "phase04-real-sqlcipher-roundtrip";
              target.setAttribute("role", "status");
              target.setAttribute("aria-live", "polite");
              document.body.appendChild(target);
              window.__phase04RealBridge = {status:"running"};
              let probeStage = "first-use";
              Object.defineProperty(window.__phase04RealBridge, "stage", {get: () => probeStage});

              async function expectRejected(action, payload, options, expected) {
                try {
                  await api.invoke(action, payload, options);
                } catch (error) {
                  if (error?.errorCode === expected) return;
                  throw new Error(action + " rejected with " + (error?.errorCode || "unknown")
                    + " instead of " + expected);
                }
                throw new Error(action + " illegally returned success");
              }

              async function waitForUi(read,label) {
                for(let i=0;i<200;i++){
                  const value=read();if(value)return value;
                  await new Promise(resolve=>setTimeout(resolve,50));
                }
                throw new Error("native consumer did not reach "+label);
              }

              async function verifyAccountPicker(role,personId) {
                const controller=window.AltamamAdminSettings;
                controller.renderAdmin({role,username:"native-consumer"});
                if(!controller.openOwnedRoute("Users"))throw new Error(role+" account route denied");
                const viewport=document.getElementById("appViewport");
                const add=await waitForUi(()=>[...viewport.querySelectorAll("button")]
                  .find(b=>b.textContent.trim()==="إضافة حساب"),"account list");
                add.click();
                const form=await waitForUi(()=>viewport.querySelector(".admin-user-form"),"account picker");
                const input=[...form.querySelectorAll("label")]
                  .find(x=>x.textContent.includes("بحث عن فرد بالاسم أو الرقم (ع)"))?.querySelector("input");
                const search=[...form.querySelectorAll("button")]
                  .find(b=>b.textContent.trim()==="البحث في كشف القوة");
                const picker=form.querySelector('select[aria-label="اختر فرداً من الكشف"]');
                if(!input||!search||!picker)throw new Error("native account picker controls missing");
                input.value="۰۰۰۰۷۱۹۲";search.click();
                await waitForUi(()=>!search.disabled&&[...picker.options].some(x=>x.value===personId),
                  "account search identity");
                picker.value=personId;picker.dispatchEvent(new Event("change",{bubbles:true}));
                if(picker.value!==personId||!form.textContent.includes("الرقم (ع): 00007192"))
                  throw new Error("account picker did not retain native immutable identity");
                input.value="999999999999";search.click();
                await waitForUi(()=>!search.disabled&&form.textContent.includes("لم يوجد فرد نشط مطابق"),
                  "empty account search");
                if(picker.value||[...picker.options].some(x=>x.value===personId)||
                   form.textContent.includes("الرقم (ع): 00007192"))
                  throw new Error("empty native account search retained stale selection");
                controller.renderAdmin({role,username:"native-consumer"});
              }

              async function malformedVersion(nonce) {
                const requestId = "phase04-bad-version-" +
                  Array.from(crypto.getRandomValues(new Uint8Array(8)),
                    value => value.toString(16).padStart(2,"0")).join("");
                await new Promise((resolve,reject) => {
                  const timeout = setTimeout(() => {
                    window.chrome.webview.removeEventListener("message", onMessage);
                    reject(new Error("malformed request had no native response"));
                  }, 8000);
                  function onMessage(event) {
                    let reply;
                    try { reply = JSON.parse(event.data); } catch { return; }
                    if (reply?.requestId !== requestId) return;
                    clearTimeout(timeout);
                    window.chrome.webview.removeEventListener("message", onMessage);
                    if (reply.success === false && reply.errorCode === "ValidationError")
                      resolve();
                    else reject(new Error("malformed protocol unexpectedly accepted"));
                  }
                  window.chrome.webview.addEventListener("message", onMessage);
                  window.chrome.webview.postMessage(JSON.stringify({
                    requestId,protocolVersion:999,action:"QueryPersons",
                    sessionNonce:nonce,payload:{term:"00007192"}
                  }));
                });
              }

              void (async () => {
                const before = await api.invoke("GetProvisioningState",{});
                if (before.data?.provisioned !== false)
                  throw new Error("synthetic first-run provisioning not empty");
                const owner = await api.invoke("AuthenticateLocalUser",{
                  username:"phase04-owner",password:__OWNER_PASSWORD__,
                  passwordConfirmation:__OWNER_PASSWORD__,
                  setupIntent:"firstTimeSetup"
                });
                const ownerNonce = owner.data?.sessionNonce;
                if (!/^[0-9a-fA-F]{32}$/.test(ownerNonce || ""))
                  throw new Error("founder session missing");
                api.setSessionNonce(ownerNonce);

                const sites = await api.invoke("QuerySites",{});
                if (!Array.isArray(sites.data) ||
                    !sites.data.some(item => item.siteId === "SITE-UNSPECIFIED"))
                  throw new Error("real SQLCipher seeded sites missing");

                probeStage = "r01-status-contract";
                const activeStatuses = await api.invoke("GetActiveAttendanceStatuses",{});
                if (!Array.isArray(activeStatuses.data) || !activeStatuses.data.length)
                  throw new Error("active status response is not a nonempty array");
                const statusPick = document.createElement("select");
                statusPick.id = "r01-native-status-picker";
                for (const status of activeStatuses.data) {
                  if (typeof status.statusId !== "string" ||
                      typeof status.displayName !== "string" ||
                      typeof status.color !== "string" || status.active !== true)
                    throw new Error("active status UI fields do not match native serialization");
                  const option = document.createElement("option");
                  option.value = status.statusId;
                  option.textContent = status.displayName;
                  statusPick.appendChild(option);
                }
                target.appendChild(statusPick);
                statusPick.value = "STS-PRESENT";
                if (statusPick.value !== "STS-PRESENT" || !statusPick.selectedOptions[0]?.textContent)
                  throw new Error("native status did not render as selectable HTML");
                const r01StatusVerified = true;

                const preview = await api.invoke("PreviewArchivedIdentity",{
                  visibleName:"فرد اختبار التكامل",serviceNumberA:"00007192",
                  siteId:"SITE-UNSPECIFIED"
                });
                if (!Array.isArray(preview.data?.candidates) ||
                    preview.data.candidates.length !== 0)
                  throw new Error("empty synthetic identity preview was incorrect");

                const added = await api.invoke("CreatePerson",{
                  visibleName:"فرد اختبار التكامل",serviceNumberA:"00007192",
                  siteId:"SITE-UNSPECIFIED"
                });
                const personId = added.data?.personId;
                if (!personId || !personId.startsWith("PRN-"))
                  throw new Error("CreatePerson returned no permanent PersonId");
                const lookup = await api.invoke("QueryPersons",{
                  term:"00007192",page:1,pageSize:10
                });
                const saved = lookup.data?.items?.find(x =>
                  x.personId === personId && x.serviceNumberA === "00007192");
                if (!saved) throw new Error("SQLCipher person not visible after native commit");

                probeStage = "r01-search-contract";
                for (const [action, term] of [
                  ["SearchPersons", "٠٠٠٠٧١٩٢"],
                  ["SearchActivePersonsForAccount", "۰۰۰۰۷۱۹۲"]
                ]) {
                  const found = await api.invoke(action,{term,take:30});
                  if (!Array.isArray(found.data?.items) || found.data.count !== 1 ||
                      found.data.items.length !== 1)
                    throw new Error(action + " items/count mismatch");
                  const person = found.data.items[0];
                  if (person.personId !== personId || person.serviceNumberA !== "00007192" ||
                      person.displayName !== "فرد اختبار التكامل" || person.organizationalStatus !== 1)
                    throw new Error(action + " lost immutable person identity");
                  const pick = document.createElement("select");
                  pick.id = "r01-native-" + action;
                  const option = document.createElement("option");
                  option.value = person.personId;
                  option.textContent = person.displayName + " — " + person.serviceNumberA;
                  pick.appendChild(option);
                  document.body.appendChild(pick);
                  if (pick.value !== personId || !pick.selectedOptions[0]?.textContent.includes("00007192"))
                    throw new Error(action + " native person did not render in HTML");
                }
                const r01SearchVerified = true;

                // Render the shipped attendance consumer, not a parallel picker.
                // Reads use the real authenticated bridge and disposable SQLCipher.
                probeStage = "r01-attendance-consumer";
                if (typeof window.AltamamAttendance?.render !== "function")
                  throw new Error("shipped attendance renderer missing");
                const attendancePage = document.createElement("section");
                document.body.appendChild(attendancePage);
                await window.AltamamAttendance.render(attendancePage);
                if (attendancePage.querySelector(".admin-warning"))
                  throw new Error("native attendance initialization reported an error");
                const attendanceStatus = attendancePage.querySelector('select[aria-label="الحالة الجديدة"]');
                const attendanceSite = attendancePage.querySelector('select[aria-label="الموقع"]');
                if (!attendanceStatus || !attendanceSite ||
                    ![...attendanceSite.options].some(item => item.value === "SITE-UNSPECIFIED"))
                  throw new Error("actual attendance site/status selectors missing");
                for (const status of activeStatuses.data) {
                  const rendered = [...attendanceStatus.options].find(item => item.value === status.statusId);
                  if (!rendered || rendered.textContent !== status.displayName)
                    throw new Error("actual attendance consumer lost native status identity/name");
                }
                const personRow = [...attendancePage.querySelectorAll("tbody tr")]
                  .find(row => row.textContent.includes("00007192"));
                if (!personRow?.querySelector('input[type="checkbox"]') ||
                    !personRow.textContent.includes("فرد اختبار التكامل"))
                  throw new Error("actual attendance roster did not render the saved person");
                attendanceStatus.value = "STS-LEAVE";
                attendanceStatus.dispatchEvent(new Event("change",{bubbles:true}));
                const range = attendancePage.querySelector(".attendance-range-fields");
                const rangeFrom = attendancePage.querySelector('input[aria-label="من تاريخ"]');
                const rangeTo = attendancePage.querySelector('input[aria-label="إلى تاريخ"]');
                if (attendanceStatus.value !== "STS-LEAVE" || !range || range.hidden ||
                    !rangeFrom?.required || !rangeTo?.required)
                  throw new Error("actual leave selection did not require its duration");
                attendanceStatus.value = "STS-PRESENT";
                attendanceStatus.dispatchEvent(new Event("change",{bubbles:true}));
                if (attendanceStatus.value !== "STS-PRESENT" || !range.hidden ||
                    rangeFrom.required || rangeTo.required)
                  throw new Error("actual present selection retained leave-only duration fields");
                probeStage = "r01-attendance-preview-cancel";
                const selectedPerson = personRow.querySelector('input[type="checkbox"]');
                selectedPerson.checked = true;
                selectedPerson.dispatchEvent(new Event("change",{bubbles:true}));
                const previewAttendance = [...attendancePage.querySelectorAll("button")]
                  .find(button => button.textContent.trim() === "معاينة التحضير");
                if (!previewAttendance) throw new Error("actual attendance preview button missing");
                previewAttendance.click();
                let cancelAttendance = null;
                for (let attempt = 0; attempt < 300; attempt++) {
                  const modal = document.getElementById("appModalBody");
                  cancelAttendance = [...(modal?.querySelectorAll("button") || [])]
                    .find(button => button.textContent.trim() === "إلغاء");
                  if (cancelAttendance) {
                    if (!modal.textContent.includes("00007192") ||
                        !modal.textContent.includes("فرد اختبار التكامل") ||
                        ![...modal.querySelectorAll("button")].some(button =>
                          button.textContent.trim() === "تأكيد وحفظ التحضير"))
                      throw new Error("native attendance review lost person or confirmation action");
                    break;
                  }
                  if (attendancePage.querySelector(".admin-warning"))
                    throw new Error("native attendance preview reported an error");
                  await new Promise(resolve => setTimeout(resolve,50));
                }
                if (!cancelAttendance) throw new Error("actual native attendance review did not open");
                cancelAttendance.click();
                for (let attempt = 0; attempt < 100 && previewAttendance.disabled; attempt++)
                  await new Promise(resolve => setTimeout(resolve,50));
                if (previewAttendance.disabled || attendancePage.querySelector(".settings-success"))
                  throw new Error("cancelled attendance review failed to return without save");
                attendancePage.remove();
                const r01AttendanceCancelVerified = true;

                probeStage = "r01-attendance-confirm-save";
                const attendancePerson = await api.invoke("CreatePerson",{
                  visibleName:"فرد اعتماد التمام",serviceNumberA:"00007195",
                  siteId:"SITE-UNSPECIFIED"
                });
                const attendancePersonId = attendancePerson.data?.personId;
                if (!attendancePersonId || attendancePersonId === personId)
                  throw new Error("independent attendance fixture identity missing");
                const commitPage = document.createElement("section");
                document.body.appendChild(commitPage);
                await window.AltamamAttendance.render(commitPage);
                const commitStatus = commitPage.querySelector('select[aria-label="الحالة الجديدة"]');
                const commitRow = [...commitPage.querySelectorAll("tbody tr")]
                  .find(row => row.textContent.includes("00007195"));
                const commitCheck = commitRow?.querySelector('input[type="checkbox"]');
                if (!commitStatus || !commitCheck || commitPage.querySelector(".admin-warning"))
                  throw new Error("actual commit roster did not load independent person");
                commitStatus.value = "STS-PRESENT";
                commitStatus.dispatchEvent(new Event("change",{bubbles:true}));
                commitCheck.checked = true;
                commitCheck.dispatchEvent(new Event("change",{bubbles:true}));
                const commitPreview = [...commitPage.querySelectorAll("button")]
                  .find(button => button.textContent.trim() === "معاينة التحضير");
                if (!commitPreview) throw new Error("actual commit preview button missing");
                commitPreview.click();
                let confirmAttendance = null;
                for (let attempt = 0; attempt < 300; attempt++) {
                  const modal = document.getElementById("appModalBody");
                  confirmAttendance = [...(modal?.querySelectorAll("button") || [])]
                    .find(button => button.textContent.trim() === "تأكيد وحفظ التحضير");
                  if (confirmAttendance) {
                    if (!modal.textContent.includes("00007195") ||
                        !modal.textContent.includes("فرد اعتماد التمام"))
                      throw new Error("commit review displayed the wrong person");
                    break;
                  }
                  if (commitPage.querySelector(".admin-warning"))
                    throw new Error("actual commit preview failed");
                  await new Promise(resolve => setTimeout(resolve,50));
                }
                if (!confirmAttendance) throw new Error("actual commit confirmation did not open");
                confirmAttendance.click();
                for (let attempt = 0; attempt < 400 && commitPreview.disabled; attempt++)
                  await new Promise(resolve => setTimeout(resolve,50));
                if (commitPreview.disabled ||
                    !commitPage.querySelector(".settings-success")?.textContent.includes("تم حفظ 1 فرداً.") ||
                    commitPage.querySelector(".admin-warning"))
                  throw new Error("actual attendance confirmation did not report a saved receipt");
                probeStage = "r01-attendance-revision";
                const revisionMode = commitPage.querySelector('select[aria-label="نوع العملية"]');
                const revisionRow = [...commitPage.querySelectorAll("tbody tr")]
                  .find(row => row.textContent.includes("00007195"));
                const revisionCheck = revisionRow?.querySelector('input[type="checkbox"]');
                if (!revisionMode || !revisionCheck)
                  throw new Error("saved person missing from actual revision roster");
                revisionMode.value = "revise";
                revisionMode.dispatchEvent(new Event("change",{bubbles:true}));
                commitStatus.value = "STS-ABSENT";
                commitStatus.dispatchEvent(new Event("change",{bubbles:true}));
                if (commitStatus.value !== "STS-ABSENT")
                  throw new Error("native absence status missing for revision");
                revisionCheck.checked = true;
                revisionCheck.dispatchEvent(new Event("change",{bubbles:true}));
                if (commitPreview.textContent.trim() !== "معاينة تغيير الحالة")
                  throw new Error("actual revision mode did not change the preview action");
                commitPreview.click();
                let confirmRevision = null;
                for (let attempt = 0; attempt < 300; attempt++) {
                  const modal = document.getElementById("appModalBody");
                  confirmRevision = [...(modal?.querySelectorAll("button") || [])]
                    .find(button => button.textContent.trim() === "تأكيد تغيير الحالة");
                  if (confirmRevision) {
                    if (!modal.textContent.includes("00007195") ||
                        !modal.textContent.includes("فرد اعتماد التمام"))
                      throw new Error("actual revision modal displayed the wrong person");
                    break;
                  }
                  if (commitPage.querySelector(".admin-warning"))
                    throw new Error("actual revision preview reported an error");
                  await new Promise(resolve => setTimeout(resolve,50));
                }
                if (!confirmRevision) throw new Error("actual revision confirmation did not open");
                confirmRevision.click();
                for (let attempt = 0; attempt < 400 && commitPreview.disabled; attempt++)
                  await new Promise(resolve => setTimeout(resolve,50));
                if (commitPreview.disabled ||
                    !commitPage.querySelector(".settings-success")?.textContent.includes("تم تأكيد تغيير حالة 1 فرداً.") ||
                    commitPage.querySelector(".admin-warning"))
                  throw new Error("actual revision did not report its saved receipt");
                commitPage.remove();
                const r01AttendanceRevisionVerified = true;

                probeStage = "r01-report-consumer";
                if (typeof window.AltamamReports?.render !== "function")
                  throw new Error("shipped personnel report renderer missing");
                const canonicalReport = (await api.invoke("BuildPersonLifetimeReport",
                  {personIds:[attendancePersonId]})).data;
                if (canonicalReport?.mode !== "group" || canonicalReport.personCount !== 1 ||
                    canonicalReport.attendanceTotal !== 1 ||
                    canonicalReport.persons?.[0]?.personId !== attendancePersonId ||
                    canonicalReport.statusTotals?.length !== 1 ||
                    canonicalReport.statusTotals[0].statusName !== "غياب" ||
                    canonicalReport.statusTotals[0].count !== 1)
                  throw new Error("canonical report lost revised attendance identity/count");
                const reportPage = document.createElement("section");
                document.body.appendChild(reportPage);
                window.AltamamReports.render(reportPage,()=>{});
                async function waitForReport(check, description) {
                  for (let attempt = 0; attempt < 300; attempt++) {
                    if (reportPage.querySelector(".admin-warning"))
                      throw new Error("actual report consumer reported an error: " + description);
                    const result = check();
                    if (result) return result;
                    await new Promise(resolve => setTimeout(resolve,50));
                  }
                  throw new Error("actual report consumer timed out: " + description);
                }
                const reportStatus = await waitForReport(() => {
                  const select = reportPage.querySelector('select[aria-label="فلتر حالة التحضير التاريخية"]');
                  return select && [...select.options].some(option => option.value === "STS-ABSENT")
                    ? select : null;
                },"status filters");
                for (const status of activeStatuses.data) {
                  const option = [...reportStatus.options].find(option => option.value === status.statusId);
                  if (!option || option.textContent !== status.displayName)
                    throw new Error("actual report filter lost native status identity/name");
                }
                const reportSearch = reportPage.querySelector('input[aria-label="البحث عن فرد"]');
                const searchReportButton = [...reportPage.querySelectorAll("button")]
                  .find(button => button.textContent.trim() === "بحث");
                if (!reportSearch || !searchReportButton)
                  throw new Error("actual report search controls missing");
                reportSearch.value = "00007195";
                searchReportButton.click();
                const reportPersonRow = await waitForReport(() =>
                  [...reportPage.querySelectorAll("tbody tr")].find(row =>
                    row.textContent.includes("00007195") &&
                    row.textContent.includes("فرد اعتماد التمام")),"person search");
                const addToReport = [...reportPersonRow.querySelectorAll("button")]
                  .find(button => button.textContent.trim() === "إضافة للمجموعة");
                if (!addToReport) throw new Error("actual report group selection missing");
                addToReport.click();
                const groupReportButton = [...reportPage.querySelectorAll("button")]
                  .find(button => button.textContent.trim() === "عرض التقرير الجماعي");
                if (!groupReportButton) throw new Error("actual group report action missing");
                groupReportButton.click();
                const renderedReport = await waitForReport(() => {
                  const popup = reportPage.querySelector(".person-report-modal");
                  return popup && !popup.hidden &&
                    popup.textContent.includes("إجمالي التحضيرات المعتمدة: 1")
                    ? popup : null;
                },"group report");
                if (!renderedReport.textContent.includes("00007195") ||
                    !renderedReport.textContent.includes("فرد اعتماد التمام") ||
                    ![...renderedReport.querySelectorAll("tbody tr")].some(row =>
                      row.cells.length === 2 && row.cells[0].textContent === "غياب" &&
                      row.cells[1].textContent === String(canonicalReport.statusTotals[0].count)))
                  throw new Error("actual report DOM disagreed with canonical identity/status totals");
                reportPage.remove();
                const r01ReportConsumerVerified = true;
                const r01AttendanceCommitVerified = true;
                const r01AttendanceConsumerVerified = true;
                target.textContent = saved.displayName + " — " + saved.serviceNumberA;
                if (!target.textContent.includes("00007192") ||
                    !target.isConnected)
                  throw new Error("real HTML was not updated from encrypted result");

                await expectRejected("CreatePerson",{
                  visibleName:"محاولة تكرار",serviceNumberA:"00007192",
                  siteId:"SITE-UNSPECIFIED"
                },undefined,"DuplicateServiceNumber");

                // The first-use person stays on the source site. Transfer a
                // separate pre-existing person with a valid earlier assignment.
                // Actual modal click -> native Windows WebView2 -> encrypted DB,
                // including a cancelled preview, a confirmed commit, and replay.
                const movableId="TST-PHASE15-MOVABLE";
                const dateNow=new Date();
                const businessDate=[dateNow.getFullYear(),
                  String(dateNow.getMonth()+1).padStart(2,"0"),
                  String(dateNow.getDate()).padStart(2,"0")].join("-");
                probeStage = "PreviewInternalTransfer";
                const previewTransfer=await api.invoke("PreviewInternalTransfer",{
                  businessDate,sourceSiteId:"SITE-UNSPECIFIED",
                  destinationSiteId:"SITE-PHASE15-E2E-DEST",personIds:[movableId]
                });
                const transfer=previewTransfer.data;
                if(!transfer||typeof transfer.previewToken!=="string"||
                   transfer.previewToken.length<32||transfer.count!==1||
                   transfer.persons?.[0]?.personId!==movableId||
                   transfer.persons?.[0]?.serviceNumberA!=="00007194"||
                   transfer.destinationName!=="وجهة نقل اختبارية")
                  throw new Error("SQLCipher transfer preview did not match the fixture");
                const review=window.AltamamDialog?.transferReview;
                if(typeof review!=="function")
                  throw new Error("real browser transfer modal is absent");
                function pressModal(label){
                  const button=[...document.querySelectorAll("#appModalBody button")]
                    .find(x=>x.textContent.trim()===label);
                  if(!button)throw new Error("transfer modal button missing: "+label);
                  button.click();
                }
                const cancelled=review(transfer);
                pressModal("إلغاء");
                if(await cancelled!==false)
                  throw new Error("cancelled transfer was not rejected");
                probeStage = "QueryMovementArchive";
                const movementsBefore=await api.invoke("QueryMovementArchive",{
                  type:"internal",dateFrom:businessDate,dateTo:businessDate,
                  page:1,pageSize:10
                });
                if(movementsBefore.data?.total!==0)
                  throw new Error("cancelled modal wrote a movement");
                const approved=review(transfer);
                pressModal("تأكيد النقل النهائي");
                if(await approved!==true)
                  throw new Error("real transfer confirmation did not approve native mutation");
                const intent={previewToken:transfer.previewToken,
                  operationId:"E2E-TRANSFER-"+crypto.randomUUID()};
                probeStage = "CommitInternalTransfer";
                const receipt=await api.invoke("CommitInternalTransfer",intent,
                  {timeoutMs:120000});
                if(receipt.data?.count!==1||
                   receipt.data?.destinationName!==transfer.destinationName)
                  throw new Error("C# transfer did not return a complete matching receipt");
                probeStage = "CommitInternalTransfer.replay";
                const replay=await api.invoke("CommitInternalTransfer",intent,
                  {timeoutMs:120000});
                if(replay.data?.count!==1||
                   replay.data?.destinationName!==transfer.destinationName)
                  throw new Error("identical lost-reply retry failed");
                probeStage = "QueryMovementArchive";
                const archive=await api.invoke("QueryMovementArchive",{
                  type:"internal",dateFrom:businessDate,dateTo:businessDate,
                  page:1,pageSize:10
                });
                if(archive.data?.total!==1||
                   archive.data?.items?.[0]?.personId!==movableId)
                  throw new Error("transfer archive contains missing or duplicate movement");

                const adminPerson = await api.invoke("CreatePerson",{
                  visibleName:"مسؤول اختبار ويندوز",serviceNumberA:"00007193",
                  siteId:"SITE-UNSPECIFIED"
                });
                if (!adminPerson.data?.personId ||
                    adminPerson.data.personId === personId)
                  throw new Error("independent administrator PersonId missing");
                const adminAccount = await api.invoke("CreateUserAccount",{
                  username:"phase13-admin",password:__ADMIN_PASSWORD__,
                  passwordConfirmation:__ADMIN_PASSWORD__,
                  role:"OrdinaryAdministration",personId:adminPerson.data.personId
                });
                if (!adminAccount.data?.userId)
                  throw new Error("linked ordinary-administration account missing");

                const supervisor = await api.invoke("CreateUserAccount",{
                  username:"phase04-supervisor",password:__SUPERVISOR_PASSWORD__,
                  passwordConfirmation:__SUPERVISOR_PASSWORD__,
                  role:"Supervisor",personId
                });
                if (!supervisor.data?.userId)
                  throw new Error("linked supervisor account not persisted");
                const ownerUsers = await api.invoke("ListUserAccounts",{});
                if (!ownerUsers?.success) throw new Error("GeneralAdmin user list denied");
                await verifyAccountPicker("GeneralAdministration",personId);
                await api.invoke("Logout",{});
                api.setSessionNonce(null);
                const adminLogin = await api.invoke("AuthenticateLocalUser",{
                  username:"phase13-admin",password:__ADMIN_PASSWORD__
                });
                const adminNonce = adminLogin.data?.sessionNonce;
                if (!/^[0-9a-fA-F]{32}$/.test(adminNonce || ""))
                  throw new Error("ordinary Admin session missing");
                api.setSessionNonce(adminNonce);
                const adminUsers = await api.invoke("ListUserAccounts",{});
                if (!adminUsers?.success)
                  throw new Error("ordinary Admin user list denied");
                const adminAudit = await api.invoke("QueryAuditLog",{page:1,pageSize:10});
                if (!adminAudit?.success)
                  throw new Error("ordinary Admin audit read denied");
                await expectRejected("PreviewFullReset",{},undefined,"Unauthorized");
                await api.invoke("Logout",{});
                api.setSessionNonce(null);
                const supervisorLogin = await api.invoke("AuthenticateLocalUser",{
                  username:"phase04-supervisor",password:__SUPERVISOR_PASSWORD__
                });
                const nonce = supervisorLogin.data?.sessionNonce;
                if (!/^[0-9a-fA-F]{32}$/.test(nonce || ""))
                  throw new Error("supervisor session missing");
                api.setSessionNonce(nonce);

                window.AltamamAdminSettings.renderAdmin({role:"Supervisor",username:"native-supervisor"});
                if(window.AltamamAdminSettings.openOwnedRoute("Users")||
                   document.querySelector("#appViewport .admin-user-form"))
                  throw new Error("Supervisor UI exposed restricted account picker");
                await expectRejected("ListUserAccounts",{},undefined,"Unauthorized");
                const supervisorAudit = await api.invoke("QueryAuditLog",{page:1,pageSize:10});
                if (!supervisorAudit?.success)
                  throw new Error("Supervisor audit read denied");
                await expectRejected("CommitClosedDayCorrection",{
                  operationId:"phase04-denied-closed-day-0001"
                },undefined,"Unauthorized");
                await malformedVersion(nonce);

                // Exercise revoked sessions through the shipped browser client,
                // then prove a fresh login recovers without reloading WebView2.
                probeStage = "r01-session-recovery";
                await api.invoke("Logout",{});
                for (const action of ["GetActiveAttendanceStatuses",
                  "SearchPersons","SearchActivePersonsForAccount"]) {
                  api.setSessionNonce(nonce);
                  await expectRejected(action,{term:"00007192",take:30},
                    undefined,"SessionExpired");
                }
                // Real expired nonce through a shipped UI consumer, not a supplied reply.
                api.setSessionNonce(nonce);
                const expiredAttendance=document.createElement("section");
                document.body.appendChild(expiredAttendance);
                await window.AltamamAttendance.render(expiredAttendance);
                if(!expiredAttendance.querySelector(".admin-warning")||
                   expiredAttendance.querySelector('input[type="checkbox"]')||
                   api.getSessionNonce()===nonce)
                  throw new Error("expired native attendance UI retained selectable data/session");
                expiredAttendance.remove();
                const r01ExpiredUiConsumerVerified=true;
                api.setSessionNonce(null);
                const recoveredLogin = await api.invoke("AuthenticateLocalUser",{
                  username:"phase04-supervisor",password:__SUPERVISOR_PASSWORD__
                });
                const recoveredNonce = recoveredLogin.data?.sessionNonce;
                if (!/^[0-9a-fA-F]{32}$/.test(recoveredNonce || "") || recoveredNonce === nonce)
                  throw new Error("fresh login did not replace revoked browser session");
                api.setSessionNonce(recoveredNonce);
                const recoveredStatuses = await api.invoke("GetActiveAttendanceStatuses",{});
                if (!Array.isArray(recoveredStatuses.data) ||
                    !recoveredStatuses.data.some(status => status.statusId === "STS-PRESENT"))
                  throw new Error("status reading did not recover after login");
                await expectRejected("SearchActivePersonsForAccount",
                  {term:"00007192",take:30},undefined,"Unauthorized");
                const supervisorRecovered = await api.invoke("SearchPersons",
                  {term:"٠٠٠٠٧١٩٢",take:30});
                if (supervisorRecovered.data?.count !== 1 ||
                    supervisorRecovered.data?.items?.[0]?.personId !== personId)
                  throw new Error("Supervisor search did not recover the original identity");
                await api.invoke("Logout",{});
                api.setSessionNonce(null);
                probeStage = "r01-admin-search-recovery";
                const recoveredAdmin = await api.invoke("AuthenticateLocalUser",{
                  username:"phase13-admin",password:__ADMIN_PASSWORD__
                });
                const recoveredAdminNonce = recoveredAdmin.data?.sessionNonce;
                if (!/^[0-9a-fA-F]{32}$/.test(recoveredAdminNonce || ""))
                  throw new Error("ordinary Admin recovery session missing");
                api.setSessionNonce(recoveredAdminNonce);
                for (const action of ["SearchPersons","SearchActivePersonsForAccount"]) {
                  const recovered = await api.invoke(action,{term:"٠٠٠٠٧١٩٢",take:30});
                  if (recovered.data?.count !== 1 || recovered.data?.items?.length !== 1 ||
                      recovered.data.items[0].personId !== personId)
                    throw new Error(action + " did not recover the original person identity");
                  const empty = await api.invoke(action,{term:"999999999999",take:30});
                  if (!Array.isArray(empty.data?.items) ||
                      empty.data.items.length !== 0 || empty.data.count !== 0)
                    throw new Error(action + " empty result retained stale person data");
                }
                await verifyAccountPicker("OrdinaryAdministration",personId);
                const r01AccountPickerConsumerVerified=true;
                const r01SessionRecoveryVerified = true;
                if (!document.getElementById("phase04-real-sqlcipher-roundtrip")
                    ?.textContent.includes("00007192"))
                  throw new Error("HTML output lost after authorization checks");

                window.__phase04RealBridge = {status:"pass",personId,movableId,
                  modalConfirmedTransfer:true,r01AccountPickerConsumerVerified,r01ExpiredUiConsumerVerified,r01StatusVerified,r01SearchVerified,r01SessionRecoveryVerified,r01AttendanceConsumerVerified,r01AttendanceCancelVerified,r01AttendanceCommitVerified,r01AttendanceRevisionVerified,r01ReportConsumerVerified,attendancePersonId};
              })().catch(error => {
                window.__phase04RealBridge = {
                  status:"failed",
                  reason:probeStage + ": " + String(error?.errorCode || error?.name || "unknown")
                    + " - " + String(error?.message || "no public message")
                };
              });
              return "STARTED";
            })()
            """;
        javascript = javascript
            .Replace("__OWNER_PASSWORD__", JsonSerializer.Serialize(ownerPassword), StringComparison.Ordinal)
            .Replace("__SUPERVISOR_PASSWORD__", JsonSerializer.Serialize(supervisorPassword), StringComparison.Ordinal)
            .Replace("__ADMIN_PASSWORD__", JsonSerializer.Serialize(adminPassword), StringComparison.Ordinal);

        string started = await core.ExecuteScriptAsync(javascript).ConfigureAwait(true);
        if (started != "\"STARTED\"")
            throw new InvalidOperationException("Phase 04 WebView2 bridge probe did not initialize: " + started);

        string? lastStage = null;
        for (int attempt = 0; attempt < 200; attempt++)
        {
            string raw = await core.ExecuteScriptAsync(
                "JSON.stringify(window.__phase04RealBridge || null)").ConfigureAwait(true);
            string? statusJson = JsonSerializer.Deserialize<string>(raw);
            if (!string.IsNullOrEmpty(statusJson) && statusJson != "null")
            {
                using var status = JsonDocument.Parse(statusJson);
                string? phase = status.RootElement.GetProperty("status").GetString();
                if (phase == "running" && status.RootElement.TryGetProperty("stage", out var stage))
                {
                    string? currentStage = stage.GetString();
                    if (currentStage != lastStage)
                    {
                        MainWindow.TraceWebViewSelfCheck("Bridge probe stage: " + currentStage);
                        lastStage = currentStage;
                    }
                }
                if (phase == "failed")
                    throw new InvalidOperationException(
                        "Phase 04 real HTML/native/SQLCipher probe failed: " +
                        status.RootElement.GetProperty("reason").GetString());
                if (phase == "pass")
                {
                    if (!status.RootElement.TryGetProperty("modalConfirmedTransfer",
                            out var transferFlag) || transferFlag.ValueKind != JsonValueKind.True)
                        throw new InvalidOperationException(
                            "Real browser skipped modal-confirmed transfer");
                    foreach (string flag in new[] { "r01AccountPickerConsumerVerified", "r01ExpiredUiConsumerVerified", "r01StatusVerified", "r01SearchVerified", "r01SessionRecoveryVerified", "r01AttendanceConsumerVerified", "r01AttendanceCancelVerified", "r01AttendanceCommitVerified", "r01AttendanceRevisionVerified", "r01ReportConsumerVerified" })
                        if (!status.RootElement.TryGetProperty(flag, out var checkedFlag) ||
                            checkedFlag.ValueKind != JsonValueKind.True)
                            throw new InvalidOperationException("Real browser skipped R01 contract probe: " + flag);
                    string personId = status.RootElement.GetProperty("personId").GetString()
                        ?? throw new InvalidOperationException("Missing tested PersonId");
                    string movableId = status.RootElement.GetProperty("movableId").GetString()
                        ?? throw new InvalidOperationException("Missing movable PersonId");
                    if (movableId != "TST-PHASE15-MOVABLE" || movableId == personId)
                        throw new InvalidOperationException(
                            "Synthetic mover must not be the same-day newly-created person");
                    using var connection = database.CreateOpenConnection();
                    using var query = connection.CreateCommand();
                    query.CommandText = """
                        SELECT COUNT(*) FROM Persons
                        WHERE PersonId=$person AND CurrentServiceNumberA='00007192'
                          AND ActiveFlag=1;
                        """;
                    query.Parameters.AddWithValue("$person", personId);
                    if (Convert.ToInt64(query.ExecuteScalar()) != 1)
                        throw new InvalidOperationException(
                            "Phase 04 SQLCipher write did not persist the rendered PersonId");
                    using var cancelledAttendance = connection.CreateCommand();
                    cancelledAttendance.CommandText =
                        "SELECT COUNT(*) FROM AttendanceRecords WHERE PersonId=$person;";
                    cancelledAttendance.Parameters.AddWithValue("$person", personId);
                    if (Convert.ToInt64(cancelledAttendance.ExecuteScalar()) != 0)
                        throw new InvalidOperationException(
                            "Cancelled real attendance review persisted an attendance record");
                    string attendancePersonId = status.RootElement.GetProperty("attendancePersonId").GetString()
                        ?? throw new InvalidOperationException("Missing confirmed attendance PersonId");
                    if (attendancePersonId == personId || attendancePersonId == movableId)
                        throw new InvalidOperationException("Attendance confirmation reused another fixture");
                    using var confirmedAttendance = connection.CreateCommand();
                    confirmedAttendance.CommandText = """
                        SELECT COUNT(*) FROM AttendanceRecords a
                        JOIN Persons p ON p.PersonId=a.PersonId
                        WHERE a.PersonId=$person AND a.StatusId='STS-ABSENT'
                          AND p.CurrentServiceNumberA='00007195';
                        """;
                    confirmedAttendance.Parameters.AddWithValue("$person", attendancePersonId);
                    if (Convert.ToInt64(confirmedAttendance.ExecuteScalar()) != 1)
                        throw new InvalidOperationException("Real modal confirmation did not persist exact attendance");
                    using var attendanceAudit = connection.CreateCommand();
                    attendanceAudit.CommandText = """
                        SELECT COUNT(*) FROM AuditEvents
                        WHERE TargetEntityId=$person AND OperationCode='ATTENDANCE_COMMIT';
                        """;
                    attendanceAudit.Parameters.AddWithValue("$person", attendancePersonId);
                    if (Convert.ToInt64(attendanceAudit.ExecuteScalar()) != 1)
                        throw new InvalidOperationException("Real attendance confirmation audit missing or duplicated");
                    using var attendanceRevision = connection.CreateCommand();
                    attendanceRevision.CommandText = """
                        SELECT COUNT(*) FROM AttendanceRevisions
                        WHERE PersonId=$person AND BeforeStatusId='STS-PRESENT'
                          AND AfterStatusId='STS-ABSENT';
                        """;
                    attendanceRevision.Parameters.AddWithValue("$person", attendancePersonId);
                    if (Convert.ToInt64(attendanceRevision.ExecuteScalar()) != 1)
                        throw new InvalidOperationException("Real revision lost the before/after history");
                    using var revisionAudit = connection.CreateCommand();
                    revisionAudit.CommandText = """
                        SELECT COUNT(*) FROM AuditEvents
                        WHERE TargetEntityId=$person AND OperationCode='ATTENDANCE_REVISE';
                        """;
                    revisionAudit.Parameters.AddWithValue("$person", attendancePersonId);
                    if (Convert.ToInt64(revisionAudit.ExecuteScalar()) != 1)
                        throw new InvalidOperationException("Real attendance revision audit missing or duplicated");
                    using var cipher = connection.CreateCommand();
                    cipher.CommandText = "PRAGMA cipher_version;";
                    if (string.IsNullOrWhiteSpace(cipher.ExecuteScalar()?.ToString()))
                        throw new InvalidOperationException("Phase 04 did not use SQLCipher");
                    using var roles = connection.CreateCommand();
                    roles.CommandText = "SELECT COUNT(*) FROM Users WHERE Role IN (1,2,3);";
                    if (Convert.ToInt64(roles.ExecuteScalar()) != 3)
                        throw new InvalidOperationException("Founder/Admin/Supervisor fixture accounts missing");
                    using var founder = connection.CreateCommand();
                    founder.CommandText = "SELECT COUNT(*) FROM Users WHERE Role=1 AND LinkedPersonId IS NULL;";
                    if (Convert.ToInt64(founder.ExecuteScalar()) != 1)
                        throw new InvalidOperationException("Founding account was incorrectly linked");
                    // The first-use actor's PersonId has not moved: preserve the
                    // original Phase 04 freshly-created person and role tests.
                    using var unmovedNewPerson = connection.CreateCommand();
                    unmovedNewPerson.CommandText = """
                        SELECT COUNT(*) FROM PersonAssignments
                         WHERE PersonId=$person AND SiteId='SITE-UNSPECIFIED'
                           AND ValidToLocalDate IS NULL;
                        """;
                    unmovedNewPerson.Parameters.AddWithValue("$person", personId);
                    if (Convert.ToInt64(unmovedNewPerson.ExecuteScalar()) != 1)
                        throw new InvalidOperationException(
                            "Newly-created person was incorrectly moved by the fixture");
                    using var transferMovement = connection.CreateCommand();
                    transferMovement.CommandText = """
                        SELECT COUNT(*) FROM PersonnelMovements
                         WHERE PersonId=$person AND MovementType=1
                           AND SourceSiteId='SITE-UNSPECIFIED'
                           AND DestinationSiteId='SITE-PHASE15-E2E-DEST';
                        """;
                    transferMovement.Parameters.AddWithValue("$person",movableId);
                    if (Convert.ToInt64(transferMovement.ExecuteScalar()) != 1)
                        throw new InvalidOperationException(
                            "Native SQLCipher transfer did not persist exactly once");
                    using var transferredAssignment = connection.CreateCommand();
                    transferredAssignment.CommandText = """
                        SELECT COUNT(*) FROM PersonAssignments
                         WHERE PersonId=$person AND SiteId='SITE-PHASE15-E2E-DEST'
                           AND ValidToLocalDate IS NULL;
                        """;
                    transferredAssignment.Parameters.AddWithValue("$person",movableId);
                    if (Convert.ToInt64(transferredAssignment.ExecuteScalar()) != 1)
                        throw new InvalidOperationException(
                            "Native current assignment does not match transfer destination");
                    using var transferAudit = connection.CreateCommand();
                    transferAudit.CommandText = """
                        SELECT COUNT(*) FROM AuditEvents
                         WHERE TargetEntityId=$person AND OperationCode='TRANSFER_INTERNAL';
                        """;
                    transferAudit.Parameters.AddWithValue("$person",movableId);
                    if (Convert.ToInt64(transferAudit.ExecuteScalar()) != 1)
                        throw new InvalidOperationException(
                            "Transfer audit missing or duplicated on operation replay");
                    MainWindow.TraceWebViewSelfCheck(
                        "PASS real Windows modal-confirmed internal transfer persisted exactly once in SQLCipher");
                    MainWindow.TraceWebViewSelfCheck(
                        "PASS real Windows GeneralAdmin Admin Supervisor SQLCipher bridge authorizations");
                    return;
                }
            }
            await Task.Delay(150).ConfigureAwait(true);
        }
        throw new TimeoutException("Phase 04 browser/native/SQLCipher round-trip exceeded 30 seconds");
    }
}
