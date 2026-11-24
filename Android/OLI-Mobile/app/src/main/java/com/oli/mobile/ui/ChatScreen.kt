package com.oli.mobile.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.slideInVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.Image
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.material.icons.filled.PhotoCamera
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Mic
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.offset
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.GraphicEq
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.oli.mobile.data.ChatSummary
import com.oli.mobile.data.ModelState
import com.oli.mobile.data.OliRepository
import com.oli.mobile.data.UsageSnapshotData
import kotlinx.coroutines.launch

/**
 * Chat in the OLI style: graphite canvas, rotating OLI mark, glass pills, model picker,
 * a chats drawer on the hamburger and the token-limit gauge in the composer.
 */
@Composable
fun ChatScreen(
    repo: OliRepository,
    messages: List<OliRepository.ChatMessage>,
    busy: Boolean,
    displayName: String,
    demo: Boolean,
    userInitial: String,
    modelState: ModelState?,
    usageLimits: UsageSnapshotData?,
    chats: List<ChatSummary>,
    activeThread: String?,
    limitNotice: Boolean,
    onOpenFiles: () -> Unit,
    onExitDemo: () -> Unit,
    onOpenSettings: () -> Unit,
    onOpenProfile: () -> Unit,
    onPickModel: (String) -> Unit,
    onOpenChat: (String?) -> Unit,
    onDeleteChat: (String) -> Unit,
    onDismissLimit: () -> Unit
) {
    var input by remember { mutableStateOf("") }
    var drawerOpen by remember { mutableStateOf(false) }
    var modelsOpen by remember { mutableStateOf(false) }
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()
    val haptics = LocalHapticFeedback.current

    // --- photo for analysis
    var attachment by remember { mutableStateOf<PendingImage?>(null) }
    var fullScreen by remember { mutableStateOf<android.graphics.Bitmap?>(null) }
    var pickedUri by remember { mutableStateOf<android.net.Uri?>(null) }
    val context = LocalContext.current
    val pickImage = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        pickedUri = uri
    }
    LaunchedEffect(pickedUri) {
        val uri = pickedUri ?: return@LaunchedEffect
        pickedUri = null
        haptics.performHapticFeedback(HapticFeedbackType.LongPress)
        attachment = loadPendingImage(context, uri)
    }

    // animated entrance for the whole screen
    val enter = remember { Animatable(0f) }
    LaunchedEffect(Unit) { enter.animateTo(1f, tween(450)) }

    // keep the chat scrolled to the newest message (and to the typing bubble)
    LaunchedEffect(messages.size, busy) {
        if (messages.isNotEmpty()) listState.animateScrollToItem(messages.size)
    }

    // swipe right anywhere on the canvas → chats drawer; swipe left → new chat
    fun onSwipe(totalX: Float) {
        if (totalX > 90f) {
            haptics.performHapticFeedback(HapticFeedbackType.TextHandleMove)
            drawerOpen = true
        } else if (totalX < -90f) {
            haptics.performHapticFeedback(HapticFeedbackType.TextHandleMove)
            scope.launch { repo.newChat() }
        }
    }

    val selectedModel = modelState?.models?.firstOrNull { it.id == modelState.selected }
    val greeting = remember(displayName) {
        val name = displayName.trim()
        if (name.isEmpty() || name.equals("гость", true)) "Привет! Чем займёмся?"
        else "Привет, $name! Чем займёмся?"
    }

    fun send(text: String) {
        val value = text.trim()
        val photo = attachment
        if (photo != null && !busy) {
            // A photo is attached: the question goes with it (empty question is fine).
            attachment = null
            input = ""
            scope.launch { repo.analyzeImage(photo.bitmap, photo.fileName, value) }
            return
        }
        if (value.isEmpty() || busy) return
        input = ""
        scope.launch { repo.send(value) }
    }

    Box(
        Modifier
            .fillMaxSize()
            .background(OliInk)
            .scale(0.98f + 0.02f * enter.value)
            .alpha(enter.value)
            // Horizontal swipes anywhere: right → chats drawer, left → new chat.
            // The vertical list keeps its own scrolling (it consumes only vertical drags).
            .pointerInput(Unit) {
                var total = 0f
                detectHorizontalDragGestures(
                    onDragStart = { total = 0f },
                    onDragEnd = { onSwipe(total); total = 0f },
                    onDragCancel = { total = 0f }
                ) { _, dragAmount -> total += dragAmount }
            }
    ) {
        // backlight
        Box(
            Modifier
                .align(Alignment.TopCenter)
                .fillMaxWidth()
                .height(460.dp)
                .background(
                    Brush.radialGradient(
                        colors = listOf(
                            Color(0xFF0F2A52).copy(alpha = 0.55f),
                            Color(0xFF081222).copy(alpha = 0.35f),
                            Color.Transparent
                        ),
                        radius = 850f
                    )
                )
        )

        // living background: drifting aurora + a light sweep across the canvas
        AuroraBackdrop(intensity = if (busy) 1.6f else 1f)
        LightSweep()

        Column(Modifier.fillMaxSize().statusBarsPadding().imePadding()) {
            if (demo) DemoBanner(onExitDemo)

            TopBar(
                modelTitle = selectedModel?.title ?: "Olim Pass",
                userInitial = userInitial,
                onMenu = { drawerOpen = true },
                onModel = { modelsOpen = true },
                onProfile = onOpenProfile,
                onNewChat = { scope.launch { repo.newChat() } }
            )

            if (limitNotice) LimitBanner(onDismissLimit, onOpenModelPicker = { modelsOpen = true })

            if (messages.isEmpty()) {
                Hero(
                    greeting = greeting,
                    busy = busy,
                    demo = demo,
                    onLogin = onExitDemo,
                    onPick = { prompt -> send(prompt) },
                    modifier = Modifier.weight(1f)
                )
            } else {
                LazyColumn(
                    state = listState,
                    modifier = Modifier.weight(1f).padding(horizontal = 14.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    item { Spacer(Modifier.height(6.dp)) }
                    items(messages, key = { it.id }) { message ->
                        MessageBubble(message, onImageTap = { fullScreen = it })
                    }
                    if (busy) item { TypingBubble() }
                    item { Spacer(Modifier.height(8.dp)) }
                }
            }

            SuggestionRow(onPick = { send(it) })

            Composer(
                value = input,
                onValueChange = { input = it },
                enabled = !busy,
                placeholder = when {
                    attachment != null -> "Что сделать с фото?"
                    demo -> "Режим просмотра — сообщение не отправится"
                    else -> "Спроси меня. Поручи мне."
                },
                usage = usageLimits,
                attachment = attachment,
                onClearAttachment = { attachment = null },
                onPickImage = { pickImage.launch("image/*") },
                onSend = { send(input) },
                onAttach = onOpenFiles,
                onOpenLimits = { modelsOpen = true }
            )
        }
    }

    if (drawerOpen) {
        ChatsDrawer(
            chats = chats,
            activeThread = activeThread,
            onDismiss = { drawerOpen = false },
            onOpen = { id -> drawerOpen = false; onOpenChat(id) },
            onNew = { drawerOpen = false; onOpenChat(null) },
            onDelete = { id -> onDeleteChat(id) },
            onSettings = { drawerOpen = false; onOpenSettings() }
        )
    }

    if (modelsOpen) {
        ModelSheet(
            state = modelState,
            usage = usageLimits,
            onDismiss = { modelsOpen = false },
            onPick = { id -> onPickModel(id); modelsOpen = false }
        )
    }

    // Tapping a picture opens it full screen (pinch-free, tap/back closes).
    fullScreen?.let { bitmap ->
        ImageViewer(bitmap) { fullScreen = null }
    }
}

// ------------------------------------------------------------------ top bar

@Composable
private fun TopBar(
    modelTitle: String,
    userInitial: String,
    onMenu: () -> Unit,
    onModel: () -> Unit,
    onProfile: () -> Unit,
    onNewChat: () -> Unit
) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        GlassCircleButton(onClick = onMenu) {
            Icon(Icons.Filled.Menu, contentDescription = "Чаты", tint = OliText, modifier = Modifier.size(20.dp))
        }
        Spacer(Modifier.width(10.dp))

        GlassPill(onClick = onModel) {
            Row(Modifier.padding(horizontal = 14.dp, vertical = 9.dp), verticalAlignment = Alignment.CenterVertically) {
                var pulse by remember { mutableStateOf(false) }
                LaunchedEffect(Unit) {
                    while (true) {
                        kotlinx.coroutines.delay(1500)
                        pulse = !pulse
                    }
                }
                val dotScale by animateFloatAsState(if (pulse) 1f else 0.62f, tween(1400), label = "dotScale")
                Box(
                    Modifier
                        .size(9.dp)
                        .scale(dotScale)
                        .clip(CircleShape)
                        .background(Brush.linearGradient(listOf(OliBlueBright, OliCyan)))
                )
                Spacer(Modifier.width(8.dp))
                Text(modelTitle, style = MaterialTheme.typography.labelMedium, color = OliText, fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.width(6.dp))
                Text("▾", style = MaterialTheme.typography.labelMedium, color = OliTextMuted)
            }
        }

        Spacer(Modifier.weight(1f))

        GlassCircleButton(onClick = onNewChat) {
            Icon(Icons.Filled.Add, contentDescription = "Новый чат", tint = OliText, modifier = Modifier.size(21.dp))
        }
        Spacer(Modifier.width(8.dp))

        // profile avatar (top-right)
        Box(
            Modifier.size(44.dp).clip(CircleShape).border(1.dp, OliOutline.copy(alpha = 0.8f), CircleShape)
                .clickable(onClick = onProfile),
            contentAlignment = Alignment.Center
        ) {
            Avatar(initial = userInitial, size = 40, ring = false)
        }
    }
}

// ------------------------------------------------------------------ hero

private data class Suggestion(val icon: String, val label: String, val prompt: String)

@Composable
private fun Hero(
    greeting: String,
    busy: Boolean,
    demo: Boolean,
    onLogin: () -> Unit,
    onPick: (String) -> Unit,
    modifier: Modifier = Modifier
) {
    val suggestions = remember {
        listOf(
            Suggestion("✦", "Идея проекта", "Предложи 3 идеи небольшого проекта, который можно сделать за вечер."),
            Suggestion("✉", "Написать текст", "Помоги написать короткое письмо коллеге о переносе встречи."),
            Suggestion("⌘", "Код", "Напиши функцию на Python, которая переименовывает файлы по дате."),
            Suggestion("◈", "Объяснить", "Объясни простыми словами, как работают большие языковые модели.")
        )
    }

    Column(
        modifier.fillMaxWidth().padding(horizontal = 22.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        val heroIn = remember { Animatable(0f) }
        LaunchedEffect(Unit) { heroIn.animateTo(1f, spring(dampingRatio = 0.7f, stiffness = Spring.StiffnessLow)) }
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier.scale(0.8f + 0.2f * heroIn.value).alpha(heroIn.value)
        ) {
            OliBurst(size = 250.dp)
            OliMark(size = 130.dp, working = busy)
            Sparkles(Modifier.offset(x = 96.dp, y = (-48).dp))
        }

        Spacer(Modifier.height(28.dp))

        AnimatedVisibility(visible = true, enter = fadeIn() + slideInVertically(initialOffsetY = { it / 4 })) {
            Text(
                greeting,
                style = MaterialTheme.typography.headlineSmall,
                color = OliText,
                textAlign = TextAlign.Center
            )
        }

        if (demo) {
            Spacer(Modifier.height(16.dp))
            GhostButton(text = "Войти", onClick = onLogin, accent = true)
            Spacer(Modifier.height(6.dp))
            Text(
                "Режим просмотра: ответы не приходят",
                style = MaterialTheme.typography.labelSmall,
                color = OliTextMuted
            )
        }

        Spacer(Modifier.height(24.dp))

        Column(verticalArrangement = Arrangement.spacedBy(9.dp)) {
            suggestions.chunked(2).forEach { row ->
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(9.dp)) {
                    row.forEach { suggestion ->
                        GlassCard(modifier = Modifier.weight(1f), onClick = { onPick(suggestion.prompt) }) {
                            Row(
                                Modifier.padding(horizontal = 13.dp, vertical = 13.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(suggestion.icon, fontSize = 14.sp, color = OliBlueBright)
                                Spacer(Modifier.width(8.dp))
                                Text(
                                    suggestion.label,
                                    style = MaterialTheme.typography.labelMedium,
                                    color = OliText,
                                    maxLines = 1
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

// ------------------------------------------------------------------ suggestions & limit

@Composable
private fun SuggestionRow(onPick: (String) -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 14.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        ActionChip("📷 Что на фото?", onClick = { onPick("Пришлите фото кнопкой 📷 — я разберу, что на нём, и отвечу по делу.") })
        ActionChip("Новый проект", onClick = { onPick("Помоги начать новый проект: спроси меня о деталях и составь план.") })
        ActionChip("Сводка дня", onClick = { onPick("Сделай короткую сводку дня и предложи 3 задачи на завтра.") })
        ActionChip("Код", onClick = { onPick("Напиши функцию на Python, которая переименовывает файлы по дате.") })
    }
}

@Composable
private fun LimitBanner(onDismiss: () -> Unit, onOpenModelPicker: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 14.dp, vertical = 4.dp)
            .clip(RoundedCornerShape(14.dp))
            .background(Color(0x33FFB020))
            .border(1.dp, Color(0x66FFB020), RoundedCornerShape(14.dp))
            .padding(start = 14.dp, end = 6.dp, top = 8.dp, bottom = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            "Лимит токенов исчерпан — отвечает Olim Son",
            style = MaterialTheme.typography.labelMedium,
            color = Color(0xFFFFD79A),
            modifier = Modifier.weight(1f)
        )
        Box(
            Modifier.clip(RoundedCornerShape(10.dp)).clickable(onClick = onOpenModelPicker)
                .padding(horizontal = 10.dp, vertical = 6.dp)
        ) { Text("Подробнее", style = MaterialTheme.typography.labelSmall, color = OliText) }
        Box(
            Modifier.clip(RoundedCornerShape(10.dp)).clickable(onClick = onDismiss)
                .padding(horizontal = 8.dp, vertical = 6.dp)
        ) { Text("✕", style = MaterialTheme.typography.labelSmall, color = OliTextMuted) }
    }
}

// ------------------------------------------------------------------ composer

@Composable
private fun Composer(
    value: String,
    onValueChange: (String) -> Unit,
    enabled: Boolean,
    placeholder: String,
    usage: UsageSnapshotData?,
    attachment: PendingImage?,
    onClearAttachment: () -> Unit,
    onPickImage: () -> Unit,
    onSend: () -> Unit,
    onAttach: () -> Unit,
    onOpenLimits: () -> Unit
) {
    Column(Modifier.fillMaxWidth().navigationBarsPadding()) {
        if (usage != null) {
            val fraction = if (usage.hourLimit > 0) (usage.hourUsed.toFloat() / usage.hourLimit).coerceIn(0f, 1f) else 0f
            val animated by animateFloatAsState(fraction, label = "limit")
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 18.dp).clickable(onClick = onOpenLimits),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Box(
                    Modifier
                        .weight(1f)
                        .height(3.dp)
                        .clip(RoundedCornerShape(2.dp))
                        .background(OliOutline)
                ) {
                    Box(
                        Modifier
                            .fillMaxWidth(animated)
                            .height(3.dp)
                            .background(
                                if (usage.exhausted) SolidColor(Color(0xFFFFB020))
                                else Brush.horizontalGradient(listOf(OliBlue, OliBlueBright))
                            )
                    )
                }
                Spacer(Modifier.width(10.dp))
                Text(
                    "${compact(usage.hourUsed)}/${compact(usage.hourLimit.toLong())} токенов · ${usage.activeUsers} онлайн",
                    style = MaterialTheme.typography.labelSmall,
                    color = if (usage.exhausted) Color(0xFFFFB020) else OliTextMuted
                )
            }
            Spacer(Modifier.height(6.dp))
        }

        // preview of the photo that will be analysed
        AnimatedVisibility(
            visible = attachment != null,
            enter = fadeIn() + slideInVertically(initialOffsetY = { it / 2 }) + scaleIn(initialScale = 0.9f),
            exit = fadeOut()
        ) {
            attachment?.let { photo ->
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 14.dp, vertical = 4.dp)
                        .clip(RoundedCornerShape(16.dp))
                        .background(OliGlass)
                        .border(1.dp, OliBlue.copy(alpha = 0.45f), RoundedCornerShape(16.dp))
                        .padding(8.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Image(
                        bitmap = photo.bitmap.asImageBitmap(),
                        contentDescription = "Фото для анализа",
                        contentScale = ContentScale.Crop,
                        modifier = Modifier.size(52.dp).clip(RoundedCornerShape(12.dp))
                    )
                    Spacer(Modifier.width(10.dp))
                    Column(Modifier.weight(1f)) {
                        Text("Фото для анализа", style = MaterialTheme.typography.labelMedium, color = OliText)
                        Text(
                            "Спросите что угодно — OLI посмотрит картинку",
                            style = MaterialTheme.typography.labelSmall,
                            color = OliTextMuted
                        )
                    }
                    Box(
                        Modifier.size(34.dp).clip(CircleShape).clickable(onClick = onClearAttachment),
                        contentAlignment = Alignment.Center
                    ) {
                        Text("✕", color = OliTextMuted, style = MaterialTheme.typography.labelMedium)
                    }
                }
            }
        }

        Row(
            Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                Modifier
                    .weight(1f)
                    .height(56.dp)
                    .clip(RoundedCornerShape(28.dp))
                    .background(OliGlass)
                    .border(1.dp, OliOutline.copy(alpha = 0.85f), RoundedCornerShape(28.dp)),
                contentAlignment = Alignment.Center
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    IconButtonBox(icon = Icons.Filled.Add, description = "Хранилище и файлы", onClick = onAttach)
                    IconButtonBox(icon = Icons.Filled.PhotoCamera, description = "Фото для анализа", onClick = onPickImage)
                    // live microphone: speech becomes text in the input field
                    VoiceButton(onText = onValueChange, enabled = enabled)
                    Box(Modifier.weight(1f)) {
                        if (value.isEmpty()) {
                            Text(placeholder, style = MaterialTheme.typography.bodyLarge, color = OliTextMuted, maxLines = 1)
                        }
                        BasicTextField(
                            value = value,
                            onValueChange = onValueChange,
                            enabled = enabled,
                            textStyle = TextStyle(color = OliText, fontSize = 16.sp),
                            cursorBrush = SolidColor(OliBlueBright),
                            maxLines = 4,
                            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
                            keyboardActions = KeyboardActions(onSend = { onSend() }),
                            modifier = Modifier.fillMaxWidth()
                        )
                    }
                }
            }

            Spacer(Modifier.width(8.dp))

            SendButton(enabled = enabled && value.isNotBlank(), onClick = onSend)
        }
    }
}

/**
 * Live microphone: turns speech into text in the input field, so the button is not a
 * decoration. Works through the system recogniser; when none is installed it says so
 * instead of doing nothing.
 */
@Composable
private fun VoiceButton(onText: (String) -> Unit, enabled: Boolean) {
    val context = LocalContext.current
    val haptics = LocalHapticFeedback.current
    var listening by remember { mutableStateOf(false) }
    var failure by remember { mutableStateOf<String?>(null) }

    val launcher = rememberLauncherForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        listening = false
        if (result.resultCode != android.app.Activity.RESULT_OK) return@rememberLauncherForActivityResult
        val spoken = result.data
            ?.getStringArrayListExtra(android.speech.RecognizerIntent.EXTRA_RESULTS)
            ?.firstOrNull()
            .orEmpty()
        if (spoken.isNotBlank()) onText(spoken)
    }

    fun start() {
        failure = null
        val intent = android.content.Intent(android.speech.RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
            putExtra(
                android.speech.RecognizerIntent.EXTRA_LANGUAGE_MODEL,
                android.speech.RecognizerIntent.LANGUAGE_MODEL_FREE_FORM
            )
            putExtra(android.speech.RecognizerIntent.EXTRA_LANGUAGE, java.util.Locale.getDefault().toLanguageTag())
            putExtra(android.speech.RecognizerIntent.EXTRA_PROMPT, "Говорите — текст появится в поле ввода")
        }
        if (intent.resolveActivity(context.packageManager) == null) {
            failure = "На телефоне нет распознавания речи"
            return
        }
        haptics.performHapticFeedback(HapticFeedbackType.LongPress)
        listening = true
        runCatching { launcher.launch(intent) }.onFailure {
            listening = false
            failure = "Не удалось запустить микрофон"
        }
    }

    val pulse by rememberInfiniteTransition(label = "mic").animateFloat(
        initialValue = 0.4f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            tween(700, easing = LinearEasing),
            RepeatMode.Reverse
        ),
        label = "micPulse"
    )

    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Box(
            Modifier
                .size(40.dp)
                .clip(CircleShape)
                .background(if (listening) OliBlue.copy(alpha = 0.35f * pulse) else Color.Transparent)
                .clickable(enabled = enabled) { start() },
            contentAlignment = Alignment.Center
        ) {
            Icon(
                androidx.compose.material.icons.Icons.Filled.Mic,
                contentDescription = "Голосовой ввод",
                tint = if (listening) OliBlueBright else OliTextMuted,
                modifier = Modifier.size(20.dp)
            )
        }
        failure?.let {
            Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.error)
            LaunchedEffect(it) {
                kotlinx.coroutines.delay(2500)
                failure = null
            }
        }
    }
}

@Composable
private fun IconButtonBox(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    description: String,
    onClick: () -> Unit
) {
    Box(
        Modifier.size(40.dp).clip(CircleShape).clickable(onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        Icon(icon, contentDescription = description, tint = OliTextMuted, modifier = Modifier.size(20.dp))
    }
}

@Composable
private fun SendButton(enabled: Boolean, onClick: () -> Unit) {
    val background = if (enabled) Brush.linearGradient(listOf(OliBlue, OliCyan))
    else Brush.linearGradient(listOf(OliGlassSoft, OliGlassSoft))

    Box(
        Modifier
            .size(56.dp)
            .clip(CircleShape)
            .background(background)
            .border(1.dp, OliOutline.copy(alpha = 0.7f), CircleShape)
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        Icon(
            if (enabled) Icons.Filled.ArrowUpward else Icons.Filled.GraphicEq,
            contentDescription = "Отправить",
            tint = if (enabled) Color.White else OliTextMuted,
            modifier = Modifier.size(22.dp)
        )
    }
}

// ------------------------------------------------------------------ messages

@Composable
private fun MessageBubble(message: OliRepository.ChatMessage, onImageTap: (android.graphics.Bitmap) -> Unit = {}) {
    val isUser = message.fromUser

    // Bubble entrance: grows from 92% and fades in, like a message "landing" in the chat.
    val appear = remember { Animatable(0f) }
    LaunchedEffect(message.id) { appear.animateTo(1f, spring(dampingRatio = 0.75f, stiffness = Spring.StiffnessMediumLow)) }

    Row(
        Modifier
            .fillMaxWidth()
            .graphicsLayer {
                alpha = appear.value
                scaleX = 0.92f + 0.08f * appear.value
                scaleY = 0.92f + 0.08f * appear.value
                transformOrigin = TransformOrigin(if (isUser) 1f else 0f, 1f)
            },
        horizontalArrangement = if (isUser) Arrangement.End else Arrangement.Start,
        verticalAlignment = Alignment.Bottom
    ) {
        if (!isUser) {
            OliMark(size = 26.dp)
            Spacer(Modifier.width(8.dp))
        }
        Box(
            Modifier
                .widthIn(max = 560.dp)
                .clip(
                    if (isUser) RoundedCornerShape(18.dp, 18.dp, 6.dp, 18.dp)
                    else RoundedCornerShape(18.dp, 18.dp, 18.dp, 6.dp)
                )
                .background(
                    if (isUser) Brush.linearGradient(listOf(OliBlue, Color(0xFF2559C9)))
                    else Brush.linearGradient(listOf(OliGlass, OliGlassSoft))
                )
                .border(
                    1.dp,
                    if (isUser) Color.Transparent else OliOutline.copy(alpha = 0.7f),
                    RoundedCornerShape(18.dp)
                )
                .padding(horizontal = 15.dp, vertical = 11.dp)
        ) {
            if (!isUser) {
                // glass sheen: a slow highlight that makes the bubble feel physical
                GlassSheen(
                    Modifier
                        .matchParentSize()
                        .clip(
                            RoundedCornerShape(18.dp, 18.dp, 18.dp, 6.dp)
                        )
                )
            }
            Column {
                message.image?.let { bitmap ->
                    Image(
                        bitmap = bitmap.asImageBitmap(),
                        contentDescription = "Приложенное изображение",
                        contentScale = ContentScale.Crop,
                        modifier = Modifier
                            .widthIn(max = 260.dp)
                            .aspectRatio(bitmap.width.toFloat() / bitmap.height.toFloat())
                            .clip(RoundedCornerShape(12.dp))
                            .pointerInput(bitmap) { detectTapGestures { onImageTap(bitmap) } }
                    )
                    Spacer(Modifier.height(8.dp))
                }
                Text(
                    message.text.ifBlank { "…" },
                    style = MaterialTheme.typography.bodyMedium,
                    color = if (isUser) Color.White else OliText
                )
                if (message.files.isNotEmpty()) {
                    Spacer(Modifier.height(6.dp))
                    Text(
                        "📎 " + message.files.joinToString(", "),
                        style = MaterialTheme.typography.labelSmall,
                        color = if (isUser) Color.White.copy(alpha = 0.85f) else OliBlueBright
                    )
                }
            }
        }
    }
}

@Composable
private fun TypingBubble() {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.Bottom) {
        Box(contentAlignment = Alignment.Center) {
            PulseHalo(active = true)
            OliMark(size = 26.dp, working = true)
        }
        Spacer(Modifier.width(8.dp))
        Box(
            Modifier
                .clip(RoundedCornerShape(18.dp, 18.dp, 18.dp, 6.dp))
                .background(OliGlass)
                .border(1.dp, OliOutline.copy(alpha = 0.7f), RoundedCornerShape(18.dp))
                .padding(horizontal = 15.dp, vertical = 12.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                EqualizerBars(bars = 4)
                Spacer(Modifier.width(10.dp))
                ThinkingWord()
            }
        }
    }
}

/** "OLI думает", "смотрит", "собирает ответ" — cycles while the model works. */
@Composable
private fun ThinkingWord() {
    val words = remember { listOf("думает", "читает", "собирает ответ", "проверяет детали") }
    var index by remember { mutableStateOf(0) }
    LaunchedEffect(Unit) {
        while (true) {
            kotlinx.coroutines.delay(1600)
            index = (index + 1) % words.size
        }
    }
    androidx.compose.animation.Crossfade(targetState = index, animationSpec = tween(400), label = "thinking") { i ->
        Text("OLI ${words[i]}…", style = MaterialTheme.typography.bodyMedium, color = OliTextMuted)
    }
}

/** Full-screen picture: tap anywhere or press back to close. */
@Composable
private fun ImageViewer(bitmap: android.graphics.Bitmap, onClose: () -> Unit) {
    Dialog(
        onDismissRequest = onClose,
        properties = DialogProperties(usePlatformDefaultWidth = false)
    ) {
        val appear = remember { Animatable(0f) }
        LaunchedEffect(Unit) { appear.animateTo(1f, tween(220)) }
        Box(
            Modifier
                .fillMaxSize()
                .background(Color.Black.copy(alpha = 0.94f * appear.value))
                .pointerInput(Unit) { detectTapGestures { onClose() } },
            contentAlignment = Alignment.Center
        ) {
            Image(
                bitmap = bitmap.asImageBitmap(),
                contentDescription = "Изображение",
                contentScale = ContentScale.Fit,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(12.dp)
                    .scale(0.85f + 0.15f * appear.value)
            )
            Text(
                "Нажмите, чтобы закрыть",
                style = MaterialTheme.typography.labelSmall,
                color = OliTextMuted,
                modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 36.dp)
            )
        }
    }
}

/** Loads and downscales a picked photo so it uploads fast and fits the bubble. */
internal fun loadPendingImage(context: android.content.Context, uri: android.net.Uri): PendingImage? {
    return runCatching {
        val bitmap = context.contentResolver.openInputStream(uri)?.use { stream ->
            val bounds = android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds = true }
            android.graphics.BitmapFactory.decodeStream(stream, null, bounds)
            stream.close()

            var sample = 1
            val longest = maxOf(bounds.outWidth, bounds.outHeight)
            while (longest / sample > 1600) sample *= 2

            val options = android.graphics.BitmapFactory.Options().apply {
                inSampleSize = sample
                inPreferredConfig = android.graphics.Bitmap.Config.ARGB_8888
            }
            context.contentResolver.openInputStream(uri)?.use { second ->
                android.graphics.BitmapFactory.decodeStream(second, null, options)
            }
        } ?: return null
        PendingImage(bitmap, fileNameFor(context, uri))
    }.getOrNull()
}

/** File name for an image picked through the system picker (falls back to a timestamp). */
internal fun fileNameFor(context: android.content.Context, uri: android.net.Uri): String {
    val fromProvider = runCatching {
        context.contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val index = cursor.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
            if (index >= 0 && cursor.moveToFirst()) cursor.getString(index) else null
        }
    }.getOrNull()
    val name = fromProvider?.takeIf { it.isNotBlank() } ?: "photo-${System.currentTimeMillis()}.jpg"
    return if (name.contains('.')) name else "$name.jpg"
}

/** An image waiting in the composer. */
internal data class PendingImage(val bitmap: android.graphics.Bitmap, val fileName: String)

// ------------------------------------------------------------------ small parts

@Composable
internal fun GlassPill(onClick: () -> Unit, content: @Composable () -> Unit) {
    Box(
        Modifier
            .clip(RoundedCornerShape(24.dp))
            .background(OliGlass)
            .border(1.dp, OliOutline.copy(alpha = 0.8f), RoundedCornerShape(24.dp))
            .clickable(onClick = onClick)
    ) { content() }
}

@Composable
internal fun GlassCircleButton(onClick: () -> Unit, content: @Composable () -> Unit) {
    Box(
        Modifier
            .pressableScale(pressedScale = 0.92f)
            .size(44.dp)
            .clip(CircleShape)
            .background(OliGlass)
            .border(1.dp, OliOutline.copy(alpha = 0.8f), CircleShape)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center
    ) { content() }
}

@Composable
internal fun GlassCard(modifier: Modifier = Modifier, onClick: () -> Unit, content: @Composable () -> Unit) {
    Box(
        modifier
            .pressableScale(pressedScale = 0.97f)
            .clip(RoundedCornerShape(16.dp))
            .background(OliGlass.copy(alpha = 0.85f))
            .border(1.dp, OliOutline.copy(alpha = 0.7f), RoundedCornerShape(16.dp))
            .clickable(onClick = onClick)
    ) { content() }
}

@Composable
internal fun ActionChip(text: String, onClick: () -> Unit) {
    Box(
        Modifier
            .pressableScale()
            .clip(RoundedCornerShape(22.dp))
            .background(OliGlass)
            .border(1.dp, OliOutline.copy(alpha = 0.8f), RoundedCornerShape(22.dp))
            .clickable(onClick = onClick)
    ) {
        Text(
            text,
            style = MaterialTheme.typography.labelMedium,
            color = OliText,
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 11.dp)
        )
    }
}

@Composable
internal fun GhostButton(text: String, onClick: () -> Unit, accent: Boolean = false) {
    Box(
        Modifier
            .clip(RoundedCornerShape(22.dp))
            .background(if (accent) OliBlue.copy(alpha = 0.16f) else OliGlass)
            .border(1.dp, if (accent) OliBlue.copy(alpha = 0.45f) else OliOutline, RoundedCornerShape(22.dp))
            .clickable(onClick = onClick)
    ) {
        Text(
            text,
            style = MaterialTheme.typography.labelMedium,
            color = if (accent) OliBlueBright else OliText,
            modifier = Modifier.padding(horizontal = 22.dp, vertical = 11.dp)
        )
    }
}

@Composable
private fun DemoBanner(onExit: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 14.dp, vertical = 6.dp)
            .clip(RoundedCornerShape(14.dp))
            .background(OliBlue.copy(alpha = 0.12f))
            .border(1.dp, OliBlue.copy(alpha = 0.35f), RoundedCornerShape(14.dp))
            .padding(start = 14.dp, end = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            "Режим просмотра",
            style = MaterialTheme.typography.labelMedium,
            color = OliBlueBright,
            modifier = Modifier.weight(1f)
        )
        Box(
            Modifier.clip(RoundedCornerShape(12.dp)).clickable(onClick = onExit).padding(horizontal = 12.dp, vertical = 8.dp)
        ) { Text("Войти", style = MaterialTheme.typography.labelMedium, color = OliText) }
    }
}

internal fun compact(value: Long): String = when {
    value >= 1_000_000 -> String.format(java.util.Locale.US, "%.1fM", value / 1_000_000.0)
    value >= 1_000 -> "${value / 1_000}k"
    else -> value.toString()
}
