package com.oli.mobile.ui

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * Ambient effects behind the chat canvas. All of them are cheap: plain Canvas draws,
 * one infinite transition each, no bitmaps and no allocations per frame beyond a few
 * offsets. They give the dark screen depth without hurting the battery.
 */

/** Slow drifting aurora blobs — the "living" backlight of the chat. */
@Composable
fun AuroraBackdrop(modifier: Modifier = Modifier, intensity: Float = 1f) {
    val transition = rememberInfiniteTransition(label = "aurora")
    val drift by transition.animateFloat(
        initialValue = 0f,
        targetValue = (Math.PI * 2).toFloat(),
        animationSpec = infiniteRepeatable(tween(26000, easing = LinearEasing), RepeatMode.Restart),
        label = "drift"
    )

    Canvas(modifier.fillMaxSize()) {
        val w = size.width
        val h = size.height
        val blobs = listOf(
            Triple(OliBlue, 0.55f, 0.18f),
            Triple(OliViolet, 0.85f, 0.42f),
            Triple(OliCyan, 0.30f, 0.75f)
        )
        blobs.forEachIndexed { index, (color, xFactor, yFactor) ->
            val phase = drift + index * 1.9f
            val cx = w * (xFactor + 0.07f * kotlin.math.cos(phase))
            val cy = h * (yFactor + 0.05f * kotlin.math.sin(phase * 0.8f))
            val radius = w * (0.55f + 0.08f * kotlin.math.sin(phase * 0.6f))
            drawCircle(
                brush = Brush.radialGradient(
                    colors = listOf(
                        color.copy(alpha = 0.16f * intensity),
                        color.copy(alpha = 0.06f * intensity),
                        Color.Transparent
                    ),
                    center = Offset(cx, cy),
                    radius = radius
                ),
                radius = radius,
                center = Offset(cx, cy)
            )
        }
    }
}

/** Thin diagonal light sweep that crosses the screen every few seconds. */
@Composable
fun LightSweep(modifier: Modifier = Modifier, color: Color = OliBlueBright) {
    val transition = rememberInfiniteTransition(label = "sweep")
    val progress by transition.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(9000, easing = LinearEasing), RepeatMode.Restart),
        label = "sweepProgress"
    )

    Canvas(modifier.fillMaxSize()) {
        if (progress > 0.55f) return@Canvas // brief pause between sweeps
        val travel = (progress / 0.55f).coerceIn(0f, 1f)
        val x = size.width * (travel * 1.6f - 0.3f)
        drawRect(
            brush = Brush.linearGradient(
                colors = listOf(Color.Transparent, color.copy(alpha = 0.05f), Color.Transparent),
                start = Offset(x - size.width * 0.25f, 0f),
                end = Offset(x + size.width * 0.25f, size.height)
            ),
            topLeft = Offset.Zero,
            size = size
        )
    }
}

/** Pulsing halo used while the assistant is working (behind the typing bubble). */
@Composable
fun PulseHalo(modifier: Modifier = Modifier, color: Color = OliBlue, active: Boolean = true) {
    val transition = rememberInfiniteTransition(label = "halo")
    val pulse by transition.animateFloat(
        initialValue = 0.35f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(1400, easing = LinearEasing), RepeatMode.Reverse),
        label = "haloPulse"
    )
    if (!active) return
    Canvas(modifier.size(64.dp)) {
        drawCircle(
            brush = Brush.radialGradient(
                colors = listOf(color.copy(alpha = 0.22f * pulse), Color.Transparent),
                center = center,
                radius = size.minDimension / 2f
            ),
            radius = size.minDimension / 2f,
            center = center
        )
        drawCircle(
            color = color.copy(alpha = 0.35f * pulse),
            radius = size.minDimension / 2f * 0.72f,
            center = center,
            style = Stroke(width = 1.5f)
        )
    }
}

/** Small composer "breathing" glow drawn behind the input pill. */
@Composable
fun ComposerGlow(modifier: Modifier = Modifier, color: Color = OliBlue, radius: Dp = 120.dp) {
    val transition = rememberInfiniteTransition(label = "glow")
    val glow by transition.animateFloat(
        initialValue = 0.25f,
        targetValue = 0.75f,
        animationSpec = infiniteRepeatable(tween(2200, easing = LinearEasing), RepeatMode.Reverse),
        label = "glowAlpha"
    )
    Canvas(modifier.fillMaxSize()) {
        drawRect(
            brush = Brush.radialGradient(
                colors = listOf(color.copy(alpha = 0.12f * glow), Color.Transparent),
                center = Offset(size.width / 2f, size.height),
                radius = radius.toPx()
            ),
            size = Size(size.width, size.height)
        )
    }
}

/** A tiny sparkle cluster — used as a decorative "magic" hint near suggestions. */
@Composable
fun Sparkles(modifier: Modifier = Modifier, color: Color = OliBlueBright) {
    val transition = rememberInfiniteTransition(label = "sparkles")
    val phase by transition.animateFloat(
        initialValue = 0f,
        targetValue = (Math.PI * 2).toFloat(),
        animationSpec = infiniteRepeatable(tween(5200, easing = LinearEasing), RepeatMode.Restart),
        label = "sparklePhase"
    )
    Canvas(modifier.size(48.dp)) {
        val points = listOf(0.25f to 0.35f, 0.7f to 0.22f, 0.55f to 0.75f, 0.85f to 0.6f)
        points.forEachIndexed { index, (x, y) ->
            val alpha = (0.35f + 0.65f * kotlin.math.abs(kotlin.math.sin(phase + index * 1.3f))).coerceIn(0f, 1f)
            drawCircle(
                color = color.copy(alpha = alpha * 0.8f),
                radius = 1.8f + index * 0.4f,
                center = Offset(size.width * x, size.height * y)
            )
        }
    }
}
