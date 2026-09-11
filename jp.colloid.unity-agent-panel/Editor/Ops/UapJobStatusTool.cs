using System;
using System.Collections.Generic;
using System.Globalization;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Fetches the state and result of a tools/call that outlived its HTTP
    /// waiter (design note 2026-09-09-jobs-and-destructive-confirm section
    /// 1). When the dispatcher reports "still running on the Unity main
    /// thread" it now names a job id; this tool returns that job's result
    /// once the main thread finishes it, optionally waiting up to
    /// <see cref="MaxWaitMillis"/> for it. Runs OFF the main thread
    /// (<see cref="IUapOffThreadTool"/>) so it answers even while the
    /// Editor is blocked by the job itself -- which is precisely when the
    /// agent wants to ask. Read-only: it consults the ledger and nothing
    /// else.
    /// </summary>
    public sealed class UapJobStatusTool : IUapOffThreadTool
    {
        public const string ToolName = "uap_job_status";

        /// <summary>
        /// Upper bound for wait_ms. Below the dispatcher's own 15 s budget
        /// and well below any CLI-side HTTP timeout, so a waiting status
        /// call can never itself become a "still running" failure.
        /// </summary>
        public const int MaxWaitMillis = 10000;

        private readonly UapJobLedger _ledger;

        public UapJobStatusTool() : this(null)
        {
        }

        public UapJobStatusTool(UapJobLedger ledger)
        {
            _ledger = ledger ?? UapJobLedger.Shared;
        }

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Gets the state and result of a uap_* call that outlived its wait -- the"
                    + " \"still running on the Unity main thread\" failure names a job id; pass it as"
                    + " job_id to fetch that call's own result once it finishes (state succeeded /"
                    + " failed / running). wait_ms (up to " + MaxWaitMillis + ") blocks that long for"
                    + " completion. Answers even while the Editor is blocked. Omit job_id to list"
                    + " every job kept in this Editor session (cleared by a domain reload).";
            }
        }

        public string Module
        {
            get { return "core"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        /// <summary>True: reads the ledger, changes nothing.</summary>
        public bool ReadOnly
        {
            get { return true; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("job_id", JsonNode.NewObject()
                            .Set("type", "string")
                            .Set("description", "Job id quoted by a \"still running on the Unity main thread\""
                                + " failure. Omit to list all jobs."))
                        .Set("wait_ms", JsonNode.NewObject()
                            .Set("type", "integer")
                            .Set("minimum", 0)
                            .Set("maximum", MaxWaitMillis)
                            .Set("description", "Milliseconds to wait for the job to complete before"
                                + " answering (default 0 = answer immediately; max " + MaxWaitMillis + ").")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            if (input == null)
            {
                input = JsonNode.NewObject();
            }
            string jobId = input["job_id"].AsString(null);
            if (string.IsNullOrEmpty(jobId))
            {
                return ListAll();
            }
            UapJob job = _ledger.Find(jobId);
            if (job == null)
            {
                throw new InvalidOperationException("Unknown job '" + jobId + "'. Jobs are kept only for the"
                    + " current Editor session (at most " + UapJobLedger.Capacity + ", oldest completed"
                    + " first to go) and are cleared by a domain reload; if the call that produced this"
                    + " id triggered a recompile, verify its effect with a query tool instead.");
            }
            int waitMillis = input["wait_ms"].AsInt(0);
            if (waitMillis < 0)
            {
                waitMillis = 0;
            }
            if (waitMillis > MaxWaitMillis)
            {
                waitMillis = MaxWaitMillis;
            }
            job.WaitForCompletion(waitMillis);
            return UapToolResults.Text(JsonWriter.Write(Describe(job, true)));
        }

        private JsonNode ListAll()
        {
            List<UapJob> jobs = _ledger.List();
            JsonNode array = JsonNode.NewArray();
            for (int i = 0; i < jobs.Count; i++)
            {
                array.Add(Describe(jobs[i], false));
            }
            JsonNode body = JsonNode.NewObject()
                .Set("jobs", array)
                .Set("note", jobs.Count == 0
                    ? "No jobs are recorded in this Editor session. A job is recorded only when a uap_*"
                      + " call outlives its wait while already running on the main thread; the record is"
                      + " cleared by a domain reload."
                    : "Newest first. Pass a jobId as job_id to get that job's result.");
            return UapToolResults.Text(JsonWriter.Write(body));
        }

        /// <summary>Pure description of one job; public so the exact shape is testable without a dispatcher.</summary>
        public static JsonNode Describe(UapJob job, bool includeResult)
        {
            UapJobState state = job.State;
            JsonNode node = JsonNode.NewObject()
                .Set("jobId", job.Id)
                .Set("tool", job.ToolName)
                .Set("state", StateWord(state))
                .Set("queuedUtc", job.QueuedUtc.ToString("o", CultureInfo.InvariantCulture));
            if (job.StartedUtc != null)
            {
                node.Set("startedUtc", job.StartedUtc.Value.ToString("o", CultureInfo.InvariantCulture));
                DateTime end = job.FinishedUtc ?? DateTime.UtcNow;
                node.Set("durationSeconds", Math.Round((end - job.StartedUtc.Value).TotalSeconds, 3));
            }
            if (job.FinishedUtc != null)
            {
                node.Set("finishedUtc", job.FinishedUtc.Value.ToString("o", CultureInfo.InvariantCulture));
            }
            switch (state)
            {
                case UapJobState.Succeeded:
                    if (includeResult)
                    {
                        node.Set("result", job.Result ?? JsonNode.NewArray());
                    }
                    break;
                case UapJobState.Failed:
                    node.Set("error", job.Error ?? string.Empty);
                    break;
                default:
                    node.Set("note", "Still running on the Unity main thread; call again (wait_ms up to "
                        + MaxWaitMillis + ") -- do not re-issue the original call.");
                    break;
            }
            return node;
        }

        public static string StateWord(UapJobState state)
        {
            switch (state)
            {
                case UapJobState.Succeeded:
                    return "succeeded";
                case UapJobState.Failed:
                    return "failed";
                case UapJobState.Running:
                    return "running";
                default:
                    return "queued";
            }
        }
    }
}
