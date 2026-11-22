using System.Text.Json;

if (args.Length < 1)
{
    Console.WriteLine("Usage: dotnet run --project OLI.Training -- add \"input\" \"output\"");
    return;
}

Directory.CreateDirectory("data/datasets");
var file = "data/datasets/oli_train.jsonl";

if (args[0].Equals("add", StringComparison.OrdinalIgnoreCase) && args.Length >= 3)
{
    var item = new
    {
        instruction = args[1],
        input = "",
        output = args[2]
    };

    await File.AppendAllTextAsync(
        file,
        JsonSerializer.Serialize(item) + Environment.NewLine);

    Console.WriteLine($"Added: {file}");
}
else
{
    Console.WriteLine("Unknown command.");
}
