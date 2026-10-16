using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookMarker.Core.Configuration;
using BookMarker.Core.Models;
using Microsoft.Extensions.Logging;

namespace BookMarker.Core.Services;

/// <summary>
/// Runs the vendored marker CLI (folder mode - one PDF per job folder) and translates the
/// process output into a <see cref="ConvertOutcome"/>. Preserves the proven invocation:
/// <code>
/// marker &lt;in-folder&gt; --output_dir &lt;out&gt; --workers N --disable_multiprocessing
///        --disable_image_extraction [--page_range ...]
///        [--use_llm --llm_service marker_openai_service.OpenAICompatibleService --config_json ...]
/// </code>
/// Notes: never uses --debug (marker 1.10.2 crashes at the end of every PDF with it);
/// per-request LLM policy (nollm / llmonly / llmfallback) is implemented here.
/// </summary>
public sealed partial class MarkerRunner
{
    /// <summary>
    /// Terminal shim log fragments meaning an LLM call gave up. Used to judge "the LLM path is
    /// unavailable": llmonly fails the request on these; llmfallback re-runs without the LLM.
    /// </summary>
    private static readonly string[] LlmFailureSignatures =
    [
        "giving up",
        "call failed",
        "could not parse JSON from response",
    ];

    [GeneratedRegex(@"Inferenced\s+(\d+)\s+pages\s+in\s+([\d.]+)\s+seconds", RegexOptions.IgnoreCase)]
    private static partial Regex InferencedRegex();

    [GeneratedRegex(@"\b(\d{1,4})\s*/\s*(\d{1,4})\b")]
    private static partial Regex FractionRegex();

    [GeneratedRegex(@"(page|render|layout|ocr|llm|table|equation|error|warn|traceback|exception|converting|inferenced|loading|model)", RegexOptions.IgnoreCase)]
    private static partial Regex InterestingLineRegex();

    private readonly BookMarkerOptions _options;
    private readonly JobRegistry _registry;
    private readonly ILogger<MarkerRunner> _logger;

    public MarkerRunner(BookMarkerOptions options, JobRegistry registry, ILogger<MarkerRunner> logger)
    {
        _options = options;
        _registry = registry;
        _logger = logger;
    }

    public async Task<ConvertOutcome> RunAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();

        switch (job.LlmMode)
        {
            case LlmMode.NoLlm:
                return await RunAttemptAsync(job, useLlm: false, total, cancellationToken);

            case LlmMode.LlmOnly when !_options.LlmConfigured:
                return Fail(job, total,
                    ["llm_unavailable: request asked for llmonly but no LLM backend is configured (OPENAI_BASE_URL / OPENAI_MODEL)"]);

            case LlmMode.LlmOnly:
            {
                var attempt = await RunAttemptAsync(job, useLlm: true, total, cancellationToken);
                if (attempt.Success && !attempt.LlmDegraded)
                {
                    return attempt;
                }

                var reason = attempt.LlmDegraded
                    ? "llm_unavailable: the LLM path failed during the run and llmonly never degrades silently"
                    : attempt.Errors?.FirstOrDefault() ?? "conversion failed";
                return Fail(job, total, [reason], attempt.LogTail);
            }

            case LlmMode.LlmFallback when !_options.LlmConfigured:
            {
                var attempt = await RunAttemptAsync(job, useLlm: false, total, cancellationToken);
                return attempt with
                {
                    LlmFellBack = true,
                    LlmNote = "llm not configured; ran without llm",
                };
            }

            case LlmMode.LlmFallback:
            {
                var attempt = await RunAttemptAsync(job, useLlm: true, total, cancellationToken);
                if (attempt.Success && !attempt.LlmDegraded)
                {
                    return attempt;
                }

                var why = attempt.LlmDegraded
                    ? "llm path degraded during the llm attempt"
                    : "llm attempt failed: " + string.Join("; ", attempt.Errors ?? []);
                _logger.LogWarning("[{TraceId}] llmfallback engaged ({Why}); re-running without llm", job.TraceId, why);
                _registry.Update(job, j => j.Phase = "fallback: no-llm re-run");
                DeleteOutput(job);

                var fallback = await RunAttemptAsync(job, useLlm: false, total, cancellationToken);
                return fallback with
                {
                    LlmFellBack = fallback.Success,
                    LlmNote = fallback.Success
                        ? $"{why}; result produced without llm"
                        : $"{why}; fallback run also failed",
                };
            }

            default:
                return Fail(job, total, ["unsupported llm mode"]);
        }
    }

    private async Task<ConvertOutcome> RunAttemptAsync(JobRecord job, bool useLlm, Stopwatch total, CancellationToken cancellationToken)
    {
        var inputDir = Path.Combine(job.JobDir, "in");
        var outputDir = Path.Combine(job.JobDir, "out");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        _registry.Update(job, j =>
        {
            j.Phase = useLlm ? "converting (llm)" : "converting";
            j.LlmUsed = useLlm;
        });

        var psi = new ProcessStartInfo
        {
            FileName = _options.MarkerBinary,
            WorkingDirectory = job.JobDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add(inputDir);
        psi.ArgumentList.Add("--output_dir");
        psi.ArgumentList.Add(outputDir);
        psi.ArgumentList.Add("--workers");
        psi.ArgumentList.Add(_options.Workers.ToString());
        if (_options.DisableMultiprocessing)
        {
            psi.ArgumentList.Add("--disable_multiprocessing");
        }

        if (_options.DisableImageExtraction)
        {
            psi.ArgumentList.Add("--disable_image_extraction");
        }

        if (!string.IsNullOrWhiteSpace(job.PageRange))
        {
            psi.ArgumentList.Add("--page_range");
            psi.ArgumentList.Add(job.PageRange);
        }

        if (useLlm)
        {
            var configPath = Path.Combine(job.JobDir, "marker_config.json");
            await File.WriteAllTextAsync(configPath, BuildLlmConfigJson(), cancellationToken);
            psi.ArgumentList.Add("--use_llm");
            psi.ArgumentList.Add("--llm_service");
            psi.ArgumentList.Add(BookMarkerOptions.LlmServiceClass);
            psi.ArgumentList.Add("--config_json");
            psi.ArgumentList.Add(configPath);
        }

        psi.Environment["PYTHONPATH"] = _options.ShimDir;
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        _logger.LogInformation("[{TraceId}] marker start ({Mode}): {Command}",
            job.TraceId,
            useLlm ? "llm" : "no-llm",
            string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

        var capture = new LogCapture();
        void OnLine(string line)
        {
            capture.Add(line);
            _registry.AppendLine(job, line);

            if (InferencedRegex().Match(line) is { Success: true } inf)
            {
                if (int.TryParse(inf.Groups[1].Value, out var pages))
                {
                    _registry.Update(job, j => j.Pages = pages);
                }
            }

            if (line.Contains("page", StringComparison.OrdinalIgnoreCase) &&
                FractionRegex().Match(line) is { Success: true } frac &&
                int.TryParse(frac.Groups[1].Value, out var done) &&
                int.TryParse(frac.Groups[2].Value, out var pageTotal) &&
                pageTotal >= done && pageTotal > 0 && pageTotal <= 100_000)
            {
                _registry.Update(job, j =>
                {
                    j.PagesDone = done;
                    j.PagesTotal = pageTotal;
                });
            }

            if (InterestingLineRegex().IsMatch(line))
            {
                _logger.LogInformation("[{TraceId}] marker: {Line}", job.TraceId, line);
            }
            else
            {
                _logger.LogDebug("[{TraceId}] marker: {Line}", job.TraceId, line);
            }
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return Fail(job, total, ["failed to start the marker process"]);
        }

        var stdoutTask = PumpAsync(process.StandardOutput, OnLine);
        var stderrTask = PumpAsync(process.StandardError, OnLine);

        using var timeoutCts = new CancellationTokenSource();
        if (_options.MaxTimeMs > 0)
        {
            timeoutCts.CancelAfter(_options.MaxTimeMs);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        string? cancelledBy = null;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelledBy = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                ? $"timed out after {_options.MaxTimeMs} ms"
                : "cancelled by caller (client disconnected)";
            TryKill(process);
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        var logTail = capture.Tail(40);

        if (cancelledBy is not null)
        {
            return Fail(job, total, [$"conversion {cancelledBy}"], logTail);
        }

        if (process.ExitCode != 0)
        {
            return Fail(job, total, [$"marker exited with code {process.ExitCode}"], logTail);
        }

        var markdownPath = FindMarkdown(outputDir);
        if (markdownPath is null)
        {
            return Fail(job, total, ["marker produced no markdown output"], logTail);
        }

        string markdown;
        try
        {
            markdown = await File.ReadAllTextAsync(markdownPath, CancellationToken.None);
        }
        catch (IOException ex)
        {
            return Fail(job, total, [$"could not read markdown output: {ex.Message}"], logTail);
        }

        var pages = job.Pages;
        if (pages <= 0 && !string.IsNullOrWhiteSpace(job.PageRange))
        {
            pages = CountPageRange(job.PageRange);
        }

        var degraded = capture.ContainsAny(LlmFailureSignatures);
        var secPerPage = pages > 0 ? Math.Round(total.Elapsed.TotalSeconds / pages, 3) : (double?)null;

        _registry.Update(job, j =>
        {
            j.ResultChars = markdown.Length;
            j.SecPerPage = secPerPage;
        });

        return new ConvertOutcome
        {
            Success = true,
            Markdown = markdown,
            Pages = pages,
            ElapsedMs = total.ElapsedMilliseconds,
            SecPerPage = secPerPage,
            RequestedLlmMode = job.LlmMode,
            LlmUsed = useLlm,
            LlmDegraded = degraded,
            LogTail = logTail,
        };
    }

    private ConvertOutcome Fail(JobRecord job, Stopwatch total, string[] errors, string? logTail = null)
    {
        _registry.Update(job, j => j.Error = string.Join("; ", errors));
        return new ConvertOutcome
        {
            Success = false,
            Pages = job.Pages,
            ElapsedMs = total.ElapsedMilliseconds,
            RequestedLlmMode = job.LlmMode,
            LlmUsed = job.LlmUsed,
            Errors = errors,
            LogTail = logTail,
        };
    }

    private string BuildLlmConfigJson()
    {
        var config = new Dictionary<string, object?>
        {
            ["openai_base_url"] = _options.LlmBaseUrl,
            ["openai_model"] = _options.LlmModel,
            ["openai_api_key"] = _options.LlmApiKey ?? "",
            ["timeout"] = _options.LlmTimeoutSeconds,
            ["max_retries"] = _options.LlmMaxRetries,
            ["retry_wait_time"] = _options.LlmRetryWaitSeconds,
        };
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            onLine(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // already exited
        }
    }

    private static string? FindMarkdown(string outputDir)
    {
        if (!Directory.Exists(outputDir))
        {
            return null;
        }

        var files = Directory.GetFiles(outputDir, "*.md", SearchOption.AllDirectories);
        return files.Length == 0
            ? null
            : files.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    /// <summary>Page count covered by a marker page_range string ("0,5-10,20" - 0-based, inclusive).</summary>
    internal static int CountPageRange(string pageRange)
    {
        var count = 0;
        foreach (var part in pageRange.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part[..dash], out var start) &&
                    int.TryParse(part[(dash + 1)..], out var end) && end >= start)
                {
                    count += end - start + 1;
                }
            }
            else if (int.TryParse(part, out _))
            {
                count++;
            }
        }

        return count;
    }

    private void DeleteOutput(JobRecord job)
    {
        var outputDir = Path.Combine(job.JobDir, "out");
        try
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{TraceId}] could not clear the output dir for the fallback re-run", job.TraceId);
        }
    }

    /// <summary>Thread-safe capped capture of the marker output (tail + failure signature scan).</summary>
    private sealed class LogCapture
    {
        private const int MaxLines = 5000;
        private readonly object _lock = new();
        private readonly List<string> _lines = [];

        public void Add(string line)
        {
            lock (_lock)
            {
                if (_lines.Count < MaxLines)
                {
                    _lines.Add(line);
                }
            }
        }

        public string Tail(int count)
        {
            lock (_lock)
            {
                return string.Join('\n', _lines.Skip(Math.Max(0, _lines.Count - count)));
            }
        }

        public bool ContainsAny(IReadOnlyList<string> fragments)
        {
            lock (_lock)
            {
                foreach (var line in _lines)
                {
                    foreach (var fragment in fragments)
                    {
                        if (line.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
