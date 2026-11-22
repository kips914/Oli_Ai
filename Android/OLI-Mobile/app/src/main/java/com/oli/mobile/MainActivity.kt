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
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
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
import com.oli.mobile.ui.OliBottomBar
import com.oli.mobile.ui.OliInk
import com.oli.mobile.ui.OliTheme
import com.oli.mobile.ui.SettingsScreen
import kotlinx.coroutines.launch

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

/** Bottom bar keeps only the two daily screens; everything else lives in the drawer. */
private enum class Tab(val label: String, val icon: ImageVector) {
    Chat("Чат", Icons.Filled.Chat),
    Files("Файлы", Icons.Filled.Folder)
}

/** Screens pushed on top of the tabs (no bottom bar). */
private enum class Overlay { None, Settings, About }

@Composable
private fun OliApp() {
    val context = LocalContext.current
    val repo = remember { OliRepository(context) }
    val session by repo.session.collectAsState()
    val messages by repo.messages.collectAsState()
    val busy by repo.busy.collectAsState()
    val usage by repo.usage.collectAsState()
    val offline by repo.defaultServerOffline.collectAsState()
    val models by repo.models.collectAsState()
    val limits by repo.usageLimits.collectAsState()
    val chats by repo.chats.collectAsState()
    val activeThread by repo.activeThread.collectAsState()
    val limitNotice by repo.limitNotice.collectAsState()

    var tab by remember { mutableStateOf(Tab.Chat) }
    var overlay by remember { mutableStateOf(Overlay.None) }
    var filesPath by remember { mutableStateOf("") }
    val scope = rememberCoroutineScope()

    LaunchedEffect(Unit) { repo.bootstrap() }

    LaunchedEffect(session) {
        if (session is OliRepository.Session.SignedIn || session is OliRepository.Session.Demo) {
            repo.refreshModels()
            repo.refreshChats()
        }
    }

    BackHandler(enabled = tab != Tab.Chat || filesPath.isNotEmpty() || overlay != Overlay.None) {
        when {
            overlay != Overlay.None -> overlay = Overlay.None
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

            when (overlay) {
                Overlay.Settings -> SettingsScreen(
                    repo = repo,
                    user = user,
                    usagePercent = usage?.percent ?: 0.0,
                    demo = demo,
                    onSignedOut = { overlay = Overlay.None },
                    onExitDemo = { repo.exitDemo(); overlay = Overlay.None },
                    onBack = { overlay = Overlay.None },
                    onOpenAbout = { overlay = Overlay.About }
                )

                Overlay.About -> com.oli.mobile.ui.AboutScreen(onBack = { overlay = Overlay.None })

                Overlay.None -> Scaffold(
                    containerColor = OliInk,
                    bottomBar = {
                        OliBottomBar(
                            current = tab.ordinal,
                            labels = Tab.entries.map { it.icon to it.label },
                            onSelect = { tab = Tab.entries[it] }
                        )
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
                                modelState = models,
                                usageLimits = limits,
                                chats = chats,
                                activeThread = activeThread,
                                limitNotice = limitNotice,
                                onOpenFiles = { filesPath = ""; tab = Tab.Files },
                                onExitDemo = { repo.exitDemo() },
                                onOpenSettings = { overlay = Overlay.Settings },
                                onPickModel = { id -> scope.launch { repo.chooseModel(id) } },
                                onOpenChat = { id -> scope.launch { repo.openChat(id) } },
                                onDeleteChat = { id -> scope.launch { repo.deleteChat(id) } },
                                onDismissLimit = { repo.dismissLimitNotice() }
                            )

                            Tab.Files -> FilesScreen(
                                repo = repo,
                                usage = if (demo) null else usage,
                                demo = demo,
                                path = filesPath,
                                onPathChange = { filesPath = it },
                                onBack = { tab = Tab.Chat }
                            )
                        }
                    }
                }
            }
        }
    }
}
