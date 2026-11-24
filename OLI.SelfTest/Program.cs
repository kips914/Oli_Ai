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
        else { failures.Add(name); Console.WriteLine($"  FAIL {name}"); Console.WriteLine($"::error title=SelfTest::{name}"); }
    }
    catch (Exception ex)
    {
        failures.Add($"{name} ({ex.GetType().Name}: {ex.Message})");
        Console.WriteLine($"  FAIL {name} — {ex.GetType().Name}: {ex.Message}");
        // GitHub annotations survive even when the raw job log is unavailable.
        Console.WriteLine($"::error title=SelfTest::{name} — {ex.GetType().Name}: {ex.Message.Replace("\n", " ")}");
    }
}

async Task CheckAsync(string name, Func<Task<bool>> test)
{
    try
    {
        if (await test()) { passes++; Console.WriteLine($"  ok   {name}"); }
        else { failures.Add(name); Console.WriteLine($"  FAIL {name}"); Console.WriteLine($"::error title=SelfTest::{name}"); }
    }
    catch (Exception ex)
    {
        failures.Add($"{name} ({ex.GetType().Name}: {ex.Message})");
        Console.WriteLine($"  FAIL {name} — {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"::error title=SelfTest::{name} — {ex.GetType().Name}: {ex.Message.Replace("\n", " ")}");
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

// ------------------------------------------------------------ image analysis
// The phone sends photos for analysis; the description block is built from the real
// bytes, so a broken or unsupported file must never crash the server.

var tinyPng = new byte[]
{
    0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // signature
    0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR length + type
    0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x20, // 64 x 32
    0x08, 0x02, 0x00, 0x00, 0x00
};

Check("image description names the file and the kind", () =>
{
    var text = OLI.Agent.ImageAnalyzer.Describe("photo.png", tinyPng);
    return text.Contains("photo.png") && text.Contains("PNG");
});
Check("png size is read from the header", () =>
    OLI.Agent.ImageAnalyzer.Describe("a.png", tinyPng).Contains("64×32"));
Check("broken bytes still produce a description", () =>
{
    var text = OLI.Agent.ImageAnalyzer.Describe("scan.bin", new byte[] { 1, 2, 3, 4 });
    return text.Contains("scan.bin") && text.Length > 40;
});
Check("description never leaks a stack trace", () =>
    !OLI.Agent.ImageAnalyzer.Describe("x.jpg", new byte[] { 0xFF, 0xD8, 0xFF }).Contains("Exception"));

// ------------------------------------------------------------ connection core
// One implementation decides "is this address usable" for the PC and the phone. These
// checks pin the rules that used to differ between them.

Console.WriteLine("ConnectionCore · normalize");
Check("address without scheme gets http://",
    () => ConnectionCore.Normalize("192.168.1.10:6767") == "http://192.168.1.10:6767");
Check("trailing slash is removed",
    () => ConnectionCore.Normalize("http://192.168.1.10:6767/") == "http://192.168.1.10:6767");
Check("tunnel address keeps https and its port",
    () => ConnectionCore.Normalize("https://quiet-river.trycloudflare.com") == "https://quiet-river.trycloudflare.com");
Check("scheme default port is dropped",
    () => ConnectionCore.Normalize("http://example.com:80") == "http://example.com");
Check("explicit custom port survives",
    () => ConnectionCore.Normalize("http://127.0.0.1:11434") == "http://127.0.0.1:11434");
Check("host is lower-cased",
    () => ConnectionCore.Normalize("HTTP://LOCALHOST:6767") == "http://localhost:6767");
Check("garbage is rejected", () => ConnectionCore.Normalize("не адрес вовсе") is null);
Check("empty is rejected", () => ConnectionCore.Normalize("   ") is null);
Check("non-http scheme is rejected", () => ConnectionCore.Normalize("ftp://host/x") is null);
Check("loopback is recognised", () => ConnectionCore.IsLoopback("http://127.0.0.1:6767"));
Check("localhost is recognised", () => ConnectionCore.IsLoopback("localhost:8080"));
Check("LAN address is not loopback", () => !ConnectionCore.IsLoopback("http://192.168.1.5:6767"));

Console.WriteLine("ConnectionCore · candidates");
Check("candidate list removes duplicates",
    () => ConnectionCore.CandidateOrder(["http://a:1", "http://a:1/", "http://b:2"]).Count == 2);
Check("candidate list keeps the given order",
    () => ConnectionCore.CandidateOrder(["http://b:2", "http://a:1"]).First() == "http://b:2");
Check("invalid candidates are dropped",
    () => ConnectionCore.CandidateOrder([null, "", "http://ok:1"]).SequenceEqual(["http://ok:1"]));

Console.WriteLine("ConnectionCore · join code");
Check("join code round-trips address and invite",
    () => ConnectionCore.ParseJoinCode(ConnectionCore.BuildJoinCode("https://x.trycloudflare.com", "SEC-42"))
        is { Address: "https://x.trycloudflare.com", InviteCode: "SEC-42", FromCode: true });
Check("join code survives whitespace pasted from a messenger", () =>
{
    var code = ConnectionCore.BuildJoinCode("http://192.168.1.10:6767", "");
    var messy = " " + code[..10] + "\n  " + code[10..] + " ";
    return ConnectionCore.ParseJoinCode(messy)?.Address == "http://192.168.1.10:6767";
});
Check("join code tolerates the oli:// prefix",
    () => ConnectionCore.ParseJoinCode("oli://" + ConnectionCore.BuildJoinCode("http://a:1"))?.FromCode == true);
Check("plain address is accepted instead of a code",
    () => ConnectionCore.ParseJoinCode("192.168.1.10:6767") == new JoinCodeInfo("http://192.168.1.10:6767", "", false));
Check("plain https address is accepted",
    () => ConnectionCore.ParseJoinCode("https://x.trycloudflare.com")?.Address == "https://x.trycloudflare.com");
Check("garbage is not a join code", () => ConnectionCore.ParseJoinCode("hello world") is null);
Check("a single word is not treated as an address", () => ConnectionCore.ParseJoinCode("привет") is null);
Check("a bare host name with a dot is an address",
    () => ConnectionCore.ParseJoinCode("my-pc.local")?.Address == "http://my-pc.local");
Check("localhost with a port is an address",
    () => ConnectionCore.ParseJoinCode("localhost:6767")?.Address == "http://localhost:6767");
Check("broken base64 is not a join code", () => ConnectionCore.ParseJoinCode("OLI1@@@") is null);

Console.WriteLine("ConnectionCore · health classification");
Check("healthy server with live engine is Ready",
    () => ConnectionCore.Classify(true, "oli-server", true, true) == ConnectionState.Ready);
Check("live server with dead engine is EngineDown, not Ready",
    () => ConnectionCore.Classify(true, "oli-server", false, true) == ConnectionState.EngineDown);
Check("silent address is ServerDown",
    () => ConnectionCore.Classify(false, null, null, null) == ConnectionState.ServerDown);
Check("another service is NotOli",
    () => ConnectionCore.Classify(true, "nginx", true, false) == ConnectionState.NotOli);
Check("state sentences mention the engine when it is down",
    () => ConnectionCore.Describe(ConnectionState.EngineDown, "http://a:1").Contains("движок"));
Check("a health answer without a service field is not OLI",
    () => ConnectionCore.ClassifyHealth(true, "", true) == ConnectionState.NotOli);
Check("ClassifyHealth is the same rule as Classify",
    () => ConnectionCore.ClassifyHealth(true, "oli-server", false) == ConnectionState.EngineDown);
Check("Every state has its own sentence", () =>
    Enum.GetValues<ConnectionState>().All(s => ConnectionCore.Describe(s).Length > 10));
Check("ServerDown sentence tells the user what to press",
    () => ConnectionCore.Describe(ConnectionState.ServerDown, "http://a:1").Contains("Открыть порт"));

Console.WriteLine("ConnectionCore · beacon");
Check("beacon round-trips port and name", () =>
{
    var info = ConnectionCore.Beacon.Decode(ConnectionCore.Beacon.Encode(6767, "DESKTOP-1", "1.13.0"));
    return info is { Port: 6767, Name: "DESKTOP-1", Version: "1.13.0" };
});
Check("beacon name cannot break the packet",
    () => ConnectionCore.Beacon.Decode(ConnectionCore.Beacon.Encode(8765, "a|b", "1"))?.Name == "ab");
Check("foreign udp packet is ignored", () => ConnectionCore.Beacon.Decode("hello"u8.ToArray()) is null);
Check("beacon with a bad port is ignored",
    () => ConnectionCore.Beacon.Decode("OLI-BEACON/2|99999|x|1"u8.ToArray()) is null);

Console.WriteLine("RetryPolicy");
Check("503 is not retried (engine is down, waiting will not help)",
    () => !RetryPolicy.ShouldRetry(503, timeout: false));
Check("429 is retried", () => RetryPolicy.ShouldRetry(429, timeout: false));
Check("transport failure is retried", () => RetryPolicy.ShouldRetry(null, timeout: false));
Check("401 is not retried", () => !RetryPolicy.ShouldRetry(401, timeout: false));

// ------------------------------------------------------------ chat core

Console.WriteLine("ChatCore · text");
Check("control characters are stripped",
    () => ChatCore.Clean("a\u0007b") == "ab");
Check("newlines survive", () => ChatCore.Clean("a\nb") == "a\nb");
Check("text is trimmed", () => ChatCore.Clean("  hi  ") == "hi");
Check("null-safe clean", () => ChatCore.Clean(null!) == "");
Check("long text is capped",
    () => ChatCore.Clean(new string('x', ChatCore.MaxTextPerMessage + 500)).Length <= ChatCore.MaxTextPerMessage + 1);

Console.WriteLine("ChatCore · titles");
Check("title comes from the first message",
    () => ChatCore.MakeTitle("Как решить интеграл?") == "Как решить интеграл");
Check("long title is cut with an ellipsis",
    () => ChatCore.MakeTitle(new string('a', 200)).EndsWith("…"));
Check("empty message keeps the fallback", () => ChatCore.MakeTitle("   ") == "Новый чат");
Check("newline in the first message does not break the title",
    () => !ChatCore.MakeTitle("first line\nsecond line").Contains('\n'));

Console.WriteLine("ChatCore · history for the model");
Check("a photo attached to a message is kept on the bubble",
    () => ChatEntry.User("что на фото?", files: ["photo.jpg"]).Files.SequenceEqual(["photo.jpg"]));
Check("failures are not sent to the model",
    () => ChatCore.ToModelHistory([ChatEntry.User("hi"), ChatEntry.Failure("boom")]).Count == 1);
Check("pending bubbles are not sent",
    () => ChatCore.ToModelHistory([ChatEntry.User("hi"), ChatEntry.PendingAnswer()]).Count == 1);
Check("system messages are not sent",
    () => ChatCore.ToModelHistory([new ChatEntry { Role = ChatRole.System, Text = "sys" }]).Count == 0);
Check("failure messages are not sent",
    () => ChatCore.ToModelHistory([ChatEntry.Failure("сеть недоступна"), ChatEntry.User("hi")]).Count == 1);
Check("newest messages win",
    () => ChatCore.ToModelHistory(Enumerable.Range(0, 100).Select(i => ChatEntry.User("m" + i)).ToList()).Last().Content == "m99");
Check("history order is chronological", () =>
{
    var history = ChatCore.ToModelHistory([ChatEntry.User("one"), ChatEntry.Assistant("two")]);
    return history[0].Content == "one" && history[1].Content == "two" && history[1].Role == "assistant";
});

Console.WriteLine("ChatCore · visible text");
Check("empty assistant answer is explained, not hidden",
    () => ChatCore.VisibleText(ChatEntry.Assistant("")).Contains("Пустой ответ"));
Check("error bubble starts with a warning sign",
    () => ChatCore.VisibleText(ChatEntry.Failure("нет движка")).StartsWith("⚠"));
Check("pending bubble has no text", () => ChatCore.VisibleText(ChatEntry.PendingAnswer()) == "");
Check("failures are not persisted", () => !ChatCore.IsPersistable(ChatEntry.Failure("x")));
Check("pending answers are not persisted", () => !ChatCore.IsPersistable(ChatEntry.PendingAnswer()));

Console.WriteLine("ChatCore · trim");
Check("short history is untouched", () => ChatCore.Trim([ChatEntry.User("a")]).Count == 1);
Check("long history keeps the cap", () =>
    ChatCore.Trim(Enumerable.Range(0, ChatCore.MaxMessagesPerThread + 50).Select(i => ChatEntry.User("m" + i)).ToList())
        .Count == ChatCore.MaxMessagesPerThread);
Check("trim keeps the first user message (the title source)", () =>
{
    var messages = new List<ChatEntry> { ChatEntry.User("первый вопрос") };
    messages.AddRange(Enumerable.Range(0, ChatCore.MaxMessagesPerThread + 10).Select(i => ChatEntry.Assistant("a" + i)));
    var trimmed = ChatCore.Trim(messages);
    return trimmed[0].Text == "первый вопрос" && trimmed.Count == ChatCore.MaxMessagesPerThread;
});

// ------------------------------------------------------------ chat store (server)

Console.WriteLine("ChatStore");
var chatRoot = Path.Combine(Path.GetTempPath(), "oli-chats-" + Guid.NewGuid().ToString("N"));
var store = new OLI.Server.Chats.ChatStore(chatRoot);
var firstThread = store.Create("acc1", "Первый вопрос про интегралы", "oli-pass");
Check("created thread is listed", () => store.List("acc1").Any(t => t.Id == firstThread.Id));
Check("title is taken from the first message",
    () => store.List("acc1").First().Title.StartsWith("Первый вопрос"));
Check("append returns the stored entry with a server id", () =>
    store.Append("acc1", firstThread.Id, ChatEntry.User("hello")).Entry is { Text: "hello" } stored && stored.Id.Length > 0);
Check("append to a deleted thread is reported as a missing thread",
    () => store.Append("acc1", "missing", ChatEntry.Assistant("x")).ThreadMissing);
Check("an empty answer is NOT reported as a deleted thread", () =>
    store.Append("acc1", firstThread.Id, ChatEntry.Assistant("")) is { ThreadMissing: false, Entry: null });
Check("messages are persisted and reloaded", () =>
{
    var reloaded = new OLI.Server.Chats.ChatStore(chatRoot);
    return reloaded.Get("acc1", firstThread.Id)?.Messages.Count >= 1;
});
Check("a read never hands out the live thread (no 'Collection was modified')", () =>
{
    var thread = store.Create("acc7", "snapshot", "oli-pass");
    store.Append("acc7", thread.Id, ChatEntry.User("первое"));
    var copy = store.Get("acc7", thread.Id)!;
    store.Append("acc7", thread.Id, ChatEntry.User("второе"));
    // The copy must not have changed under us while the server serialised it.
    return copy.Messages.Count == 1 && store.Get("acc7", thread.Id)!.Messages.Count == 2;
});
Check("a deleted thread is not recreated by Resolve",
    () => store.Resolve("acc7", "gone-forever", "привет", "oli-pass") is null);
Check("Resolve creates a thread when no id is given",
    () => store.Resolve("acc7", null, "новый вопрос", "oli-pass")?.Title == "новый вопрос");
Check("Exists tells the server whether the thread is still alive", () =>
{
    var thread = store.Create("acc7", "жив ли", "oli-pass");
    return store.Exists("acc7", thread.Id) && !store.Exists("acc7", "nope");
});
Check("parallel appends from two threads do not lose messages", () =>
{
    var thread = store.Create("acc1", "parallel", "oli-pass");
    Parallel.For(0, 40, i => store.Append("acc1", thread.Id, ChatEntry.Assistant("m" + i)));
    return store.Get("acc1", thread.Id)?.Messages.Count == 40;
});
Check("second thread is listed first (newest on top)", () =>
{
    var first = store.Create("acc3", "старый", "oli-pass");
    Thread.Sleep(5);
    var second = store.Create("acc3", "новый", "oli-pass");
    return store.List("acc3")[0].Id == second.Id && store.Latest("acc3")?.Id == second.Id;
});
Check("renaming updates the drawer title", () =>
    store.Rename("acc1", firstThread.Id, "Новое имя") && store.List("acc1").First(t => t.Id == firstThread.Id).Title == "Новое имя");
Check("delete removes the thread", () => store.Delete("acc1", firstThread.Id) && store.Get("acc1", firstThread.Id) is null);
Check("accounts are isolated", () => store.List("acc2").Count == 0);
Check("thread titles are trimmed to one line", () =>
{
    var thread = store.Create("acc4", "первая строка\nвторая строка", "oli-pass");
    var title = store.List("acc4").First(t => t.Id == thread.Id).Title;
    return !title.Contains('\n') && title.StartsWith("первая строка");
});
Check("missing title falls back to 'Новый чат'", () =>
    store.Create("acc5", "", "oli-pass").Title == "Новый чат");
Check("thread history is capped, not unbounded", () =>
{
    var thread = store.Create("acc6", "cap", "oli-pass");
    for (var i = 0; i < ChatCore.MaxMessagesPerThread + 20; i++)
        store.Append("acc6", thread.Id, ChatEntry.User("m" + i));
    return store.Get("acc6", thread.Id)!.Messages.Count == ChatCore.MaxMessagesPerThread;
});
Check("latest thread is returned for 'current chat'",
    () => store.Latest("acc1") is not null);
try { Directory.Delete(chatRoot, true); } catch { }

// ------------------------------------------------------------ port guard
// "address already in use" was the only thing the user saw about a busy port. These checks
// pin the behaviour that replaces it: detect the holder, reuse a working OLI, or move.

Console.WriteLine("PortGuard");
var busyPort = FreePort();
var holder = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, busyPort);
holder.Start();
try
{
    Check("a bound port is reported as busy", () => !PortGuard.IsFree(busyPort));
    Check("an unbound port is reported as free", () => PortGuard.IsFree(busyPort + 1));
    Check("the next free port is found automatically", () =>
    {
        var found = PortGuard.FindFree(busyPort + 1);
        return found > busyPort && PortGuard.IsFree(found);
    });
    Check("moving never returns a busy port", () => PortGuard.FindFree(busyPort, 3) != busyPort);
    Check("port 0 and out-of-range values are never 'free'", () => !PortGuard.IsFree(0) && !PortGuard.IsFree(70000));
}
finally { holder.Stop(); }
Check("the busy port becomes usable again after release", () => PortGuard.IsFree(busyPort));

// ------------------------------------------------------------ setup & components
// First launch now installs what OLI needs and proves the connection works. These checks
// pin the rules that decide "is the tunnel binary usable" and "what exactly is broken".

Console.WriteLine("ComponentInstaller");
var componentDir = Path.Combine(Path.GetTempPath(), "oli-comp-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(componentDir);
try
{
    var spec = new ComponentSpec("test", "Тестовый файл", "tool.exe",
        "https://example.invalid/tool.exe", "", "нужен для теста");

    Check("missing component is reported as missing",
        () => ComponentInstaller.Inspect(Path.Combine(componentDir, "tool.exe"), spec) == ComponentState.Missing);

    var truncated = Path.Combine(componentDir, "tool.exe");
    File.WriteAllBytes(truncated, new byte[100]);
    Check("a tiny file is broken, not accepted",
        () => ComponentInstaller.Inspect(truncated, spec) == ComponentState.Broken);

    // A real executable header ("MZ") with a plausible size stands in for cloudflared.
    var bytes = new byte[2 * 1024 * 1024];
    bytes[0] = (byte)'M';
    bytes[1] = (byte)'Z';
    File.WriteAllBytes(truncated, bytes);
    Check("a downloaded program with an MZ header is accepted",
        () => ComponentInstaller.Inspect(truncated, spec) == ComponentState.Ready);

    // An HTML error page saved with .exe must never be "installed" and then executed.
    File.WriteAllText(truncated, "<html>blocked by proxy</html>");
    Check("an HTML page saved as .exe is rejected",
        () => ComponentInstaller.Inspect(truncated, spec) == ComponentState.Broken);

    var wrongHash = spec with { Sha256 = new string('A', 64) };
    File.WriteAllBytes(truncated, bytes);
    Check("a pinned hash mismatch is rejected",
        () => ComponentInstaller.Inspect(truncated, wrongHash) == ComponentState.Broken);

    var rightHash = spec with { Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) };
    Check("a matching pinned hash is accepted",
        () => ComponentInstaller.Inspect(truncated, rightHash) == ComponentState.Ready);

    Check("a failed download does not leave a broken file behind", () =>
    {
        // No network here: the installer must report a failure and keep the target untouched.
        var broken = Path.Combine(componentDir, "never.exe");
        var dead = spec with { Id = "dead", FileName = "never.exe", Url = "http://127.0.0.1:9/x.exe", MirrorUrl = null };
        var report = new ComponentInstaller(componentDir).EnsureAsync(dead).GetAwaiter().GetResult();
        return report.State == ComponentState.Failed && report.Error.Length > 0 && !File.Exists(broken);
    });
}
finally
{
    try { Directory.Delete(componentDir, true); } catch { }
}

Check("every known component has a name and a reason for the user",
    () => ComponentInstaller.Known.All(c => c.DisplayName.Length > 0 && c.Why.Length > 0));

Console.WriteLine("SetupCheckCore");
Check("all-good check summarises as 'готово'", () =>
{
    var check = SetupCheckCore.RunAsync(6767, true, "https://x.trycloudflare.com",
        ["192.168.1.10"], _ => Task.FromResult(true)).GetAwaiter().GetResult();
    return check.AllOk && check.Summary.Contains("готово", StringComparison.OrdinalIgnoreCase);
});
Check("a dead local server is reported with an action, not a generic error", () =>
{
    var check = SetupCheckCore.RunAsync(6767, true, null, [], _ => Task.FromResult(false)).GetAwaiter().GetResult();
    var step = check.Steps.First(s => s.Title == "Сервер OLI");
    return step.State == SetupStepState.Failed && step.Action == "Запустить сервер"
        && check.Summary.Contains("Запустить сервер");
});
Check("loopback works but LAN does not → firewall is named as the cause", () =>
{
    var check = SetupCheckCore.RunAsync(6767, true, null, ["192.168.1.10"],
        url => Task.FromResult(url.Contains("127.0.0.1"))).GetAwaiter().GetResult();
    var step = check.Steps.First(s => s.Title == "Адрес в Wi-Fi");
    return step.State == SetupStepState.Failed && step.Action == "Открыть порт"
        && step.Detail.Contains("брандмауэр", StringComparison.OrdinalIgnoreCase);
});
Check("a tunnel address that does not answer is a failed step", () =>
{
    var check = SetupCheckCore.RunAsync(6767, true, "https://x.trycloudflare.com", ["192.168.1.10"],
        url => Task.FromResult(!url.Contains("trycloudflare"))).GetAwaiter().GetResult();
    var step = check.Steps.First(s => s.Title == "Доступ из интернета");
    return step.State == SetupStepState.Failed && step.Action == "Журнал туннеля";
});
Check("no LAN address at all is a warning, not a failure", () =>
{
    var check = SetupCheckCore.RunAsync(6767, false, null, [], _ => Task.FromResult(false)).GetAwaiter().GetResult();
    return check.Steps.Any(s => s.Title == "Адрес в Wi-Fi" && s.State == SetupStepState.Warning)
        && check.Steps.Any(s => s.Title == "Сервер OLI" && s.State == SetupStepState.Skipped);
});
Check("a disabled tunnel is skipped, not reported as broken", () =>
{
    var check = SetupCheckCore.RunAsync(6767, true, null, ["192.168.1.10"], _ => Task.FromResult(true))
        .GetAwaiter().GetResult();
    return check.Steps.Any(s => s.Title == "Доступ из интернета" && s.State == SetupStepState.Skipped);
});
Check("every step line starts with a readable marker", () =>
{
    var check = SetupCheckCore.RunAsync(6767, true, null, [], _ => Task.FromResult(true)).GetAwaiter().GetResult();
    return check.Lines.All(l => l.Length > 3 && "✓⚠✗·".Contains(l[0]));
});

// ------------------------------------------------------------ live server round-trip
// Start the real OLI server on a free port and walk the exact phone path:
// health → register → login → chat thread → drawer → rename → delete. The model engine
// is definitely absent here, so the chat must answer 503 with a reason (not 500).

Console.WriteLine("Live server round-trip");
var livePort = FreePort();
var liveRoot = Path.Combine(Path.GetTempPath(), "oli-live-" + Guid.NewGuid().ToString("N"));
await using (var live = new OLI.Server.ServerHost(new OLI.Server.ServerOptions
{
    DataRoot = liveRoot,
    Host = "127.0.0.1",
    Port = livePort,
    LlamaBaseUrl = "http://127.0.0.1:9", // nothing listens here on purpose
    AllowRegistration = true,
    AdminToken = "selftest-admin",
}))
{
    await live.StartAsync();
    using var client = new OLI.Engine.OliServerClient($"http://127.0.0.1:{livePort}");

    var probe = await client.ProbeAsync();
    Check("live health says the engine is down, not 'unknown'",
        () => probe.State == ConnectionState.EngineDown);
    Check("live health names the engine", () => probe.Engine.Length > 0);

    var register = await client.RegisterAsync("student01", "Study2026", "Студент", "", default);
    Check("registration succeeds", () => register.Ok && register.Token is not null);
    client.Token = register.Token;

    var duplicate = await client.RegisterAsync("student01", "Study2026", "Студент", "", default);
    Check("duplicate registration is refused with a reason", () => !duplicate.Ok && duplicate.Error.Length > 0);

    var badLogin = await new OLI.Engine.OliServerClient($"http://127.0.0.1:{livePort}")
        .LoginAsync("student01", "wrong-password1", default);
    Check("wrong password is a 401, not a retry loop", () => !badLogin.Ok && badLogin.HttpStatus == 401);

    var login = await new OLI.Engine.OliServerClient($"http://127.0.0.1:{livePort}")
        .LoginAsync("student01", "Study2026", default);
    Check("login returns a fresh token", () => login.Ok && login.Token is not null);

    var emptyChat = await client.SendAsync("Как дела?", null, default);
    Check("chat without an engine is a 503, not a 500 or a hang", () =>
        !emptyChat.Ok && emptyChat.HttpStatus == 503 && emptyChat.EngineDown);
    Check("the 503 carries a reason and a next step", () =>
        emptyChat.Error.Length > 10 && emptyChat.Error.Contains("Пульт админа", StringComparison.OrdinalIgnoreCase));
    Check("a failed answer does not invent a thread that the client cannot open",
        () => emptyChat.ThreadId is null || emptyChat.ThreadId.Length > 0);

    var threads = await client.ListChatsAsync();
    Check("the failed turn still created (and titled) a thread", () =>
        threads.Count == 1 && threads[0].Title.StartsWith("Как дела"));

    IReadOnlyList<ChatEntry> loaded = threads.Count > 0 ? await client.LoadChatAsync(threads[0].Id) : [];
    Check("the user's message is stored once", () => loaded.Count(m => m.Role == ChatRole.User) == 1);
    Check("no empty assistant bubble is stored", () => loaded.All(m => m.Role != ChatRole.Assistant || m.Text.Length > 0));

    var unauthorised = new OLI.Engine.OliServerClient($"http://127.0.0.1:{livePort}");
    var refusedChat = await unauthorised.SendAsync("hi", null, default);
    Check("chat without a token is refused", () => !refusedChat.Ok);
    var refusedList = await unauthorised.ListChatsAsync();
    Check("chats list without a token is empty, not an exception", () => refusedList.Count == 0);

    var firstId = threads.Count > 0 ? threads[0].Id : "";
    var renamed = await client.RenameAsync(firstId, "Новое имя");
    Check("rename over HTTP works", () => renamed);

    if (renamed)
    {
        var afterRename = await client.ListChatsAsync();
        Check("the new title is visible in the drawer",
            () => afterRename.Any(t => t.Id == firstId && t.Title == "Новое имя"));
    }

    var freshId = await client.NewChatAsync("Пустой чат");
    Check("a new empty thread can be created", () => !string.IsNullOrWhiteSpace(freshId));
    await CheckAsync("latest chat points at the newest thread", async () => await client.LatestChatAsync() == freshId);

    var deleteSucceeded = await client.DeleteChatAsync(freshId ?? "");
    Check("delete over HTTP works", () => deleteSucceeded);
    await CheckAsync("latest chat falls back after delete", async () => await client.LatestChatAsync() != freshId);

    // The phone keeps the deleted id in memory for a moment: the server must refuse it
    // instead of quietly creating a new chat under the same idea ("чат вернулся сам").
    var deletedChat = await client.SendAsync("привет", freshId, default);
    Check("sending to a deleted chat is refused, not silently recreated", () =>
        deletedChat.HttpStatus == 409 && !deletedChat.Ok);

    var afterDelete = await client.ListChatsAsync();
    Check("the deleted chat did not come back", () => afterDelete.All(t => t.Id != freshId));
}

// The exact reported failure: a second server on the same port. It must reuse the running
// OLI instead of throwing "address already in use" at the UI.
Console.WriteLine("Server on a busy port");
var busyPort2 = FreePort();
var busyRoot = Path.Combine(Path.GetTempPath(), "oli-busy-" + Guid.NewGuid().ToString("N"));
await using (var firstServer = new OLI.Server.ServerHost(new OLI.Server.ServerOptions
{
    DataRoot = busyRoot,
    Host = "127.0.0.1",
    Port = busyPort2,
    LlamaBaseUrl = "http://127.0.0.1:9",
}))
{
    await firstServer.StartAsync();
    Check("the first server binds its port", () => firstServer.IsRunning && firstServer.ActivePort == busyPort2);

    await using var second = new OLI.Server.ServerHost(new OLI.Server.ServerOptions
    {
        DataRoot = Path.Combine(busyRoot, "second"),
        Host = "127.0.0.1",
        Port = busyPort2,
        LlamaBaseUrl = "http://127.0.0.1:9",
    });
    var startedWithoutException = true;
    try { await second.StartAsync(); }
    catch { startedWithoutException = false; }

    Check("a second server reuses a running OLI instead of crashing", () => startedWithoutException && second.IsRunning);
    Check("reusing is explained in one sentence, not a stack trace",
        () => second.PortNotice.Contains("уже работает", StringComparison.OrdinalIgnoreCase));
    Check("stopping the reusing instance does not kill the working server", () =>
    {
        second.StopAsync().GetAwaiter().GetResult();
        return firstServer.IsRunning;
    });
}

// A foreign program on the port: the server must move, and say where.
var foreign = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
foreign.Start();
var foreignPort = ((System.Net.IPEndPoint)foreign.LocalEndpoint).Port;
try
{
    await using var moved = new OLI.Server.ServerHost(new OLI.Server.ServerOptions
    {
        DataRoot = Path.Combine(busyRoot, "moved"),
        Host = "127.0.0.1",
        Port = foreignPort,
        LlamaBaseUrl = "http://127.0.0.1:9",
    });
    await moved.StartAsync();
    Check("a port held by another program makes OLI move to a free one", () =>
        moved.ActivePort != foreignPort && moved.ActivePort > 0 && moved.IsRunning);
    Check("the move is reported to the user", () => moved.PortNotice.Contains("переехал") || moved.PortNotice.Contains("занят"));
}
finally { foreign.Stop(); }
try { Directory.Delete(busyRoot, true); } catch { }

try { Directory.Delete(liveRoot, true); } catch { }

static int FreePort()
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

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
