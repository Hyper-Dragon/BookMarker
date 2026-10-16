using System.Text;
using System.Text.Json.Serialization;
using BookMarker.Core.Configuration;
using BookMarker.Core.Models;
using BookMarker.Core.Services;
using BookMarker.Web;
using Microsoft.AspNetCore.Http.Features;

var options = BookMarkerOptions.FromEnvironment();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<JobRegistry>();
builder.Services.AddSingleton<MarkerRunner>();
builder.Services.AddSingleton<ConversionCoordinator>();
builder.Services.AddOpenApi();

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss.fff ";
});
builder.Logging.SetMinimumLevel(
    Enum.TryParse<LogLevel>(options.LogLevel, ignoreCase: true, out var level) ? level : LogLevel.Information);
// Keep the console useful during conversions: framework request logs only when something goes wrong.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = options.MaxUploadBytes);
builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = options.MaxUploadBytes);

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

logger.LogInformation(
    "{Name} {Version} starting: port={Port} concurrency={Concurrency} workers={Workers} llmConfigured={LlmConfigured} llmModel={LlmModel} scratchRoot={ScratchRoot}",
    BookMarkerInfo.Name, BookMarkerInfo.Version, options.Port, BookMarkerOptions.Concurrency,
    options.Workers, options.LlmConfigured, options.LlmConfigured ? options.LlmModel : "(none)", options.ScratchRoot);

app.MapOpenApi();

// ---------------------------------------------------------------------------
// GET /health - always answers, including while a conversion is running.
// ---------------------------------------------------------------------------
app.MapGet("/health", (ConversionCoordinator coordinator) => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTimeOffset.UtcNow,
    service = BookMarkerInfo.Name,
    version = BookMarkerInfo.Version,
    engine = BookMarkerInfo.Engine,
    commit = BookMarkerInfo.Commit,
    busy = coordinator.IsBusy,
    concurrency = new { max = BookMarkerOptions.Concurrency, available = coordinator.IsBusy ? 0 : 1 },
}));

// ---------------------------------------------------------------------------
// GET / - read-only status page (free/busy + live processing details).
// ---------------------------------------------------------------------------
app.MapGet("/", () => Results.Content(StatusPage.Html, "text/html"));

app.MapGet("/api/status", (JobRegistry registry) =>
{
    var (current, recent, completed, failed) = registry.Snapshot();
    Array.Reverse(recent); // newest first
    return Results.Ok(new
    {
        service = BookMarkerInfo.Name,
        version = BookMarkerInfo.Version,
        engine = BookMarkerInfo.Engine,
        commit = BookMarkerInfo.Commit,
        description = BookMarkerInfo.Description,
        state = current is null ? "free" : "busy",
        startedAt = registry.StartedAt,
        uptimeSeconds = (long)(DateTimeOffset.UtcNow - registry.StartedAt).TotalSeconds,
        totals = new { completed, failed },
        config = new
        {
            concurrency = BookMarkerOptions.Concurrency,
            port = options.Port,
            workers = options.Workers,
            disableMultiprocessing = options.DisableMultiprocessing,
            disableImageExtraction = options.DisableImageExtraction,
            llmConfigured = options.LlmConfigured,
            llmModel = options.LlmConfigured ? options.LlmModel : null,
            llmTimeoutSeconds = options.LlmTimeoutSeconds,
            maxTimeMs = options.MaxTimeMs,
            maxUploadBytes = options.MaxUploadBytes,
            busyRetryAfterSeconds = options.BusyRetryAfterSeconds,
            scratchRoot = options.ScratchRoot,
            markerBinary = options.MarkerBinary,
        },
        current,
        recent,
    });
});

// ---------------------------------------------------------------------------
// POST /convert - multipart (file + optional page_range + optional llm) or a raw
// PDF body with the same fields as query parameters.
//   llm: nollm (default) | llmonly | llmfallback   [tri-state, per request]
// ---------------------------------------------------------------------------
app.MapPost("/convert", async (HttpContext http, ConversionCoordinator coordinator) =>
{
    var traceId = http.Request.Headers["X-Trace-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(traceId))
    {
        traceId = Guid.NewGuid().ToString("N");
    }

    var ct = http.RequestAborted;

    // Capacity refusal is immediate and does not read the body (deliberate backpressure:
    // callers requeue on 503; the request is never held or queued).
    if (coordinator.IsBusy)
    {
        return Busy(options, traceId, http);
    }

    string? pageRange = null;
    string? llmRaw = null;
    IFormFile? formFile = null;
    Stream? rawBody = null;

    var jobDir = Path.Combine(options.ScratchRoot, traceId);
    var committed = false;

    try
    {
        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(ct);
            formFile = form.Files.FirstOrDefault();
            pageRange = form["page_range"].ToString();
            llmRaw = form["llm"].ToString();
        }
        else
        {
            if (http.Request.ContentLength is > 0 and var declared && declared > options.MaxUploadBytes)
            {
                return TooLarge(options, traceId);
            }

            pageRange = http.Request.Query["page_range"].ToString();
            llmRaw = http.Request.Query["llm"].ToString();
            rawBody = http.Request.Body;
        }

        pageRange = string.IsNullOrWhiteSpace(pageRange) ? null : pageRange.Trim();

        var llmMode = LlmModeParser.Parse(llmRaw);
        if (llmMode is null)
        {
            return BadRequestJson($"invalid llm value '{llmRaw}' (expected one of: nollm, llmonly, llmfallback)", traceId);
        }

        if (llmMode == LlmMode.LlmOnly && !options.LlmConfigured)
        {
            return Results.Json(new ApiError
            {
                Errors = ["llm_unavailable: request asked for llmonly but no LLM backend is configured (OPENAI_BASE_URL / OPENAI_MODEL)"],
                TraceId = traceId,
            }, statusCode: StatusCodes.Status500InternalServerError);
        }

        // ---- spool the upload into this service's own scratch workspace ----
        var inputDir = Path.Combine(jobDir, "in");
        Directory.CreateDirectory(inputDir);

        string fileName;
        Stream source;
        if (formFile is not null)
        {
            if (formFile.Length == 0)
            {
                return BadRequestJson("empty file", traceId);
            }

            if (formFile.Length > options.MaxUploadBytes)
            {
                return TooLarge(options, traceId);
            }

            fileName = SanitizeFileName(formFile.FileName);
            source = formFile.OpenReadStream();
        }
        else
        {
            fileName = "document.pdf";
            source = rawBody!;
        }

        var inputPath = Path.Combine(inputDir, fileName);
        long bytes = 0;
        var magic = string.Empty;
        await using (var destination = File.Create(inputPath))
        {
            var buffer = new byte[128 * 1024];
            var firstChunk = true;
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }

                if (firstChunk)
                {
                    firstChunk = false;
                    magic = Encoding.ASCII.GetString(buffer, 0, Math.Min(read, 1024));
                }

                bytes += read;
                if (bytes > options.MaxUploadBytes)
                {
                    return TooLarge(options, traceId);
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        if (bytes == 0)
        {
            return BadRequestJson("empty file", traceId);
        }

        if (!magic.Contains("%PDF-", StringComparison.Ordinal))
        {
            return Results.Json(new ApiError
            {
                Errors = ["uploaded content is not a PDF (no %PDF header found)"],
                TraceId = traceId,
            }, statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        // ---- run it: exactly one conversion at a time; refused => 503 ----
        var job = new JobRecord
        {
            TraceId = traceId,
            FileName = fileName,
            FileBytes = bytes,
            PageRange = pageRange,
            LlmMode = llmMode.Value,
            StartedAt = DateTimeOffset.UtcNow,
            JobDir = jobDir,
        };

        var (accepted, outcome) = await coordinator.TryConvertAsync(job, ct);
        committed = accepted; // once accepted, the coordinator owns (and deletes) the scratch dir

        if (!accepted)
        {
            return Busy(options, traceId, http);
        }

        http.Response.Headers["X-Trace-Id"] = traceId;

        if (outcome is not { Success: true })
        {
            return Results.Json(new ApiError
            {
                Errors = outcome?.Errors ?? ["conversion failed"],
                TraceId = traceId,
                LogTail = ClampTail(outcome?.LogTail),
            }, statusCode: StatusCodes.Status500InternalServerError);
        }

        http.Response.Headers["X-Pages"] = outcome.Pages.ToString();
        http.Response.Headers["X-Elapsed-Ms"] = outcome.ElapsedMs.ToString();
        if (outcome.SecPerPage is { } secPerPage)
        {
            http.Response.Headers["X-Sec-Per-Page"] = secPerPage.ToString("0.###");
        }

        http.Response.Headers["X-Llm-Mode"] = llmMode.Value.ToWire();
        http.Response.Headers["X-Llm-Used"] = outcome.LlmUsed ? "llm" : "none";
        http.Response.Headers["X-Llm-Fallback"] = outcome.LlmFellBack ? "true" : "false";

        return Results.Text(outcome.Markdown ?? string.Empty, "text/markdown", Encoding.UTF8);
    }
    catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
    {
        return TooLarge(options, traceId);
    }
    catch (BadHttpRequestException ex)
    {
        return BadRequestJson($"bad request: {ex.Message}", traceId);
    }
    catch (OperationCanceledException)
    {
        return Results.Empty;
    }
    finally
    {
        // in -> process -> delete / in -> fail -> delete: scratch never survives its job.
        if (!committed)
        {
            TryDelete(jobDir);
        }
    }
});

// Configure the port (only override when ASPNETCORE_URLS isn't set).
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(urls))
{
    app.Urls.Clear();
    app.Urls.Add($"http://0.0.0.0:{options.Port}");
    logger.LogInformation("{Name} listening on http://0.0.0.0:{Port}", BookMarkerInfo.Name, options.Port);
}

app.Run();

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

static IResult Busy(BookMarkerOptions options, string traceId, HttpContext http)
{
    http.Response.Headers.RetryAfter = options.BusyRetryAfterSeconds.ToString();
    return Results.Json(new ApiError
    {
        Errors = [$"Service at capacity (concurrency {BookMarkerOptions.Concurrency}); retry after {options.BusyRetryAfterSeconds}s"],
        TraceId = traceId,
    }, statusCode: StatusCodes.Status503ServiceUnavailable);
}

static IResult TooLarge(BookMarkerOptions options, string traceId) =>
    Results.Json(new ApiError
    {
        Errors = [$"upload exceeds the maximum accepted size ({options.MaxUploadBytes} bytes)"],
        TraceId = traceId,
    }, statusCode: StatusCodes.Status413PayloadTooLarge);

static IResult BadRequestJson(string message, string traceId) =>
    Results.Json(new ApiError
    {
        Errors = [message],
        TraceId = traceId,
    }, statusCode: StatusCodes.Status400BadRequest);

static void TryDelete(string dir)
{
    try
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
    catch
    {
        // best effort
    }
}

static string? ClampTail(string? tail)
{
    const int max = 8000;
    return tail is null || tail.Length <= max ? tail : tail[^max..];
}

static string SanitizeFileName(string? name)
{
    name = Path.GetFileName(name ?? string.Empty);
    var sb = new StringBuilder(name.Length);
    foreach (var ch in name)
    {
        if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or ' ' or '(' or ')' or '[' or ']')
        {
            sb.Append(ch);
        }
    }

    var cleaned = sb.ToString().Trim();
    if (cleaned.Length == 0)
    {
        cleaned = "document.pdf";
    }

    return cleaned.Length > 120 ? cleaned[..120] : cleaned;
}

/// <summary>Exposed so the test host can start the real application pipeline.</summary>
public partial class Program
{
}
