package com.oli.mobile

import android.app.Application
import com.oli.mobile.data.CredentialStore
import com.oli.mobile.data.OliApi

class OliApplication : Application() {
    override fun onCreate() {
        super.onCreate()
        // Warm the encrypted store and restore the saved server address before the UI asks.
        val store = CredentialStore.get(this)
        OliApi.shared.baseUrl = store.serverUrl
        OliApi.shared.token = store.token.ifBlank { null }
    }
}
