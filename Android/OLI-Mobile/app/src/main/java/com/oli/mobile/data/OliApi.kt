package com.oli.mobile.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedReader
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
        val text = stream.bufferedReader().use(BufferedReader::readText)
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
        baseUrl = normalize(server)
        try {
            val conn = open("/api/health", "GET", null, null)
            val json = JSONObject(read(conn))
            conn.disconnect()
            ServerHealth(
                ok = json.optString("status") == "ok",
                version = json.optString("version"),
                registration = json.optBoolean("registration", true),
                llama = json.optBoolean("llama", false)
            )
        } catch (e: ApiException) {
            throw e
        } catch (e: Exception) {
            throw ApiException("Не удалось подключиться к серверу: ${e.message ?: "ошибка сети"}")
        } finally {
            if (baseUrl.isBlank()) baseUrl = previous
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

    suspend fun me(): UserProfile = UserProfile.from(call("/api/me"))

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

    // ---------------------------------------------------------------- chat

    suspend fun chat(message: String): ChatReply {
        val json = call("/api/chat", "POST", JSONObject().put("message", message))
        val files = mutableListOf<String>()
        json.optJSONArray("files")?.let { array ->
            for (i in 0 until array.length()) files.add(array.optString(i))
        }
        return ChatReply(text = json.optString("text"), files = files)
    }

    suspend fun resetChat() {
        call("/api/chat/reset", "POST", JSONObject())
    }

    // ---------------------------------------------------------------- files

    suspend fun usage(): Usage = withContext(Dispatchers.IO) {
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

    fun downloadUrl(path: String): String =
        baseUrl + "/api/files/download?path=" + URLEncoder.encode(path, "UTF-8")

    fun archiveUrl(): String = baseUrl + "/api/files/archive"

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
