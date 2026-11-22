package com.oli.mobile.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.foundation.BorderStroke
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
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.GraphicEq
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.VolumeUp
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
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
import com.oli.mobile.data.OliRepository
import kotlinx.coroutines.launch

/**
 * Chat screen in the OLI "orb" style: near-black canvas, a glossy animated orb as the
 * hero, glass pills for actions, suggestion chips above a pill-shaped composer.
 */
@Composable
fun ChatScreen(
    repo: OliRepository,
    messages: List<OliRepository.ChatMessage>,
    busy: Boolean,
    displayName: String,
    demo: Boolean,
    onOpenFiles: () -> Unit,
    onExitDemo: () -> Unit,
    onOpenSettings: () -> Unit = {}
) {
    var input by remember { mutableStateOf("") }
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()

    LaunchedEffect(messages.size, busy) {
        if (messages.isNotEmpty()) listState.animateScrollToItem(messages.size)
    }

    val greeting = remember(displayName) {
        val name = displayName.trim()
        if (name.isEmpty() || name.equals("гость", true)) "Привет! Что сделаем сегодня?"
        else "Привет, $name! Что сделаем сегодня?"
    }

    fun send(text: String) {
        val value = text.trim()
        if (value.isEmpty() || busy) return
        input = ""
        scope.launch { repo.send(value) }
    }

    Box(
        Modifier
            .fillMaxSize()
            .background(OliInk)
    ) {
        // Subtle blue glow behind the orb, like a backlit screen.
        Box(
            Modifier
                .align(Alignment.TopCenter)
                .fillMaxWidth()
                .height(520.dp)
                .background(
                    Brush.radialGradient(
                        colors = listOf(
                            Color(0xFF12305F).copy(alpha = 0.55f),
                            Color(0xFF0A1424).copy(alpha = 0.35f),
                            Color.Transparent
                        ),
                        radius = 900f
                    )
                )
        )

        Column(
            Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .imePadding()
        ) {
            if (demo) DemoBanner(onExitDemo)

            TopBar(
                onMenu = onOpenSettings,
                onNewChat = { scope.launch { repo.resetChat() } },
                onFiles = onOpenFiles
            )

            if (messages.isEmpty()) {
                EmptyHero(
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
                    modifier = Modifier.weight(1f).padding(horizontal = 16.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    item { Spacer(Modifier.height(6.dp)) }
                    items(messages, key = { it.id }) { message -> MessageBubble(message) }
                    if (busy) item { TypingBubble() }
                    item { Spacer(Modifier.height(8.dp)) }
                }
            }

            QuickActions(
                onNewProject = { send("Помоги начать новый проект. Спроси меня о деталях.") },
                onOther = onOpenFiles
            )

            Composer(
                value = input,
                onValueChange = { input = it },
                enabled = !busy,
                placeholder = if (demo) "Режим просмотра — сообщение не отправится" else "Спроси меня. Поручи мне.",
                onSend = { send(input) },
                onAttach = onOpenFiles
            )
        }
    }
}

// ------------------------------------------------------------------ top bar

@Composable
private fun TopBar(onMenu: () -> Unit, onNewChat: () -> Unit, onFiles: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        GlassCircleButton(onClick = onMenu) {
            Icon(Icons.Filled.Menu, contentDescription = "Меню", tint = OliText, modifier = Modifier.size(20.dp))
        }
        Spacer(Modifier.width(10.dp))

        GlassPill(onClick = onNewChat) {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(horizontal = 14.dp, vertical = 9.dp)) {
                Text(
                    "K3",
                    style = MaterialTheme.typography.labelMedium,
                    color = OliTextMuted,
                    fontWeight = FontWeight.SemiBold
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    "Высокий",
                    style = MaterialTheme.typography.labelMedium,
                    color = OliText
                )
            }
        }

        Spacer(Modifier.weight(1f))

        GlassCircleButton(onClick = onFiles) {
            Icon(Icons.Filled.VolumeUp, contentDescription = "Файлы", tint = OliBlueBright, modifier = Modifier.size(20.dp))
        }
        Spacer(Modifier.width(8.dp))
        GlassCircleButton(onClick = onNewChat) {
            Icon(Icons.Filled.Refresh, contentDescription = "Новый диалог", tint = OliText, modifier = Modifier.size(18.dp))
        }
    }
}

// ------------------------------------------------------------------ hero

private data class Suggestion(val icon: String, val label: String, val prompt: String)

@Composable
private fun EmptyHero(
    greeting: String,
    busy: Boolean,
    demo: Boolean,
    onLogin: () -> Unit,
    onPick: (String) -> Unit,
    modifier: Modifier = Modifier
) {
    val suggestions = remember {
        listOf(
            Suggestion("💡", "Идея для проекта", "Предложи 3 идеи небольшого проекта, который можно сделать за вечер."),
            Suggestion("✉️", "Написать текст", "Помоги написать короткое письмо коллеге о переносе встречи."),
            Suggestion("📊", "Разобрать файл", "Подскажи, как разложить файлы в моём workspace по папкам."),
            Suggestion("🧠", "Объяснить просто", "Объясни простыми словами, как работает нейросеть.")
        )
    }

    Column(
        modifier.fillMaxWidth().padding(horizontal = 24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Box(contentAlignment = Alignment.Center) {
            OrbitRing(size = 230.dp, dots = 3)
            OliOrb(size = 118.dp, thinking = busy)
        }

        Spacer(Modifier.height(30.dp))

        AnimatedVisibility(
            visible = true,
            enter = fadeIn() + slideInVertically(initialOffsetY = { it / 4 })
        ) {
            Text(
                greeting,
                style = MaterialTheme.typography.headlineSmall,
                color = OliText,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(horizontal = 8.dp)
            )
        }

        if (demo) {
            Spacer(Modifier.height(18.dp))
            GhostButton(text = "Log in", onClick = onLogin, accent = true)
            Spacer(Modifier.height(8.dp))
            Text(
                "Режим просмотра: ответы не приходят",
                style = MaterialTheme.typography.labelSmall,
                color = OliTextMuted
            )
        }

        Spacer(Modifier.height(26.dp))

        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            suggestions.chunked(2).forEach { row ->
                Row(
                    Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    row.forEach { suggestion ->
                        GlassCard(
                            modifier = Modifier.weight(1f),
                            onClick = { onPick(suggestion.prompt) }
                        ) {
                            Row(
                                Modifier.padding(horizontal = 14.dp, vertical = 13.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(suggestion.icon, fontSize = 15.sp)
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

// ------------------------------------------------------------------ quick actions

@Composable
private fun QuickActions(onNewProject: () -> Unit, onOther: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 14.dp, vertical = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        ActionChip(text = "Новый проект", onClick = onNewProject)
        ActionChip(text = "Файлы", onClick = onOther)
        ActionChip(text = "Слайды", onClick = onNewProject)
        ActionChip(text = "Сводка дня", onClick = onNewProject)
    }
}

// ------------------------------------------------------------------ composer

@Composable
private fun Composer(
    value: String,
    onValueChange: (String) -> Unit,
    enabled: Boolean,
    placeholder: String,
    onSend: () -> Unit,
    onAttach: () -> Unit
) {
    Row(
        Modifier
            .fillMaxWidth()
            .navigationBarsPadding()
            .padding(horizontal = 14.dp, vertical = 10.dp),
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
                IconButton(onClick = onAttach) {
                    Icon(Icons.Filled.Add, contentDescription = "Добавить", tint = OliTextMuted, modifier = Modifier.size(22.dp))
                }
                Box(Modifier.weight(1f)) {
                    if (value.isEmpty()) {
                        Text(
                            placeholder,
                            style = MaterialTheme.typography.bodyLarge,
                            color = OliTextMuted,
                            maxLines = 1
                        )
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
                IconButton(onClick = onSend) {
                    Icon(Icons.Filled.GraphicEq, contentDescription = "Голос", tint = OliTextMuted, modifier = Modifier.size(20.dp))
                }
            }
        }

        Spacer(Modifier.width(8.dp))

        SendButton(enabled = enabled && value.isNotBlank(), onClick = onSend)
    }
}

@Composable
private fun SendButton(enabled: Boolean, onClick: () -> Unit) {
    val background = if (enabled) {
        Brush.linearGradient(listOf(OliBlue, OliBlueBright))
    } else {
        Brush.linearGradient(listOf(OliGlassSoft, OliGlassSoft))
    }

    Box(
        Modifier
            .size(56.dp)
            .clip(CircleShape)
            .background(background)
            .border(1.dp, OliOutline.copy(alpha = 0.7f), CircleShape)
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        if (enabled) {
            Icon(Icons.Filled.ArrowUpward, contentDescription = "Отправить", tint = Color.White, modifier = Modifier.size(22.dp))
        } else {
            Icon(Icons.Filled.GraphicEq, contentDescription = "Голос", tint = OliTextMuted, modifier = Modifier.size(22.dp))
        }
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
            OliOrbMini(size = 26.dp)
            Spacer(Modifier.width(8.dp))
        }

        Box(
            Modifier
                .widthIn(max = 560.dp)
                .clip(
                    if (isUser) RoundedCornerShape(20.dp, 20.dp, 6.dp, 20.dp)
                    else RoundedCornerShape(20.dp, 20.dp, 20.dp, 6.dp)
                )
                .background(
                    if (isUser) Brush.linearGradient(listOf(OliBlue, Color(0xFF2F63D0)))
                    else Brush.linearGradient(listOf(OliGlass, OliGlassSoft))
                )
                .border(
                    1.dp,
                    if (isUser) Color.Transparent else OliOutline.copy(alpha = 0.7f),
                    RoundedCornerShape(20.dp)
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
        OliOrbMini(size = 26.dp, thinking = true)
        Spacer(Modifier.width(8.dp))
        Box(
            Modifier
                .clip(RoundedCornerShape(20.dp, 20.dp, 20.dp, 6.dp))
                .background(OliGlass)
                .border(1.dp, OliOutline.copy(alpha = 0.7f), RoundedCornerShape(20.dp))
                .padding(horizontal = 15.dp, vertical = 12.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(
                    Modifier.size(14.dp),
                    strokeWidth = 2.dp,
                    color = OliBlueBright
                )
                Spacer(Modifier.width(10.dp))
                Text("OLI думает…", style = MaterialTheme.typography.bodyMedium, color = OliTextMuted)
            }
        }
    }
}

// ------------------------------------------------------------------ small parts

@Composable
private fun GlassPill(onClick: () -> Unit, content: @Composable () -> Unit) {
    Box(
        Modifier
            .clip(RoundedCornerShape(24.dp))
            .background(OliGlass)
            .border(1.dp, OliOutline.copy(alpha = 0.8f), RoundedCornerShape(24.dp))
            .clickable(onClick = onClick)
    ) { content() }
}

@Composable
private fun GlassCircleButton(onClick: () -> Unit, content: @Composable () -> Unit) {
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
private fun GlassCard(modifier: Modifier = Modifier, onClick: () -> Unit, content: @Composable () -> Unit) {
    Box(
        modifier
            .clip(RoundedCornerShape(18.dp))
            .background(OliGlass.copy(alpha = 0.85f))
            .border(1.dp, OliOutline.copy(alpha = 0.7f), RoundedCornerShape(18.dp))
            .clickable(onClick = onClick)
    ) { content() }
}

@Composable
private fun ActionChip(text: String, onClick: () -> Unit) {
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
private fun GhostButton(text: String, onClick: () -> Unit, accent: Boolean) {
    Box(
        Modifier
            .clip(RoundedCornerShape(22.dp))
            .background(if (accent) OliBlue.copy(alpha = 0.16f) else OliGlass)
            .border(
                1.dp,
                if (accent) OliBlue.copy(alpha = 0.45f) else OliOutline,
                RoundedCornerShape(22.dp)
            )
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
            .clip(RoundedCornerShape(26.dp))
            .background(OliGlass)
            .border(1.dp, OliOutline.copy(alpha = 0.8f), RoundedCornerShape(26.dp))
            .padding(6.dp),
        horizontalArrangement = Arrangement.SpaceEvenly,
        verticalAlignment = Alignment.CenterVertically
    ) {
        labels.forEachIndexed { index, (icon, label) ->
            val selected = index == current
            Row(
                Modifier
                    .weight(1f)
                    .clip(RoundedCornerShape(20.dp))
                    .background(if (selected) OliBlue.copy(alpha = 0.18f) else Color.Transparent)
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
            "👁 Режим просмотра",
            style = MaterialTheme.typography.labelMedium,
            color = OliBlueBright,
            modifier = Modifier.weight(1f)
        )
        Box(
            Modifier
                .clip(RoundedCornerShape(12.dp))
                .clickable(onClick = onExit)
                .padding(horizontal = 12.dp, vertical = 8.dp)
        ) {
            Text("Войти", style = MaterialTheme.typography.labelMedium, color = OliText)
        }
    }
}
