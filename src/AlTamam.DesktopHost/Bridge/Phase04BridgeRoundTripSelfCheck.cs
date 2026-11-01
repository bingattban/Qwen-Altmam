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

                target.textContent = saved.displayName + " — " + saved.serviceNumberA;
                if (!target.textContent.includes("00007192") ||
                    !target.isConnected)
                  throw new Error("real HTML was not updated from encrypted result");

                await expectRejected("CreatePerson",{
                  visibleName:"محاولة تكرار",serviceNumberA:"00007192",
                  siteId:"SITE-UNSPECIFIED"
                },undefined,"DuplicateServiceNumber");

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

                window.__phase04RealBridge = {status:"pass",personId};
              })().catch(error => {
                window.__phase04RealBridge = {
                  status:"failed",reason:String(error?.message || error)
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
                    string personId = status.RootElement.GetProperty("personId").GetString()
                        ?? throw new InvalidOperationException("Missing tested PersonId");
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
                    roles.CommandText = "SELECT COUNT(*) FROM Users WHERE Role IN (1,3);";
                    if (Convert.ToInt64(roles.ExecuteScalar()) != 3)
                        throw new InvalidOperationException("Founder/Admin/Supervisor fixture accounts missing");
                    using var founder = connection.CreateCommand();
                    founder.CommandText = "SELECT COUNT(*) FROM Users WHERE Role=1 AND LinkedPersonId IS NULL;";
                    if (Convert.ToInt64(founder.ExecuteScalar()) != 1)
                        throw new InvalidOperationException("Founding account was incorrectly linked");
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
