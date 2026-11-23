package com.oli.mobile

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.ui.input.pointer.pointerInput
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
import kotlinx.coroutines.CoroutineScope
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

            // Every screen change animates: forward pages slide in from the right,
            // going back slides in from the left, both with a soft fade/scale.
            AnimatedContent(
                targetState = screen,
                modifier = Modifier.fillMaxSize(),
                transitionSpec = {
                    val forward = targetState.ordinal >= initialState.ordinal
                    val dir = if (forward) 1 else -1
                    (fadeIn(tween(220)) +
                        slideInHorizontally(tween(280)) { it / 6 * dir } +
                        scaleIn(tween(280), initialScale = 0.97f))
                        .togetherWith(
                            fadeOut(tween(160)) +
                                slideOutHorizontally(tween(220)) { it / 8 * -dir } +
                                scaleOut(tween(220), targetScale = 0.97f)
                        )
                },
                label = "screen"
            ) { current ->
                OliScreen(
                    screen = current,
                    repo = repo,
                    demo = demo,
                    user = user,
                    initial = initial,
                    messages = messages,
                    busy = busy,
                    usage = usage,
                    models = models,
                    limits = limits,
                    chats = chats,
                    activeThread = activeThread,
                    limitNotice = limitNotice,
                    filesPath = filesPath,
                    onFilesPathChange = { filesPath = it },
                    onNavigate = { screen = it },
                    scope = scope
                )
            }
        }
    }
}

/**
 * One screen of the app. Kept in a separate composable so the navigation graph above
 * stays readable and every screen gets the same animation wrapper.
 *
 * Gestures: a horizontal swipe anywhere on the page works like the system back gesture —
 * right swipe from a page returns to the chat, left swipe on a sub-page goes back one level.
 */
@Composable
private fun OliScreen(
    screen: Screen,
    repo: OliRepository,
    demo: Boolean,
    user: UserProfile,
    initial: String,
    messages: List<OliRepository.ChatMessage>,
    busy: Boolean,
    usage: com.oli.mobile.data.Usage?,
    models: com.oli.mobile.data.ModelState?,
    limits: com.oli.mobile.data.UsageSnapshotData?,
    chats: List<com.oli.mobile.data.ChatSummary>,
    activeThread: String?,
    limitNotice: Boolean,
    filesPath: String,
    onFilesPathChange: (String) -> Unit,
    onNavigate: (Screen) -> Unit,
    scope: kotlinx.coroutines.CoroutineScope
) {
    val haptics = androidx.compose.ui.platform.LocalHapticFeedback.current
    fun back() {
        haptics.performHapticFeedback(androidx.compose.ui.hapticfeedback.HapticFeedbackType.TextHandleMove)
        onNavigate(Screen.Chat)
    }

    val swipeBack = Modifier
        .fillMaxSize()
        .pointerInput(screen, filesPath) {
            var total = 0f
            androidx.compose.foundation.gestures.detectHorizontalDragGestures(
                onDragStart = { total = 0f },
                onDragEnd = {
                    if (total > 100f && screen != Screen.Chat) {
                        if (screen == Screen.Files && filesPath.isNotEmpty())
                            onFilesPathChange(filesPath.substringBeforeLast('/', ""))
                        else back()
                    }
                    total = 0f
                },
                onDragCancel = { total = 0f }
            ) { _, dragAmount -> total += dragAmount }
        }

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
            onOpenFiles = { onFilesPathChange(""); onNavigate(Screen.Files) },
            onExitDemo = { repo.exitDemo() },
            onOpenSettings = { onNavigate(Screen.Settings) },
            onOpenProfile = { onNavigate(Screen.Profile) },
            onPickModel = { id -> scope.launch { repo.chooseModel(id) } },
            onOpenChat = { id -> scope.launch { repo.openChat(id) } },
            onDeleteChat = { id -> scope.launch { repo.deleteChat(id) } },
            onDismissLimit = { repo.dismissLimitNotice() }
        )

        Screen.Files -> Box(swipeBack) {
            FilesScreen(
                repo = repo,
                usage = if (demo) null else usage,
                demo = demo,
                path = filesPath,
                onPathChange = onFilesPathChange,
                onBack = { back() }
            )
        }

        Screen.Settings -> Box(swipeBack) {
            SettingsScreen(
                repo = repo,
                user = user,
                usagePercent = usage?.percent ?: 0.0,
                demo = demo,
                onSignedOut = { onNavigate(Screen.Chat) },
                onExitDemo = { repo.exitDemo(); onNavigate(Screen.Chat) },
                onBack = { back() },
                onOpenAbout = { onNavigate(Screen.About) },
                onOpenStorage = { onFilesPathChange(""); onNavigate(Screen.Files) },
                onOpenProfile = { onNavigate(Screen.Profile) }
            )
        }

        Screen.Profile -> Box(swipeBack) {
            ProfileScreen(
                repo = repo,
                user = user,
                usage = limits,
                filesUsedBytes = usage?.used ?: 0L,
                filesQuotaBytes = usage?.quota ?: (1024L * 1024 * 1024),
                demo = demo,
                onBack = { back() },
                onOpenStorage = { onFilesPathChange(""); onNavigate(Screen.Files) },
                onOpenSettings = { onNavigate(Screen.Settings) },
                onLogout = {
                    scope.launch {
                        repo.logout(forgetPassword = false)
                        onNavigate(Screen.Chat)
                    }
                }
            )
        }

        Screen.About -> Box(swipeBack) {
            AboutScreen(onBack = { onNavigate(Screen.Settings) })
        }
    }
}
