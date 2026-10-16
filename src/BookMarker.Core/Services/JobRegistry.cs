using BookMarker.Core.Models;

namespace BookMarker.Core.Services;

/// <summary>In-memory registry of the running job + recent job history (status page source).</summary>
public sealed class JobRegistry
{
    public const int MaxRecent = 25;
    public const int MaxProgressLines = 60;

    private readonly object _lock = new();
    private readonly LinkedList<JobRecord> _recent = new();
    private JobRecord? _current;
    private long _completed;
    private long _failed;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void Begin(JobRecord job)
    {
        lock (_lock)
        {
            _current = job;
        }
    }

    /// <summary>Mutates a job record under the registry lock (safe for concurrent status reads).</summary>
    public void Update(JobRecord job, Action<JobRecord> mutate)
    {
        lock (_lock)
        {
            mutate(job);
        }
    }

    /// <summary>Appends a marker output line to the job's capped ring buffer.</summary>
    public void AppendLine(JobRecord job, string line)
    {
        lock (_lock)
        {
            job.ProgressLines.Add(line);
            if (job.ProgressLines.Count > MaxProgressLines)
            {
                job.ProgressLines.RemoveRange(0, job.ProgressLines.Count - MaxProgressLines);
            }

            job.LastProgress = line;
        }
    }

    public void Finish(JobRecord job, bool success)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_current, job))
            {
                _current = null;
            }

            job.FinishedAt = DateTimeOffset.UtcNow;
            job.State = success ? "succeeded" : "failed";
            if (success)
            {
                _completed++;
            }
            else
            {
                _failed++;
            }

            _recent.AddLast(job);
            while (_recent.Count > MaxRecent)
            {
                _recent.RemoveFirst();
            }
        }
    }

    public (JobRecord? Current, JobRecord[] Recent, long Completed, long Failed) Snapshot()
    {
        lock (_lock)
        {
            return (_current, _recent.ToArray(), _completed, _failed);
        }
    }
}
