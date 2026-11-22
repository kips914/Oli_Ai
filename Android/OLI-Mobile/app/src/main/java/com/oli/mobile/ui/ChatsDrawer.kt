package com.oli.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.oli.mobile.data.ChatSummary

/**
 * Side panel with the user's saved chats (opened from the hamburger).
 * Also hosts "новый чат" and the settings entry, since the bottom bar only holds
 * three primary tabs.
 */
@Composable
fun ChatsDrawer(
    chats: List<ChatSummary>,
    activeThread: String?,
    onDismiss: () -> Unit,
    onOpen: (String) -> Unit,
    onNew: () -> Unit,
    onDelete: (String) -> Unit,
    onSettings: () -> Unit
) {
    Box(Modifier.fillMaxSize()) {
        // scrim
        Box(
            Modifier
                .fillMaxSize()
                .background(Color.Black.copy(alpha = 0.6f))
                .clickable(onClick = onDismiss)
        )

        Column(
            Modifier
                .fillMaxHeight()
                .fillMaxWidth(0.84f)
                .background(OliSurface)
                .border(1.dp, OliOutline.copy(alpha = 0.6f))
        ) {
            Column(Modifier.statusBarsPadding().padding(16.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OliMark(size = 34.dp)
                    Spacer(Modifier.width(10.dp))
                    Column(Modifier.weight(1f)) {
                        Text("OLI", style = MaterialTheme.typography.titleMedium, color = OliText,
                            fontWeight = FontWeight.Bold)
                        Text("ваши чаты", style = MaterialTheme.typography.labelSmall, color = OliTextMuted)
                    }
                    Box(
                        Modifier
                            .size(36.dp)
                            .clip(RoundedCornerShape(12.dp))
                            .background(OliGlass)
                            .border(1.dp, OliOutline, RoundedCornerShape(12.dp))
                            .clickable(onClick = onSettings),
                        contentAlignment = Alignment.Center
                    ) {
                        Icon(Icons.Filled.Settings, contentDescription = "Настройки",
                            tint = OliTextMuted, modifier = Modifier.size(18.dp))
                    }
                }

                Spacer(Modifier.height(16.dp))

                Row(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(14.dp))
                        .background(OliBlue.copy(alpha = 0.16f))
                        .border(1.dp, OliBlue.copy(alpha = 0.4f), RoundedCornerShape(14.dp))
                        .clickable(onClick = onNew)
                        .padding(horizontal = 14.dp, vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Icon(Icons.Filled.Add, contentDescription = null, tint = OliBlueBright, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(10.dp))
                    Text("Новый чат", style = MaterialTheme.typography.labelMedium, color = OliText)
                }

                Spacer(Modifier.height(14.dp))
                Text("ИСТОРИЯ", style = MaterialTheme.typography.labelSmall, color = OliTextMuted)
            }

            if (chats.isEmpty()) {
                Box(Modifier.fillMaxWidth().padding(20.dp), contentAlignment = Alignment.Center) {
                    Text(
                        "Пока пусто — задайте первый вопрос, и чат сохранится здесь.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = OliTextMuted
                    )
                }
            } else {
                LazyColumn(
                    Modifier.weight(1f).padding(horizontal = 10.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp)
                ) {
                    items(chats, key = { it.id }) { chat ->
                        val active = chat.id == activeThread
                        Row(
                            Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(12.dp))
                                .background(if (active) OliBlue.copy(alpha = 0.14f) else Color.Transparent)
                                .clickable { onOpen(chat.id) }
                                .padding(start = 12.dp, top = 10.dp, bottom = 10.dp, end = 4.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Column(Modifier.weight(1f)) {
                                Text(
                                    chat.title,
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = OliText,
                                    maxLines = 1
                                )
                                Text(
                                    shortDate(chat.updatedAt),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = OliTextMuted
                                )
                            }
                            Box(
                                Modifier
                                    .size(34.dp)
                                    .clip(RoundedCornerShape(10.dp))
                                    .clickable { onDelete(chat.id) },
                                contentAlignment = Alignment.Center
                            ) {
                                Icon(Icons.Filled.Delete, contentDescription = "Удалить",
                                    tint = OliTextMuted, modifier = Modifier.size(17.dp))
                            }
                        }
                    }
                    item { Spacer(Modifier.height(12.dp)) }
                }
            }
        }
    }
}

private fun shortDate(raw: String): String {
    // "2026-11-23T01:34:12.123+00:00" → "23.11 · 01:34"
    return try {
        val date = raw.substring(0, 16).replace('T', ' ')
        val parts = date.split(' ')
        val d = parts[0].split('-')
        "${d[2]}.${d[1]} · ${parts[1]}"
    } catch (_: Exception) {
        raw
    }
}
