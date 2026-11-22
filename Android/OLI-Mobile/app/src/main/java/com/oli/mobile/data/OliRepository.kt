package com.oli.mobile.data

import android.content.Context
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

    private var nextId = 1L


    sealed interface Session {
        data object Loading : Session
        data object SignedOut : Session
        data class SignedIn(val user: UserProfile) : Session
    }

    val serverUrl: String get() = creds.serverUrl
    val lastUserName: String get() = creds.lastLoginName()

    /** Called once at startup: restores the cached login and verifies it against the server. */
    suspend fun bootstrap() {
        api.baseUrl = creds.serverUrl
        if (creds.serverUrl.isBlank()) {
            _session.value = Session.SignedOut
            return
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
                // Token expired — fall through to the cached password.
            }
        }

        if (creds.userName.isNotBlank() && creds.password.isNotBlank()) {
            try {
                val result = api.login(creds.serverUrl, creds.userName, creds.password)
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
        api.baseUrl = normalized
        return health
    }

    suspend fun login(server: String, userName: String, password: String, remember: Boolean) {
        if (server.isNotBlank()) setServer(server)
        val result = api.login(server.ifBlank { creds.serverUrl }, userName, password)
        api.token = result.token
        creds.saveSession(result.token, result.user, remember, password)
        _session.value = Session.SignedIn(result.user)
        _messages.value = emptyList()
        refreshUsage()
    }

    suspend fun register(server: String, userName: String, password: String, displayName: String, invite: String, remember: Boolean) {
        if (server.isNotBlank()) setServer(server)
        val result = api.register(server.ifBlank { creds.serverUrl }, userName, password, displayName, invite)
        api.token = result.token
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

    suspend fun send(text: String): String? {
        if (text.isBlank()) return null
        _messages.value = _messages.value + ChatMessage(nextId++, fromUser = true, text = text.trim())
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

    /** Changes the password and transparently signs in again (the server revokes old tokens). */
    suspend fun changePassword(old: String, new: String) {
        api.changePassword(old, new)
        val result = api.login(creds.serverUrl, creds.userName, new)
        api.token = result.token
        creds.saveSession(result.token, result.user, remember = true, password = new)
        _session.value = Session.SignedIn(result.user)
    }

    suspend fun resetChat() {
        try {
            api.resetChat()
        } catch (_: Exception) {
        }
        _messages.value = emptyList()
    }

    suspend fun refreshUsage() {
        try {
            _usage.value = api.usage()
        } catch (_: Exception) {
        }
    }

    suspend fun loadFiles(path: String = ""): Result<List<FileEntry>> = try {
        val list = api.list(path)
        _files.value = list
        Result.success(list)
    } catch (e: Exception) {
        Result.failure(e)
    }

    suspend fun deleteFile(path: String): Result<String> = try {
        val message = api.delete(path)
        refreshUsage()
        Result.success(message)
    } catch (e: Exception) {
        Result.failure(e)
    }

    suspend fun makeDir(path: String): Result<String> = try {
        Result.success(api.makeDir(path))
    } catch (e: Exception) {
        Result.failure(e)
    }

    suspend fun readText(path: String): Result<String> = try {
        Result.success(api.readText(path))
    } catch (e: Exception) {
        Result.failure(e)
    }

    suspend fun upload(entryPath: String, name: String, mime: String, length: Long, open: () -> java.io.InputStream): Result<String> = try {
        val message = api.upload(entryPath, name, mime, length, open)
        refreshUsage()
        Result.success(message)
    } catch (e: Exception) {
        Result.failure(e)
    }

    suspend fun download(path: String, sink: (java.io.InputStream) -> Long): Result<Long> = try {
        Result.success(api.download(path, sink))
    } catch (e: Exception) {
        Result.failure(e)
    }

    fun downloadUrl(path: String): String = api.downloadUrl(path)
    fun archiveUrl(): String = api.archiveUrl()
}
