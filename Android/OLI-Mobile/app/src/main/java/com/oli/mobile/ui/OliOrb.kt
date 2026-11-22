package com.oli.mobile.ui

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
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.rotate
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import kotlin.math.cos
import kotlin.math.sin

/**
 * The OLI orb: a glossy blue sphere with two white "eyes", a slow sheen sweeps across
 * the surface and a soft halo pulses around it. Used as the hero element of the chat
 * and as a compact "thinking" indicator while the model answers.
 */
@Composable
fun OliOrb(
    modifier: Modifier = Modifier,
    size: Dp = 96.dp,
    thinking: Boolean = false
) {
    val transition = rememberInfiniteTransition(label = "orb")

    // Halo breathing: slow, low amplitude — it should feel alive, not busy.
    val halo by transition.animateFloat(
        initialValue = 0.75f,
        targetValue = if (thinking) 1.0f else 0.92f,
        animationSpec = infiniteRepeatable(
            animation = tween(if (thinking) 900 else 2400, easing = LinearEasing),
            repeatMode = RepeatMode.Reverse
        ),
        label = "halo"
    )

    // Specular highlight drifting across the sphere.
    val sheen by transition.animateFloat(
        initialValue = 0f,
        targetValue = 360f,
        animationSpec = infiniteRepeatable(
            animation = tween(if (thinking) 2600 else 9000, easing = LinearEasing),
            repeatMode = RepeatMode.Restart
        ),
        label = "sheen"
    )

    // Eyes blink every few seconds; while thinking they stay alert.
    val blink by transition.animateFloat(
        initialValue = 1f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(1, easing = LinearEasing),
            repeatMode = RepeatMode.Reverse
        ),
        label = "blink"
    )

    Box(modifier.size(size), contentAlignment = Alignment.Center) {
        Canvas(Modifier.size(size)) {
            val radius = this.size.minDimension / 2f
            val center = Offset(this.size.width / 2f, this.size.height / 2f)

            // --- outer halo (two soft rings)
            drawCircle(
                brush = Brush.radialGradient(
                    colors = listOf(OliBlue.copy(alpha = 0.32f * halo), Color.Transparent),
                    center = center,
                    radius = radius * 1.9f
                ),
                radius = radius * 1.9f,
                center = center
            )
            drawCircle(
                color = OliBlueBright.copy(alpha = 0.10f * halo),
                radius = radius * 1.28f,
                center = center,
                style = Stroke(width = radius * 0.05f)
            )

            // --- sphere body: light from the top-left, deep blue at the bottom-right
            drawCircle(
                brush = Brush.radialGradient(
                    colors = listOf(
                        Color(0xFF8FBCFF),
                        OliBlue,
                        Color(0xFF1E4FB8),
                        Color(0xFF102B66)
                    ),
                    center = Offset(center.x - radius * 0.32f, center.y - radius * 0.36f),
                    radius = radius * 1.55f
                ),
                radius = radius,
                center = center
            )

            // --- rim light
            drawCircle(
                brush = Brush.sweepGradient(
                    colors = listOf(
                        Color.Transparent,
                        OliBlueBright.copy(alpha = 0.55f),
                        Color.Transparent
                    ),
                    center = center
                ),
                radius = radius - radius * 0.02f,
                center = center,
                style = Stroke(width = radius * 0.06f)
            )

            // --- drifting sheen
            rotate(sheen, pivot = center) {
                drawArc(
                    brush = Brush.linearGradient(
                        colors = listOf(Color.Transparent, Color.White.copy(alpha = 0.18f), Color.Transparent),
                        start = Offset(center.x - radius, center.y - radius),
                        end = Offset(center.x + radius, center.y + radius)
                    ),
                    startAngle = 200f,
                    sweepAngle = 55f,
                    useCenter = false,
                    topLeft = Offset(center.x - radius * 0.92f, center.y - radius * 0.92f),
                    size = Size(radius * 1.84f, radius * 1.84f),
                    style = Stroke(width = radius * 0.26f)
                )
            }

            // --- specular dot (soft, upper-left)
            drawCircle(
                brush = Brush.radialGradient(
                    colors = listOf(Color.White.copy(alpha = 0.55f), Color.Transparent),
                    center = Offset(center.x - radius * 0.34f, center.y - radius * 0.42f),
                    radius = radius * 0.42f
                ),
                radius = radius * 0.42f,
                center = Offset(center.x - radius * 0.34f, center.y - radius * 0.42f)
            )

            // --- eyes
            val eyeOffsetX = radius * 0.30f
            val eyeOffsetY = radius * 0.06f
            val eyeW = radius * 0.17f
            val eyeH = radius * 0.30f * blink
            drawEye(center + Offset(-eyeOffsetX, eyeOffsetY), eyeW, eyeH)
            drawEye(center + Offset(eyeOffsetX, eyeOffsetY), eyeW, eyeH)
        }
    }
}

private fun DrawScope.drawEye(center: Offset, width: Float, height: Float) {
    drawOval(
        brush = Brush.verticalGradient(
            colors = listOf(Color.White, Color(0xFFEDF3FF)),
            startY = center.y - height,
            endY = center.y + height
        ),
        topLeft = Offset(center.x - width / 2f, center.y - height / 2f),
        size = Size(width, height)
    )
}

/**
 * Small orb used inline (assistant avatar, typing row). Cheaper: no halo animation.
 */
@Composable
fun OliOrbMini(size: Dp = 26.dp, thinking: Boolean = false) {
    val transition = rememberInfiniteTransition(label = "orb-mini")
    val pulse by transition.animateFloat(
        initialValue = 0.86f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(if (thinking) 700 else 1800, easing = androidx.compose.animation.core.FastOutSlowInEasing),
            repeatMode = RepeatMode.Reverse
        ),
        label = "pulse"
    )

    Canvas(Modifier.size(size)) {
        val radius = this.size.minDimension / 2f * if (thinking) pulse else 1f
        val center = Offset(this.size.width / 2f, this.size.height / 2f)

        if (thinking) {
            drawCircle(
                brush = Brush.radialGradient(
                    colors = listOf(OliBlue.copy(alpha = 0.35f), Color.Transparent),
                    center = center,
                    radius = radius * 1.8f
                ),
                radius = radius * 1.8f,
                center = center
            )
        }

        drawCircle(
            brush = Brush.radialGradient(
                colors = listOf(Color(0xFF9CC4FF), OliBlue, Color(0xFF173468)),
                center = Offset(center.x - radius * 0.3f, center.y - radius * 0.35f),
                radius = radius * 1.5f
            ),
            radius = radius,
            center = center
        )

        val eyeW = radius * 0.18f
        val eyeH = radius * 0.32f
        drawOval(
            color = Color.White,
            topLeft = Offset(center.x - radius * 0.34f, center.y - eyeH / 2f),
            size = Size(eyeW, eyeH)
        )
        drawOval(
            color = Color.White,
            topLeft = Offset(center.x + radius * 0.16f, center.y - eyeH / 2f),
            size = Size(eyeW, eyeH)
        )
    }
}

/** A slowly rotating dotted ring used as a "signal / working" decoration. */
@Composable
fun OrbitRing(size: Dp = 220.dp, color: Color = OliBlue, dots: Int = 3) {
    val transition = rememberInfiniteTransition(label = "orbit")
    val angle by transition.animateFloat(
        initialValue = 0f,
        targetValue = 360f,
        animationSpec = infiniteRepeatable(tween(6000, easing = LinearEasing), RepeatMode.Restart),
        label = "orbit-angle"
    )

    Canvas(Modifier.size(size)) {
        val radius = this.size.minDimension / 2f
        val center = Offset(this.size.width / 2f, this.size.height / 2f)
        drawCircle(
            color = color.copy(alpha = 0.10f),
            radius = radius,
            center = center,
            style = Stroke(width = 1.dp.toPx())
        )
        for (i in 0 until dots) {
            val a = Math.toRadians((angle + i * 360f / dots).toDouble())
            val dot = Offset(
                center.x + (radius * cos(a)).toFloat(),
                center.y + (radius * sin(a)).toFloat()
            )
            drawCircle(color = color.copy(alpha = 0.55f), radius = 2.5.dp.toPx(), center = dot)
        }
    }
}
