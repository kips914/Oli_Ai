package com.oli.mobile.data

import android.content.Context
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Remembers the server address, login and password so the user does not retype them.
 *
 * The values are encrypted with an AES-256-GCM key that lives in the Android Keystore
 * (hardware-backed when the phone has a TEE/StrongBox). Consequences:
 *  - the plaintext never touches disk;
 *  - the OLI administrator (who only ever sees PBKDF2 hashes on the server) cannot read them;
 *  - a copy of the app's files pulled off the phone is useless without the Keystore key.
 *
 * If the Keystore cannot be opened on a broken/older device, nothing secret is written
 * at all — not even to a plain file — and the user simply logs in again next time.
 */
class CredentialStore private constructor(private val context: Context) {

    private interface Kv {
        fun str(key: String): String?
        fun long(key: String): Long?
        fun bool(key: String): Boolean?
        fun put(key: String, value: String?)
        fun put(key: String, value: Long)
        fun put(key: String, value: Boolean)
        fun clear()
    }

    private class EncryptedKv(context: Context) : Kv {
        private val prefs = run {
            val masterKey = MasterKey.Builder(context)
                .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                .build()
            EncryptedSharedPreferences.create(
                context,
                "oli_secure_prefs",
                masterKey,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
            )
        }

        override fun str(key: String): String? = prefs.getString(key, null)
        override fun long(key: String): Long? = if (prefs.contains(key)) prefs.getLong(key, 0L) else null
        override fun bool(key: String): Boolean? = if (prefs.contains(key)) prefs.getBoolean(key, false) else null
        override fun put(key: String, value: String?) { prefs.edit().putString(key, value).apply() }
        override fun put(key: String, value: Long) { prefs.edit().putLong(key, value).apply() }
        override fun put(key: String, value: Boolean) { prefs.edit().putBoolean(key, value).apply() }
        override fun clear() { prefs.edit().clear().apply() }
    }

    /** Memory-only store: used when the Keystore is unavailable (never persisted). */
    private class MemoryKv : Kv {
        private val values = mutableMapOf<String, Any>()

        override fun str(key: String): String? = values[key] as? String
        override fun long(key: String): Long? = values[key] as? Long
        override fun bool(key: String): Boolean? = values[key] as? Boolean
        override fun put(key: String, value: String?) { if (value == null) values.remove(key) else values[key] = value }
        override fun put(key: String, value: Long) { values[key] = value }
        override fun put(key: String, value: Boolean) { values[key] = value }
        override fun clear() = values.clear()
    }

    /** False when secrets can only live in memory for this run. */
    @Volatile var secureStorageAvailable: Boolean = true
        private set

    private val kv: Kv = try {
        EncryptedKv(context)
    } catch (e: Exception) {
        secureStorageAvailable = false
        MemoryKv()
    }

    var serverUrl: String
        get() = kv.str(KEY_SERVER) ?: ""
        set(value) = kv.put(KEY_SERVER, value.trim())

    var userName: String
        get() = kv.str(KEY_USER) ?: ""
        private set(value) = kv.put(KEY_USER, value)

    var password: String
        get() = kv.str(KEY_PASSWORD) ?: ""
        private set(value) = kv.put(KEY_PASSWORD, value)

    var token: String
        get() = kv.str(KEY_TOKEN) ?: ""
        private set(value) = kv.put(KEY_TOKEN, value)

    var displayName: String
        get() = kv.str(KEY_DISPLAY) ?: ""
        set(value) = kv.put(KEY_DISPLAY, value)

    var rememberMe: Boolean
        get() = kv.bool(KEY_REMEMBER) ?: true
        set(value) = kv.put(KEY_REMEMBER, value)

    var quota: Long
        get() = kv.long(KEY_QUOTA) ?: 0L
        set(value) = kv.put(KEY_QUOTA, value)

    /**
     * "default" — the OLI cloud server shipped with the app (owner's PC behind it);
     * "custom"  — the user's own PC ("Свой сервер" in settings).
     */
    var serverMode: String
        get() = kv.str(KEY_SERVER_MODE) ?: MODE_DEFAULT
        set(value) = kv.put(KEY_SERVER_MODE, if (value == MODE_CUSTOM) MODE_CUSTOM else MODE_DEFAULT)

    var customServer: String
        get() = kv.str(KEY_CUSTOM_SERVER) ?: ""
        set(value) = kv.put(KEY_CUSTOM_SERVER, value.trim())

    /** Once the user signed in against a server, remember its address (e.g. after a link). */
    fun rememberEndpoint(url: String) {
        if (url.isNotBlank()) kv.put(KEY_LAST_ENDPOINT, url.trim())
    }

    var lastEndpoint: String
        get() = kv.str(KEY_LAST_ENDPOINT) ?: ""
        private set(value) = kv.put(KEY_LAST_ENDPOINT, value)

    fun saveSession(token: String, user: UserProfile, remember: Boolean, password: String?) {
        this.token = token
        this.userName = user.userName
        this.displayName = user.displayName
        this.quota = user.quota
        rememberMe = remember
        // Unchecked "remember" (or no Keystore) → the plaintext password is not kept at all.
        this.password = if (remember && secureStorageAvailable) password ?: "" else ""
    }

    fun clearSession(keepPassword: Boolean = true) {
        token = ""
        if (!keepPassword) {
            password = ""
            userName = ""
            displayName = ""
        }
    }

    /** Last successful login for the sign-in form (user name only, never the password). */
    fun lastLoginName(): String = userName

    companion object {
        private const val KEY_SERVER = "server"
        private const val KEY_USER = "user"
        private const val KEY_PASSWORD = "password"
        private const val KEY_TOKEN = "token"
        private const val KEY_DISPLAY = "display"
        private const val KEY_REMEMBER = "remember"
        private const val KEY_QUOTA = "quota"
        private const val KEY_SERVER_MODE = "server_mode"
        private const val KEY_CUSTOM_SERVER = "custom_server"
        private const val KEY_LAST_ENDPOINT = "last_endpoint"

        const val MODE_DEFAULT = "default"
        const val MODE_CUSTOM = "custom"

        @Volatile private var instance: CredentialStore? = null

        fun get(context: Context): CredentialStore =
            instance ?: synchronized(this) {
                instance ?: CredentialStore(context.applicationContext).also { instance = it }
            }

        /** Wipes every stored secret (used by "выйти и забыть"). */
        suspend fun reset(context: Context) = withContext(Dispatchers.IO) {
            get(context).kv.clear()
        }
    }
}
