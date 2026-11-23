using System.IO.Compression;
using OLI.Agent;
using OLI.Core;
using OLI.Web;
using OLI.Workspace;

// Headless checks for the parts that are easy to get wrong: sandbox path
// containment, quota accounting, zip round-trips, tool-call parsing and the
// search allow/block policy. Runs in CI on every build (`dotnet run`), no UI.

var failures = new List<string>();
var passes = 0;

void Check(string name, Func<bool> test)
{
    try
    {
        if (test()) { passes++; Console.WriteLine($"  ok   {name}"); }
        else { failures.Add(name); Console.WriteLine($"  FAIL {name}"); }
    }
    catch (Exception ex)
    {
        failures.Add($"{name} ({ex.GetType().Name}: {ex.Message})");
        Console.WriteLine($"  FAIL {name} — {ex.GetType().Name}: {ex.Message}");
    }
}

async Task CheckAsync(string name, Func<Task<bool>> test)
{
    try
    {
        if (await test()) { passes++; Console.WriteLine($"  ok   {name}"); }
        else { failures.Add(name); Console.WriteLine($"  FAIL {name}"); }
    }
    catch (Exception ex)
    {
        failures.Add($"{name} ({ex.GetType().Name}: {ex.Message})");
        Console.WriteLine($"  FAIL {name} — {ex.GetType().Name}: {ex.Message}");
    }
}

bool Throws<T>(Action action) where T : Exception
{
    try { action(); return false; }
    catch (T) { return true; }
}

var sandboxRoot = Path.Combine(Path.GetTempPath(), "oli-selftest-" + Guid.NewGuid().ToString("N"));
var workspace = new WorkspaceEngine(
    sandboxRoot,
    sandboxRoot,
    Path.Combine(sandboxRoot, "archives"),
    quotaBytes: 32L * 1024 * 1024,
    maxFileBytes: 4L * 1024 * 1024,
    commandsEnabled: false,
    allowedCommands: ["python"]);

Console.WriteLine($"OLI self-test — sandbox {sandboxRoot}");

Console.WriteLine("PathGuard");
Check("relative path resolves inside root",
    () => PathGuard.Resolve(sandboxRoot, "docs/plan.md").EndsWith(Path.Combine("docs", "plan.md")));
Check("'..' traversal rejected", () => Throws<WorkspaceException>(() => PathGuard.Resolve(sandboxRoot, "../escape.txt")));
Check("nested traversal rejected", () => Throws<WorkspaceException>(() => PathGuard.Resolve(sandboxRoot, "a/../../escape.txt")));
Check("absolute path rejected", () => Throws<WorkspaceException>(() => PathGuard.Resolve(sandboxRoot, "C:\\Windows\\evil.dll")));
Check("unix absolute path rejected", () => Throws<WorkspaceException>(() => PathGuard.Resolve(sandboxRoot, "/etc/passwd")));
Check("URL rejected", () => Throws<WorkspaceException>(() => PathGuard.Resolve(sandboxRoot, "http://example.com/x")));
Check("reserved device name rejected",
    () => Throws<WorkspaceException>(() => PathGuard.EnsureCreatableFileName("CON.txt")));
Check("safe file name strips separators", () => !PathGuard.SafeFileName("../../etc/passwd").Contains('/'));

Console.WriteLine("Workspace files");
await workspace.WriteFileAsync("notes/a.txt", "hello");
await CheckAsync("write then read round-trips",
    async () => (await workspace.ReadFileAsync("notes/a.txt")) == "hello");
await CheckAsync("append adds to the end", async () =>
{
    await workspace.WriteFileAsync("notes/a.txt", " world", append: true);
    return (await workspace.ReadFileAsync("notes/a.txt")).Trim() == "hello world";
});
await CheckAsync("edit replaces once", async () =>
{
    await workspace.WriteFileAsync("notes/b.txt", "x x x");
    await workspace.EditFileAsync("notes/b.txt", "x", "y");
    return (await workspace.ReadFileAsync("notes/b.txt")) == "y x x";
});
await CheckAsync("edit all=true replaces every hit", async () =>
{
    await workspace.WriteFileAsync("notes/c.txt", "x x x");
    await workspace.EditFileAsync("notes/c.txt", "x", "y", all: true);
    return (await workspace.ReadFileAsync("notes/c.txt")) == "y y y";
});
Check("listing shows files", () => workspace.List("", 100, true).Any(e => e.RelativePath == "notes/a.txt"));
Check("delete removes the file", () => workspace.Delete("notes/c.txt").Contains("Удалено"));
await CheckAsync("write outside sandbox is rejected", async () =>
{
    try { await workspace.WriteFileAsync("../outside.txt", "x"); return false; }
    catch (WorkspaceException) { return true; }
});

Console.WriteLine("Quota");
await CheckAsync("oversized single file is rejected", async () =>
{
    var big = new string('a', (int)workspace.MaxFileBytes + 10);
    try { await workspace.WriteFileAsync("huge.txt", big); return false; }
    catch (WorkspaceException) { return true; }
});

Console.WriteLine("Archives");
await CheckAsync("zip a folder and unzip it back", async () =>
{
    await workspace.WriteFileAsync("project/readme.md", "# demo");
    await workspace.WriteFileAsync("project/src/main.py", "print('hi')");
    var zipped = await workspace.ZipAsync("project", "archives/project.zip");
    if (!zipped.Contains("архив создан", StringComparison.OrdinalIgnoreCase) &&
        !zipped.Contains("архив", StringComparison.OrdinalIgnoreCase)) return false;
    if (!File.Exists(Path.Combine(sandboxRoot, "archives", "project.zip"))) return false;

    workspace.Delete("project", recursive: true);
    var unzipped = await workspace.UnzipAsync("archives/project.zip", "restored");
    if (!unzipped.Contains("Распаковано")) return false;

    // Entry names are prefixed with the archived folder name.
    return File.Exists(Path.Combine(sandboxRoot, "restored", "project", "src", "main.py"))
           || File.Exists(Path.Combine(sandboxRoot, "restored", "src", "main.py"));
});
Check("zip-slip entries never escape the sandbox", () => ZipSlipBlocked(workspace, sandboxRoot).GetAwaiter().GetResult());

Check("archives are listed", () => workspace.ListArchives().Any(a => a.Name == "project.zip"));
Check("usage stays under quota", () => workspace.GetUsage().UsedBytes <= workspace.QuotaBytes);

Console.WriteLine("Commands");
await CheckAsync("non-allow-listed command is refused", async () =>
{
    try { await workspace.RunCommandAsync("cmd", ["/c", "dir"]); return false; }
    catch (WorkspaceException) { return true; }
});

Console.WriteLine("Tool protocol");
Check("single call parsed", () =>
{
    var calls = ToolProtocol.Parse("OLI_TOOL: save_file {\"path\":\"a.txt\",\"content\":\"hi\"}");
    return calls.Count == 1 && calls[0].Name == "save_file" && calls[0].Str("path") == "a.txt";
});
Check("two calls parsed in order", () =>
{
    var calls = ToolProtocol.Parse(
        "OLI_TOOL: make_dir {\"path\":\"docs\"}\nOLI_TOOL: save_file {\"path\":\"docs/a.md\",\"content\":\"x\"}");
    return calls.Count == 2 && calls[1].Name == "save_file";
});
Check("pretty-printed JSON is parsed", () =>
{
    var calls = ToolProtocol.Parse("OLI_TOOL: save_file {\n  \"path\": \"p.txt\",\n  \"content\": \"multi\\nline\"\n}");
    return calls.Count == 1 && calls[0].Str("path") == "p.txt";
});
Check("arrays survive", () =>
{
    var calls = ToolProtocol.Parse("OLI_TOOL: run_command {\"command\":\"python\",\"args\":[\"a.py\",\"--x\"]}");
    return calls.Count == 1 && calls[0].StrList("args").Count == 2;
});
Check("fenced calls are parsed", () =>
{
    var calls = ToolProtocol.Parse("```\nOLI_TOOL: list_files {}\n```");
    return calls.Count == 1 && calls[0].Name == "list_files";
});
Check("tool lines are stripped from the answer", () =>
{
    var text = "Готово.\nOLI_TOOL: save_file {\"path\":\"a.txt\",\"content\":\"1\"}";
    return !ToolProtocol.StripToolLines(text).Contains("OLI_TOOL");
});

Console.WriteLine("Protocol markers");
Check("search marker only when it is the whole reply",
    () => ProtocolMarkers.ExtractSearchQuery("OLI_SEARCH: погода СПб") == "погода СПб"
          && ProtocolMarkers.ExtractSearchQuery("вот ответ\nOLI_SEARCH: x") is null);
Check("open markers parsed", () =>
{
    var urls = ProtocolMarkers.ExtractOpenUrls("OLI_OPEN: https://a.ru/x\nOLI_OPEN: https://b.ru/y");
    return urls.Count == 2;
});
Check("open markers ignore mixed replies",
    () => ProtocolMarkers.ExtractOpenUrls("Ответ тут\nOLI_OPEN: https://a.ru").Count == 0);
Check("memory facts extracted and removed", () =>
{
    var (clean, facts) = ProtocolMarkers.ExtractMemoryFacts("Привет!\nOLI_REMEMBER: любит тёмную тему");
    return facts.Count == 1 && !clean.Contains("OLI_REMEMBER");
});

Console.WriteLine("Web policy");
var search = new WebSearch(5, OliSearchProvider.Auto)
{
    OpenWeb = true,
    BlockedDomains = DefaultAllowedSites.DefaultBlocked(),
    AllowedDomains = [],
};
Check("open-web allows normal sites", () => search.IsAllowed("https://habr.com/ru/post/1"));
Check("blacklisted domain blocked", () => search.IsAllowed("https://doubleclick.net/x") == false);
Check("localhost blocked", () => search.IsAllowed("http://localhost:8080/x") == false);
Check("private IP blocked", () => search.IsAllowed("http://192.168.1.1/admin") == false);
Check("ec2 metadata blocked", () => search.IsAllowed("http://169.254.169.254/latest") == false);
Check("allow-list mode restricts", () =>
{
    search.OpenWeb = false;
    search.AllowedDomains = ["wikipedia.org"];
    var wiki = search.IsAllowed("https://ru.wikipedia.org/wiki/Кот");
    var other = search.IsAllowed("https://example.com/");
    search.OpenWeb = true;
    return wiki && !other;
});
Check("non-http scheme rejected", () =>
{
    var ok = WebSearch.TryValidateUrl("file:///C:/Windows/win.ini", out _, out _);
    return !ok;
});
Check("html extraction drops scripts", () =>
{
    var text = HtmlTools.ToReadableText("<html><head><title>T</title><script>var x=1;</script></head>" +
                                        "<body><main><h1>Заголовок</h1><p>Текст страницы.</p></main></body></html>");
    return text.Contains("Текст страницы") && !text.Contains("var x=1");
});
Check("duckduckgo redirect unwrapped",
    () => UrlResolver.Resolve("//duckduckgo.com/l/?uddg=https%3A%2F%2Fhabr.com%2Fru%2F&rut=x").StartsWith("https://habr.com"));
search.Dispose();

Console.WriteLine("Models");
Check("qwen3.5 4b is the default", () => ModelCatalog.ById(ModelCatalog.DefaultId).Gguf.Contains("Qwen3.5-4B"));
Check("custom gguf spec preserved", () => ModelCatalog.Resolve("org/custom-GGUF:Q5_K_M").Gguf == "org/custom-GGUF:Q5_K_M");
Check("catalogue applies to settings", () =>
{
    var settings = new OliSettings();
    ModelCatalog.Apply(settings, ModelCatalog.ById("qwen3.5-2b-q4km"));
    return settings.ModelGguf.Contains("Qwen3.5-2B");
});

Console.WriteLine("Settings upgrade");
Check("settings store writes and reads back", () =>
{
    var file = Path.Combine(Path.GetTempPath(), "oli-settings-" + Guid.NewGuid().ToString("N") + ".json");
    SettingsStore.AtomicWrite(file, "{\"ModelGguf\":\"unsloth/Qwen3.5-4B-GGUF:Q4_K_M\",\"ContextSize\":8192}");
    var json = File.ReadAllText(file);
    File.Delete(file);
    return json.Contains("Qwen3.5-4B") && json.Contains("8192");
});

// ------------------------------------------------------------ LAN / pairing
// The phone finds the PC by scanning its own subnet, so these helpers must be sane:
// a wrong preferred address or a broken prefix makes "сервер не виден" again.

Check("subnet prefix of a home address", () => NetworkAddresses.SubnetPrefix("192.168.1.42") == "192.168.1.");
Check("subnet prefix of a 10.x address", () => NetworkAddresses.SubnetPrefix("10.0.5.7") == "10.0.5.");
Check("APIPA address has no prefix", () => NetworkAddresses.SubnetPrefix("169.254.10.20") is null);
Check("garbage address has no prefix", () => NetworkAddresses.SubnetPrefix("not-an-ip") is null);
Check("localhost is never offered to the phone", () => !NetworkAddresses.Local().Contains("127.0.0.1"));
Check("no APIPA address is offered to the phone", () => NetworkAddresses.Local().All(a => !a.StartsWith("169.254.")));
Check("preferred address is one of the local ones",
    () => NetworkAddresses.Preferred() is null || NetworkAddresses.Local().Contains(NetworkAddresses.Preferred()!));
Check("address description is never empty",
    () => !string.IsNullOrWhiteSpace(NetworkAddresses.DescribeAll()));

await CheckAsync("LAN scan rejects a dead port quickly", async () =>
{
    var local = NetworkAddresses.Preferred();
    if (local is null) return true; // offline CI: nothing to scan
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var found = await LanScanner.FindServerAsync(local, port: 1, TimeSpan.FromMilliseconds(80));
    return found is null && watch.Elapsed < TimeSpan.FromSeconds(30);
});

// ------------------------------------------------------------------ helpers

static async Task<bool> ZipSlipBlocked(WorkspaceEngine workspace, string root)
{
    var evil = Path.Combine(root, "archives", "evil.zip");
    Directory.CreateDirectory(Path.GetDirectoryName(evil)!);
    if (File.Exists(evil)) File.Delete(evil);

    using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("../../pwned.txt");
        await using var writer = new StreamWriter(entry.Open());
        await writer.WriteAsync("bad");
    }

    await workspace.UnzipAsync("archives/evil.zip", "slip");
    var escaped = Path.Combine(Directory.GetParent(root)!.Parent!.FullName, "pwned.txt");
    return !File.Exists(Path.Combine(root, "..", "..", "pwned.txt")) && !File.Exists(escaped);
}

Console.WriteLine();
Console.WriteLine(failures.Count == 0
    ? $"PASS: {passes} checks"
    : $"FAILED: {failures.Count} of {passes + failures.Count} checks");

try { Directory.Delete(sandboxRoot, true); } catch { /* temp cleanup is best-effort */ }
return failures.Count == 0 ? 0 : 1;
