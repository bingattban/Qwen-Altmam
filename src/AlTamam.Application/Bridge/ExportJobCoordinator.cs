using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;

namespace AlTamam.Application.Bridge;

/// <summary>
/// In-process evidence for CURRENT-session Excel jobs. A job is never treated
/// as complete before the existing XLSX writer commits its real destination.
/// No export is launched as an unobserved background task; the original bridge
/// response continues to return the actual completed file (or an error).
/// </summary>
public sealed class ExportJobCoordinator
{
    private sealed class Job
    {
        public readonly object Gate = new();
        public readonly CancellationTokenSource Source;
        public readonly string Id, Kind;
        public readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;
        public int Processed, Total;
        public bool TotalKnown, Finished, CancelRequested;
        public string Status = "running";
        public string? FilePath;

        public Job(string id,string kind,CancellationToken token)
        {
            Id=id;Kind=kind;
            Source=CancellationTokenSource.CreateLinkedTokenSource(token);
        }
    }

    // Keyed by authenticated session nonce, NEVER by mutable displayed name.
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private const int MaxTrackedSessions = 128;

    /// <summary>One active export per session; concurrent tabs may not overwrite it.</summary>
    public bool TryBegin(string sessionNonce,string jobId,string kind,
        CancellationToken cancellation,out ExportRun? run)
    {
        run=null;
        if(string.IsNullOrWhiteSpace(sessionNonce)||string.IsNullOrWhiteSpace(jobId)
            ||string.IsNullOrWhiteSpace(kind)) return false;
        var next = new Job(jobId,kind,cancellation);
        while(true)
        {
            if(_jobs.TryGetValue(sessionNonce,out var existing))
            {
                lock(existing.Gate)
                {
                    if(!existing.Finished)
                    {
                        next.Source.Dispose();
                        return false;
                    }
                }
                if(_jobs.TryUpdate(sessionNonce,next,existing))
                {
                    existing.Source.Dispose();
                    run=new ExportRun(this,sessionNonce,next);
                    return true;
                }
                continue;
            }
            // The live authenticated session limit is lower than this cap. Do
            // not accumulate an unbounded history of completed export metadata.
            if(_jobs.Count >= MaxTrackedSessions)
            {
                foreach(var pair in _jobs.ToArray())
                {
                    lock(pair.Value.Gate)
                    {
                        if(!pair.Value.Finished) continue;
                        if(_jobs.TryRemove(new KeyValuePair<string,Job>(pair.Key,pair.Value)))
                            pair.Value.Source.Dispose();
                    }
                }
                if(_jobs.Count >= MaxTrackedSessions)
                {
                    next.Source.Dispose();
                    return false;
                }
            }
            if(_jobs.TryAdd(sessionNonce,next))
            {
                run=new ExportRun(this,sessionNonce,next);
                return true;
            }
        }
    }

    public BridgeResponse Get(string sessionNonce)
    {
        if(!_jobs.TryGetValue(sessionNonce,out var job))
            return BridgeHelpers.Error(BridgeErrorCodes.MissingData,
                "لا توجد عملية تصدير لهذه الجلسة.");
        lock(job.Gate)
        {
            int? percentage=job.Status=="completed"?100:
                job.TotalKnown && job.Total>0?
                    Math.Min(99,job.Processed*100/job.Total):null;
            return BridgeHelpers.Ok(new JsonObject
            {
                ["jobId"]=job.Id,["kind"]=job.Kind,["status"]=job.Status,
                ["processed"]=job.Processed,["total"]=job.TotalKnown?job.Total:null,
                ["percentage"]=percentage,["completed"]=job.Status=="completed",
                ["cancelRequested"]=job.CancelRequested,
                ["startedAtUtc"]=job.StartedAtUtc.ToString("O"),
                ["filePath"]=job.Status=="completed"?job.FilePath:null
            });
        }
    }

    /// <summary>Cancellation is only requested. It is NOT reported as canceled
    /// until the writer observes the signal and removes its .part file.</summary>
    public BridgeResponse Cancel(string sessionNonce)
    {
        if(!_jobs.TryGetValue(sessionNonce,out var job))
            return BridgeHelpers.Error(BridgeErrorCodes.MissingData,
                "لا توجد عملية تصدير لهذه الجلسة.");
        lock(job.Gate)
        {
            if(job.Finished) return BridgeHelpers.Error(BridgeErrorCodes.MissingData,
                "انتهت عملية التصدير؛ لا يمكن إلغاؤها بعد الحفظ.");
            job.CancelRequested=true;
            job.Status="canceling";
            // Cancellation callback cannot re-enter coordinator while Gate held;
            // CancellationTokenSource.Cancel is safe here because XLSX writers
            // have no callbacks that mutate the coordinator synchronously.
            job.Source.Cancel();
            return BridgeHelpers.Ok(new JsonObject
            {
                ["jobId"]=job.Id,["cancelRequested"]=true,["completed"]=false
            });
        }
    }

    public sealed class ExportRun : IDisposable
    {
        private readonly ExportJobCoordinator _owner;
        private readonly string _session;
        private readonly Job _job;

        private ExportRun(ExportJobCoordinator owner,string session,Job job)
        { _owner=owner;_session=session;_job=job; }
        public CancellationToken Token => _job.Source.Token;

        public void Report(int processed,int total)
        {
            lock(_job.Gate)
            {
                if(_job.Finished)return;
                if(total>0)
                {
                    _job.TotalKnown=true;
                    _job.Total=Math.Max(_job.Total,total);
                    _job.Processed=Math.Clamp(Math.Max(_job.Processed,processed),0,_job.Total);
                }
            }
        }

        public void Finish(bool success,string? filePath=null)
        {
            lock(_job.Gate)
            {
                if(_job.Finished || !_owner._jobs.TryGetValue(_session,out var active)
                    || !ReferenceEquals(active,_job))return;
                _job.Finished=true;
                _job.Status=success?"completed":
                    _job.Source.IsCancellationRequested?"canceled":"failed";
                _job.FilePath=success?filePath:null;
            }
        }

        public void Dispose()=>Finish(false);
    }
}
