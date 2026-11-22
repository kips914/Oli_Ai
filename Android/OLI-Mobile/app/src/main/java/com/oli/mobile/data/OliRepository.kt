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

    data class ChatMessage(val id: Long, val fromUser: Boolean, val text: String, val files: List<String> = emptyList())

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

    /** True when the OLI (default) server could not be reached at startup. */
    private val _defaultServerOffline = MutableStateFlow(false)
    val defaultServerOffline: StateFlow<Boolean> = _defaultServerOffline.asStateFlow()

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
        api.baseUrl = creds.lastEndpoint.ifBlank { BuildConfig.DEFAULT_SERVER }
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
        val result = api.login(target, userName, password)
        api.token = result.token
        creds.rememberEndpoint(target)
        creds.saveSession(result.token, result.user, remember, password)
        _session.value = Session.SignedIn(result.user)
        _messages.value = emptyList()
        refreshUsage()
    }

    suspend fun register(server: String, userName: String, password: String, displayName: String, invite: String, remember: Boolean) {
        val target = server.ifBlank { api.baseUrl }
        val result = api.register(target, userName, password, displayName, invite)
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
            val reply = api.chat(text.trim())
            _messages.value = _messages.value + ChatMessage(nextId++, fromUser = false, text = reply.text, files = reply.files)
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

    // ------------------------------------------------------------- files

    suspend fun refreshUsage() {
        if (isDemo) return
        try {
            _usage.value = api.usage()
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
