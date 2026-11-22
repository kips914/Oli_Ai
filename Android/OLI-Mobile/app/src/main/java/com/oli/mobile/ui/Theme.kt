package com.oli.mobile.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

private val Accent = Color(0xFF5B8CFF)
private val AccentDark = Color(0xFF3F6FE0)

private val Dark = darkColorScheme(
    primary = Accent,
    onPrimary = Color.White,
    primaryContainer = Color(0xFF1E2A47),
    onPrimaryContainer = Color(0xFFD7E2FF),
    secondary = Color(0xFF7FD1AE),
    background = Color(0xFF101319),
    onBackground = Color(0xFFE7EAF0),
    surface = Color(0xFF171B23),
    onSurface = Color(0xFFE7EAF0),
    surfaceVariant = Color(0xFF222835),
    onSurfaceVariant = Color(0xFFAEB6C6),
    error = Color(0xFFFF6B6B)
)

private val Light = lightColorScheme(
    primary = AccentDark,
    onPrimary = Color.White,
    primaryContainer = Color(0xFFDCE6FF),
    onPrimaryContainer = Color(0xFF102044),
    secondary = Color(0xFF2E8B68),
    background = Color(0xFFF5F7FB),
    onBackground = Color(0xFF141821),
    surface = Color.White,
    onSurface = Color(0xFF141821),
    surfaceVariant = Color(0xFFEDF1F8),
    onSurfaceVariant = Color(0xFF4E5769),
    error = Color(0xFFC62828)
)

@Composable
fun OliTheme(darkTheme: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = if (darkTheme) Dark else Light,
        typography = Typography(
            headlineSmall = TextStyle(fontSize = 22.sp, fontWeight = FontWeight.SemiBold),
            titleMedium = TextStyle(fontSize = 16.sp, fontWeight = FontWeight.SemiBold),
            bodyMedium = TextStyle(fontSize = 15.sp, lineHeight = 21.sp)
        ),
        content = content
    )
}
