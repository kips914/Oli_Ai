package com.oli.mobile.data

import android.content.Context
import com.oli.mobile.BuildConfig
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Single source of truth for the UI: session state, chat history and file browsing. */
class OliRepository(private val context: Context) {

    private val api = OliApi.shared
    private val creds = CredentialStore.get(context)

    companion object {
        /** Default OLI server port (must match ServerSettings.Port on the PC). */
        const val DEFAULT_PORT = 8765
    }

    data class ChatMessage(
        val id: Long,
        val fromUser: Boolean,
        val text: String,
        val files: List<String> = emptyList(),
        /** Local thumbnail of an attached photo (null for pure text messages). */
        val image: android.graphics.Bitmap? = null,
        /** True while the answer to an attached image is still being prepared. */
        val analyzing: Boolean = false
    )

    private val _session = MutableStateFlow<Session>(Session.Loading)
    val session: StateFlow<Session> = _session.asStateFlow()

    private val _messages = MutableStateFlow<List<ChatMessage>>(emptyList())
    val messages: StateFlow<List<ChatMessage>> = _messages.asStateFlow()

    private val _busy = MutableStateFlow(false)
    val busy: StateFlow<Boolean> = _busy.asStateFlow()

    private val _usage = MutableStateFlow<Usage?>(null)
    val usage: StateFlow<Usage?> = _usage.asStateFlow()

    private val _files = MutableStateFlow<List<FileEntry>>(emptyList())
    val files: StateFlow<List<FileEntry>> = _files.asStateFlow()

    private val _models = MutableStateFlow<ModelState?>(null)
    val models: StateFlow<ModelState?> = _models.asStateFlow()

    private val _usageLimits = MutableStateFlow<UsageSnapshotData?>(null)
    val usageLimits: StateFlow<UsageSnapshotData?> = _usageLimits.asStateFlow()

    private val _chats = MutableStateFlow<List<ChatSummary>>(emptyList())
    val chats: StateFlow<List<ChatSummary>> = _chats.asStateFlow()

    private val _activeThread = MutableStateFlow<String?>(null)
    val activeThread: StateFlow<String?> = _activeThread.asStateFlow()

    /** Called when the answer says the limit is exhausted (UI switches to Olim Son). */
    private val _limitNotice = MutableStateFlow(false)
    val limitNotice: StateFlow<Boolean> = _limitNotice.asStateFlow()

    /** True when the OLI (default) server could not be reached at startup. */
    private val _defaultServerOffline = MutableStateFlow(false)
    val defaultServerOffline: StateFlow<Boolean> = _defaultServerOffline.asStateFlow()

    /** True while the app is searching the local network for the PC with the OLI server. */
    private val _discovering = MutableStateFlow(false)
    val discovering: StateFlow<Boolean> = _discovering.asStateFlow()

    private var nextId = 1L

    sealed interface Session {
        data object Loading : Session
        data object SignedOut : Session
        data class SignedIn(val user: UserProfile) : Session
        /** Preview of the interface without an account: nothing is sent to any server. */
        data class Demo(val returnTo: Session) : Session
    }

    // ------------------------------------------------------------- server address

    val isDemo: Boolean get() = _session.value is Session.Demo

    /** "default" (OLI cloud server) or "custom" (the user's own PC). */
    val serverMode: String get() = creds.serverMode
    val usingOwnServer: Boolean get() = creds.serverMode == CredentialStore.MODE_CUSTOM

    val customServer: String get() = creds.customServer

    val serverUrl: String get() = when {
        creds.serverMode == CredentialStore.MODE_CUSTOM && creds.customServer.isNotBlank() -> creds.customServer
        creds.lastEndpoint.isNotBlank() -> creds.lastEndpoint
        else -> BuildConfig.DEFAULT_SERVER
    }

    val lastUserName: String get() = creds.lastLoginName()

    /** Switches to the default OLI server and clears the custom address. */
    suspend fun useDefaultServer(): ServerHealth? {
        creds.serverMode = CredentialStore.MODE_DEFAULT
        creds.rememberEndpoint(BuildConfig.DEFAULT_SERVER)
        return probeDefaultServer()
    }

    /** Switches to the user's own PC at [url]; returns null when it is unreachable. */
    suspend fun useOwnServer(url: String): ServerHealth? {
        val normalized = url.trim().trimEnd('/')
        val health = api.health(normalized)
        creds.serverMode = CredentialStore.MODE_CUSTOM
        creds.customServer = normalized
        creds.rememberEndpoint(normalized)
        api.baseUrl = normalized
        _defaultServerOffline.value = false
        return health
    }

    /**
     * Looks for the OLI server on this Wi-Fi: remembered address first, then the whole
     * local subnet. Fixes "сервер не виден", when the PC's IP changed or was never typed.
     * Returns the address that answered, or null when nothing was found.
     */
    suspend fun findOwnServer(): String? {
        // 1. The address that worked last time (the PC usually keeps it).
        for (candidate in listOf(creds.customServer, creds.lastEndpoint)) {
            if (candidate.isBlank()) continue
            val normalized = candidate.trim().trimEnd('/')
            // A remembered https:// address is a tunnel: it only answers while the PC's
            // OLI is open, so a failure here just means "ask again / use a fresh code".
            if (runCatching { api.health(normalized) }.isSuccess) {
                selectOwnServer(normalized)
                return normalized
            }
        }

        // 2. Ask the local network.
        _discovering.value = true
        try {
            val found = LanScanner.findServer(port = DEFAULT_PORT) ?: return null
            val normalized = found.trimEnd('/')
            val health = runCatching { api.health(normalized) }.getOrNull() ?: return null
            if (!health.ok) return null
            selectOwnServer(normalized)
            return normalized
        } finally {
            _discovering.value = false
        }
    }

    /**
     * Connects with the "join code" from OLI on the PC (Пульт админа → интернет-доступ).
     * This is the path that works on mobile data at university: the code carries the
     * public tunnel address, so the phone needs no Wi-Fi and no typing of IPs.
     * Returns the health of the reached server (throws ApiException when it is silent).
     */
    suspend fun useJoinCode(code: String): ServerHealth {
        val parsed = api.parseJoinCode(code)
            ?: throw OliApi.ApiException("Это не похоже на код подключения OLI. Скопируйте строку из пульта администратора целиком.")
        val (url, invite) = parsed
        val health = try {
            api.health(url)
        } catch (e: Exception) {
            throw OliApi.ApiException(api.explain(url, e))
        }
        selectOwnServer(url)
        if (invite.isNotBlank()) pendingInvite = invite
        return health
    }

    /** Invite code that came inside a join code; the registration form prefills it. */
    var pendingInvite: String = ""
        private set

    fun consumeInvite(): String {
        val value = pendingInvite
        pendingInvite = ""
        return value
    }

    /** Makes [url] the active own-PC server without re-probing it. */
    private fun selectOwnServer(url: String) {
        creds.serverMode = CredentialStore.MODE_CUSTOM
        creds.customServer = url
        creds.rememberEndpoint(url)
        api.baseUrl = url
        _defaultServerOffline.value = false
    }

    /** Checks the default server; remembers its address when it answers. */
    suspend fun probeDefaultServer(): ServerHealth? {
        val candidates = buildList {
            BuildConfig.DEFAULT_SERVER.takeIf { it.isNotBlank() }?.let(::add)
            creds.lastEndpoint
            "http://10.0.2.2:8765" // Android emulator → host machine, handy while testing
        }.distinct()
        // Optional directory endpoint: lets the owner move the cloud server without a new APK.
        if (BuildConfig.DIRECTORY_URL.isNotBlank()) {
            api.resolveServer(BuildConfig.DIRECTORY_URL)?.let { resolved -> creds.rememberEndpoint(resolved) }
        }

        for (candidate in candidates) {
            try {
                val health = api.health(candidate)
                api.baseUrl = candidate.trimEnd('/')
                creds.rememberEndpoint(candidate)
                _defaultServerOffline.value = false
                return health
            } catch (_: Exception) {
                // try the next candidate
            }
        }

        // Last resort: maybe this PC is running the OLI server right next to the phone.
        _discovering.value = true
        try {
            val found = LanScanner.findServer(port = DEFAULT_PORT)
            if (found != null) {
                val health = runCatching { api.health(found) }.getOrNull()
                if (health != null && health.ok) {
                    api.baseUrl = found.trimEnd('/')
                    creds.rememberEndpoint(found)
                    _defaultServerOffline.value = false
                    return health
                }
            }
        } finally {
            _discovering.value = false
        }

        api.baseUrl = creds.lastEndpoint.ifBlank { BuildConfig.DEFAULT_SERVER }
        _defaultServerOffline.value = true
        return null
    }

    // ------------------------------------------------------------- lifecycle

    /** Called once at startup: restores the cached login or signs in with the saved password. */
    suspend fun bootstrap() {
        if (creds.serverMode == CredentialStore.MODE_DEFAULT) {
            val health = probeDefaultServer()
            _defaultServerOffline.value = health == null
        } else {
            api.baseUrl = creds.customServer.ifBlank { creds.lastEndpoint }
        }

        if (creds.token.isNotBlank()) {
            api.token = creds.token
            try {
                val user = api.me()
                creds.quota = user.quota
                _session.value = Session.SignedIn(user)
                refreshUsage()
                return
            } catch (_: Exception) {
                // Token expired or the server is unreachable — try the cached password below.
            }
        }

        if (creds.userName.isNotBlank() && creds.password.isNotBlank()) {
            try {
                val result = api.login(api.baseUrl, creds.userName, creds.password)
                creds.saveSession(result.token, result.user, remember = creds.rememberMe, password = creds.password)
                api.token = result.token
                _session.value = Session.SignedIn(result.user)
                refreshUsage()
                return
            } catch (_: Exception) {
            }
        }

        api.token = null
        _session.value = Session.SignedOut
    }

    suspend fun setServer(url: String): ServerHealth {
        val normalized = url.trim().trimEnd('/')
        val health = api.health(normalized)
        creds.serverUrl = normalized
        creds.rememberEndpoint(normalized)
        api.baseUrl = normalized
        return health
    }

    suspend fun login(server: String, userName: String, password: String, remember: Boolean) {
        val target = server.ifBlank { api.baseUrl }
        val result = try {
            api.login(target, userName, password)
        } catch (e: OliApi.ApiException) {
            // 401 stays as the server said it; transport failures get a human reason.
            if (e.code == 0) throw OliApi.ApiException(api.explain(target, e)) else throw e
        }
        api.token = result.token
        creds.rememberEndpoint(target)
        creds.saveSession(result.token, result.user, remember, password)
        _session.value = Session.SignedIn(result.user)
        _messages.value = emptyList()
        refreshUsage()
    }

    suspend fun register(server: String, userName: String, password: String, displayName: String, invite: String, remember: Boolean) {
        val target = server.ifBlank { api.baseUrl }
        val result = try {
            api.register(target, userName, password, displayName, invite)
        } catch (e: OliApi.ApiException) {
            if (e.code == 0) throw OliApi.ApiException(api.explain(target, e)) else throw e
        }
        api.token = result.token
        creds.rememberEndpoint(target)
        creds.saveSession(result.token, result.user, remember, password)
        _session.value = Session.SignedIn(result.user)
        _messages.value = emptyList()
        refreshUsage()
    }

    suspend fun logout(forgetPassword: Boolean) {
        api.logout()
        creds.clearSession(keepPassword = !forgetPassword)
        if (forgetPassword) CredentialStore.reset(context)
        _messages.value = emptyList()
        _usage.value = null
        _files.value = emptyList()
        _session.value = Session.SignedOut
    }

    // ------------------------------------------------------------- view mode

    /** Opens the interface without an account; requests never leave the device. */
    fun enterDemo() {
        if (_session.value is Session.Demo) return
        _messages.value = listOf(
            ChatMessage(
                id = nextId++,
                fromUser = false,
                text = "Это режим просмотра. Интерфейс настоящий, но запросы никуда не отправляются: " +
                    "сообщения остаются только на этом экране.\n\n" +
                    "Чтобы пообщаться с OLI по-настоящему — войдите или зарегистрируйтесь."
            )
        )
        _session.value = Session.Demo(returnTo = _session.value)
    }

    /** Leaves view mode and returns to the sign-in screen. */
    fun exitDemo() {
        val current = _session.value
        if (current is Session.Demo) {
            _session.value = Session.SignedOut
        }
    }

    // ------------------------------------------------------------- chat

    suspend fun send(text: String): String? {
        if (text.isBlank()) return null
        _messages.value = _messages.value + ChatMessage(nextId++, fromUser = true, text = text.trim())

        if (isDemo) {
            // View mode: acknowledge locally, never touch the network.
            _messages.value = _messages.value + ChatMessage(
                nextId++, fromUser = false,
                text = "👁 Режим просмотра: сообщение не отправлено. Войдите в аккаунт, чтобы OLI ответил."
            )
            return null
        }

        _busy.value = true
        return try {
            val reply = api.chat(text.trim(), _activeThread.value)
            _messages.value = _messages.value + ChatMessage(nextId++, fromUser = false, text = reply.text, files = reply.files)
            if (reply.threadId.isNotBlank() && _activeThread.value != reply.threadId) {
                _activeThread.value = reply.threadId
                refreshChats()
            }
            _limitNotice.value = reply.limited
            refreshUsage()
            null
        } catch (e: Exception) {
            val message = e.message ?: "Ошибка сети"
            _messages.value = _messages.value + ChatMessage(nextId++, fromUser = false, text = "⚠ $message")
            message
        } finally {
            _busy.value = false
        }
    }

    /**
     * Sends a photo for analysis: the bytes go to the user's workspace first, then the
     * question is asked about that file. The bubble appears instantly with the picture,
     * so the user sees what was attached while the answer is being prepared.
     */
    suspend fun analyzeImage(bitmap: android.graphics.Bitmap, fileName: String, question: String?) {
        val bytes = java.io.ByteArrayOutputStream().use { out ->
            bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG, 88, out)
            out.toByteArray()
        }
        val prompt = question?.trim().orEmpty().ifBlank { "Что на этом изображении? Опиши подробно." }
        val messageId = nextId++
        _messages.value = _messages.value + ChatMessage(
            id = messageId,
            fromUser = true,
            text = prompt,
            image = bitmap
        )

        if (isDemo) {
            _messages.value = _messages.value + ChatMessage(
                nextId++, fromUser = false,
                text = "👁 Режим просмотра: изображение не отправлено. Войдите в аккаунт, чтобы OLI его разобрал."
            )
            return
        }

        _busy.value = true
        try {
            val path = api.uploadImage(fileName.ifBlank { "photo.jpg" }, bytes)
            val reply = api.vision(path, prompt)
            _messages.value = _messages.value + ChatMessage(
                nextId++, fromUser = false, text = reply.text.ifBlank { "Не удалось разобрать изображение." }
            )
            _limitNotice.value = reply.limited
            refreshUsage()
            refreshChats()
        } catch (e: Exception) {
            _messages.value = _messages.value + ChatMessage(
                nextId++, fromUser = false,
                text = "⚠ Не удалось проанализировать изображение: ${e.message ?: "ошибка сети"}"
            )
        } finally {
            _busy.value = false
        }
    }

    /** Renames the user (display name only). Returns the updated profile or throws. */
    suspend fun updateDisplayName(name: String): UserProfile {
        if (isDemo) {
            val current = (session.value as? Session.SignedIn)?.user
            val updated = (current ?: UserProfile("demo", "гость", name, 0, false))
                .copy(displayName = name)
            _session.value = Session.SignedIn(updated)
            return updated
        }
        val updated = api.updateProfile(name)
        creds.displayName = updated.displayName
        _session.value = Session.SignedIn(updated)
        return updated
    }

    val serverEndpoint: String get() = api.baseUrl

    suspend fun changePassword(old: String, new: String) {
        api.changePassword(old, new)
        val result = api.login(creds.lastEndpoint, creds.userName, new)
        api.token = result.token
        creds.saveSession(result.token, result.user, remember = true, password = new)
        _session.value = Session.SignedIn(result.user)
    }

    suspend fun resetChat() {
        if (isDemo) {
            _messages.value = emptyList()
            return
        }
        try {
            api.resetChat()
        } catch (_: Exception) {
        }
        _messages.value = emptyList()
    }

    // ------------------------------------------------------------- chats & limits

    suspend fun refreshChats() {
        if (isDemo) return
        try {
            _chats.value = api.chats()
        } catch (_: Exception) {
        }
    }

    /** Opens a saved conversation (or starts a fresh one when [threadId] is null). */
    suspend fun openChat(threadId: String?) {
        _activeThread.value = threadId
        if (isDemo || threadId.isNullOrBlank()) {
            _messages.value = emptyList()
            return
        }
        _messages.value = try {
            api.chatMessages(threadId).map { ChatMessage(nextId++, fromUser = it.fromUser, text = it.text) }
        } catch (e: Exception) {
            listOf(ChatMessage(nextId++, fromUser = false, text = "⚠ Не удалось открыть чат: ${e.message}"))
        }
    }

    suspend fun newChat(): String? {
        _activeThread.value = null
        _messages.value = emptyList()
        if (isDemo) return null
        return try {
            val id = api.newChat()
            _activeThread.value = id
            refreshChats()
            id
        } catch (_: Exception) {
            null
        }
    }

    suspend fun renameChat(id: String, title: String): Boolean = try {
        api.renameChat(id, title)
        refreshChats()
        true
    } catch (_: Exception) {
        false
    }

    suspend fun deleteChat(id: String): Boolean = try {
        api.deleteChat(id)
        if (_activeThread.value == id) {
            _activeThread.value = null
            _messages.value = emptyList()
        }
        refreshChats()
        true
    } catch (_: Exception) {
        false
    }

    suspend fun refreshModels() {
        if (isDemo) {
            _models.value = ModelState(
                selected = "oli-pass",
                models = listOf(
                    OliModel("oli-pass", "Olim Pass", "Флагман OLI: сложные задачи, код, анализ.", 640),
                    OliModel("oli-son", "Olim Son", "Лёгкая и быстрая модель OLI.", 320)
                ),
                hourUsed = 12_400, hourLimit = 150_000,
                dayUsed = 48_200, dayLimit = 600_000,
                activeUsers = 1, exhausted = false
            )
            _usageLimits.value = UsageSnapshotData(12_400, 150_000, 48_200, 600_000, 120_000, 1, false)
            return
        }
        try {
            val state = api.models()
            _models.value = state
            _usageLimits.value = api.usage()
        } catch (_: Exception) {
        }
    }

    suspend fun chooseModel(modelId: String): Boolean = try {
        if (isDemo) {
            _models.value = _models.value?.copy(selected = modelId)
            true
        } else {
            api.chooseModel(modelId)
            refreshModels()
            true
        }
    } catch (_: Exception) {
        false
    }

    fun dismissLimitNotice() {
        _limitNotice.value = false
    }

    // ------------------------------------------------------------- files

    suspend fun refreshUsage() {
        if (isDemo) return
        try {
            _usage.value = api.usageFiles()
        } catch (_: Exception) {
        }
    }

    suspend fun loadFiles(path: String = ""): Result<List<FileEntry>> = try {
        if (isDemo) {
            Result.success(listOf(
                FileEntry("Пример отчёта.txt", "Пример отчёта.txt", false, 1024, "сегодня"),
                FileEntry("Архив.zip", "Архив.zip", false, 4096, "вчера"),
                FileEntry("Папка проекта", "Папка проекта", true, 0, "вчера")
            ))
        } else {
            val list = api.list(path)
            _files.value = list
            Result.success(list)
        }
    } catch (e: Exception) {
        Result.failure(e)
    }

    /** Chat is the only place requests go; files are read-only in view mode. */
    private val demoReadOnlyMessage = "Режим просмотра: действие недоступно. Войдите в аккаунт."

    suspend fun deleteFile(path: String): Result<String> =
        if (isDemo) Result.failure(IllegalStateException(demoReadOnlyMessage))
        else try {
            val message = api.delete(path)
            refreshUsage()
            Result.success(message)
        } catch (e: Exception) {
            Result.failure(e)
        }

    suspend fun makeDir(path: String): Result<String> =
        if (isDemo) Result.failure(IllegalStateException(demoReadOnlyMessage))
        else try {
            Result.success(api.makeDir(path))
        } catch (e: Exception) {
            Result.failure(e)
        }

    suspend fun readText(path: String): Result<String> = try {
        if (isDemo) Result.success("Это пример файла в режиме просмотра. Содержимое появится после входа в аккаунт.")
        else Result.success(api.readText(path))
    } catch (e: Exception) {
        Result.failure(e)
    }

    suspend fun upload(entryPath: String, name: String, mime: String, length: Long, open: () -> java.io.InputStream): Result<String> =
        if (isDemo) Result.failure(IllegalStateException(demoReadOnlyMessage))
        else try {
            val message = api.upload(entryPath, name, mime, length, open)
            refreshUsage()
            Result.success(message)
        } catch (e: Exception) {
            Result.failure(e)
        }

    suspend fun download(path: String, sink: (java.io.InputStream) -> Long): Result<Long> =
        if (isDemo) Result.failure(IllegalStateException(demoReadOnlyMessage))
        else try {
            Result.success(api.download(path, sink))
        } catch (e: Exception) {
            Result.failure(e)
        }

    suspend fun downloadArchive(name: String, sink: (java.io.InputStream) -> Long): Result<Long> =
        if (isDemo) Result.failure(IllegalStateException(demoReadOnlyMessage))
        else try {
            Result.success(api.downloadArchive(name, sink))
        } catch (e: Exception) {
            Result.failure(e)
        }
}
