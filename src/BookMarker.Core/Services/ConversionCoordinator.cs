using BookMarker.Core.Models;
using Microsoft.Extensions.Logging;

namespace BookMarker.Core.Services;

/// <summary>
/// Owns the single conversion slot. Exactly one conversion runs at a time; a request that
/// arrives while the slot is taken is refused immediately (TryConvertAsync returns
/// Accepted=false and the web layer answers 503 + Retry-After) - never queued, never held.
///
/// Submitted files are deleted when the job ends, success or failure alike:
///   in -> process -> delete        in -> fail -> delete
/// Failed conversions are handled upstream, so nothing is retained for reprocessing.
/// </summary>
public sealed class ConversionCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JobRegistry _registry;
    private readonly MarkerRunner _runner;
    private readonly ILogger<ConversionCoordinator> _logger;

    public ConversionCoordinator(JobRegistry registry, MarkerRunner runner, ILogger<ConversionCoordinator> logger)
    {
        _registry = registry;
        _runner = runner;
        _logger = logger;
    }

    public bool IsBusy => _gate.CurrentCount == 0;

    public async Task<(bool Accepted, ConvertOutcome? Outcome)> TryConvertAsync(JobRecord job, CancellationToken cancellationToken)
    {
        if (!_gate.Wait(0))
        {
            return (false, null);
        }

        try
        {
            _registry.Begin(job);
            ConvertOutcome outcome;
            try
            {
                outcome = await _runner.RunAsync(job, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _registry.Update(job, j => j.Error = "conversion cancelled (client disconnected)");
                outcome = new ConvertOutcome
                {
                    Success = false,
                    RequestedLlmMode = job.LlmMode,
                    LlmUsed = job.LlmUsed,
                    Errors = ["conversion cancelled (client disconnected)"],
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{TraceId}] unexpected conversion failure", job.TraceId);
                _registry.Update(job, j => j.Error = $"unexpected service error: {ex.Message}");
                outcome = new ConvertOutcome
                {
                    Success = false,
                    RequestedLlmMode = job.LlmMode,
                    LlmUsed = job.LlmUsed,
                    Errors = [$"unexpected service error: {ex.Message}"],
                };
            }

            _registry.Finish(job, outcome.Success);
            LogSummary(job, outcome);
            return (true, outcome);
        }
        finally
        {
            DeleteScratch(job);
            _gate.Release();
        }
    }

    private void LogSummary(JobRecord job, ConvertOutcome outcome)
    {
        var secPerPage = outcome.SecPerPage is { } value ? $"{value:0.###}" : "n/a";
        _logger.LogInformation(
            "[{TraceId}] job {State}: file={File} bytes={Bytes} llm={LlmMode} llmUsed={LlmUsed} fellBack={FellBack} pages={Pages} wall={Wall:0.0}s s/page={SecPerPage} resultChars={Chars}{Error}",
            job.TraceId,
            job.State,
            job.FileName,
            job.FileBytes,
            job.LlmMode.ToWire(),
            outcome.LlmUsed,
            outcome.LlmFellBack,
            outcome.Pages,
            job.ElapsedSeconds,
            secPerPage,
            job.ResultChars,
            outcome.Success ? string.Empty : $" error={string.Join("; ", outcome.Errors ?? [])}");
    }

    private void DeleteScratch(JobRecord job)
    {
        try
        {
            if (Directory.Exists(job.JobDir))
            {
                Directory.Delete(job.JobDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{TraceId}] could not delete scratch dir {Dir}", job.TraceId, job.JobDir);
        }
    }
}
