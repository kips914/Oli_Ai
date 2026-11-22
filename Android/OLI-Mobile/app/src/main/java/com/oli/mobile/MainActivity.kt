package com.oli.mobile

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Chat
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import com.oli.mobile.data.OliRepository
import com.oli.mobile.data.UserProfile
import com.oli.mobile.ui.AuthScreen
import com.oli.mobile.ui.ChatScreen
import com.oli.mobile.ui.FilesScreen
import com.oli.mobile.ui.OliTheme
import com.oli.mobile.ui.SettingsScreen

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            OliTheme {
                Surface(Modifier.fillMaxSize()) {
                    OliApp()
                }
            }
        }
    }
}

private enum class Tab(val label: String, val icon: ImageVector) {
    Chat("Чат", Icons.Filled.Chat),
    Files("Файлы", Icons.Filled.Folder),
    Settings("Настройки", Icons.Filled.Settings)
}

@Composable
private fun OliApp() {
    val context = LocalContext.current
    val repo = remember { OliRepository(context) }
    val session by repo.session.collectAsState()
    val messages by repo.messages.collectAsState()
    val busy by repo.busy.collectAsState()
    val usage by repo.usage.collectAsState()
    val offline by repo.defaultServerOffline.collectAsState()
    var tab by remember { mutableStateOf(Tab.Chat) }
    var filesPath by remember { mutableStateOf("") }

    LaunchedEffect(Unit) { repo.bootstrap() }

    // Android Back: up one folder inside Files, then back to the chat, then exit.
    BackHandler(enabled = tab != Tab.Chat || filesPath.isNotEmpty()) {
        when {
            tab == Tab.Files && filesPath.isNotEmpty() -> filesPath = filesPath.substringBeforeLast('/', "")
            tab != Tab.Chat -> tab = Tab.Chat
            else -> filesPath = ""
        }
    }

    when (val state = session) {
        is OliRepository.Session.Loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            CircularProgressIndicator()
        }

        is OliRepository.Session.SignedOut -> AuthScreen(
            repo = repo,
            defaultServerOffline = offline,
            onDemo = { repo.enterDemo(); tab = Tab.Chat }
        )

        else -> {
            val demo = state is OliRepository.Session.Demo
            val user = when (state) {
                is OliRepository.Session.SignedIn -> state.user
                else -> UserProfile("demo", "гость", "Гость", 1024L * 1024 * 1024, false)
            }

            Scaffold(
                bottomBar = {
                    NavigationBar {
                        Tab.entries.forEach { entry ->
                            NavigationBarItem(
                                selected = tab == entry,
                                onClick = { tab = entry },
                                icon = { Icon(entry.icon, contentDescription = entry.label) },
                                label = { Text(entry.label) }
                            )
                        }
                    }
                }
            ) { padding ->
                Box(Modifier.fillMaxSize().padding(padding)) {
                    when (tab) {
                        Tab.Chat -> ChatScreen(
                            repo = repo,
                            messages = messages,
                            busy = busy,
                            displayName = if (demo) "гость" else user.displayName,
                            demo = demo,
                            onOpenFiles = { filesPath = ""; tab = Tab.Files },
                            onExitDemo = { repo.exitDemo() }
                        )

                        Tab.Files -> FilesScreen(
                            repo = repo,
                            usage = if (demo) null else usage,
                            demo = demo,
                            path = filesPath,
                            onPathChange = { filesPath = it },
                            onBack = { tab = Tab.Chat }
                        )

                        Tab.Settings -> SettingsScreen(
                            repo = repo,
                            user = user,
                            usagePercent = usage?.percent ?: 0.0,
                            demo = demo,
                            onSignedOut = { tab = Tab.Chat },
                            onExitDemo = { repo.exitDemo(); tab = Tab.Chat }
                        )
                    }
                }
            }
        }
    }
}
