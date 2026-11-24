using System.Net.Http;

namespace OLI.Core;

/// <summary>How one setup step ended.</summary>
public enum SetupStepState
{
    /// <summary>Работает.</summary>
    Ok,
    /// <summary>Мешает пользоваться, но поправимо автоматически (кнопкой).</summary>
    Warning,
    /// <summary>Точно не работает — нужен шаг пользователя (UAC, VPN, другой порт).</summary>
    Failed,
    /// <summary>Не проверялось (нет условий: сервер выключен, туннель не включён).</summary>
    Skipped,
}

/// <summary>One line of the connectivity check, with an honest reason when it fails.</summary>
public sealed record SetupStep(string Title, SetupStepState State, string Detail, string? Action = null)
{
    public bool Ok => State is SetupStepState.Ok or SetupStepState.Skipped;

    public string Line => (State switch
    {
        SetupStepState.Ok => "✓ ",
        SetupStepState.Warning => "⚠ ",
        SetupStepState.Failed => "✗ ",
        _ => "· ",
    }) + Title + (Detail.Length > 0 ? ": " + Detail : "");
}

/// <summary>Result of the whole mini-test, plus one sentence the user can act on.</summary>
public sealed record SetupCheck(IReadOnlyList<SetupStep> Steps, string Summary)
{
    public bool AllOk => Steps.All(s => s.State is SetupStepState.Ok or SetupStepState.Skipped);

    public IReadOnlyList<string> Lines => Steps.Select(s => s.Line).ToList();
}

/// <summary>
/// The first-run connectivity test — the same three questions the phone asks, run on the PC:
/// does the local server answer, can the LAN reach it (firewall), and does the public tunnel
/// address work from the outside?
///
/// It exists because "туннель не поднимается" was impossible to diagnose: the old code
/// reported one line and one guess. Every step here names its own cause and its own fix.
/// </summary>
public static class SetupCheckCore
{
    public const int DefaultPort = ConnectionCore.DefaultPort;

    /// <summary>
    /// Runs the checks. <paramref name="publicUrl"/> is the tunnel address (empty when the
    /// tunnel is off). Never throws: a failure is a step with a reason.
    /// </summary>
    public static async Task<SetupCheck> RunAsync(
        int port,
        bool serverExpected,
        string? publicUrl,
        IEnumerable<string> lanAddresses,
        Func<string, Task<bool>>? probe = null,
        CancellationToken ct = default)
    {
        var steps = new List<SetupStep>();
        probe ??= async url => await DefaultProbeAsync(url, ct);

        // 1. Local server. This is the step that fails when the port is taken, the app is
        //    not elevated, or the server was never started.
        var localUrl = $"http://127.0.0.1:{port}";
        var localReady = await probe(localUrl);
        steps.Add(localReady
            ? new SetupStep("Сервер OLI", SetupStepState.Ok, $"отвечает на {port}")
            : serverExpected
                ? new SetupStep("Сервер OLI", SetupStepState.Failed,
                    $"не отвечает на порту {port}. Проверьте, что сервер запущен и порт не занят.",
                    "Запустить сервер")
                : new SetupStep("Сервер OLI", SetupStepState.Skipped, "сервер выключен в настройках"));

        // 2. The address the phone must use. Loopback works even when the firewall is closed,
        //    so this is the only honest firewall test there is.
        var lan = ConnectionCore.CandidateOrder(lanAddresses).Take(3).ToList();
        if (lan.Count == 0)
        {
            steps.Add(new SetupStep("Адрес в Wi-Fi", SetupStepState.Warning,
                "не найден адрес Wi-Fi/Ethernet — телефон по локальной сети не подключится"));
        }
        else
        {
            SetupStep? firstWorking = null;
            var failures = new List<string>();
            foreach (var address in lan)
            {
                var url = $"http://{address}:{port}";
                if (await probe(url))
                {
                    firstWorking ??= new SetupStep("Адрес в Wi-Fi", SetupStepState.Ok, url);
                }
                else
                {
                    failures.Add(address);
                }
            }

            steps.Add(firstWorking ?? new SetupStep("Адрес в Wi-Fi", SetupStepState.Failed,
                $"{string.Join(", ", failures)} не отвечают, хотя локальный сервер работает — " +
                "порт закрыт брандмауэром Windows.",
                "Открыть порт"));
        }

        // 3. Public address: gives an honest "works from mobile data" answer before the
        //    user leaves the house.
        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            steps.Add(new SetupStep("Доступ из интернета", SetupStepState.Skipped,
                "выключен — телефон подключится только по домашнему Wi-Fi"));
        }
        else if (ConnectionCore.Normalize(publicUrl) is null)
        {
            steps.Add(new SetupStep("Доступ из интернета", SetupStepState.Failed,
                "адрес туннеля не распознан: " + publicUrl));
        }
        else
        {
            var ok = await probe(publicUrl);
            steps.Add(ok
                ? new SetupStep("Доступ из интернета", SetupStepState.Ok, publicUrl)
                : new SetupStep("Доступ из интернета", SetupStepState.Failed,
                    $"адрес {publicUrl} выдан, но не отвечает. Проверьте лог туннеля: " +
                    "cloudflared мог не скачаться, либо провайдер блокирует туннель.",
                    "Журнал туннеля"));
        }

        return new SetupCheck(steps, Summarize(steps));
    }

    /// <summary>One sentence at the bottom of the window — what to do next.</summary>
    public static string Summarize(IReadOnlyList<SetupStep> steps)
    {
        var failed = steps.Where(s => s.State == SetupStepState.Failed).ToList();
        var warnings = steps.Where(s => s.State == SetupStepState.Warning).ToList();

        if (failed.Count == 0 && warnings.Count == 0)
            return "Всё готово: OLI работает, телефон подключится и дома, и на мобильном интернете.";

        if (failed.Count == 0)
            return warnings[0].Detail;

        var first = failed[0];
        return first.Action is { Length: > 0 }
            ? $"{first.Detail} Нажмите «{first.Action}» ниже."
            : first.Detail;
    }

    private static async Task<bool> DefaultProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await http.GetAsync(url.TrimEnd('/') + "/api/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
