# Архитектура OLI 1.9

Документ для тех, кто будет дорабатывать код: что где лежит, как проходит
запрос, какие есть ограничения безопасности.

## Поток одного сообщения

```
Пользователь (WPF / Telegram / Android-приложение)
        │
        ▼
AgentSession.SendAsync(text)
        │  собирает system prompt (Persona + ToolPromptSection)
        │  добавляет память и профиль
        ▼
   ┌───────────────────────────────┐
   │  цикл (не более 8 шагов)      │
   │  1. есть OLI_TOOL: … ? ───────┼──► ToolDispatcher ──► WorkspaceEngine
   │  2. есть OLI_SEARCH: … ? ─────┼──► WebSearch (DDG/Bing/Mojeek/Wikipedia)
   │  3. есть OLI_OPEN: … ? ───────┼──► WebSearch.ReadPageAsync
   │  иначе — это ответ           │
   └───────────────────────────────┘
        │
        ▼
   финальный текст + OLI_REMEMBER + send_file
```

Каждый шаг цикла — отдельная генерация. Промежуточные ответы модели попадают в
историю, чтобы она видела, что уже сделала. Ответ стримится в UI
(`AgentEventKind.Delta`), в Telegram — редактированием сообщения «⏳ Думаю...».

## Проекты

| Проект | Ответственность | Зависит от |
|--------|-----------------|-----------|
| `OLI.Core` | настройки (+апгрейд старых), локализация, персона, каталог моделей, пути, логи | — |
| `OLI.Engine` | HTTP-клиент llama.cpp: sampling, стриминг, health | Core |
| `OLI.Memory` | память и профиль пользователя (JSON, атомарная запись) | Core |
| `OLI.Web` | провайдеры поиска, чтение страниц, политика домена, SSRF-защита | Core |
| `OLI.Workspace` | песочница файлов: квота, `PathGuard`, zip/unzip, команды | Core |
| `OLI.Server` | HTTP API для телефона: аккаунты (PBKDF2), токены, квоты, чат, файлы; `ServerHost` живёт и в WPF, и в `OLI.Server.exe` | Core, Engine, Agent, Web, Memory, Workspace |
| `Android/OLI-Mobile` | Kotlin/Compose-клиент: вход/регистрация, чат, файлы, EncryptedSharedPreferences | — (HTTP к OLI.Server) |
| `OLI.Agent` | `AgentSession`, протокол инструментов, `ToolDispatcher`, маркеры | Core, Engine, Web, Memory, Workspace |
| `OLI.Telegram` | бот (long polling), `TelegramApi`, туннели (relay/proxy/DoH/direct) | Core, Agent, Workspace |
| `OLI.Client` | WPF UI, тема, настройки, связывание всего | все |
| `OLI.SelfTest` | headless-проверки песконицы/zip/парсеров/политики (CI) | Core, Workspace, Web, Agent |
| `OLI.MobileGateway` | HTTP-шлюз для мобильного клиента | Core, Engine |

## Протокол инструментов

Модель не использует function calling — он ненадёжен на маленьких GGUF.
Вместо этого — строковый протокол, который одинаково работает в UI и Telegram:

```
OLI_TOOL: save_file {"path":"docs/a.md","content":"..."}
OLI_SEARCH: погода в Санкт-Петербурге
OLI_OPEN: https://example.com/page
OLI_REMEMBER: пользователь учится на программиста
```

* `OLI_TOOL` — можно несколько строк подряд; JSON допускается многострочный
  (парсер считает баланс скобок с учётом строк).
* `OLI_SEARCH` / `OLI_OPEN` срабатывают только если это **весь** ответ —
  случайное упоминание в тексте не запускает поиск.
* `OLI_REMEMBER` вырезается из ответа и сохраняется в память.

Добавить инструмент: метод в `ToolDispatcher.ExecuteOneAsync`, строка в
`ToolPromptSection.Build` (и в `ToolDispatcher.DescribeCall` для UI).

## Безопасность

**Workspace.** Всё, что делает модель, проходит через `PathGuard.Resolve`:
абсолютные пути, `..`, диски, UNC, `~`, управляющие символы и URL отклоняются;
результат проверяется на вложенность в корень. Распаковка zip проверяет каждый
entry (zip-slip), запись и распаковка заряжаются в квоту. Команды — только из
белого списка, запускаются в корне песочницы, с таймаутом и обрезкой вывода.
Для Telegram есть отдельный флаг «разрешить команды» (по умолчанию выключен).

**Сеть.** `WebSearch` блокирует localhost, приватные диапазоны, link-local и
`.local`/`.internal` (SSRF), соблюдает чёрный список и (в режиме списка) —
белый. Страницы читаются с лимитом 8 МБ и 12 000 символов текста.

**Telegram.** Токен не хранится в туннелях — подставляется в URL в момент
запроса. Опциональный `RELAY_SECRET` защищает воркер. Список разрешённых
Telegram ID ограничивает доступ.

## Тема и UI

`OLI.Client/App.xaml` — дизайн-система (цвета, стили кнопок, полей, табов,
карточек). `App.ApplyTheme` перезаписывает кисти для светлой/тёмной/системной
темы. Все элементы интерфейса ищут кисти по ключу, поэтому новые экраны
автоматически поддерживают темы.

## Модели

`OLI.Core/ModelCatalog.cs` (UI) и `scripts/models.json` (движок) должны
содержать одинаковые `gguf`-спеки. `start-llama.ps1` берёт модель из
`settings.json → ModelGguf`, поэтому смена модели в UI + `-Restart` перезапускает
сервер с новым `-hf`.

## CI

`.github/workflows/build-exe.yml` на каждый push собирает библиотеки, гоняет
`OLI.SelfTest`, публикует single-file `OLI.exe` и на теге `v*` прикладывает
`OLI.exe` + `OLI-win-x64.zip` к GitHub Release.

## Многопользовательский режим (1.9)

```
Android ──HTTP──► OLI.Server (Kestrel, 0.0.0.0:8765)
Telegram ───────►      │
WPF «Пульт админа» ────┤ (loopback)
                       ▼
                AccountStore  ── accounts.json (только хеши паролей и токенов)
                UserWorkspaceRegistry ── users/<accountId>/ (по 1 ГБ)
                AgentSession на пользователя ── llama.cpp (общая модель, общий WebSearch)
```

* **Пароли**: `PBKDF2-HMAC-SHA256`, 210 000 итераций, 16-байтовая соль на
  пользователя, 32-байтовый хеш; сравнение `FixedTimeEquals`. Открытого пароля
  нет нигде — админ может только задать новый (сброс) и разослать его.
* **Токены**: 32 случайных байта, на сервере хранится SHA-256; 30 дней; не более
  8 устройств на аккаунт; смена/сброс пароля и блокировка отзывают все токены.
* **Файлы**: у каждого аккаунта своя `WorkspaceEngine` с `PathGuard` внутри
  `users/<id>/`; квота — `ServerSettings.UserQuotaBytes` (по умолчанию 1 ГБ);
  `run_command` для пользователей сервера выключен на уровне `AgentSession`.
* **Админ-API**: `/api/admin/*` разрешён с loopback (сам ПК) или по заголовку
  `X-OLI-Admin: <AdminToken>`, либо аккаунту с `IsAdmin`.
* **Телефон**: логин/пароль — `EncryptedSharedPreferences` (AES-256-GCM, ключ в
  Android Keystore), исключены из резервных копий; адрес сервера можно зашить
  при сборке: `-POLI_DEFAULT_SERVER=http://192.168.1.10:8765`.
