package com.oli.mobile.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Send
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.oli.mobile.data.OliRepository
import kotlinx.coroutines.launch

@Composable
fun ChatScreen(
    repo: OliRepository,
    messages: List<OliRepository.ChatMessage>,
    busy: Boolean,
    displayName: String,
    demo: Boolean,
    onOpenFiles: () -> Unit,
    onExitDemo: () -> Unit
) {
    var input by remember { mutableStateOf("") }
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()

    LaunchedEffect(messages.size, busy) {
        if (messages.isNotEmpty()) listState.animateScrollToItem(messages.size)
    }

    Column(Modifier.fillMaxSize().imePadding()) {
        if (demo) {
            DemoBanner(onExitDemo)
        }
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(Modifier.weight(1f)) {
                Text("Чат", style = MaterialTheme.typography.titleMedium)
                Text(
                    if (demo) "Режим просмотра · запросы не отправляются"
                    else if (busy) "OLI думает…" else "Локальная модель на вашем ПК",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            IconButton(onClick = {
                scope.launch { repo.resetChat() }
            }) { Icon(Icons.Filled.Refresh, contentDescription = "Новый диалог") }
            IconButton(onClick = onOpenFiles) { Icon(Icons.Filled.AttachFile, contentDescription = "Файлы") }
        }

        Box(Modifier.weight(1f)) {
            if (messages.isEmpty()) {
                Column(
                    Modifier.fillMaxSize().padding(32.dp),
                    verticalArrangement = Arrangement.Center,
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Text("Привет, $displayName!", style = MaterialTheme.typography.headlineSmall)
                    Spacer(Modifier.height(8.dp))
                    Text(
                        if (demo)
                            "Это демонстрация интерфейса: можно ходить по разделам и нажимать кнопки, " +
                                "но сообщения не уходят на сервер. Войдите, чтобы OLI отвечал."
                        else
                            "Спросите что угодно. OLI может искать в интернете и сохранять файлы в вашем личном пространстве на 1 ГБ.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            } else {
                LazyColumn(
                    state = listState,
                    modifier = Modifier.fillMaxSize().padding(horizontal = 12.dp),
                    verticalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    items(messages, key = { it.id }) { message -> MessageBubble(message) }
                    if (busy) item { TypingBubble() }
                }
            }
        }

        Surface(
            color = MaterialTheme.colorScheme.surfaceVariant,
            shape = RoundedCornerShape(topStart = 20.dp, topEnd = 20.dp)
        ) {
            Row(
                Modifier.fillMaxWidth().padding(10.dp),
                verticalAlignment = Alignment.Bottom
            ) {
                OutlinedTextField(
                    value = input,
                    onValueChange = { input = it },
                    placeholder = { Text(if (demo) "Сообщение (в режиме просмотра не отправится)…" else "Сообщение…") },
                    modifier = Modifier.weight(1f),
                    maxLines = 5,
                    shape = RoundedCornerShape(18.dp)
                )
                Spacer(Modifier.width(8.dp))
                FilledIconButton(
                    onClick = {
                        val text = input
                        if (text.isBlank() || busy) return@FilledIconButton
                        input = ""
                        scope.launch { repo.send(text) }
                    },
                    enabled = input.isNotBlank() && !busy,
                    modifier = Modifier.size(48.dp)
                ) { Icon(Icons.Filled.Send, contentDescription = "Отправить") }
            }
        }
    }
}

@Composable
private fun MessageBubble(message: OliRepository.ChatMessage) {
    val bubbleColor = if (message.fromUser) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surface
    val textColor = if (message.fromUser) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurface

    Row(
        Modifier.fillMaxWidth(),
        horizontalArrangement = if (message.fromUser) Arrangement.End else Arrangement.Start
    ) {
        Surface(
            color = bubbleColor,
            shape = RoundedCornerShape(
                topStart = 18.dp,
                topEnd = 18.dp,
                bottomStart = if (message.fromUser) 18.dp else 4.dp,
                bottomEnd = if (message.fromUser) 4.dp else 18.dp
            ),
            tonalElevation = if (message.fromUser) 0.dp else 1.dp,
            modifier = Modifier.widthIn(max = 520.dp).fillMaxWidth(0.92f)
        ) {
            Column(Modifier.padding(horizontal = 14.dp, vertical = 10.dp)) {
                Text(
                    message.text.ifBlank { "…" },
                    style = MaterialTheme.typography.bodyMedium,
                    color = textColor
                )
                if (message.files.isNotEmpty()) {
                    Spacer(Modifier.height(6.dp))
                    Text(
                        "📎 " + message.files.joinToString(", "),
                        style = MaterialTheme.typography.labelSmall,
                        fontWeight = FontWeight.Medium,
                        color = textColor.copy(alpha = 0.85f)
                    )
                }
            }
        }
    }
}

@Composable
private fun TypingBubble() {
    Row(Modifier.fillMaxWidth()) {
        Surface(
            color = MaterialTheme.colorScheme.surface,
            shape = RoundedCornerShape(18.dp),
            tonalElevation = 1.dp
        ) {
            Row(Modifier.padding(horizontal = 14.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(10.dp))
                Text("Печатает…", style = MaterialTheme.typography.bodyMedium)
            }
        }
    }
}

@Composable
private fun DemoBanner(onExit: () -> Unit) {
    Surface(color = MaterialTheme.colorScheme.secondaryContainer) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                "👁 Режим просмотра",
                style = MaterialTheme.typography.labelMedium,
                fontWeight = FontWeight.SemiBold,
                modifier = Modifier.weight(1f)
            )
            androidx.compose.material3.TextButton(onClick = onExit) { Text("Войти") }
        }
    }
}
