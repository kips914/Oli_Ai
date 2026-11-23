package com.oli.mobile

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.CircularProgressIndicator
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
import androidx.compose.ui.platform.LocalContext
import com.oli.mobile.data.OliRepository
import com.oli.mobile.data.UserProfile
import com.oli.mobile.ui.AboutScreen
import com.oli.mobile.ui.AuthScreen
import com.oli.mobile.ui.ChatScreen
import com.oli.mobile.ui.FilesScreen
import com.oli.mobile.ui.OliTheme
import com.oli.mobile.ui.ProfileScreen
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

/**
 * There is no bottom bar any more: the chat is the app. Everything else opens as a
 * full screen from the header (avatar → profile) or from the chats drawer (settings).
 */
private enum class Screen { Chat, Files, Settings, Profile, About }

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

    var screen by remember { mutableStateOf(Screen.Chat) }
    var filesPath by remember { mutableStateOf("") }
    val scope = rememberCoroutineScope()

    LaunchedEffect(Unit) { repo.bootstrap() }

    LaunchedEffect(session) {
        if (session is OliRepository.Session.SignedIn || session is OliRepository.Session.Demo) {
            repo.refreshModels()
            repo.refreshChats()
        }
    }

    BackHandler(enabled = screen != Screen.Chat || filesPath.isNotEmpty()) {
        when {
            screen == Screen.Files && filesPath.isNotEmpty() -> filesPath = filesPath.substringBeforeLast('/', "")
            else -> screen = Screen.Chat
        }
    }

    when (val state = session) {
        is OliRepository.Session.Loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            CircularProgressIndicator()
        }

        is OliRepository.Session.SignedOut -> AuthScreen(
            repo = repo,
            defaultServerOffline = offline,
            onDemo = { repo.enterDemo(); screen = Screen.Chat }
        )

        else -> {
            val demo = state is OliRepository.Session.Demo
            val user = when (state) {
                is OliRepository.Session.SignedIn -> state.user
                else -> UserProfile("demo", "гость", "Гость", 1024L * 1024 * 1024, false)
            }
            val initial = (user.displayName.ifBlank { user.userName }).trim().take(1).uppercase().ifBlank { "O" }

            when (screen) {
                Screen.Chat -> ChatScreen(
                    repo = repo,
                    messages = messages,
                    busy = busy,
                    displayName = if (demo) "гость" else user.displayName,
                    demo = demo,
                    userInitial = initial,
                    modelState = models,
                    usageLimits = limits,
                    chats = chats,
                    activeThread = activeThread,
                    limitNotice = limitNotice,
                    onOpenFiles = { filesPath = ""; screen = Screen.Files },
                    onExitDemo = { repo.exitDemo() },
                    onOpenSettings = { screen = Screen.Settings },
                    onOpenProfile = { screen = Screen.Profile },
                    onPickModel = { id -> scope.launch { repo.chooseModel(id) } },
                    onOpenChat = { id -> scope.launch { repo.openChat(id) } },
                    onDeleteChat = { id -> scope.launch { repo.deleteChat(id) } },
                    onDismissLimit = { repo.dismissLimitNotice() }
                )

                Screen.Files -> FilesScreen(
                    repo = repo,
                    usage = if (demo) null else usage,
                    demo = demo,
                    path = filesPath,
                    onPathChange = { filesPath = it },
                    onBack = { screen = Screen.Chat }
                )

                Screen.Settings -> SettingsScreen(
                    repo = repo,
                    user = user,
                    usagePercent = usage?.percent ?: 0.0,
                    demo = demo,
                    onSignedOut = { screen = Screen.Chat },
                    onExitDemo = { repo.exitDemo(); screen = Screen.Chat },
                    onBack = { screen = Screen.Chat },
                    onOpenAbout = { screen = Screen.About },
                    onOpenStorage = { filesPath = ""; screen = Screen.Files },
                    onOpenProfile = { screen = Screen.Profile }
                )

                Screen.Profile -> ProfileScreen(
                    repo = repo,
                    user = user,
                    usage = limits,
                    filesUsedBytes = usage?.used ?: 0L,
                    filesQuotaBytes = usage?.quota ?: (1024L * 1024 * 1024),
                    demo = demo,
                    onBack = { screen = Screen.Chat },
                    onOpenStorage = { filesPath = ""; screen = Screen.Files },
                    onOpenSettings = { screen = Screen.Settings },
                    onLogout = {
                        scope.launch {
                            repo.logout(forgetPassword = false)
                            screen = Screen.Chat
                        }
                    }
                )

                Screen.About -> AboutScreen(onBack = { screen = Screen.Settings })
            }
        }
    }
}
