using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace BookMarker.Tests;

/// <summary>
/// The service contract from SPEC.md, exercised through the real pipeline against the
/// marker stand-in: status codes, headers, LLM policy, backpressure and scratch cleanup.
/// </summary>
public sealed class ConvertEndpointTests
{
    private const string PdfBody =
        "%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<<>>\n%%EOF\n";

    private static MultipartFormDataContent PdfForm(
        string? llm = null,
        string? pageRange = null,
        string fileName = "sample.pdf")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes(PdfBody));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", fileName);

        if (llm is not null)
        {
            content.Add(new StringContent(llm), "llm");
        }

        if (pageRange is not null)
        {
            content.Add(new StringContent(pageRange), "page_range");
        }

        return content;
    }

    private static ByteArrayContent RawBody(string body, string mediaType = "application/octet-stream")
    {
        var content = new ByteArrayContent(Encoding.ASCII.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        HttpContent content,
        string? traceId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/convert") { Content = content };
        if (traceId is not null)
        {
            request.Headers.Add("X-Trace-Id", traceId);
        }

        return await client.SendAsync(request);
    }

    // ---- identity -----------------------------------------------------------

    [Fact]
    public async Task Health_answers_when_idle_and_names_the_engine_it_packages()
    {
        using var host = new ServiceHost();
        using var response = await host.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var health = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", health.GetProperty("status").GetString());
        Assert.Equal("BookMarker", health.GetProperty("service").GetString());
        Assert.False(health.GetProperty("busy").GetBoolean());
        Assert.StartsWith("0.1.0", health.GetProperty("version").GetString());
        Assert.StartsWith("marker-pdf", health.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task Status_reports_free_state_and_the_engine()
    {
        using var host = new ServiceHost();
        using var response = await host.Client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("free", status.GetProperty("state").GetString());
        Assert.StartsWith("marker-pdf", status.GetProperty("engine").GetString());
        Assert.Equal(1, status.GetProperty("config").GetProperty("concurrency").GetInt32());
    }

    // ---- happy path ---------------------------------------------------------

    [Fact]
    public async Task Convert_returns_markdown_and_the_documented_headers()
    {
        using var host = new ServiceHost();
        using var response = await PostAsync(host.Client, PdfForm(), "trace-success");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("trace-success", response.Headers.GetValues("X-Trace-Id").Single());
        Assert.Equal("3", response.Headers.GetValues("X-Pages").Single());
        Assert.Equal("nollm", response.Headers.GetValues("X-Llm-Mode").Single());
        Assert.Equal("none", response.Headers.GetValues("X-Llm-Used").Single());
        Assert.Equal("false", response.Headers.GetValues("X-Llm-Fallback").Single());
        Assert.True(response.Headers.Contains("X-Elapsed-Ms"));

        Assert.Contains("Converted by the marker stub", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Convert_generates_a_trace_id_when_the_caller_does_not_supply_one()
    {
        using var host = new ServiceHost();
        using var response = await PostAsync(host.Client, PdfForm());

        var traceId = response.Headers.GetValues("X-Trace-Id").Single();
        Assert.Equal(32, traceId.Length);
        Assert.True(traceId.All(Uri.IsHexDigit));
    }

    [Fact]
    public async Task Page_range_page_count_is_used_when_the_engine_does_not_report_one()
    {
        using var host = new ServiceHost(("STUB_PAGES", "0"));
        using var response = await PostAsync(host.Client, PdfForm(pageRange: "0-2"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("3", response.Headers.GetValues("X-Pages").Single());
    }

    // ---- retention ----------------------------------------------------------

    [Fact]
    public async Task Scratch_is_deleted_after_a_successful_job()
    {
        using var host = new ServiceHost();
        using var response = await PostAsync(host.Client, PdfForm(), "trace-cleanup");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(host.ScratchRoot, "trace-cleanup")));
    }

    [Fact]
    public async Task Scratch_is_deleted_after_a_failed_job()
    {
        using var host = new ServiceHost(("STUB_EXIT_CODE", "1"));
        using var response = await PostAsync(host.Client, PdfForm(), "trace-failed-scratch");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(host.ScratchRoot, "trace-failed-scratch")));
    }

    // ---- engine failure -----------------------------------------------------

    [Fact]
    public async Task Engine_failure_is_500_and_carries_the_log_tail()
    {
        using var host = new ServiceHost(("STUB_EXIT_CODE", "1"));
        using var response = await PostAsync(host.Client, PdfForm());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("marker exited with code 1", body);
        Assert.Contains("logTail", body);
    }

    [Fact]
    public async Task Missing_engine_output_is_500_rather_than_an_empty_200()
    {
        using var host = new ServiceHost(("STUB_NO_OUTPUT", "1"));
        using var response = await PostAsync(host.Client, PdfForm());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("marker produced no markdown output", await response.Content.ReadAsStringAsync());
    }

    // ---- request validation -------------------------------------------------

    [Fact]
    public async Task Content_without_a_pdf_header_is_415()
    {
        using var host = new ServiceHost();
        using var response = await PostAsync(host.Client, RawBody("this is not a pdf"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("not a PDF", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_empty_body_is_400()
    {
        using var host = new ServiceHost();
        using var response = await PostAsync(host.Client, RawBody(string.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("empty file", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_llm_value_is_400_and_names_the_accepted_values()
    {
        using var host = new ServiceHost();
        using var response = await PostAsync(host.Client, PdfForm(llm: "nonsense"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid llm value 'nonsense'", body);
        Assert.Contains("nollm, llmonly, llmfallback", body);
    }

    [Fact]
    public async Task An_upload_over_the_limit_is_413()
    {
        using var host = new ServiceHost(("MAX_UPLOAD_BYTES", "1024"));
        using var response = await PostAsync(
            host.Client,
            RawBody("%PDF-1.4\n" + new string('x', 4096)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // ---- backpressure -------------------------------------------------------

    [Fact]
    public async Task A_second_request_while_busy_is_refused_immediately_with_Retry_After()
    {
        using var host = new ServiceHost(("STUB_SLEEP_MS", "2500"));

        var first = PostAsync(host.Client, PdfForm(), "trace-slow");
        await Task.Delay(800);

        using var second = await PostAsync(host.Client, PdfForm(), "trace-busy");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal("15", second.Headers.GetValues("Retry-After").Single());
        Assert.Contains("Service at capacity", await second.Content.ReadAsStringAsync());

        using var firstResponse = await first;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
    }

    // ---- LLM policy ---------------------------------------------------------

    [Fact]
    public async Task Llmonly_without_a_backend_fails_before_running_the_engine()
    {
        using var host = new ServiceHost();
        using var response = await PostAsync(host.Client, PdfForm(llm: "llmonly"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("llm_unavailable", body);
        Assert.Contains("OPENAI_BASE_URL", body);
    }

    [Fact]
    public async Task Llmfallback_without_a_backend_succeeds_and_says_it_fell_back()
    {
        using var host = new ServiceHost();
        using var response = await PostAsync(host.Client, PdfForm(llm: "llmfallback"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("llmfallback", response.Headers.GetValues("X-Llm-Mode").Single());
        Assert.Equal("none", response.Headers.GetValues("X-Llm-Used").Single());
        Assert.Equal("true", response.Headers.GetValues("X-Llm-Fallback").Single());
    }

    [Fact]
    public async Task Llmonly_discards_a_degraded_run_instead_of_returning_it()
    {
        using var host = new ServiceHost(LlmSettings);
        using var response = await PostAsync(host.Client, PdfForm(llm: "llmonly"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("llm_unavailable", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Llmfallback_re_runs_without_the_llm_and_returns_that_result()
    {
        using var host = new ServiceHost(LlmSettings);
        using var response = await PostAsync(host.Client, PdfForm(llm: "llmfallback"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("none", response.Headers.GetValues("X-Llm-Used").Single());
        Assert.Equal("true", response.Headers.GetValues("X-Llm-Fallback").Single());
        Assert.Contains("Converted by the marker stub", await response.Content.ReadAsStringAsync());
    }

    private static readonly (string Key, string? Value)[] LlmSettings =
    [
        ("OPENAI_BASE_URL", "http://127.0.0.1:9/v1"),
        ("OPENAI_MODEL", "stub-model"),
        ("STUB_DEGRADE_LLM", "1"),
    ];
}
