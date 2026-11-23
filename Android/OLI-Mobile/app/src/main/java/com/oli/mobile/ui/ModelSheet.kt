package com.oli.mobile.ui

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.Orientation
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.gestures.draggable
import androidx.compose.foundation.gestures.rememberDraggableState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import com.oli.mobile.data.ModelState
import com.oli.mobile.data.OliModel
import com.oli.mobile.data.UsageSnapshotData
import kotlinx.coroutines.launch
import kotlin.math.roundToInt

/**
 * Bottom sheet with the model list and the live token limit.
 *
 * Gestures: drag the handle (or the sheet itself) down to collapse it — it follows the
 * finger, springs back when released halfway, and closes when dragged far enough.
 * Tapping the scrim or the handle closes it too. Olim Pass is the flagship, Olim Son is
 * the light model that is used after the token limit is exhausted.
 */
@Composable
fun ModelSheet(
    state: ModelState?,
    usage: UsageSnapshotData?,
    onDismiss: () -> Unit,
    onPick: (String) -> Unit
) {
    val models = state?.models?.takeIf { it.isNotEmpty() } ?: listOf(
        OliModel("oli-pass", "Olim Pass", "Флагман OLI: сложные задачи, код, анализ.", 640),
        OliModel("oli-son", "Olim Son", "Лёгкая и быстрая модель OLI.", 320)
    )
    val selected = state?.selected ?: "oli-pass"
    val hourUsed = usage?.hourUsed ?: state?.hourUsed ?: 0
    val hourLimit = (usage?.hourLimit ?: state?.hourLimit ?: 0).toLong()
    val dayUsed = usage?.dayUsed ?: state?.dayUsed ?: 0
    val dayLimit = (usage?.dayLimit ?: state?.dayLimit ?: 0).toLong()
    val online = usage?.activeUsers ?: state?.activeUsers ?: 1

    val haptics = LocalHapticFeedback.current
    val scope = rememberCoroutineScope()

    // 0 = fully open, 1 = fully collapsed. Dragging moves this value directly.
    val drag = remember { Animatable(1f) }
    var sheetHeightPx by remember { mutableStateOf(900f) }

    fun close() {
        haptics.performHapticFeedback(HapticFeedbackType.TextHandleMove)
        scope.launch {
            drag.animateTo(1f, tween(180, easing = FastOutSlowInEasing))
            onDismiss()
        }
    }

    LaunchedEffect(Unit) {
        drag.animateTo(0f, spring(dampingRatio = 0.82f, stiffness = Spring.StiffnessMediumLow))
    }

    val dragState = rememberDraggableState { delta ->
        val next = (drag.value + delta / sheetHeightPx).coerceIn(0f, 1f)
        scope.launch { drag.snapTo(next) }
    }

    Box(Modifier.fillMaxSize()) {
        // scrim — fades as the sheet slides away
        Box(
            Modifier
                .fillMaxSize()
                .background(Color.Black.copy(alpha = 0.62f * (1f - drag.value)))
                .clickable(onClick = ::close)
        )

        Column(
            Modifier
                .align(Alignment.BottomCenter)
                .fillMaxWidth()
                .offset { IntOffset(0, (drag.value * sheetHeightPx).roundToInt()) }
                .onSizeChanged { sheetHeightPx = it.height.toFloat().coerceAtLeast(1f) }
                .clip(RoundedCornerShape(topStart = 28.dp, topEnd = 28.dp))
                .background(OliSurface)
                .border(
                    1.dp,
                    OliBlue.copy(alpha = 0.25f * (1f - drag.value)),
                    RoundedCornerShape(topStart = 28.dp, topEnd = 28.dp)
                )
                .navigationBarsPadding()
                .draggable(
                    state = dragState,
                    orientation = Orientation.Vertical,
                    onDragStopped = {
                        if (drag.value > 0.28f) close()
                        else {
                            haptics.performHapticFeedback(HapticFeedbackType.TextHandleMove)
                            scope.launch { drag.animateTo(0f, spring(dampingRatio = 0.8f)) }
                        }
                    }
                )
                .padding(horizontal = 18.dp)
                .padding(bottom = 18.dp)
        ) {
            // handle: the drag target everybody expects on a bottom sheet
            Box(
                Modifier
                    .fillMaxWidth()
                    .pointerInput(Unit) {
                        detectVerticalDragGestures(
                            onDragEnd = {
                                if (drag.value > 0.2f) close()
                                else scope.launch { drag.animateTo(0f, spring(dampingRatio = 0.8f)) }
                            }
                        ) { _, dy ->
                            scope.launch {
                                drag.snapTo((drag.value + dy / sheetHeightPx).coerceIn(0f, 1f))
                            }
                        }
                    }
                    .padding(vertical = 10.dp),
                contentAlignment = Alignment.Center
            ) {
                Box(
                    Modifier
                        .width(46.dp)
                        .height(4.dp)
                        .clip(RoundedCornerShape(2.dp))
                        .background(OliOutline)
                )
            }

            Text("Модель", style = MaterialTheme.typography.titleMedium, color = OliText)
            Text(
                "Pass — максимум качества. Son — самая лёгкая и быстрая; включается автоматически, " +
                    "когда лимит токенов исчерпан. Потяните вниз, чтобы свернуть.",
                style = MaterialTheme.typography.labelSmall,
                color = OliTextMuted,
                modifier = Modifier.padding(top = 4.dp)
            )

            Spacer(Modifier.height(14.dp))

            models.forEachIndexed { index, model ->
                ModelRow(
                    model = model,
                    selected = model.id == selected,
                    index = index,
                    onPick = {
                        haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                        onPick(model.id)
                    }
                )
                Spacer(Modifier.height(10.dp))
            }

            Spacer(Modifier.height(4.dp))
            Text("Лимит токенов", style = MaterialTheme.typography.titleMedium, color = OliText)
            Text(
                "Считаются и отправленные, и полученные токены. Чем больше людей на сервере сейчас, " +
                    "тем меньше лимит на каждого — и наоборот.",
                style = MaterialTheme.typography.labelSmall,
                color = OliTextMuted,
                modifier = Modifier.padding(top = 4.dp)
            )

            Spacer(Modifier.height(12.dp))
            LimitRow("За час", hourUsed, hourLimit)
            Spacer(Modifier.height(8.dp))
            LimitRow("За сутки", dayUsed, dayLimit)

            Spacer(Modifier.height(14.dp))
            OnlinePill(online)

            Spacer(Modifier.height(6.dp))
        }
    }
}

/** One model card: springs in, glows when selected, pulses while it is being picked. */
@Composable
private fun ModelRow(
    model: OliModel,
    selected: Boolean,
    index: Int,
    onPick: () -> Unit
) {
    val isPass = model.id == "oli-pass"

    val appear = remember { Animatable(0f) }
    LaunchedEffect(Unit) {
        kotlinx.coroutines.delay(60L * index)
        appear.animateTo(1f, spring(dampingRatio = 0.7f, stiffness = Spring.StiffnessMedium))
    }

    val glow by animateFloatAsState(
        targetValue = if (selected) 1f else 0f,
        animationSpec = tween(380),
        label = "glow"
    )

    // subtle breathing accent on the selected card
    val transition = rememberInfiniteTransition(label = "modelPulse")
    val pulse by transition.animateFloat(
        initialValue = 0.45f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(1800, easing = LinearEasing), RepeatMode.Reverse),
        label = "pulse"
    )

    Row(
        Modifier
            .fillMaxWidth()
            .graphicsLayer {
                alpha = appear.value
                translationY = (1f - appear.value) * 60f
            }
            .clip(RoundedCornerShape(20.dp))
            .background(
                if (selected) OliBlue.copy(alpha = 0.13f)
                else OliGlass
            )
            .border(
                1.dp,
                if (selected) OliBlue.copy(alpha = 0.35f + 0.25f * pulse * glow) else OliOutline.copy(alpha = 0.8f),
                RoundedCornerShape(20.dp)
            )
            .clickable(onClick = onPick)
            .padding(14.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            Modifier
                .size(42.dp)
                .scale(0.94f + 0.06f * glow)
                .clip(RoundedCornerShape(13.dp))
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
                modifier = Modifier.size(21.dp)
            )
        }

        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    model.title,
                    style = MaterialTheme.typography.bodyLarge,
                    color = OliText,
                    fontWeight = FontWeight.SemiBold
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    if (isPass) "флагман" else "лёгкая",
                    style = MaterialTheme.typography.labelSmall,
                    color = if (isPass) OliBlueBright else OliCyan
                )
            }
            Text(model.description, style = MaterialTheme.typography.labelSmall, color = OliTextMuted)
        }

        // the check pops in only for the active model
        Box(
            Modifier
                .size(26.dp)
                .scale(if (selected) 1f else 0.4f)
                .then(
                    if (selected) Modifier
                        .clip(RoundedCornerShape(13.dp))
                        .background(OliBlue.copy(alpha = 0.2f))
                    else Modifier
                ),
            contentAlignment = Alignment.Center
        ) {
            if (selected) {
                Icon(
                    Icons.Filled.Check,
                    contentDescription = "Выбрано",
                    tint = OliBlueBright,
                    modifier = Modifier.size(17.dp)
                )
            }
        }
    }
}

@Composable
private fun OnlinePill(online: Int) {
    val words = when {
        online <= 1 -> "вы единственный на сервере — лимит максимальный"
        online < 5 -> "$online человека на сервере — лимит почти максимальный"
        else -> "$online человек на сервере — лимит поделён между всеми"
    }
    Row(verticalAlignment = Alignment.CenterVertically) {
        val transition = rememberInfiniteTransition(label = "online")
        val alpha by transition.animateFloat(
            initialValue = 0.35f,
            targetValue = 1f,
            animationSpec = infiniteRepeatable(tween(1100, easing = LinearEasing), RepeatMode.Reverse),
            label = "dot"
        )
        Box(
            Modifier
                .size(7.dp)
                .clip(RoundedCornerShape(4.dp))
                .background(OliCyan.copy(alpha = alpha))
        )
        Spacer(Modifier.width(8.dp))
        Text(words, style = MaterialTheme.typography.labelSmall, color = OliTextMuted)
    }
}

@Composable
private fun LimitRow(label: String, used: Long, limit: Long) {
    val fraction = if (limit > 0) (used.toFloat() / limit).coerceIn(0f, 1f) else 0f
    val animated by animateFloatAsState(fraction, tween(700), label = "limitBar")
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
                    .fillMaxWidth(animated)
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

