package com.oli.mobile.ui

import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.rotate
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * OLI mark: an open ring (the "O") with a rising bar inside (the "I" / signal level).
 * Three concentric arcs rotate at different speeds — it reads as "thinking" without
 * looking like a chatbot face. Used as the app logo and as the chat hero.
 */
@Composable
fun OliMark(
    modifier: Modifier = Modifier,
    size: Dp = 120.dp,
    working: Boolean = false,
    accent: Color = OliBlue,
    accentBright: Color = OliBlueBright
) {
    val transition = rememberInfiniteTransition(label = "mark")
    val spin by transition.animateFloat(
        initialValue = 0f,
        targetValue = 360f,
        animationSpec = infiniteRepeatable(
            tween(if (working) 2600 else 12000, easing = LinearEasing),
            RepeatMode.Restart
        ),
        label = "spin"
    )
    val counterSpin by transition.animateFloat(
        initialValue = 360f,
        targetValue = 0f,
        animationSpec = infiniteRepeatable(
            tween(if (working) 4200 else 18000, easing = LinearEasing),
            RepeatMode.Restart
        ),
        label = "counter"
    )
    val breathe by transition.animateFloat(
        initialValue = 0.82f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            tween(if (working) 800 else 2600, easing = FastOutSlowInEasing),
            RepeatMode.Reverse
        ),
        label = "breathe"
    )

    Box(modifier.size(size), contentAlignment = Alignment.Center) {
        Canvas(Modifier.size(size)) {
            val r = this.size.minDimension / 2f
            val c = Offset(this.size.width / 2f, this.size.height / 2f)

            // ambient glow
            drawCircle(
                brush = Brush.radialGradient(
                    colors = listOf(accent.copy(alpha = 0.28f * breathe), Color.Transparent),
                    center = c,
                    radius = r
                ),
                radius = r,
                center = c
            )

            // outer arc — thin, bright at the head
            rotate(spin, pivot = c) {
                drawArc(
                    brush = Brush.sweepGradient(
                        listOf(Color.Transparent, accentBright.copy(alpha = 0.9f), accent, Color.Transparent),
                        center = c
                    ),
                    startAngle = 0f,
                    sweepAngle = 260f,
                    useCenter = false,
                    topLeft = Offset(c.x - r * 0.92f, c.y - r * 0.92f),
                    size = Size(r * 1.84f, r * 1.84f),
                    style = Stroke(width = r * 0.06f, cap = StrokeCap.Round)
                )
            }

            // middle arc — counter rotation
            rotate(counterSpin, pivot = c) {
                drawArc(
                    color = accent.copy(alpha = 0.45f),
                    startAngle = 40f,
                    sweepAngle = 150f,
                    useCenter = false,
                    topLeft = Offset(c.x - r * 0.70f, c.y - r * 0.70f),
                    size = Size(r * 1.40f, r * 1.40f),
                    style = Stroke(width = r * 0.035f, cap = StrokeCap.Round)
                )
            }

            // inner solid disc with the "I" bars
            drawCircle(
                brush = Brush.linearGradient(
                    colors = listOf(accent, Color(0xFF1E4FB8)),
                    start = Offset(c.x - r * 0.4f, c.y - r * 0.4f),
                    end = Offset(c.x + r * 0.4f, c.y + r * 0.4f)
                ),
                radius = r * 0.36f,
                center = c
            )

            // equalizer bars (the OLI "i" / signal)
            val bars = listOf(0.30f, 0.52f, 0.40f)
            val barW = r * 0.075f
            val gap = r * 0.055f
            val totalW = bars.size * barW + (bars.size - 1) * gap
            var x = c.x - totalW / 2f
            bars.forEachIndexed { index, height ->
                val scale = if (working) 0.7f + 0.5f * ((breathe + index * 0.33f) % 1f) else 1f
                val h = r * height * scale
                drawRoundRect(
                    color = Color.White.copy(alpha = 0.92f),
                    topLeft = Offset(x, c.y - h / 2f),
                    size = Size(barW, h),
                    cornerRadius = androidx.compose.ui.geometry.CornerRadius(barW / 2f)
                )
                x += barW + gap
            }
        }
    }
}

/** Compact logo for the top bar / buttons. */
@Composable
fun OliMarkSmall(size: Dp = 26.dp) = OliMark(size = size)

/** Decorative burst used behind the hero: thin rays that fade out. */
@Composable
fun OliBurst(modifier: Modifier = Modifier, size: Dp = 260.dp, color: Color = OliBlue) {
    val transition = rememberInfiniteTransition(label = "burst")
    val phase by transition.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(7000, easing = LinearEasing), RepeatMode.Restart),
        label = "phase"
    )
    Canvas(modifier.size(size)) {
        val r = this.size.minDimension / 2f
        val c = Offset(this.size.width / 2f, this.size.height / 2f)
        drawRingRays(c, r, phase, color)
    }
}

private fun DrawScope.drawRingRays(center: Offset, radius: Float, phase: Float, color: Color) {
    val rays = 36
    for (i in 0 until rays) {
        val angle = Math.toRadians((i * 360.0 / rays) + phase * 360.0)
        val inner = radius * 0.72f
        val outer = radius * (0.80f + 0.18f * ((i % 3) / 3f))
        val head = Offset(
            center.x + (inner * kotlin.math.cos(angle)).toFloat(),
            center.y + (inner * kotlin.math.sin(angle)).toFloat()
        )
        val tail = Offset(
            center.x + (outer * kotlin.math.cos(angle)).toFloat(),
            center.y + (outer * kotlin.math.sin(angle)).toFloat()
        )
        val alpha = 0.10f + 0.22f * (((i + phase * rays) % rays) / rays)
        drawLine(color = color.copy(alpha = alpha), start = head, end = tail, strokeWidth = 2f)
    }

    // faint dotted circle
    val dots = 48
    for (i in 0 until dots) {
        val a = Math.toRadians(i * 360.0 / dots)
        drawCircle(
            color = color.copy(alpha = 0.16f),
            radius = 1.6f,
            center = Offset(
                center.x + (radius * 0.62f * kotlin.math.cos(a)).toFloat(),
                center.y + (radius * 0.62f * kotlin.math.sin(a)).toFloat()
            )
        )
    }
}

/** Curved gradient band used as an accent under headers. */
@Composable
fun GradientHairline(modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val path = Path().apply {
            moveTo(0f, size.height / 2f)
            quadraticBezierTo(size.width * 0.5f, 0f, size.width, size.height / 2f)
        }
        drawPath(
            path = path,
            brush = Brush.horizontalGradient(
                listOf(Color.Transparent, OliBlue.copy(alpha = 0.8f), Color.Transparent)
            ),
            style = Stroke(width = 1.5f)
        )
    }
}
