package com.oli.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.ElectricBolt
import androidx.compose.material.icons.filled.Speed
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.oli.mobile.data.ModelState
import com.oli.mobile.data.UsageSnapshotData

/**
 * Bottom sheet with the OLI model list and the live token limit.
 * Olim Pass — flagship, Olim Son — the light model used after the limit.
 */
@Composable
fun ModelSheet(
    state: ModelState?,
    usage: UsageSnapshotData?,
    onDismiss: () -> Unit,
    onPick: (String) -> Unit
) {
    val models = state?.models ?: listOf(
        com.oli.mobile.data.OliModel("oli-pass", "Olim Pass", "Флагман OLI: сложные задачи, код, анализ.", 640),
        com.oli.mobile.data.OliModel("oli-son", "Olim Son", "Лёгкая и быстрая модель OLI.", 320)
    )
    val selected = state?.selected ?: "oli-pass"
    val hourUsed = usage?.hourUsed ?: state?.hourUsed ?: 0
    val hourLimit = usage?.hourLimit ?: state?.hourLimit ?: 0
    val dayUsed = usage?.dayUsed ?: state?.dayUsed ?: 0
    val dayLimit = usage?.dayLimit ?: state?.dayLimit ?: 0
    val online = usage?.activeUsers ?: state?.activeUsers ?: 1

    Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.6f)).clickable(onClick = onDismiss)) {
        Column(
            Modifier
                .align(Alignment.BottomCenter)
                .fillMaxWidth()
                .clip(RoundedCornerShape(topStart = 26.dp, topEnd = 26.dp))
                .background(OliSurface)
                .border(1.dp, OliOutline.copy(alpha = 0.7f), RoundedCornerShape(topStart = 26.dp, topEnd = 26.dp))
                .navigationBarsPadding()
                .clickable(enabled = false) {}
                .padding(18.dp)
        ) {
            Box(
                Modifier
                    .align(Alignment.CenterHorizontally)
                    .width(44.dp)
                    .height(4.dp)
                    .clip(RoundedCornerShape(2.dp))
                    .background(OliOutline)
            )
            Spacer(Modifier.height(16.dp))

            Text("Модель", style = MaterialTheme.typography.titleMedium, color = OliText)
            Text(
                "Pass — максимум качества. Son — самая лёгкая и быстрая; включается автоматически, " +
                    "когда лимит токенов исчерпан.",
                style = MaterialTheme.typography.labelSmall,
                color = OliTextMuted,
                modifier = Modifier.padding(top = 4.dp)
            )

            Spacer(Modifier.height(14.dp))

            models.forEach { model ->
                val isSelected = model.id == selected
                val isPass = model.id == "oli-pass"
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(bottom = 10.dp)
                        .clip(RoundedCornerShape(18.dp))
                        .background(if (isSelected) OliBlue.copy(alpha = 0.14f) else OliGlass)
                        .border(
                            1.dp,
                            if (isSelected) OliBlue.copy(alpha = 0.55f) else OliOutline.copy(alpha = 0.8f),
                            RoundedCornerShape(18.dp)
                        )
                        .clickable { onPick(model.id) }
                        .padding(14.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Box(
                        Modifier
                            .size(40.dp)
                            .clip(RoundedCornerShape(12.dp))
                            .background(
                                if (isPass) Brush.linearGradient(listOf(OliBlue, OliViolet))
                                else Brush.linearGradient(listOf(OliCyan, Color(0xFF1E9C8B)))
                            ),
                        contentAlignment = Alignment.Center
                    ) {
                        Icon(
                            if (isPass) Icons.Filled.ElectricBolt else Icons.Filled.Speed,
                            contentDescription = null,
                            tint = Color.White,
                            modifier = Modifier.size(20.dp)
                        )
                    }
                    Spacer(Modifier.width(12.dp))
                    Column(Modifier.weight(1f)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(model.title, style = MaterialTheme.typography.bodyLarge, color = OliText,
                                fontWeight = FontWeight.SemiBold)
                            Spacer(Modifier.width(8.dp))
                            Text(
                                if (isPass) "флагман" else "лёгкая",
                                style = MaterialTheme.typography.labelSmall,
                                color = if (isPass) OliBlueBright else OliCyan
                            )
                        }
                        Text(
                            model.description,
                            style = MaterialTheme.typography.labelSmall,
                            color = OliTextMuted
                        )
                    }
                    if (isSelected) {
                        Icon(Icons.Filled.Check, contentDescription = "Выбрано",
                            tint = OliBlueBright, modifier = Modifier.size(20.dp))
                    }
                }
            }

            Spacer(Modifier.height(6.dp))
            Text("Лимит токенов", style = MaterialTheme.typography.titleMedium, color = OliText)
            Text(
                "Считаются и отправленные, и полученные токены. Чем больше людей на сервере сейчас, " +
                    "тем меньше лимит на каждого — и наоборот.",
                style = MaterialTheme.typography.labelSmall,
                color = OliTextMuted,
                modifier = Modifier.padding(top = 4.dp)
            )

            Spacer(Modifier.height(12.dp))
            LimitRow("За час", hourUsed, hourLimit.toLong())
            Spacer(Modifier.height(8.dp))
            LimitRow("За сутки", dayUsed, dayLimit.toLong())

            Spacer(Modifier.height(12.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(7.dp).clip(RoundedCornerShape(4.dp)).background(OliCyan))
                Spacer(Modifier.width(8.dp))
                Text(
                    "$online пользовател${if (online == 1) "ь" else "ей"} на сервере сейчас",
                    style = MaterialTheme.typography.labelSmall,
                    color = OliTextMuted
                )
            }

            Spacer(Modifier.height(16.dp))
        }
    }
}

@Composable
private fun LimitRow(label: String, used: Long, limit: Long) {
    val fraction = if (limit > 0) (used.toFloat() / limit).coerceIn(0f, 1f) else 0f
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = OliText, modifier = Modifier.width(74.dp))
        Box(
            Modifier
                .weight(1f)
                .height(6.dp)
                .clip(RoundedCornerShape(3.dp))
                .background(OliOutline)
        ) {
            Box(
                Modifier
                    .fillMaxWidth(fraction)
                    .height(6.dp)
                    .background(
                        if (fraction > 0.9f) androidx.compose.ui.graphics.SolidColor(Color(0xFFFFB020))
                        else Brush.horizontalGradient(listOf(OliBlue, OliCyan))
                    )
            )
        }
        Spacer(Modifier.width(10.dp))
        Text(
            "${compact(used)}/${compact(limit)}",
            style = MaterialTheme.typography.labelSmall,
            color = OliTextMuted
        )
    }
}
