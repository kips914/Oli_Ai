package com.oli.mobile.data

import org.json.JSONObject

data class ServerHealth(
    val ok: Boolean,
    val version: String,
    val registration: Boolean,
    val llama: Boolean,
    /** True when the answer came in one piece (tunnel) instead of a local Wi-Fi socket. */
    val viaInternet: Boolean = false,
    /** "ollama" or "llama.cpp" — the engine running on the PC. */
    val engine: String = ""
)

data class UserProfile(
    val id: String,
    val userName: String,
    val displayName: String,
    val quota: Long,
    val isAdmin: Boolean
) {
    companion object {
        fun from(json: JSONObject) = UserProfile(
            id = json.optString("id"),
            userName = json.optString("userName"),
            displayName = json.optString("displayName").ifBlank { json.optString("userName") },
            quota = json.optLong("quota"),
            isAdmin = json.optBoolean("isAdmin", false)
        )
    }
}

data class AuthResult(val token: String, val user: UserProfile) {
    companion object {
        fun from(json: JSONObject) = AuthResult(
            token = json.optString("token"),
            user = UserProfile.from(json.optJSONObject("user") ?: JSONObject())
        )
    }
}

data class ChatReply(
    val text: String,
    val files: List<String>,
    val threadId: String = "",
    val limited: Boolean = false,
    val modelName: String = ""
)

data class OliModel(
    val id: String,
    val title: String,
    val description: String,
    val maxTokens: Int
)

data class ModelState(
    val selected: String,
    val models: List<OliModel>,
    val hourUsed: Long,
    val hourLimit: Int,
    val dayUsed: Long,
    val dayLimit: Int,
    val activeUsers: Int,
    val exhausted: Boolean
)

data class UsageSnapshotData(
    val hourUsed: Long,
    val hourLimit: Int,
    val dayUsed: Long,
    val dayLimit: Int,
    val totalAllTime: Long,
    val activeUsers: Int,
    val exhausted: Boolean
)

data class ChatSummary(
    val id: String,
    val title: String,
    val modelId: String,
    val updatedAt: String
)

data class StoredChatMessage(
    val fromUser: Boolean,
    val text: String,
    /** Workspace files the answer produced (📎 line under the bubble). */
    val files: List<String> = emptyList()
)

/** Live values mirrored from the last /api/chat response so the composer can show them. */
object UsageSnapshot {
    @Volatile var hourUsed: Long = 0
    @Volatile var hourLimit: Int = 0
    @Volatile var dayUsed: Long = 0
    @Volatile var dayLimit: Int = 0
    @Volatile var activeUsers: Int = 1
    @Volatile var limited: Boolean = false
    @Volatile var modelName: String = ""
    @Volatile var modelId: String = ""
}

data class FileEntry(
    val name: String,
    val path: String,
    val isDirectory: Boolean,
    val size: Long,
    val modified: String
)

data class Usage(val used: Long, val quota: Long, val files: Int, val percent: Double)
