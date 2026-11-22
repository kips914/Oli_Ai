# OLI 1.4.0

Крупное обновление: новая модель, workspace с файлами и архивами, полноценный
поиск, Telegram-бот с обходом блокировок, новый интерфейс и сборка exe.

## 1. Модель Qwen3.5 4B

* Модель по умолчанию — `unsloth/Qwen3.5-4B-GGUF:Q4_K_M` (~2.9 ГБ).
* Каталог моделей в настройках: 4B IQ4_XS / Q4_K_M / Q6_K, 2B Q4_K_M,
  9B Q4_K_M, Qwen3 4B Instruct как fallback.
* Qwen-sampling: `top_p`, `top_k`, `repeat_penalty`, `temperature` — из настроек.
* Режим размышлений (thinking) через `--chat-template-kwargs enable_thinking`.
* `scripts/start-llama.ps1` читает выбранную модель из `settings.json` или
  принимает `-Model <id>`, `-Cpu`.

## 2. Workspace 1 ГБ с инструментами

Новый проект **OLI.Workspace** и раздел «Workspace» в UI.

* Инструменты модели: `save_file`, `append_file`, `read_file`, `edit_file`,
  `list_files`, `make_dir`, `move_path`, `delete_path`, `zip_files`,
  `unzip_files`, `list_archives`, `run_command`, `send_file`.
* Квота 1 ГБ (256 МБ…5 ГБ), лимит на файл, контроль свободного места при
  записи и распаковке.
* `PathGuard`: защита от `..`, абсолютных путей, дисков, UNC и симлинков;
  zip-slip защита при распаковке; отказ от зарезервированных имён Windows.
* `run_command` — только белый список программ, запуск в `files/`, таймаут,
  ограничение вывода; доступен и из Telegram-бота (по настройке).
* В UI: просмотр файлов, переход по папкам, удаление, «Zip всё в архив»,
  индикатор занятого места и список архивов.

## 3. Поиск в интернете

* Четыре провайдера с автоматическим фолбэком: DuckDuckGo, Bing, Mojeek,
  Wikipedia; опционально SearXNG.
* Режим «весь интернет» (по умолчанию) с чёрным списком доменов, либо
  «только разрешённые сайты».
* Реальное чтение страниц: кодировка, title/description, читаемый текст,
  приоритет `<main>`/`<article>`.
* Защита от SSRF: localhost, приватные диапазоны, `.local`/`.internal`
  блокируются всегда.
* Настраиваемый лимит результатов и таймаут, провайдер выбирается в UI.

## 4. Telegram-бот + обход блокировки в РФ

Новый проект **OLI.Telegram**.

* Long polling, отдельная история на каждый чат, память и профиль общие.
* Отправка файлов из workspace (`send_file`, `/zip`), приём ваших файлов
  в `workspace/inbox`.
* Команды: `/start`, `/new`, `/files`, `/zip`, `/status`, `/help`.
* Ограничение доступа по списку Telegram ID.
* Транспорты с автопереключением: **Cloudflare-релей → прокси/SOCKS5 →
  DNS-over-HTTPS → прямое подключение**. Первый рабочий запоминается.
* Готовый Cloudflare Worker в `relay/cloudflare-worker` + инструкция
  (включая вариант с nginx на VPS).

## 5. Новый дизайн

* Боковая навигация: Чат / Workspace / Поиск / Telegram / Консоль.
* Дизайн-система: палитра, акцентный градиент, карточки, чипы, кастомные
  слайдеры, комбобоксы, чекбоксы, кнопки.
* Тёмная и светлая темы, «Системная» учитывает настройку Windows.
* Живые статусы: «ищу», «читаю страницу», «сохраняю файл» прямо в чате.
* Пузыри сообщений с ролями, плавная прокрутка, аккуратная консоль.

## 6. Сборка exe

* `scripts\build-oli.ps1 -Portable` → `dist\OLI.exe` (self-contained,
  single-file) и портативный zip.
* `Install-OLI.ps1` создаёт ярлыки через `OLI.cmd` → `scripts\start-oli.ps1`.
* GitHub Actions `.github/workflows/build-exe.yml` собирает exe на
  `windows-latest`, складывает в артефакты и (на теге `v*`) в релиз.
* `scripts\install-oli.ps1` теперь выбирает бэкенд: CUDA (NVIDIA),
  Vulkan (AMD/Intel) или CPU, и поддерживает зеркало GitHub
  (`$env:OLI_GITHUB_MIRROR`) для машин в РФ.

## Совместимость

* Старые `settings.json` читаются: новые поля получают значения по умолчанию,
  `AllowedSites` дополняются новыми сайтами.
* Данные из `data/` по-прежнему мигрируют в `%LOCALAPPDATA%\OLI`.
