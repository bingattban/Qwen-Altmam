using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;
using AlTamam.Domain.Enums;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Builds the complete dispatcher registration set from the canonical action manifest.
/// Phase 04 wires authentic handlers for the shared-screen actions (provisioning, authentication,
/// session, sites, statuses, person search, drafts). Every other canonical action is registered with
/// its manifest ownership metadata and a truthful "not yet implemented" response — so unknown actions
/// are rejected while known-but-unwired actions fail honestly instead of pretending success.
/// </summary>
public static class BridgeHandlerCatalog
{
    public static WebView2BridgeDispatcher Create(BridgeSharedServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registrations = new List<BridgeActionRegistration>(ActionManifest.All.Length);

        foreach (var meta in ActionManifest.All)
        {
            bool allowUnauthenticated = meta.AllowedRoles.Contains("Unauthenticated", StringComparer.Ordinal);
            BridgeActionHandler handler = BuildHandler(meta.Action, services);
            registrations.Add(new BridgeActionRegistration(
                meta.Action, meta.OwningService, meta.AllowedRoles, meta.IsMutating, allowUnauthenticated, handler));
        }
        return new WebView2BridgeDispatcher(registrations);
    }

    private static BridgeActionHandler BuildHandler(string action, BridgeSharedServices services) => action switch
    {
        "GetProvisioningState" => async (req, principal, ct) =>
        {
            bool provisioned = await services.IsProvisionedAsync(ct).ConfigureAwait(false);
            return Done(req, new JsonObject { ["provisioned"] = provisioned });
        },
        "AuthenticateLocalUser" => async (req, principal, ct) =>
        {
            // A live user MUST explicitly logout before any other login.
            if(principal is not null)
                return Fail(req,BridgeSharedServices.Error(BridgeErrorCodes.Unauthorized,
                    "لتغيير المستخدم، انتقل إلى الإعدادات ثم تسجيل الخروج أولاً."));
            string username = req.Payload?["username"]?.GetValue<string>() ?? "";
            // Authentication alone must NEVER silently create a privileged account.
            // First-use setup requires an explicit owner-intent marker AND matching
            // confirmation; C# validates the strong password and persists atomically.
            if (!await services.IsProvisionedAsync(ct).ConfigureAwait(false))
            {
                string intent = req.Payload?["setupIntent"]?.GetValue<string>() ?? "";
                string password0 = req.Payload?["password"]?.GetValue<string>() ?? "";
                string confirmation = req.Payload?["passwordConfirmation"]?.GetValue<string>() ?? "";
                string fullName = req.Payload?["fullName"]?.GetValue<string>()?.Trim() ?? "";
                if (intent != "firstTimeSetup" || password0.Length == 0 ||
                    !string.Equals(password0,confirmation,StringComparison.Ordinal))
                    return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.ValidationError,
                        "يجب إكمال التهيئة الأولى وتأكيد كلمة المرور المطابقة قبل إنشاء الإدارة العامة."));
                BridgeResponse created = await services.ProvisionFirstAdministratorAsync(
                    username, password0, ct, fullName).ConfigureAwait(false);
                if (!created.Success) return Fail(req, created);
            }
            string password = req.Payload?["password"]?.GetValue<string>() ?? "";
            var (authPrincipal, failure) = await services.AuthenticateAsync(username, password, ct).ConfigureAwait(false);
            if (authPrincipal is null) return Fail(req, failure!);
            // Authentication creates a session; it must not broadcast SessionEnded.
            return Done(req, SessionData(authPrincipal));
        },
        "GetCurrentSession" => async (req, principal, ct) =>
        {
            if(principal is null)
                return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.SessionExpired,
                    "لا توجد جلسة نشطة."));
            // A WebView2 client can forge payload.userActivity from JavaScript.
            // The authenticated request only reads session state; ONLY the native
            // Windows host may renew a deadline after verifying recent foreground
            // OS input for this exact live session nonce.
            return Done(req,SessionData(principal));
        },
        // A read-only projection of the canonical server-side role matrix.
        // UI visibility is advisory; the dispatcher independently enforces RBAC.
        "GetNavigationPermissions" => (req, principal, ct) =>
        {
            if(principal is null)
                return Task.FromResult(Fail(req,BridgeSharedServices.Error(
                    BridgeErrorCodes.SessionExpired,"لا توجد جلسة نشطة.")));
            string role=principal.Role.ToString();
            var allowed=new JsonArray();
            foreach(var meta in ActionManifest.All)
                if(meta.AllowedRoles.Contains(role,StringComparer.Ordinal))
                    allowed.Add(meta.Action);
            return Task.FromResult(Done(req,new JsonObject
            {
                ["role"]=role,
                ["allowedActions"]=allowed
            }));
        },
        "Logout" => async (req, principal, ct) =>
        {
            await services.RevokeSessionAsync(req.SessionNonce, ct).ConfigureAwait(false);
            return Done(req, new JsonObject { ["loggedOut"] = true }, Invalidation(UiInvalidationEvents.SessionEnded));
        },
        // Real read-only projections: all SQL executes locally against SQLCipher.
        // Phase 07: actual, read-only SQLCipher ledger status; never create a
        // run on page load, and never pretend an unrecorded failure succeeded.
        "GetAutoAttendanceRun" => async (req, principal, ct) =>
        {
            int take = 10;
            if (req.Payload?["take"] is JsonNode requested)
            {
                try { take = requested.GetValue<int>(); }
                catch
                {
                    return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.ValidationError,
                        "عدد سجلات التحضير المطلوبة غير صالح."));
                }
                if (take < 1 || take > 60)
                    return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.ValidationError,
                        "حدد عدد السجلات من 1 إلى 60."));
            }
            var snapshot = await new AlTamam.Application.Scheduling.AutoAttendanceService(
                services.Db, services.Time).ReadStatusAsync(take, ct).ConfigureAwait(false);
            var history = new JsonArray();
            foreach (var run in snapshot.Runs)
            {
                history.Add(new JsonObject
                {
                    ["jobKey"] = run.JobKey,
                    ["businessDate"] = run.BusinessDate.ToString("yyyy-MM-dd"),
                    ["completed"] = true,
                    ["isCatchUpRun"] = run.IsCatchUpRun,
                    ["copiedCount"] = run.CopiedCount,
                    ["skippedNoYesterdayCount"] = run.SkippedNoYesterdayCount,
                    ["skippedTransferExemptCount"] = run.SkippedTransferExemptCount,
                    ["summaryArabic"] = run.SummaryArabic
                });
            }
            return Done(req, new JsonObject
            {
                ["runs"] = history,
                ["hasCompletedRuns"] = snapshot.Runs.Count > 0,
                ["lastRunDate"] = snapshot.Runs.Count > 0
                    ? snapshot.Runs[0].BusinessDate.ToString("yyyy-MM-dd") : null,
                ["nowUtc"] = snapshot.NowUtc.ToString("O"),
                ["localNow"] = snapshot.LocalNowIso,
                ["nextDueLocal"] = snapshot.NextDueLocalWallClock,
                ["currentDayDue"] = snapshot.CurrentDayDue,
                ["clockIntegrityOk"] = snapshot.ClockIntegrityOk,
                ["pendingCatchUpDays"] = snapshot.PendingCatchUpDays,
                // Only successful runs are recorded in AutoAttendanceRuns.
                // Missing/failed attempts cannot be inferred from the lack of a row.
                ["failureHistoryAvailable"] = false
            });
        },
        "GetDashboardSummary" => async (req, principal, ct) =>
        {
            var read = new DailyMonthlyReadService(services.Db);
            BridgeResponse inner = await read.DashboardSummaryAsync(req.Payload, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "QueryDailyRoster" => async (req, principal, ct) =>
        {
            var read = new DailyMonthlyReadService(services.Db);
            BridgeResponse inner = await read.DailyRosterAsync(req.Payload, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        // A dedicated read-only unprepared view. Never trust a caller-supplied
        // statusId: a transferred person must not be marked unprepared that day.
        "QueryUnprepared" => async (req, principal, ct) =>
        {
            var filters = req.Payload?.DeepClone() as JsonObject ?? new JsonObject();
            filters["statusId"] = "__UNPREPARED__";
            var read = new DailyMonthlyReadService(services.Db);
            BridgeResponse inner = await read.DailyRosterAsync(filters, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        // The movement picker must list live active identities only, even if
        // JavaScript forges includeArchived or organizationalStatus filters.
        "QueryActivePersonsBySite" => async (req, principal, ct) =>
        {
            var filters = req.Payload?.DeepClone() as JsonObject ?? new JsonObject();
            filters["includeArchived"] = false;
            filters["organizationalStatus"] = 1;
            var roster = new PersonRosterService(services.Db);
            BridgeResponse inner = await roster.QueryPersonsAsync(filters, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "GetAvailableArchivePeriods" => async (req, principal, ct) =>
        {
            var read = new DailyMonthlyReadService(services.Db);
            BridgeResponse inner = await read.ArchivePeriodsAsync(ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "QueryMonthlyGrid" => async (req, principal, ct) =>
        {
            var read = new DailyMonthlyReadService(services.Db);
            BridgeResponse inner = await read.MonthlyGridAsync(req.Payload, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "SelectImportWorkbook" => async (req, principal, ct) =>
        {
            var import = new ExcelPersonnelImportService(services.Db, services.Time);
            var result = await import.SelectAsync(req.Payload, principal!.UserId, ct).ConfigureAwait(false);
            return result with { RequestId = req.RequestId };
        },
        "AnalyzeImport" => async (req, principal, ct) =>
        {
            var import = new ExcelPersonnelImportService(services.Db, services.Time);
            var result = await import.AnalyzeAsync(req.Payload, principal!.UserId, ct).ConfigureAwait(false);
            return result with { RequestId = req.RequestId };
        },
        "PreviewImportPlan" => async (req, principal, ct) =>
        {
            var import = new ExcelPersonnelImportService(services.Db, services.Time);
            var result = await import.PreviewAsync(req.Payload, principal!.UserId, ct).ConfigureAwait(false);
            return result with { RequestId = req.RequestId };
        },
        "CommitImport" => async (req, principal, ct) =>
        {
            var import = new ExcelPersonnelImportService(services.Db, services.Time);
            var result = await import.CommitAsync(req.Payload, principal!.UserId, principal.Username, ct).ConfigureAwait(false);
            return result with { RequestId = req.RequestId };
        },
        "QueryImportOutcome" => async (req, principal, ct) =>
        {
            var outcome = new ExcelImportOutcomeService(services.Db);
            var result = await outcome.QueryAsync(req.Payload, principal!.UserId, ct).ConfigureAwait(false);
            return result with { RequestId = req.RequestId };
        },
        "ExportImportIssues" => async (req, principal, ct) =>
        {
            var outcome = new ExcelImportOutcomeService(services.Db);
            var result = await outcome.ExportIssuesAsync(req.Payload, principal!.UserId, ct).ConfigureAwait(false);
            return result with { RequestId = req.RequestId };
        },
        "GetExportablePeriods" => async (req, principal, ct) =>
        {
            var excel = new MonthlyExcelExportService(services.Db);
            var result = await excel.PeriodsAsync(ct).ConfigureAwait(false);
            return result with { RequestId = req.RequestId };
        },
        "PreviewMonthlyExport" => async (req, principal, ct) =>
        {
            var excel = new MonthlyExcelExportService(services.Db);
            var result = await excel.PreviewAsync(req.Payload, ct).ConfigureAwait(false);
            return result with { RequestId = req.RequestId };
        },
        "ExportMonthlyWorkbookSet" => async (req, principal, ct) =>
        {
            var excel = new MonthlyExcelExportService(services.Db);
            var result = await excel.ExportAsync(req.Payload, ct).ConfigureAwait(false);
            return result with { RequestId = req.RequestId };
        },
        "GetMonthlyCellDetails" => async (req, principal, ct) =>
        {
            var read = new DailyMonthlyReadService(services.Db);
            BridgeResponse inner = await read.MonthlyCellDetailsAsync(req.Payload as JsonObject, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "QuerySites" => async (req, principal, ct) =>
        {
            BridgeResponse inner = await services.ListSitesAsync(includeInactive: principal?.Role != UserRole.Supervisor, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "GetActiveAttendanceStatuses" or "QueryAttendanceStatuses" => async (req, principal, ct) =>
        {
            BridgeResponse inner = await services.ListActiveStatusesAsync(ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "SearchPersons" or "SearchActivePersonsForAccount" => async (req, principal, ct) =>
        {
            string term = req.Payload?["term"]?.GetValue<string>() ?? "";
            int take = TryInt(req.Payload?["take"], 25);
            BridgeResponse inner = await services.SearchPersonsAsync(term, take, ct,
                activeOnly: req.Action == "SearchActivePersonsForAccount").ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "SetClosedDayCorrectionPolicy" => async (req,principal,ct) =>
        {
            var service = new ClosedDayCorrectionService(services.Db,services.Time);
            var result = await service.SetPolicyAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "PreviewClosedDayCorrection" => async (req,principal,ct) =>
        {
            var service = new ClosedDayCorrectionService(services.Db,services.Time);
            var result = await service.PreviewAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "CommitClosedDayCorrection" => async (req,principal,ct) =>
        {
            var service = new ClosedDayCorrectionService(services.Db,services.Time);
            var result = await service.CommitAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "PreviewAttendanceChange" => async (req,principal,ct) =>
        {
            BridgeResponse result = req.Payload?["mode"]?.GetValue<string>() == "carry"
                ? await new ManualCarryForwardService(services.Db,services.Time)
                    .PreviewAsync(req.Payload,principal!,ct).ConfigureAwait(false)
                : await new AttendanceCommitService(services.Db,services.Time)
                    .PreviewAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "SubmitAttendance" => async (req,principal,ct) =>
        {
            BridgeResponse result = req.Payload?["mode"]?.GetValue<string>() == "carry"
                ? await new ManualCarryForwardService(services.Db,services.Time)
                    .CommitAsync(req.Payload,principal!,ct).ConfigureAwait(false)
                : await new AttendanceCommitService(services.Db,services.Time)
                    .CommitAsync(req.Payload,principal!,false,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "ReviseTodayAttendance" => async (req,principal,ct) =>
        {
            var svc=new AttendanceCommitService(services.Db,services.Time);
            BridgeResponse result=await svc.CommitAsync(req.Payload,principal!,true,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "ListAttendanceDrafts" => async (req, principal, ct) =>
        {
            BridgeResponse inner = await services.ListDraftsAsync(principal!.UserId, ct).ConfigureAwait(false);
            return Done(req, inner.Data);
        },
        "SaveAttendanceDraft" => async (req, principal, ct) =>
        {
            if (req.Payload is not JsonObject payload)
                return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.ValidationError, "بيانات المسودة مطلوبة."));
            BridgeResponse inner = await services.SaveDraftAsync(principal!.UserId, payload, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "DeleteAttendanceDraft" => async (req, principal, ct) =>
        {
            if (req.Payload is not JsonObject payload)
                return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.ValidationError, "بيانات المسودة مطلوبة."));
            BridgeResponse inner = await services.DeleteDraftAsync(principal!.UserId, payload, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "GetApplicationVersion" => (req, principal, ct) =>
            Task.FromResult(Done(req, new JsonObject
            {
                ["productNameAr"] = "التمام اليومي",
                ["version"] = "0.4.0",
                ["offline"] = true,
                ["bridgeProtocol"] = BridgeProtocol.ProtocolVersion
            })),
        // Phase 05 already contains real person CRUD; expose its canonical bridge
        // contracts instead of accidentally routing QueryPersons to a 25-row search.
        // Historical manifest read actions are compatibility entry points into
        // the existing canonical PersonId-keyed lifetime report. Reuse its
        // effective-dated filters, saved snapshots and paging WITHOUT creating
        // a competing SQL history engine or changing the current report screen.
        // The response is deliberately the canonical full report shape, so
        // existing fields (attendance/statusTotals/audit/movements) retain their
        // exact names and meaning for current and older consumers.
        "QueryPersonAttendanceTimeline" or "QueryPersonAttendanceTotals"
            or "QueryPersonOperationsTimeline" or "QueryPersonMovements" =>
            async (req, principal, ct) =>
        {
            BridgeResponse result = await new PersonnelReportService(services.Db)
                .ReportAsync(req.Payload, ct).ConfigureAwait(false);
            return result with { RequestId = req.RequestId };
        },
        "BuildPersonLifetimeReport" => async (req, principal, ct) =>
        {
            BridgeResponse result=req.Payload?["exportPreview"]?.GetValue<bool>()==true
                ? await new PersonnelExcelExportService(services.Db)
                    .PreviewReportAsync(req.Payload,ct).ConfigureAwait(false)
                : await new PersonnelReportService(services.Db)
                    .ReportAsync(req.Payload,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "ExportPersonLifetimeExcel" => async (req, principal, ct) =>
        {
            var service=new PersonnelExcelExportService(services.Db);
            BridgeResponse result=await service.ExportAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "QueryPersons" => async (req, principal, ct) =>
        {
            var svc = new PersonRosterService(services.Db);
            BridgeResponse inner = await svc.QueryPersonsAsync(req.Payload ?? new JsonObject(), ct).ConfigureAwait(false);
            return inner with { RequestId = req.RequestId };
        },
        "PreviewArchivedIdentity" => async (req,principal,ct) =>
        {
            var service=new ArchivedReturnService(services.Db,services.Time);
            var result=await service.PreviewAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "CommitArchivedReturn" => async (req,principal,ct) =>
        {
            var service=new ArchivedReturnService(services.Db,services.Time);
            var result=await service.CommitReturnAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "CreatePerson" => async (req, principal, ct) =>
        {
            var svc = new PersonRosterService(services.Db);
            BridgeResponse inner = await svc.CreatePersonAsync(principal!.UserId,
                principal.Username, req.Payload ?? new JsonObject(), ct).ConfigureAwait(false);
            return inner with { RequestId = req.RequestId };
        },
        "GetPersonSummary" => async (req, principal, ct) =>
        {
            var svc = new PersonRosterService(services.Db);
            BridgeResponse inner = await svc.GetPersonSummaryAsync(req.Payload ?? new JsonObject(), ct).ConfigureAwait(false);
            return inner with { RequestId = req.RequestId };
        },
        "PreviewPersonIdentityUpdate" => async (req, principal, ct) =>
        {
            var svc = new PersonRosterService(services.Db);
            BridgeResponse inner = await svc.PreviewPersonIdentityUpdateAsync(req.Payload ?? new JsonObject(), ct).ConfigureAwait(false);
            return inner with { RequestId = req.RequestId };
        },
        "UpdatePersonIdentity" => async (req, principal, ct) =>
        {
            var svc = new PersonRosterService(services.Db);
            BridgeResponse inner = await svc.UpdatePersonIdentityAsync(principal!.UserId, principal.Username,
                req.Payload ?? new JsonObject(), ct).ConfigureAwait(false);
            return inner with { RequestId = req.RequestId };
        },
        "PreviewArchivePersons" => async (req, principal, ct) =>
        {
            var svc = new PersonRosterService(services.Db);
            BridgeResponse inner = await svc.PreviewArchivePersonsAsync(req.Payload ?? new JsonObject(), principal!.UserId, ct).ConfigureAwait(false);
            return inner with { RequestId = req.RequestId };
        },
        "ArchivePersons" => async (req, principal, ct) =>
        {
            var svc = new PersonRosterService(services.Db);
            BridgeResponse inner = await svc.ArchivePersonsAsync(principal!.UserId, principal.Username,
                req.Payload ?? new JsonObject(), ct).ConfigureAwait(false);
            return inner with { RequestId = req.RequestId };
        },
        // Configuration writes are registered with server-side role checks and
        // audit/identity-history updates in the same encrypted transaction.
        "CreateSite" => async (req, principal, ct) =>
        {
            var svc = new AdminConfigurationService(services.Db);
            BridgeResponse inner = await svc.CreateSiteAsync(req.Payload, principal!, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data, Invalidation(UiInvalidationEvents.SiteCreated)) : Fail(req, inner);
        },
        "RenameSite" => async (req, principal, ct) =>
        {
            var svc = new AdminConfigurationService(services.Db);
            BridgeResponse inner = await svc.RenameSiteAsync(req.Payload, principal!, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data, Invalidation(UiInvalidationEvents.SiteRenamed)) : Fail(req, inner);
        },
        "RemoveSiteAndReassignCurrentPersons" => async (req, principal, ct) =>
        {
            var svc=new AdminConfigurationService(services.Db,services.Time);
            BridgeResponse inner=await svc.RemoveSiteAndReassignCurrentPersonsAsync(
                req.Payload,principal!,ct).ConfigureAwait(false);
            return inner.Success
                ? Done(req,inner.Data,Invalidation(UiInvalidationEvents.SiteRenamed))
                : Fail(req,inner);
        },
        "GetCustomFieldDefinitions" => async (req, principal, ct) =>
        {
            var service=new RosterColumnStructureService(services.Db,services.Time);
            var result=await service.ListAsync(req.Payload,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "CreateCustomFieldDefinition" => async (req, principal, ct) =>
        {
            var service=new RosterColumnStructureService(services.Db,services.Time);
            var result=await service.CreateAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "UpdateCustomFieldMetadata" => async (req, principal, ct) =>
        {
            var service=new RosterColumnStructureService(services.Db,services.Time);
            var result=await service.UpdateAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "DeactivateCustomField" => async (req, principal, ct) =>
        {
            var service=new RosterColumnStructureService(services.Db,services.Time);
            var result=await service.DeactivateAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "CreateAttendanceStatus" => async (req, principal, ct) =>
        {
            var svc = new AdminConfigurationService(services.Db);
            BridgeResponse inner = await svc.CreateStatusAsync(req.Payload, principal!, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data, Invalidation(UiInvalidationEvents.StatusDefinitionCreated)) : Fail(req, inner);
        },
        "UpdateAttendanceStatus" => async (req, principal, ct) =>
        {
            var svc = new AdminConfigurationService(services.Db);
            BridgeResponse inner = await svc.UpdateStatusAsync(req.Payload, principal!, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data, Invalidation(UiInvalidationEvents.StatusDefinitionChanged)) : Fail(req, inner);
        },
        "DeactivateOrDeleteStatus" => async (req, principal, ct) =>
        {
            var svc = new AdminConfigurationService(services.Db);
            BridgeResponse inner = await svc.DeactivateStatusAsync(req.Payload, principal!, ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data, Invalidation(UiInvalidationEvents.StatusDefinitionChanged)) : Fail(req, inner);
        },
        "PreviewInternalTransfer" => async(req,principal,ct) =>
        {
            var service=new PersonnelMovementService(services.Db,services.Time);
            BridgeResponse result=await service.PreviewAsync(req.Payload,principal!,false,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "CommitInternalTransfer" => async(req,principal,ct) =>
        {
            var service=new PersonnelMovementService(services.Db,services.Time);
            BridgeResponse result=await service.CommitAsync(req.Payload,principal!,false,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "PreviewExternalTransfer" => async(req,principal,ct) =>
        {
            var service=new PersonnelMovementService(services.Db,services.Time);
            BridgeResponse result=await service.PreviewAsync(req.Payload,principal!,true,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "CommitExternalTransfer" => async(req,principal,ct) =>
        {
            var service=new PersonnelMovementService(services.Db,services.Time);
            BridgeResponse result=await service.CommitAsync(req.Payload,principal!,true,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "QueryMovementArchive" => async(req,principal,ct) =>
        {
            var service=new PersonnelMovementService(services.Db,services.Time);
            BridgeResponse result=await service.ArchiveAsync(req.Payload,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "GetMovementDetails" => async(req,principal,ct) =>
        {
            var service=new PersonnelMovementService(services.Db,services.Time);
            BridgeResponse result=await service.DetailsAsync(req.Payload,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        // Separation is a soft historical transition, not deletion.
        "PreviewSeparation" => async(req,principal,ct) =>
        {
            var service=new PersonnelSeparationService(services.Db,services.Time);
            BridgeResponse response=await service.PreviewAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return response with {RequestId=req.RequestId};
        },
        "CommitSeparation" => async(req,principal,ct) =>
        {
            var service=new PersonnelSeparationService(services.Db,services.Time);
            BridgeResponse response=await service.CommitAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return response with {RequestId=req.RequestId};
        },
        "QuerySeparationArchive" => async(req,principal,ct) =>
        {
            var service=new PersonnelSeparationService(services.Db,services.Time);
            BridgeResponse response=await service.ArchiveAsync(req.Payload,ct).ConfigureAwait(false);
            return response with {RequestId=req.RequestId};
        },
        "GetSeparationDetails" => async(req,principal,ct) =>
        {
            var service=new PersonnelSeparationService(services.Db,services.Time);
            BridgeResponse response=await service.DetailsAsync(req.Payload,ct).ConfigureAwait(false);
            return response with {RequestId=req.RequestId};
        },
        // Settings: read real configuration, audit manual encrypted backup.
        // No fake success for scheduled backups, restore or destructive reset.
        "SetOutputDirectory" => async (req, principal, ct) =>
        {
            bool choose = req.Payload?["choose"]?.GetValue<bool>() == true;
            string? selected = req.Payload?["directory"]?.GetValue<string>();
            if (choose)
            {
                if (services.PickOutputDirectoryAsync is null)
                    return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.ValidationError,
                        "منتقي مجلدات Windows غير متاح."));
                selected = await services.PickOutputDirectoryAsync(
                    await new OutputDirectorySettings(services.Db).GetAsync(ct).ConfigureAwait(false));
                if (selected is null)
                    return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.ValidationError,
                        "لم يتم اختيار مجلد؛ لم تتغير الإعدادات."));
            }
            var answer = await new OutputDirectorySettings(services.Db)
                .SaveAsync(selected, principal!, ct).ConfigureAwait(false);
            return answer.Success ? Done(req, answer.Data) : Fail(req, answer);
        },
        "GetAutoAttendancePolicy" => async (req, principal, ct) =>
        {
            var svc = new AutoAttendancePolicyService(services.Db,services.Time);
            var result = await svc.GetAsync(ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "SetAutoAttendanceDefaultStatus" => async (req, principal, ct) =>
        {
            var svc = new AutoAttendancePolicyService(services.Db,services.Time);
            var result = await svc.SetAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "SetAttendanceSchedule" => async (req, principal, ct) =>
        {
            var svc = new AutoAttendancePolicyService(services.Db,services.Time);
            var result = await svc.SetScheduleAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "GetSettingsOverview" => async (req, principal, ct) =>
        {
            var svc = new SettingsReadBackupService(services.Db);
            BridgeResponse inner = await svc.OverviewAsync(principal!, ct).ConfigureAwait(false);
            return inner with { RequestId = req.RequestId };
        },
        "SetTheme" => async (req, principal, ct) =>
        {
            var service=new SettingsReadBackupService(services.Db,services.Time);
            BridgeResponse result=await service.SetThemeAsync(req.Payload as JsonObject,ct)
                .ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "GetBackupSchedule" => async (req, principal, ct) =>
        {
            var svc = new SettingsReadBackupService(services.Db);
            BridgeResponse inner = await svc.ScheduleAsync(ct).ConfigureAwait(false);
            return inner with { RequestId = req.RequestId };
        },
        "UpdateBackupSchedule" => async (req,principal,ct) =>
        {
            var svc=new SettingsReadBackupService(services.Db,services.Time);
            var result=await svc.ConfigureBackupScheduleAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "VerifyBackup" => async(req,principal,ct) =>
        {
            string? path;
            try{path=req.Payload?["path"]?.GetValue<string>();}
            catch{path=null;}
            var result=await new BackupInspectionService(services.Db)
                .VerifyAsync(path,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "InspectBackup" => async(req,principal,ct) =>
        {
            if(services.PickBackupFileAsync is null)
                return Fail(req,BridgeSharedServices.Error(BridgeErrorCodes.ValidationError,
                    "منتقي ملف النسخة غير متاح في هذه الجلسة."));
            string folder=await new OutputDirectorySettings(services.Db)
                .GetAsync(ct).ConfigureAwait(false);
            string? selected=await services.PickBackupFileAsync(folder).ConfigureAwait(false);
            if(string.IsNullOrWhiteSpace(selected))
                return Fail(req,BridgeSharedServices.Error(BridgeErrorCodes.ValidationError,
                    "تم إلغاء اختيار ملف النسخة الاحتياطية."));
            var result=await new BackupInspectionService(services.Db)
                .VerifyAsync(selected,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        // Read-only inspection for authorized administrators. The destructive
        // RestoreDatabaseAtomically action remains unavailable until the native
        // reauthentication, shutdown and offline acceptance gates are complete.
        "PreviewRestore" => async(req,principal,ct) =>
        {
            string? path,sha256;
            try
            {
                path=req.Payload?["path"]?.GetValue<string>();
                sha256=req.Payload?["expectedSha256"]?.GetValue<string>();
            }
            catch(Exception e) when(e is InvalidOperationException or FormatException)
            {
                return Fail(req,BridgeSharedServices.Error(BridgeErrorCodes.ValidationError,
                    "بيانات النسخة غير صالحة؛ أعد اختيار الملف وفحصه."));
            }
            var result=await new BackupInspectionService(services.Db)
                .PreviewRestoreAsync(path,sha256,ct).ConfigureAwait(false);
            return result with {RequestId=req.RequestId};
        },
        "RunDatabaseIntegrityCheck" => (req, principal, ct) =>
        {
            var svc = new SettingsReadBackupService(services.Db);
            return Task.FromResult(svc.Integrity() with { RequestId = req.RequestId });
        },
        "CreateVerifiedBackup" => async (req, principal, ct) =>
        {
            var svc = new SettingsReadBackupService(services.Db);
            BridgeResponse inner = await svc.BackupAsync(principal!, ct).ConfigureAwait(false);
            return inner with { RequestId = req.RequestId };
        },
        "PreviewFullReset" => async(req,principal,ct) =>
        {
            var svc=new FullDataResetService(services.Db,services.Time);
            var response=await svc.PreviewAsync(principal!,ct).ConfigureAwait(false);
            return response with {RequestId=req.RequestId};
        },
        "CommitFullReset" => async(req,principal,ct) =>
        {
            var svc=new FullDataResetService(services.Db,services.Time);
            var response=await svc.CommitAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return response with {RequestId=req.RequestId};
        },
        // Phase 11: secure user administration, not the older "not implemented" fallback.
        "GetAllowedRoles" => (req, principal, ct) =>
        {
            var svc = new UserAccountManagementService(services.Db,services.Time);
            BridgeResponse inner=svc.AllowedRoles(principal!);
            return Task.FromResult(inner.Success?Done(req,inner.Data):Fail(req,inner));
        },
        "ListUserAccounts" => async(req,principal,ct) =>
        {
            var svc=new UserAccountManagementService(services.Db,services.Time);
            BridgeResponse inner=await svc.ListAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return inner.Success?Done(req,inner.Data):Fail(req,inner);
        },
        "CreateUserAccount" => async(req,principal,ct) =>
        {
            var svc=new UserAccountManagementService(services.Db,services.Time);
            BridgeResponse inner=await svc.CreateAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return inner.Success?Done(req,inner.Data,Invalidation(UiInvalidationEvents.UserAccountChanged)):Fail(req,inner);
        },
        "ChangeUserPassword" => async(req,principal,ct) =>
        {
            var svc=new UserAccountManagementService(services.Db,services.Time);
            BridgeResponse inner=await svc.ChangePasswordAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return inner.Success?Done(req,inner.Data,Invalidation(UiInvalidationEvents.UserAccountChanged)):Fail(req,inner);
        },
        "DeactivateUserAccount" => async(req,principal,ct) =>
        {
            var svc=new UserAccountManagementService(services.Db,services.Time);
            BridgeResponse inner=await svc.DeactivateAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return inner.Success?Done(req,inner.Data,Invalidation(UiInvalidationEvents.UserAccountChanged)):Fail(req,inner);
        },
        "ReactivateUserAccount" => async(req,principal,ct) =>
        {
            var svc=new UserAccountManagementService(services.Db,services.Time);
            BridgeResponse inner=await svc.ReactivateAsync(req.Payload,principal!,ct).ConfigureAwait(false);
            return inner.Success?Done(req,inner.Data,Invalidation(UiInvalidationEvents.UserAccountChanged)):Fail(req,inner);
        },
        // ---------------- Phase 11: UserAuditService — audit portion (immutable evidence + overlays)
        "QueryAuditLog" => async (req, principal, ct) =>
        {
            var svc = new AuditOverlayService(services.Db, services.Time);
            BridgeResponse inner = await svc.QueryAuditLogAsync(req.Payload ?? new JsonObject(), ct, principal).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "GetAuditEntryDetails" => async (req, principal, ct) =>
        {
            var svc = new AuditOverlayService(services.Db, services.Time);
            BridgeResponse inner = await svc.GetAuditEntryDetailsAsync(req.Payload ?? new JsonObject(), ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        "UpdateAuditPresentation" => async (req, principal, ct) =>
        {
            if (req.Payload is not JsonObject payload)
                return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.MissingData, "حمولة التعديل مطلوبة."));
            var svc = new AuditOverlayService(services.Db, services.Time);
            BridgeResponse inner = await svc.UpdateAuditPresentationAsync(payload, principal!, ct).ConfigureAwait(false);
            return inner.Success
                ? Done(req, inner.Data, Invalidation(UiInvalidationEvents.VisibleAuditOverlayChanged))
                : Fail(req, inner);
        },
        "DeleteAuditPresentation" => async (req, principal, ct) =>
        {
            if (req.Payload is not JsonObject payload)
                return Fail(req, BridgeSharedServices.Error(BridgeErrorCodes.MissingData, "حمولة الحذف الظاهري مطلوبة."));
            var svc = new AuditOverlayService(services.Db, services.Time);
            BridgeResponse inner = await svc.DeleteAuditPresentationAsync(payload, principal!, ct).ConfigureAwait(false);
            return inner.Success
                ? Done(req, inner.Data, Invalidation(UiInvalidationEvents.VisibleAuditOverlayChanged))
                : Fail(req, inner);
        },
        "QueryAuditPresentationChanges" => async (req, principal, ct) =>
        {
            var svc = new AuditOverlayService(services.Db, services.Time);
            BridgeResponse inner = await svc.QueryAuditPresentationChangesAsync(req.Payload ?? new JsonObject(), ct).ConfigureAwait(false);
            return inner.Success ? Done(req, inner.Data) : Fail(req, inner);
        },
        _ => (req, principal, ct) => Task.FromResult(Fail(req, BridgeSharedServices.Error(
                BridgeErrorCodes.MissingData,
                $"الإجراء '{req.Action}' معتمد في العقد لكنه يُنفَّذ في مراحله المخصصة؛ لا تتوفر بياناته بعد.")))
    };

    private static int TryInt(JsonNode? node, int fallback)
    {
        try { return node?.GetValue<int>() ?? fallback; }
        catch { return fallback; }
    }

    private static object SessionData(BridgePrincipal principal) => new JsonObject
    {
        ["userId"] = principal.UserId,
        ["username"] = principal.Username,
        ["role"] = principal.Role.ToString(),
        ["roleLabelAr"] = principal.Role.ToArabicLabel(),
        ["sessionNonce"] = principal.SessionNonce
    };

    private static BridgeResponse Done(BridgeRequest req, object? data, IReadOnlyList<Contracts.Events.UiInvalidationEventPayload>? events = null)
        => new(req.RequestId, true, data, null, null, null, events);

    private static BridgeResponse Fail(BridgeRequest req, BridgeResponse inner)
        => new(req.RequestId, false, null, inner.ErrorCode ?? BridgeErrorCodes.ValidationError, inner.UserMessage ?? "فشلت العملية.");

    private static IReadOnlyList<Contracts.Events.UiInvalidationEventPayload> Invalidation(string eventName) =>
        new[] { new Contracts.Events.UiInvalidationEventPayload(eventName, Array.Empty<string>(), DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), 1) };
}
