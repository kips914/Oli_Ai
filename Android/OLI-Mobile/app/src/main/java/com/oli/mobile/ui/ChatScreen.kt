package com.oli.mobile.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.foundation.background
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
import androidx.compose.material.icons.filled.ElectricBolt
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
    modelState: ModelState?,
    usageLimits: UsageSnapshotData?,
    chats: List<ChatSummary>,
    activeThread: String?,
    limitNotice: Boolean,
    onOpenFiles: () -> Unit,
    onExitDemo: () -> Unit,
    onOpenSettings: () -> Unit,
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

    LaunchedEffect(messages.size, busy) {
        if (messages.isNotEmpty()) listState.animateScrollToItem(messages.size)
    }

    val selectedModel = modelState?.models?.firstOrNull { it.id == modelState.selected }
    val greeting = remember(displayName) {
        val name = displayName.trim()
        if (name.isEmpty() || name.equals("гость", true)) "Привет! Чем займёмся?"
        else "Привет, $name! Чем займёмся?"
    }

    fun send(text: String) {
        val value = text.trim()
        if (value.isEmpty() || busy) return
        input = ""
        scope.launch { repo.send(value) }
    }

    Box(Modifier.fillMaxSize().background(OliInk)) {
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

        Column(Modifier.fillMaxSize().statusBarsPadding().imePadding()) {
            if (demo) DemoBanner(onExitDemo)

            TopBar(
                modelTitle = selectedModel?.title ?: "Olim Pass",
                modelSubtitle = "k3",
                onMenu = { drawerOpen = true },
                onModel = { modelsOpen = true },
                onFiles = onOpenFiles,
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
                    items(messages, key = { it.id }) { message -> MessageBubble(message) }
                    if (busy) item { TypingBubble() }
                    item { Spacer(Modifier.height(8.dp)) }
                }
            }

            SuggestionRow(onPick = { send(it) })

            Composer(
                value = input,
                onValueChange = { input = it },
                enabled = !busy,
                placeholder = if (demo) "Режим просмотра — сообщение не отправится" else "Спроси меня. Поручи мне.",
                usage = usageLimits,
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
}

// ------------------------------------------------------------------ top bar

@Composable
private fun TopBar(
    modelTitle: String,
    modelSubtitle: String,
    onMenu: () -> Unit,
    onModel: () -> Unit,
    onFiles: () -> Unit,
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
                Icon(Icons.Filled.ElectricBolt, contentDescription = null, tint = OliBlueBright, modifier = Modifier.size(15.dp))
                Spacer(Modifier.width(6.dp))
                Text(modelTitle, style = MaterialTheme.typography.labelMedium, color = OliText, fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.width(6.dp))
                Text("▾", style = MaterialTheme.typography.labelMedium, color = OliTextMuted)
            }
        }

        Spacer(Modifier.weight(1f))

        GlassCircleButton(onClick = onFiles) {
            Icon(Icons.Filled.GraphicEq, contentDescription = "Файлы", tint = OliBlueBright, modifier = Modifier.size(19.dp))
        }
        Spacer(Modifier.width(8.dp))
        GlassCircleButton(onClick = onMenu) {
            Icon(Icons.Filled.Add, contentDescription = "Новый чат", tint = OliText, modifier = Modifier.size(21.dp))
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
        Box(contentAlignment = Alignment.Center) {
            OliBurst(size = 250.dp)
            OliMark(size = 130.dp, working = busy)
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
        ActionChip("Новый проект", onClick = { onPick("Помоги начать новый проект: спроси меня о деталях и составь план.") })
        ActionChip("Сводка дня", onClick = { onPick("Сделай короткую сводку дня и предложи 3 задачи на завтра.") })
        ActionChip("Переписать", onClick = { onPick("Перепиши мой текст короче и яснее — я пришлю его следующим сообщением.") })
        ActionChip("Перевод", onClick = { onPick("Помоги перевести текст — я пришлю его следующим сообщением.") })
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
                    IconButtonBox(icon = Icons.Filled.Add, description = "Файлы", onClick = onAttach)
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
                    IconButtonBox(icon = Icons.Filled.GraphicEq, description = "Голос", onClick = {})
                }
            }

            Spacer(Modifier.width(8.dp))

            SendButton(enabled = enabled && value.isNotBlank(), onClick = onSend)
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
        Modifier.size(44.dp).clip(CircleShape).clickable(onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        Icon(icon, contentDescription = description, tint = OliTextMuted, modifier = Modifier.size(21.dp))
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
private fun MessageBubble(message: OliRepository.ChatMessage) {
    val isUser = message.fromUser
    Row(
        Modifier.fillMaxWidth(),
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
            Column {
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
        OliMark(size = 26.dp, working = true)
        Spacer(Modifier.width(8.dp))
        Box(
            Modifier
                .clip(RoundedCornerShape(18.dp, 18.dp, 18.dp, 6.dp))
                .background(OliGlass)
                .border(1.dp, OliOutline.copy(alpha = 0.7f), RoundedCornerShape(18.dp))
                .padding(horizontal = 15.dp, vertical = 12.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(14.dp), strokeWidth = 2.dp, color = OliBlueBright)
                Spacer(Modifier.width(10.dp))
                Text("OLI думает…", style = MaterialTheme.typography.bodyMedium, color = OliTextMuted)
            }
        }
    }
}

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

/** Floating glass navigation bar in the OLI palette (used by the app shell). */
@Composable
fun OliBottomBar(
    current: Int,
    labels: List<Pair<androidx.compose.ui.graphics.vector.ImageVector, String>>,
    onSelect: (Int) -> Unit
) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 8.dp)
            .clip(RoundedCornerShape(24.dp))
            .background(OliGlass)
            .border(1.dp, OliOutline.copy(alpha = 0.8f), RoundedCornerShape(24.dp))
            .padding(6.dp),
        horizontalArrangement = Arrangement.SpaceEvenly,
        verticalAlignment = Alignment.CenterVertically
    ) {
        labels.forEachIndexed { index, (icon, label) ->
            val selected = index == current
            Row(
                Modifier
                    .weight(1f)
                    .clip(RoundedCornerShape(18.dp))
                    .background(if (selected) OliBlue.copy(alpha = 0.16f) else Color.Transparent)
                    .clickable { onSelect(index) }
                    .padding(vertical = 10.dp),
                horizontalArrangement = Arrangement.Center,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Icon(
                    icon,
                    contentDescription = label,
                    tint = if (selected) OliBlueBright else OliTextMuted,
                    modifier = Modifier.size(20.dp)
                )
                if (selected) {
                    Spacer(Modifier.width(8.dp))
                    Text(label, style = MaterialTheme.typography.labelMedium, color = OliText)
                }
            }
        }
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
