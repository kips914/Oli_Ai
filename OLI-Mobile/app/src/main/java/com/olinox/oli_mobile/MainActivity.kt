package com.olinox.oli_mobile

import android.content.Intent
import android.graphics.Color
import android.net.Uri
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import com.google.android.gms.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.android.gms.mlkit.vision.codescanner.GmsBarcodeScanning

class MainActivity : AppCompatActivity() {
    private lateinit var webView: WebView
    private lateinit var setupPanel: LinearLayout
    private lateinit var urlInput: EditText
    private val prefs by lazy { getSharedPreferences("oli", MODE_PRIVATE) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        buildUi()
    }

    private fun buildUi() {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Color.rgb(11, 15, 20))
            setPadding(24, 24, 24, 24)
        }

        setupPanel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
        }
        val title = TextView(this).apply {
            text = "OLI Mobile"
            textSize = 30f
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
        }
        val subtitle = TextView(this).apply {
            text = "Подключение к OLI на ПК через Tailscale"
            textSize = 16f
            setTextColor(Color.LTGRAY)
            gravity = Gravity.CENTER
            setPadding(0, 12, 0, 24)
        }
        urlInput = EditText(this).apply {
            hint = "http://100.x.x.x:8765"
            setText(prefs.getString("server", ""))
            setTextColor(Color.WHITE)
            setHintTextColor(Color.GRAY)
            setSingleLine(true)
        }
        setupPanel.addView(title)
        setupPanel.addView(subtitle)
        setupPanel.addView(urlInput, LinearLayout.LayoutParams(-1, 60))
        setupPanel.addView(button("Сканировать QR Tailscale") { scanQr(true) })
        setupPanel.addView(button("Сканировать QR OLI") { scanQr(false) })
        setupPanel.addView(button("Подключиться") { connect(urlInput.text.toString()) })
        setupPanel.addView(button("Открыть Tailscale") {
            openExternal("tailscale://")
        })

        root.addView(setupPanel, LinearLayout.LayoutParams(-1, -1))

        webView = WebView(this).apply {
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            settings.allowFileAccess = false
            settings.allowContentAccess = false
            webViewClient = object : WebViewClient() {
                override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                    val u = request.url.toString()
                    if (u.startsWith("http://") || u.startsWith("https://")) return false
                    openExternal(u); return true
                }
            }
        }
        root.addView(webView, LinearLayout.LayoutParams(-1, 0, 1f))
        setContentView(root)
        showSetup()
    }

    private fun button(text: String, action: () -> Unit) = Button(this).apply {
        this.text = text
        setOnClickListener { action() }
    }

    private fun showSetup() {
        setupPanel.visibility = View.VISIBLE
        webView.visibility = View.GONE
    }

    private fun connect(url: String) {
        val clean = url.trim().removeSuffix("/")
        if (!clean.startsWith("http://") && !clean.startsWith("https://")) {
            Toast.makeText(this, "Укажи адрес вида http://100.x.x.x:8765", Toast.LENGTH_LONG).show()
            return
        }
        prefs.edit().putString("server", clean).apply()
        setupPanel.visibility = View.GONE
        webView.visibility = View.VISIBLE
        webView.loadUrl(clean)
    }

    private fun scanQr(tailscale: Boolean) {
        val options = GmsBarcodeScannerOptions.Builder().enableAutoZoom().build()
        GmsBarcodeScanning.getClient(this, options).startScan()
            .addOnSuccessListener { barcode ->
                val value = barcode.rawValue?.trim().orEmpty()
                if (value.isEmpty()) return@addOnSuccessListener
                if (tailscale) {
                    // Tailscale Android supports QR-based login. We do not try to
                    // control its VPN service from OLI; instead we hand the official
                    // login URL to the system so the user can authorize Tailscale.
                    if (value.startsWith("http://") || value.startsWith("https://")) {
                        openExternal(value)
                        Toast.makeText(this, "Открыл ссылку авторизации Tailscale", Toast.LENGTH_LONG).show()
                    } else {
                        Toast.makeText(this, "QR не похож на ссылку Tailscale", Toast.LENGTH_LONG).show()
                    }
                } else {
                    // Accept either a plain OLI gateway URL or an OLI deep link.
                    val uri = runCatching { Uri.parse(value) }.getOrNull()
                    val server = if (uri?.scheme == "oli") uri.getQueryParameter("server") else value
                    if (!server.isNullOrBlank()) {
                        urlInput.setText(server)
                        connect(server)
                    }
                }
            }
            .addOnFailureListener { e ->
                Toast.makeText(this, "QR: ${e.message ?: "сканирование отменено"}", Toast.LENGTH_LONG).show()
            }
    }

    private fun openExternal(value: String) {
        try { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(value))) }
        catch (_: Exception) { Toast.makeText(this, "Не удалось открыть ссылку", Toast.LENGTH_LONG).show() }
    }

    override fun onBackPressed() {
        if (webView.visibility == View.VISIBLE && webView.canGoBack()) webView.goBack()
        else super.onBackPressed()
    }
}
