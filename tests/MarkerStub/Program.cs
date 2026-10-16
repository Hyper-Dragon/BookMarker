// Stand-in for the `marker` CLI, used by the service test suite.
//
// BookMarker's contract is about what it does *around* the engine: backpressure, the LLM
// policy, scratch cleanup, status codes and headers. CI has no GPU, no model weights and
// no upstream install, so this implements just enough of marker's observable behaviour for
// those tests: it writes one markdown file under --output_dir and prints the
// "Inferenced N pages in X seconds" line the runner parses for the page count.
//
// Behaviour is selected by environment variables so one build covers every case:
//   STUB_EXIT_CODE=1        exit non-zero (conversion failure)
//   STUB_SLEEP_MS=1500      delay before writing output (concurrency / 503 tests)
//   STUB_DEGRADE_LLM=1      on --use_llm runs, emit a terminal shim failure ("giving up")
//   STUB_PAGES=7            page count to report (default 3)
//   STUB_NO_OUTPUT=1        exit 0 without writing markdown
//
// It is a test fixture. It is not packaged, not published and not copied into the image.

var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();

var inputDir = arguments.FirstOrDefault(arg => !arg.StartsWith('-') && Directory.Exists(arg));
var outputIndex = Array.IndexOf(arguments, "--output_dir");
var outputDir = outputIndex >= 0 && outputIndex + 1 < arguments.Length ? arguments[outputIndex + 1] : null;
var useLlm = arguments.Contains("--use_llm");

if (inputDir is null || outputDir is null)
{
    Console.Error.WriteLine("marker-stub: usage: marker <input_dir> --output_dir <dir>");
    return 2;
}

// Progress lines, so the runner's fraction regex has something plausible to read.
var pages = int.TryParse(Environment.GetEnvironmentVariable("STUB_PAGES"), out var configuredPages)
    ? configuredPages
    : 3;

Console.WriteLine("Loading models");
Console.WriteLine($"Converting {pages} pages");

if (int.TryParse(Environment.GetEnvironmentVariable("STUB_SLEEP_MS"), out var sleepMs) && sleepMs > 0)
{
    await Task.Delay(sleepMs);
}

if (useLlm && Environment.GetEnvironmentVariable("STUB_DEGRADE_LLM") == "1")
{
    Console.Error.WriteLine("[marker_openai_service] call failed: 400 invalid json_schema");
    Console.Error.WriteLine("[marker_openai_service] giving up after 4 attempts");
}

if (Environment.GetEnvironmentVariable("STUB_NO_OUTPUT") != "1")
{
    Directory.CreateDirectory(outputDir);
    foreach (var pdf in Directory.GetFiles(inputDir, "*.pdf"))
    {
        var stem = Path.GetFileNameWithoutExtension(pdf);
        var docDir = Path.Combine(outputDir, stem);
        Directory.CreateDirectory(docDir);
        await File.WriteAllTextAsync(
            Path.Combine(docDir, $"{stem}.md"),
            $"# Converted by the marker stub\n\nsource: {Path.GetFileName(pdf)}\n");
    }
}

Console.WriteLine($"Inferenced {pages} pages in 1.234 seconds");

return int.TryParse(Environment.GetEnvironmentVariable("STUB_EXIT_CODE"), out var exitCode)
    ? exitCode
    : 0;
