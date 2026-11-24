using System.Text;
using OLI.Core;
using OLI.Engine;
using OLI.Memory;
using OLI.Web;
using OLI.Workspace;

namespace OLI.Agent;

public enum AgentEventKind { Status, Search, Page, Tool, Answer, Error, File, Delta }

/// <summary>A progress notification emitted while a turn is running (UI + Telegram).</summary>
public sealed record AgentEvent(AgentEventKind Kind, string Text);

public sealed record AgentAnswer(string Text, List<string> FilesToSend);

/// <summary>
/// One conversation with OLI: keeps history, memory and profile, and runs the
/// bounded think → search → open pages → use tools → answer loop. Both the WPF
/// client and the Telegram bot drive this same class, so behaviour is identical.
/// </summary>
public sealed class AgentSession : IDisposable
{
    private const int MaxSteps = 8;

    private readonly LlamaClient _llama;
    private readonly WebSearch _web;
    private readonly MemoryStore _memory;
    private readonly UserProfileStore _profile;
    private readonly ContextBuilder _context = new();
    private readonly OliSettings _settings;
    private readonly WorkspaceEngine? _workspace;
    private readonly ToolDispatcher? _tools;
    private readonly List<ChatMessage> _history = [];
    private readonly object _historyLock = new();
    private readonly bool _ownsWebSearch;

    public AgentSession(
        OliSettings settings,
        LlamaClient llama,
        WebSearch web,
        MemoryStore memory,
        UserProfileStore profile,
        WorkspaceEngine? workspace,
        bool ownsWebSearch = true,
        bool? allowCommands = null)
    {
        _settings = settings;
        _ownsWebSearch = ownsWebSearch;
        _llama = llama;
        _web = web;
        _memory = memory;
        _profile = profile;
        _workspace = workspace;
        if (workspace is not null)
            _tools = new ToolDispatcher(workspace, allowCommands ?? settings.AllowCommandExecution);
        ApplySearchSettings();
    }

    public void ApplySearchSettings()
    {
        _web.OpenWeb = _settings.WebSearchOpenWeb;
        _web.BlockedDomains = _settings.WebSearchBlockedDomains;
        _web.AllowedDomains = _settings.AllowedSites.Where(s => s.IsEnabled).Select(s => s.Domain).ToList();
        _web.MaxPageChars = Math.Max(4000, _settings.ContextSize * 2);
    }

    public IReadOnlyList<ChatMessage> History
    {
        get { lock (_historyLock) return _history.ToList(); }
    }

    /// <summary>
    /// Switches the model for the next answer (Olim Pass / Olim Son). The llama.cpp
    /// server must already be serving it; otherwise the request fails and the caller
    /// sees a clear error.
    /// </summary>
    public void SelectModel(string modelId)
    {
        var info = OLI.Core.ModelCatalog.Resolve(modelId);
        _settings.ModelGguf = info.Gguf;
        _settings.Model = info.Id;
        _settings.ModelDisplayName = info.DisplayName;
        _settings.MaxTokens = _answerBudget > 0 ? _answerBudget : info.Id == OLI.Core.ModelCatalog.LiteId ? 320 : 640;
    }

    private int _answerBudget;

    /// <summary>Caps the answer length (used when the user's token limit is exhausted).</summary>
    public void SetAnswerBudget(int maxTokens)
    {
        _answerBudget = Math.Max(0, maxTokens);
        if (_answerBudget > 0) _settings.MaxTokens = _answerBudget;
    }

    public void Reset()
    {
        lock (_historyLock) _history.Clear();
        _loadedThread = "";
    }

    /// <summary>
    /// The thread the in-memory history belongs to. The server switches between the user's
    /// threads, and without this the model would remember the previous chat — the single
    /// most confusing bug of the old chat core.
    /// </summary>
    private string _loadedThread = "";

    /// <summary>
    /// Points the session at a chat thread. When the thread changes, the history is
    /// replaced by that thread's transcript (so "продолжи прошлую мысль" works after a
    /// restart) — and never mixes two conversations together.
    /// </summary>
    public void UseThread(string threadId, IReadOnlyList<ChatEntry> history)
    {
        var id = threadId ?? "";
        lock (_historyLock)
        {
            if (string.Equals(_loadedThread, id, StringComparison.Ordinal)) return;
            _loadedThread = id;
            _history.Clear();
            foreach (var item in ChatCore.ToModelHistory(history))
                _history.Add(item);
        }
    }

    /// <summary>Id of the thread the history belongs to ("" for the desktop single chat).</summary>
    public string LoadedThread => _loadedThread;

    public async Task<AgentAnswer> SendAsync(
        string userText,
        IProgress<AgentEvent>? progress = null,
        CancellationToken ct = default)
    {
        var files = new List<string>();
        progress?.Report(new AgentEvent(AgentEventKind.Status, "думаю"));

        _profile.LearnFrom(userText);

        var memoryContext = _memory.BuildContext();
        var profileContext = _profile.Profile.BuildContext();
        var systemPrompt = Persona.BuildSystemPrompt(
            _settings.AllowedSites,
            webSearchEnabled: _settings.WebSearchEnabled,
            userMemory: profileContext,
            mode: _settings.Mode,
            openWeb: _settings.WebSearchOpenWeb,
            workspaceEnabled: _workspace is not null && _settings.WorkspaceEnabled,
            workspaceCommands: _workspace is not null && (_tools?.CommandsAllowed ?? false),
            telegram: _settings.Telegram.Enabled,
            extra: _settings.SystemPromptExtra,
            thinking: _settings.ThinkingEnabled);

        var userTurn = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(profileContext))
            userTurn.AppendLine(Localization.T("profile.label") + "\n" + profileContext + "\n");
        if (!string.IsNullOrWhiteSpace(memoryContext))
            userTurn.AppendLine(Localization.T("memory.label") + "\n" + memoryContext + "\n");
        userTurn.Append(userText);
        lock (_historyLock) _history.Add(new ChatMessage("user", userTurn.ToString()));

        var answer = await AskStreamingAsync(systemPrompt, progress, ct);
        var usedSearch = false;

        for (var step = 0; step < MaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            var trimmed = answer.Trim();

            // 1) Workspace tools have priority.
            var toolCalls = _tools is not null ? ToolProtocol.Parse(trimmed) : [];
            if (toolCalls.Count > 0)
            {
                lock (_historyLock) _history.Add(new ChatMessage("assistant", answer));
                foreach (var call in toolCalls)
                    progress?.Report(new AgentEvent(AgentEventKind.Tool, _tools!.DescribeCall(call)));

                var result = await _tools!.ExecuteAsync(toolCalls, ct);
                foreach (var path in _tools.FilesToSend)
                {
                    files.Add(path);
                    progress?.Report(new AgentEvent(AgentEventKind.File, path));
                }
                _tools.FilesToSend.Clear();
                lock (_historyLock) _history.Add(new ChatMessage("user", result));

                progress?.Report(new AgentEvent(AgentEventKind.Status, "думаю"));
                answer = await AskStreamingAsync(systemPrompt, progress, ct);
                continue;
            }

            // 2) Search request.
            var searchQuery = ProtocolMarkers.ExtractSearchQuery(trimmed);
            if (searchQuery is not null && _settings.WebSearchEnabled)
            {
                lock (_historyLock) _history.Add(new ChatMessage("assistant", answer));
                progress?.Report(new AgentEvent(AgentEventKind.Search, searchQuery));
                var block = await BuildSearchBlockAsync(searchQuery, ct);
                lock (_historyLock) _history.Add(new ChatMessage("user", block));
                usedSearch = true;
                progress?.Report(new AgentEvent(AgentEventKind.Status, "думаю"));
                answer = await AskStreamingAsync(systemPrompt, progress, ct);
                continue;
            }

            // 3) Open page(s).
            var urls = ProtocolMarkers.ExtractOpenUrls(trimmed);
            if (urls.Count > 0 && _settings.WebSearchEnabled)
            {
                lock (_historyLock) _history.Add(new ChatMessage("assistant", answer));
                var block = await BuildPagesBlockAsync(urls, progress, ct);
                lock (_historyLock) _history.Add(new ChatMessage("user", block));
                progress?.Report(new AgentEvent(AgentEventKind.Status, "думаю"));
                answer = await AskStreamingAsync(systemPrompt, progress, ct);
                continue;
            }

            break;
        }

        var (clean, facts) = ProtocolMarkers.ExtractMemoryFacts(answer);
        clean = ToolProtocol.StripToolLines(clean);
        foreach (var fact in facts)
        {
            await _memory.AddAsync(fact);
            OliLog.Info("MEMORY", $"Сохранено: {fact}");
        }

        if (clean.Trim().Length == 0)
            clean = facts.Count > 0 ? Localization.T("remembered") : Localization.T("protocol.exhausted");

        lock (_historyLock) _history.Add(new ChatMessage("assistant", clean));
        if (usedSearch) OliLog.Info("AGENT", "Ответ построен с использованием поиска");
        progress?.Report(new AgentEvent(AgentEventKind.Answer, clean));
        return new AgentAnswer(clean, files);
    }

    private Task<string> AskAsync(string systemPrompt, CancellationToken ct)
    {
        IReadOnlyList<ChatMessage> messages;
        lock (_historyLock)
            messages = _context.Build(_history, systemPrompt, _settings.ContextSize);

        OliLog.Debug("CONTEXT", $"messages={messages.Count}, limit={_settings.ContextSize}");
        return _llama.ChatAsync(
            messages,
            _settings.Model,
            _settings.Temperature,
            _settings.MaxTokens,
            _settings.TopP,
            _settings.TopK,
            _settings.RepeatPenalty,
            ct);
    }

    /// <summary>Final answer generation with token streaming (UI shows it live).</summary>
    private async Task<string> AskStreamingAsync(string systemPrompt, IProgress<AgentEvent>? progress, CancellationToken ct)
    {
        IReadOnlyList<ChatMessage> messages;
        lock (_historyLock)
            messages = _context.Build(_history, systemPrompt, _settings.ContextSize);

        if (progress is null)
            return await AskAsync(systemPrompt, ct);

        var answer = await _llama.ChatStreamAsync(
            messages,
            _settings.Model,
            _settings.Temperature,
            _settings.MaxTokens,
            delta => progress.Report(new AgentEvent(AgentEventKind.Delta, delta)),
            _settings.TopP,
            _settings.TopK,
            _settings.RepeatPenalty,
            ct);

        // Models sometimes prefix the answer with a bare /no_think marker.
        return answer.Replace("/no_think", "", StringComparison.Ordinal).Trim();
    }

    private async Task<string> BuildSearchBlockAsync(string query, CancellationToken ct)
    {
        try
        {
            ApplySearchSettings();
            var results = await _web.SearchAsync(query, _settings.WebSearchResultLimit, ct: ct);
            if (results.Count == 0)
                return Localization.T("search.none", query);

            var sb = new StringBuilder();
            sb.AppendLine(Localization.T("search.block", query));
            foreach (var r in results)
            {
                sb.AppendLine($"- {r.Title}");
                if (!string.IsNullOrWhiteSpace(r.Snippet)) sb.AppendLine($"  {r.Snippet}");
                sb.AppendLine($"  Источник: {r.Url}");
            }
            sb.AppendLine(Localization.T("search.followup"));
            return sb.ToString();
        }
        catch (WebSearchException ex)
        {
            OliLog.Warning("WEB", ex.Message);
            return Localization.T("search.unavailable", ex.Message);
        }
    }

    private async Task<string> BuildPagesBlockAsync(List<string> urls, IProgress<AgentEvent>? progress, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Localization.T("page.block"));
        foreach (var url in urls)
        {
            progress?.Report(new AgentEvent(AgentEventKind.Page, url));
            sb.AppendLine();
            try
            {
                var page = await _web.ReadPageAsync(url, ct: ct);
                progress?.Report(new AgentEvent(AgentEventKind.Page, page.Title.Length > 0 ? page.Title : url));
                sb.AppendLine(page.ToPromptBlock(Math.Max(4000, _settings.ContextSize * 2)));
            }
            catch (WebSearchException ex)
            {
                OliLog.Warning("WEB", $"{url}: {ex.Message}");
                sb.AppendLine(Localization.T("page.failed", ex.Message));
            }
        }
        sb.AppendLine();
        sb.AppendLine(Localization.T("page.followup"));
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_ownsWebSearch) _web.Dispose();
    }
}
