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

                await expectRejected("ListUserAccounts",{},undefined,"Unauthorized");
                const supervisorAudit = await api.invoke("QueryAuditLog",{page:1,pageSize:10});
                if (!supervisorAudit?.success)
                  throw new Error("Supervisor audit read denied");
                await expectRejected("CommitClosedDayCorrection",{
                  operationId:"phase04-denied-closed-day-0001"
                },undefined,"Unauthorized");
                await malformedVersion(nonce);
                if (!document.getElementById("phase04-real-sqlcipher-roundtrip")
                    ?.textContent.includes("00007192"))
                  throw new Error("HTML output lost after authorization checks");

                window.__phase04RealBridge = {status:"pass",personId,movableId,
                  modalConfirmedTransfer:true,r01StatusVerified,r01SearchVerified};
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

        for (int attempt = 0; attempt < 200; attempt++)
        {
            string raw = await core.ExecuteScriptAsync(
                "JSON.stringify(window.__phase04RealBridge || null)").ConfigureAwait(true);
            string? statusJson = JsonSerializer.Deserialize<string>(raw);
            if (!string.IsNullOrEmpty(statusJson) && statusJson != "null")
            {
                using var status = JsonDocument.Parse(statusJson);
                string? phase = status.RootElement.GetProperty("status").GetString();
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
                    foreach (string flag in new[] { "r01StatusVerified", "r01SearchVerified" })
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
