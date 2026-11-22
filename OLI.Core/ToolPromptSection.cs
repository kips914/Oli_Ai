using System.Text;

namespace OLI.Core;

/// <summary>
/// The workspace / Telegram tool catalogue that is appended to the system prompt.
/// Living in OLI.Core (rather than OLI.Agent) keeps Persona dependency-free while
/// still describing exactly the tools OLI.Agent actually implements.
/// </summary>
public static class ToolPromptSection
{
    public static string Build(bool workspaceEnabled, bool workspaceCommands, bool telegram)
    {
        if (!workspaceEnabled && !telegram) return "";

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Инструменты (workspace):");
        sb.AppendLine("У тебя есть личная папка workspace с файлами. Пользователь её видит и может скачать любой файл.");
        sb.AppendLine("Чтобы вызвать инструмент, выведи ОДНУ строку вида (валидный JSON, без markdown-обёртки):");
        sb.AppendLine("OLI_TOOL: <имя_инструмента> {json-аргументы}");
        sb.AppendLine("Если нужно несколько действий — выведи несколько таких строк, каждая на своей строке.");
        sb.AppendLine("Не смешивай вызов инструмента с обычным текстом в одном ответе. После вызова ты получишь сообщение с результатом и продолжишь.");
        sb.AppendLine();
        sb.AppendLine("Инструменты:");
        sb.AppendLine("- save_file   {\"path\":\"otchet.md\",\"content\":\"...\"}            — создать/перезаписать файл");
        sb.AppendLine("- append_file {\"path\":\"otchet.md\",\"content\":\"...\"}            — дописать текст в конец файла");
        sb.AppendLine("- read_file   {\"path\":\"otchet.md\"}                             — прочитать файл (txt, md, код, docx, epub, odt)");
        sb.AppendLine("- edit_file   {\"path\":\"a.py\",\"find\":\"старое\",\"replace\":\"новое\",\"all\":false} — точечная замена");
        sb.AppendLine("- list_files  {\"path\":\"\",\"recursive\":true}                    — показать содержимое папки");
        sb.AppendLine("- make_dir    {\"path\":\"proekty/2026\"}                          — создать папку");
        sb.AppendLine("- move_path   {\"from\":\"a.txt\",\"to\":\"docs/a.txt\"}               — переместить/переименовать");
        sb.AppendLine("- delete_path {\"path\":\"a.txt\",\"recursive\":false}               — удалить файл или папку");
        sb.AppendLine("- zip_files   {\"path\":\"docs\",\"destination\":\"archives/docs.zip\"} — упаковать файл/папку в .zip");
        sb.AppendLine("- unzip_files {\"path\":\"archives/docs.zip\",\"destination\":\"docs\"} — распаковать .zip");
        sb.AppendLine("- list_archives {}                                            — список архивов");
        if (workspaceCommands)
            sb.AppendLine("- run_command {\"command\":\"python\",\"args\":[\"script.py\"],\"timeout\":60} — запустить разрешённую программу (python, node, git, dotnet, ...) в папке files");

        sb.AppendLine();
        sb.AppendLine("Правила работы с файлами:");
        sb.AppendLine("- Пути только относительные и только внутри workspace (например «docs/plan.md»). Запрещены «..», абсолютные пути, диски.");
        sb.AppendLine("- Квота workspace ограничена (по умолчанию 1 ГБ): если места нет — сначала удали ненужное.");
        sb.AppendLine("- Если пользователь просит что-то собрать, сохранить или упаковать — реально создавай файлы этими инструментами, а не только описывай.");
        sb.AppendLine("- Для больших файлов создавай их частями: save_file, затем append_file.");
        sb.AppendLine("- Архивы всегда .zip; их можно отдать пользователю (send_file).");

        if (telegram)
        {
            sb.AppendLine();
            sb.AppendLine("Telegram:");
            sb.AppendLine("Чтобы отправить пользователю файл из workspace в Telegram, выведи:");
            sb.AppendLine("OLI_TOOL: send_file {\"path\":\"otchet.md\"}");
            sb.AppendLine("Отвечай кратко: сообщения в Telegram не должны быть длиннее 3500 символов.");
        }

        return sb.ToString();
    }
}
