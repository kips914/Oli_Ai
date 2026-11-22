package com.oli.mobile.data

import android.content.Context
import android.content.SharedPreferences
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
 *  - the OLI administrator (who only sees password *hashes* on the server) cannot read them;
 *  - a copy of the app's files pulled off the phone is useless without the Keystore key.
 */
class CredentialStore private constructor(context: Context) {

    private val prefs: SharedPreferences = run {
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

    var serverUrl: String
        get() = prefs.getString(KEY_SERVER, "") ?: ""
        set(value) = prefs.edit().putString(KEY_SERVER, value.trim()).apply()

    var userName: String
        get() = prefs.getString(KEY_USER, "") ?: ""
        private set(value) = prefs.edit().putString(KEY_USER, value).apply()

    var password: String
        get() = prefs.getString(KEY_PASSWORD, "") ?: ""
        private set(value) = prefs.edit().putString(KEY_PASSWORD, value).apply()

    var token: String
        get() = prefs.getString(KEY_TOKEN, "") ?: ""
        private set(value) = prefs.edit().putString(KEY_TOKEN, value).apply()

    var displayName: String
        get() = prefs.getString(KEY_DISPLAY, "") ?: ""
        private set(value) = prefs.edit().putString(KEY_DISPLAY, value).apply()

    var rememberMe: Boolean
        get() = prefs.getBoolean(KEY_REMEMBER, true)
        set(value) = prefs.edit().putBoolean(KEY_REMEMBER, value).apply()

    var quota: Long
        get() = prefs.getLong(KEY_QUOTA, 0L)
        private set(value) = prefs.edit().putLong(KEY_QUOTA, value).apply()

    fun saveSession(token: String, user: UserProfile, remember: Boolean, password: String?) {
        this.token = token
        this.userName = user.userName
        this.displayName = user.displayName
        this.quota = user.quota
        rememberMe = remember
        // When the user unchecks "remember", the plaintext password is not stored at all.
        this.password = if (remember) password ?: "" else ""
    }

    fun clearSession(keepPassword: Boolean = true) {
        token = ""
        if (!keepPassword) {
            password = ""
            userName = ""
            displayName = ""
        }
    }

    /** Last successful login for the sign-in form (user name only, never shown as a password). */
    fun lastLoginName(): String = userName

    companion object {
        private const val KEY_SERVER = "server"
        private const val KEY_USER = "user"
        private const val KEY_PASSWORD = "password"
        private const val KEY_TOKEN = "token"
        private const val KEY_DISPLAY = "display"
        private const val KEY_REMEMBER = "remember"
        private const val KEY_QUOTA = "quota"

        @Volatile private var instance: CredentialStore? = null

        fun get(context: Context): CredentialStore =
            instance ?: synchronized(this) {
                instance ?: CredentialStore(context.applicationContext).also { instance = it }
            }

        /** Wipes every stored secret (used by "выйти и забыть"). */
        suspend fun reset(context: Context) = withContext(Dispatchers.IO) {
            get(context).prefs.edit().clear().commit()
        }
    }
}
