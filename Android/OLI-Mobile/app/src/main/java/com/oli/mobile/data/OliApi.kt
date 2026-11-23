package com.oli.mobile.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.DataOutputStream
import java.io.InputStream
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

/** Thin HTTP client for the OLI server (no third-party networking deps). */
class OliApi private constructor() {

    @Volatile var baseUrl: String = ""
    @Volatile var token: String? = null

    class ApiException(message: String, val code: Int = 0) : Exception(message)

    // ---------------------------------------------------------------- core

    private fun normalize(server: String): String {
        var s = server.trim()
        if (s.isEmpty()) return s
        if (!s.startsWith("http://") && !s.startsWith("https://")) s = "http://$s"
        return s.trimEnd('/')
    }

    private fun open(path: String, method: String, body: String?, contentType: String?): HttpURLConnection {
        if (baseUrl.isBlank()) throw ApiException("Не указан адрес сервера. Откройте «Настройки».")
        val url = URL(baseUrl + path)
        val conn = url.openConnection() as HttpURLConnection
        conn.requestMethod = method
        conn.connectTimeout = 8000
        conn.readTimeout = 180_000 // local LLM answers can be slow
        conn.setRequestProperty("Accept", "application/json")
        conn.setRequestProperty("X-OLI-Device", "android")
        token?.let { conn.setRequestProperty("Authorization", "Bearer $it") }
        if (body != null) {
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", contentType ?: "application/json; charset=utf-8")
            conn.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
        }
        return conn
    }

    private fun read(conn: HttpURLConnection): String {
        val stream: InputStream = try {
            conn.inputStream
        } catch (e: Exception) {
            conn.errorStream ?: throw ApiException("Сервер недоступен (${conn.responseCode})", conn.responseCode)
        }
        val text = stream.bufferedReader().use { it.readText() }
        if (conn.responseCode >= 400) {
            val message = try {
                JSONObject(text).optString("error").ifBlank { "Ошибка ${conn.responseCode}" }
            } catch (_: Exception) {
                "Ошибка ${conn.responseCode}"
            }
            throw ApiException(message, conn.responseCode)
        }
        return text
    }

    private suspend fun call(path: String, method: String = "GET", json: JSONObject? = null): JSONObject =
        withContext(Dispatchers.IO) {
            val conn = open(path, method, json?.toString(), null)
            try {
                val text = read(conn)
                if (text.isBlank()) JSONObject() else JSONObject(text)
            } finally {
                conn.disconnect()
            }
        }

    // ---------------------------------------------------------------- auth

    suspend fun health(server: String): ServerHealth = withContext(Dispatchers.IO) {
        val previous = baseUrl
        val target = normalize(server)
        if (target.isBlank()) throw ApiException("Не указан адрес сервера.")
        baseUrl = target
        try {
            val conn = open("/api/health", "GET", null, null)
            try {
                val json = JSONObject(read(conn))
                ServerHealth(
                    ok = json.optString("status") == "ok",
                    version = json.optString("version"),
                    registration = json.optBoolean("registration", true),
                    llama = json.optBoolean("llama", false),
                    viaInternet = target.startsWith("https://"),
                    engine = json.optString("engine")
                )
            } finally {
                conn.disconnect()
            }
        } catch (e: ApiException) {
            baseUrl = previous
            throw e
        } catch (e: Exception) {
            baseUrl = previous
            // Throwable is kept as the cause so callers can explain() the real reason.
            throw ApiException("Не удалось подключиться к серверу: ${e.message ?: "ошибка сети"}")
        }
    }

    // ---------------------------------------------------------------- join code

    /**
     * "Join code" printed by OLI on the PC (Пульт админа → интернет-доступ).
     * It is base64url("OLI1|https://xxx.trycloudflare.com|invite") so a student can
     * paste one short line instead of retyping a random tunnel address.
     * Returns the server URL and the invite code, or null when the text is not a code.
     */
    fun parseJoinCode(text: String): Pair<String, String>? {
        var raw = text.trim()
        if (raw.isEmpty()) return null
        // Accept the code with whitespace/newlines pasted from a messenger.
        raw = raw.filterNot { it.isWhitespace() }.removePrefix("oli://").removePrefix("OLI://")
        if (!raw.startsWith("OLI1") && raw.length < 12) return null
        return try {
            val padded = raw.replace('-', '+').replace('_', '/').let {
                when (it.length % 4) {
                    2 -> "$it=="
                    3 -> "$it="
                    else -> it
                }
            }
            val decoded = String(android.util.Base64.decode(padded, android.util.Base64.DEFAULT), Charsets.UTF_8)
            val parts = decoded.split('|')
            if (parts.size < 2 || parts[0] != "OLI1" || parts[1].isBlank()) null
            else parts[1].trimEnd('/') to parts.getOrElse(2) { "" }
        } catch (_: Exception) {
            null
        }
    }

    /** Turns a transport failure into something a student can act on. */
    fun explain(server: String, error: Throwable?): String {
        val target = normalize(server)
        val raw = error?.message?.lowercase().orEmpty()
        val hint = when {
            raw.contains("failed to connect") || raw.contains("connection refused") ||
                raw.contains("econnrefused") || raw.contains("timeout") || raw.contains("timed out") ->
                "Компьютер не ответил. Чаще всего это брандмауэр Windows: на ПК откройте «Пульт админа» → " +
                    "«🛡 Открыть порт» (или нажмите «🌍 Дам доступ из интернета» — работает и на мобильном интернете). " +
                    "Если вы не дома — вставьте код подключения."
            raw.contains("unknownhost") || raw.contains("unable to resolve") || raw.contains("nodename") ->
                "Адрес не найден. Ссылка на туннель живёт только пока OLI на ПК открыт — скопируйте свежий код подключения."
            raw.contains("cleartext") ->
                "Android заблокировал незащищённое соединение — используйте адрес, который начинается с https://"
            raw.contains("движок") || raw.contains("модель «") || raw.contains("не найдена на") ->
                error?.message ?: "Модель на ПК не отвечает — запустите движок модели."
            raw.contains("401") -> "Неверный логин или пароль."
            else -> "Сервер не отвечает. Проверьте, что OLI на ПК открыт, и попробуйте снова."
        }
        val where = target.replace("https://", "").replace("http://", "").ifBlank { "адрес не указан" }
        return "$hint\nСейчас: $where"
    }

    /** Looks up the app's default (cloud) server from a small JSON endpoint. */
    suspend fun resolveServer(directoryUrl: String): String? = withContext(Dispatchers.IO) {
        val url = directoryUrl.trim().trimEnd('/') + "/api/oli/server"
        val conn = URL(url).openConnection() as HttpURLConnection
        conn.connectTimeout = 6000
        conn.readTimeout = 6000
        conn.setRequestProperty("Accept", "application/json")
        try {
            if (conn.responseCode >= 400) return@withContext null
            val json = JSONObject(conn.inputStream.bufferedReader().use { it.readText() })
            val server = json.optString("server")
            if (server.isBlank()) null else normalize(server)
        } catch (_: Exception) {
            null
        } finally {
            conn.disconnect()
        }
    }

    suspend fun register(server: String, userName: String, password: String, displayName: String, invite: String): AuthResult {
        baseUrl = normalize(server)
        val body = JSONObject()
            .put("userName", userName)
            .put("password", password)
            .put("displayName", displayName)
            .put("inviteCode", invite)
        val json = call("/api/register", "POST", body)
        return AuthResult.from(json)
    }

    suspend fun login(server: String, userName: String, password: String): AuthResult {
        baseUrl = normalize(server)
        val json = call("/api/login", "POST", JSONObject().put("userName", userName).put("password", password))
        return AuthResult.from(json)
    }

    suspend fun me(): UserProfile {
        val json = call("/api/me")
        val node = json.optJSONObject("user") ?: json
        return UserProfile.from(node)
    }

    suspend fun logout() {
        try {
            call("/api/logout", "POST", JSONObject())
        } catch (_: Exception) {
        } finally {
            token = null
        }
    }

    suspend fun changePassword(old: String, new: String): JSONObject =
        call("/api/password", "POST", JSONObject().put("oldPassword", old).put("newPassword", new))

    suspend fun updateProfile(displayName: String): UserProfile {
        val json = call("/api/profile", "POST", JSONObject().put("displayName", displayName))
        return UserProfile.from(json.optJSONObject("user") ?: JSONObject())
    }

    // ---------------------------------------------------------------- chat

    suspend fun chat(message: String, threadId: String?): ChatReply {
        val body = JSONObject().put("message", message)
        if (!threadId.isNullOrBlank()) body.put("threadId", threadId)
        val json = call("/api/chat", "POST", body)
        val files = mutableListOf<String>()
        json.optJSONArray("files")?.let { array ->
            for (i in 0 until array.length()) files.add(array.optString(i))
        }
        json.optJSONObject("usage")?.let { u ->
            UsageSnapshot.hourUsed = u.optLong("hourUsed")
            UsageSnapshot.hourLimit = u.optInt("hourLimit")
            UsageSnapshot.dayUsed = u.optLong("dayUsed")
            UsageSnapshot.dayLimit = u.optInt("dayLimit")
            UsageSnapshot.activeUsers = u.optInt("activeUsers", 1)
        }
        UsageSnapshot.limited = json.optBoolean("limited")
        UsageSnapshot.modelName = json.optString("model")
        UsageSnapshot.modelId = json.optString("modelId")
        return ChatReply(
            text = json.optString("text"),
            files = files,
            threadId = json.optString("threadId"),
            limited = json.optBoolean("limited"),
            modelName = json.optString("model")
        )
    }

    // ---------------------------------------------------------------- models & limits

    suspend fun models(): ModelState = withContext(Dispatchers.IO) {
        val json = call("/api/models")
        val list = mutableListOf<OliModel>()
        json.optJSONArray("models")?.let { array ->
            for (i in 0 until array.length()) {
                val m = array.getJSONObject(i)
                list.add(
                    OliModel(
                        id = m.optString("id"),
                        title = m.optString("title"),
                        description = m.optString("description"),
                        maxTokens = m.optInt("maxTokens")
                    )
                )
            }
        }
        val limit = json.optJSONObject("limit")
        ModelState(
            selected = json.optString("selected"),
            models = list,
            hourUsed = limit?.optLong("hourUsed") ?: 0,
            hourLimit = limit?.optInt("hourLimit") ?: 0,
            dayUsed = limit?.optLong("dayUsed") ?: 0,
            dayLimit = limit?.optInt("dayLimit") ?: 0,
            activeUsers = limit?.optInt("activeUsers") ?: 1,
            exhausted = limit?.optBoolean("exhausted") ?: false
        )
    }

    suspend fun chooseModel(modelId: String) {
        call("/api/models", "POST", JSONObject().put("modelId", modelId))
    }

    /** Token usage and the current dynamic limits. */
    suspend fun usage(): UsageSnapshotData = withContext(Dispatchers.IO) {
        val json = call("/api/usage")
        UsageSnapshotData(
            hourUsed = json.optLong("hourUsed"),
            hourLimit = json.optInt("hourLimit"),
            dayUsed = json.optLong("dayUsed"),
            dayLimit = json.optInt("dayLimit"),
            totalAllTime = json.optLong("totalAllTime"),
            activeUsers = json.optInt("activeUsers", 1),
            exhausted = json.optBoolean("exhausted")
        )
    }

    // ---------------------------------------------------------------- chats

    suspend fun chats(): List<ChatSummary> = withContext(Dispatchers.IO) {
        val json = call("/api/chats")
        val array = json.optJSONArray("chats") ?: JSONArray()
        val result = mutableListOf<ChatSummary>()
        for (i in 0 until array.length()) {
            val c = array.getJSONObject(i)
            result.add(
                ChatSummary(
                    id = c.optString("id"),
                    title = c.optString("title").ifBlank { "Новый чат" },
                    modelId = c.optString("modelId"),
                    updatedAt = c.optString("updatedAt")
                )
            )
        }
        result
    }

    suspend fun chatMessages(id: String): List<StoredChatMessage> = withContext(Dispatchers.IO) {
        val json = call("/api/chats/" + URLEncoder.encode(id, "UTF-8"))
        val array = json.optJSONArray("messages") ?: JSONArray()
        val result = mutableListOf<StoredChatMessage>()
        for (i in 0 until array.length()) {
            val m = array.getJSONObject(i)
            result.add(
                StoredChatMessage(
                    fromUser = m.optString("role") == "user",
                    text = m.optString("text")
                )
            )
        }
        result
    }

    suspend fun newChat(title: String? = null): String {
        val body = JSONObject()
        if (!title.isNullOrBlank()) body.put("title", title)
        return call("/api/chats", "POST", body).optString("id")
    }

    suspend fun renameChat(id: String, title: String) {
        call("/api/chats/" + URLEncoder.encode(id, "UTF-8") + "/rename", "POST", JSONObject().put("title", title))
    }

    suspend fun deleteChat(id: String) {
        call("/api/chats/" + URLEncoder.encode(id, "UTF-8"), "DELETE")
    }

    suspend fun resetChat() {
        call("/api/chat/reset", "POST", JSONObject())
    }

    // ---------------------------------------------------------------- files

    /** File-quota usage (bytes). */
    suspend fun usageFiles(): Usage = withContext(Dispatchers.IO) {
        val json = call("/api/files/usage")
        Usage(
            used = json.optLong("used"),
            quota = json.optLong("quota"),
            files = json.optInt("files"),
            percent = json.optDouble("percent", 0.0)
        )
    }

    suspend fun list(path: String): List<FileEntry> {
        val json = call("/api/files?path=" + URLEncoder.encode(path, "UTF-8"))
        val array: JSONArray = json.optJSONArray("entries") ?: JSONArray()
        val result = mutableListOf<FileEntry>()
        for (i in 0 until array.length()) {
            val entry = array.getJSONObject(i)
            val relative = entry.optString("path")
            result.add(
                FileEntry(
                    name = entry.optString("name"),
                    path = relative,
                    isDirectory = entry.optBoolean("isDirectory"),
                    size = entry.optLong("size"),
                    modified = entry.optString("modified").take(16).replace('T', ' ')
                )
            )
        }
        return result.sortedWith(compareByDescending<FileEntry> { it.isDirectory }.thenBy { it.name.lowercase() })
    }

    suspend fun readText(path: String): String =
        call("/api/files/read?path=" + URLEncoder.encode(path, "UTF-8")).optString("content")

    suspend fun delete(path: String, recursive: Boolean = true): String =
        call("/api/files/delete", "POST", JSONObject().put("path", path).put("recursive", recursive))
            .optString("message", "Удалено")

    suspend fun makeDir(path: String): String =
        call("/api/files/mkdir", "POST", JSONObject().put("path", path)).optString("message", "Папка создана")

    suspend fun writeText(path: String, content: String): String =
        call(
            "/api/files/write", "POST",
            JSONObject().put("path", path).put("content", content).put("append", false)
        ).optString("message", "Сохранено")

    /** Saves the whole workspace as a zip via the authenticated endpoint. */
    suspend fun downloadArchive(name: String, sink: (InputStream) -> Long): Long = withContext(Dispatchers.IO) {
        val conn = open("/api/files/archive", "GET", null, null)
        try {
            if (conn.responseCode >= 400) read(conn)
            conn.inputStream.use(sink)
        } finally {
            conn.disconnect()
        }
    }

    /** Downloads a file into local storage; returns the received byte count. */
    suspend fun download(path: String, sink: (InputStream) -> Long): Long = withContext(Dispatchers.IO) {
        val conn = open("/api/files/download?path=" + URLEncoder.encode(path, "UTF-8"), "GET", null, null)
        try {
            if (conn.responseCode >= 400) read(conn)
            conn.inputStream.use(sink)
        } finally {
            conn.disconnect()
        }
    }

    /** Streams a local file to the server (multipart/form-data). */
    suspend fun upload(directory: String, fileName: String, mime: String, length: Long, open: () -> InputStream): String =
        withContext(Dispatchers.IO) {
            val boundary = "----OLIBoundary" + System.currentTimeMillis()
            val conn = open("/api/files/upload", "POST", null, null)
            conn.setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")
            conn.doOutput = true
            conn.setChunkedStreamingMode(64 * 1024)
            try {
                DataOutputStream(conn.outputStream).use { out ->
                    out.writeBytes("--$boundary\r\n")
                    out.writeBytes("Content-Disposition: form-data; name=\"path\"\r\n\r\n$directory\r\n")
                    out.writeBytes("--$boundary\r\n")
                    out.writeBytes(
                        "Content-Disposition: form-data; name=\"file\"; filename=\"${fileName.replace("\"", "")}\"\r\n"
                    )
                    out.writeBytes("Content-Type: $mime\r\n\r\n")
                    open().use { input ->
                        val buffer = ByteArray(64 * 1024)
                        while (true) {
                            val read = input.read(buffer)
                            if (read <= 0) break
                            out.write(buffer, 0, read)
                        }
                    }
                    out.writeBytes("\r\n--$boundary--\r\n")
                    out.flush()
                }
                val json = JSONObject(read(conn))
                json.optString("message", "Файл загружен")
            } catch (e: ApiException) {
                throw e
            } finally {
                conn.disconnect()
            }
        }

    // ---------------------------------------------------------------- images

    /**
     * Sends raw image bytes (the new /api/files/upload endpoint) and returns the stored
     * path inside the user's workspace, e.g. "images/2026-11-23-photo.jpg".
     */
    suspend fun uploadImage(fileName: String, bytes: ByteArray): String = withContext(Dispatchers.IO) {
        val safe = fileName.replace("\"", "").ifBlank { "photo.jpg" }
        val conn = open("/api/files/upload?name=" + URLEncoder.encode(safe, "UTF-8"), "POST", null, null)
        conn.setRequestProperty("Content-Type", "application/octet-stream")
        conn.doOutput = true
        conn.setFixedLengthStreamingMode(bytes.size)
        try {
            conn.outputStream.use { it.write(bytes) }
            val json = JSONObject(read(conn))
            val path = json.optString("path")
            if (path.isBlank()) throw ApiException(json.optString("error", "Не удалось загрузить изображение"))
            path
        } finally {
            conn.disconnect()
        }
    }

    /** Asks the server to analyse an uploaded image; the answer arrives as chat text. */
    suspend fun vision(path: String, question: String): ChatReply {
        val json = call("/api/vision", "POST", JSONObject().put("path", path).put("question", question))
        json.optJSONObject("usage")?.let { u ->
            UsageSnapshot.hourUsed = u.optLong("hourUsed")
            UsageSnapshot.hourLimit = u.optInt("hourLimit")
            UsageSnapshot.dayUsed = u.optLong("dayUsed")
            UsageSnapshot.dayLimit = u.optInt("dayLimit")
            UsageSnapshot.activeUsers = u.optInt("activeUsers", 1)
        }
        UsageSnapshot.limited = json.optBoolean("limited", false)
        UsageSnapshot.modelName = json.optString("model", "")
        return ChatReply(
            text = json.optString("text"),
            files = emptyList(),
            threadId = "",
            limited = json.optBoolean("limited", false),
            modelName = json.optString("model", "")
        )
    }

    companion object {
        val shared: OliApi by lazy { OliApi() }

        fun prettySize(bytes: Long): String {
            if (bytes <= 0) return "0 Б"
            val units = arrayOf("Б", "КБ", "МБ", "ГБ")
            var value = bytes.toDouble()
            var index = 0
            while (value >= 1024 && index < units.lastIndex) {
                value /= 1024
                index++
            }
            return if (index == 0) "${bytes} Б" else String.format(java.util.Locale.US, "%.1f %s", value, units[index])
        }

        fun validUserName(name: String) = Regex("^[a-zA-Z0-9._-]{3,32}$").matches(name)
        fun validPassword(password: String) = password.length >= 8 && password.any(Char::isLetter) && password.any(Char::isDigit)
    }
}
