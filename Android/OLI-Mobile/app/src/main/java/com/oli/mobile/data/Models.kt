package com.oli.mobile.data

import org.json.JSONObject

data class ServerHealth(
    val ok: Boolean,
    val version: String,
    val registration: Boolean,
    val llama: Boolean
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

data class ChatReply(val text: String, val files: List<String>)

data class FileEntry(
    val name: String,
    val path: String,
    val isDirectory: Boolean,
    val size: Long,
    val modified: String
)

data class Usage(val used: Long, val quota: Long, val files: Int, val percent: Double)
