using System.Collections.Concurrent;
using OLI.Core;
using OLI.Engine;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:8765");
var app = builder.Build();
var llama = new LlamaClient("http://127.0.0.1:8080");
var devices = new ConcurrentDictionary<string, DateTimeOffset>();
const int PairMinutes = 10;
const int MaxMessages = 64;
const int MaxMessageChars = 16000;

app.MapGet("/api/health", () => Results.Ok(new { service = "OLI Mobile Gateway", status = "ready", version = "1.3.1" }));
app.MapPost("/api/pair", () =>
{
    var token = Guid.NewGuid().ToString("N");
    devices[token] = DateTimeOffset.UtcNow.AddMinutes(PairMinutes);
    return Results.Ok(new { token, expiresInMinutes = PairMinutes });
});
app.MapPost("/api/chat", async (MobileChatRequest request, HttpRequest http, CancellationToken ct) =>
{
    if (!http.Headers.TryGetValue("X-OLI-Device", out var token) || !devices.TryGetValue(token.ToString(), out var expires) || expires <= DateTimeOffset.UtcNow)
        return Results.Unauthorized();
    if (request.Messages is null || request.Messages.Count == 0 || request.Messages.Count > MaxMessages)
        return Results.BadRequest(new { error = "Invalid message count" });
    if (request.Messages.Any(x => string.IsNullOrWhiteSpace(x.Content) || x.Content.Length > MaxMessageChars))
        return Results.BadRequest(new { error = "Message is too large" });
    var messages = request.Messages.Select(x => new ChatMessage(x.Role, x.Content)).ToList();
    var answer = await llama.ChatAsync(messages, request.Model ?? "oli", request.Temperature ?? 0.7, Math.Clamp(request.MaxTokens ?? 512, 1, 4096), ct: ct);
    return Results.Ok(new { text = answer });
});
app.Run();

public record MobileChatMessage(string Role, string Content);
public record MobileChatRequest(List<MobileChatMessage> Messages, string? Model, double? Temperature, int? MaxTokens);
