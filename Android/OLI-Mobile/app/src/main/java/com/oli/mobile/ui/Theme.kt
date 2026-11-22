package com.oli.mobile.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

// ---------------------------------------------------------------- palette
// Near-black canvas, soft glass surfaces and one vivid blue accent (the OLI orb).

val OliInk = Color(0xFF07080B)
val OliSurface = Color(0xFF111216)
val OliGlass = Color(0xFF1A1C21)
val OliGlassSoft = Color(0xFF212430)
val OliOutline = Color(0xFF2B2E38)
val OliText = Color(0xFFF1F2F5)
val OliTextMuted = Color(0xFF9AA0AC)

val OliBlue = Color(0xFF2F7BFF)
val OliBlueBright = Color(0xFF6FE3FF)
val OliCyan = Color(0xFF43D9C8)
val OliViolet = Color(0xFF7B61FF)
val OliBlueDeep = Color(0xFF14274F)

private val Dark = darkColorScheme(
    primary = OliBlue,
    onPrimary = Color.White,
    primaryContainer = Color(0xFF10233F),
    onPrimaryContainer = Color(0xFFD8E5FF),
    secondary = Color(0xFF7FD1AE),
    onSecondary = Color(0xFF07130E),
    secondaryContainer = Color(0xFF182A24),
    onSecondaryContainer = Color(0xFFC9F0DF),
    tertiary = OliBlueBright,
    background = OliInk,
    onBackground = OliText,
    surface = OliSurface,
    onSurface = OliText,
    surfaceVariant = OliGlass,
    onSurfaceVariant = OliTextMuted,
    surfaceContainer = Color(0xFF0C0D10),
    surfaceContainerHigh = OliGlass,
    surfaceContainerHighest = OliGlassSoft,
    outline = OliOutline,
    outlineVariant = Color(0xFF23262E),
    error = Color(0xFFFF6B6B),
    onError = Color.White,
    scrim = Color(0xCC000000)
)

private val Light = lightColorScheme(
    primary = Color(0xFF2A63D8),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFDCE7FF),
    onPrimaryContainer = Color(0xFF0C1C3C),
    secondary = Color(0xFF2E8B68),
    background = Color(0xFFF6F7FA),
    onBackground = Color(0xFF101218),
    surface = Color.White,
    onSurface = Color(0xFF101218),
    surfaceVariant = Color(0xFFECEEF4),
    onSurfaceVariant = Color(0xFF565C69),
    outline = Color(0xFFD5D9E2),
    error = Color(0xFFC62828)
)

/**
 * OLI is a dark-first app: the chat canvas stays near-black like the reference design.
 * The light scheme is kept for completeness (system/accessibility use).
 */
@Composable
fun OliTheme(darkTheme: Boolean = true, content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = if (darkTheme) Dark else Light,
        typography = Typography(
            headlineLarge = TextStyle(fontSize = 34.sp, fontWeight = FontWeight.SemiBold, letterSpacing = (-0.5).sp),
            headlineSmall = TextStyle(fontSize = 24.sp, fontWeight = FontWeight.Medium, letterSpacing = (-0.2).sp),
            titleMedium = TextStyle(fontSize = 16.sp, fontWeight = FontWeight.SemiBold),
            bodyLarge = TextStyle(fontSize = 17.sp, lineHeight = 25.sp),
            bodyMedium = TextStyle(fontSize = 15.sp, lineHeight = 22.sp),
            labelMedium = TextStyle(fontSize = 12.5.sp, fontWeight = FontWeight.Medium),
            labelSmall = TextStyle(fontSize = 11.5.sp, lineHeight = 15.sp)
        ),
        content = content
    )
}
