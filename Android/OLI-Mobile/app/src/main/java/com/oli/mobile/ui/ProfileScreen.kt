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
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.unit.dp
import com.oli.mobile.BuildConfig
import com.oli.mobile.data.OliApi
import com.oli.mobile.data.OliRepository
import com.oli.mobile.data.UserProfile
import com.oli.mobile.data.UsageSnapshotData
import kotlinx.coroutines.launch

/**
 * User profile, opened from the round avatar in the chat header:
 * display name, storage/token stats, server address and the way out.
 */
@Composable
fun ProfileScreen(
    repo: OliRepository,
    user: UserProfile,
    usage: UsageSnapshotData?,
    filesUsedBytes: Long,
    filesQuotaBytes: Long,
    demo: Boolean,
    onBack: () -> Unit,
    onOpenStorage: () -> Unit,
    onOpenSettings: () -> Unit,
    onLogout: () -> Unit
) {
    var name by remember { mutableStateOf(if (demo) "" else user.displayName) }
    var saving by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    val initial = (user.displayName.ifBlank { user.userName }).trim().take(1).uppercase().ifBlank { "O" }

    Column(
        Modifier.fillMaxSize().background(OliInk).statusBarsPadding()
    ) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 10.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(Modifier.size(40.dp).clip(RoundedCornerShape(12.dp)).clickable(onClick = onBack),
                contentAlignment = Alignment.Center) {
                Text("‹", style = MaterialTheme.typography.headlineSmall, color = OliText)
            }
            Spacer(Modifier.width(6.dp))
            Text("Профиль", style = MaterialTheme.typography.titleMedium, color = OliText)
        }

        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .imePadding()
                .padding(horizontal = 16.dp, vertical = 8.dp)
        ) {
            // --- avatar + identity
            Row(verticalAlignment = Alignment.CenterVertically) {
                Avatar(initial = initial, size = 68)
                Spacer(Modifier.width(14.dp))
                Column {
                    Text(
                        if (demo) "Гость" else user.displayName,
                        style = MaterialTheme.typography.headlineSmall,
                        color = OliText
                    )
                    Text(
                        if (demo) "режим просмотра" else "@${user.userName}",
                        style = MaterialTheme.typography.labelSmall,
                        color = OliTextMuted
                    )
                }
            }

            Spacer(Modifier.height(18.dp))

            // --- editable name
            if (!demo) {
                Text("Имя", style = MaterialTheme.typography.labelMedium, color = OliTextMuted)
                Spacer(Modifier.height(6.dp))
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it.take(40) },
                    singleLine = true,
                    enabled = !saving,
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Words),
                    modifier = Modifier.fillMaxWidth()
                )
                Button(
                    enabled = !saving && name.trim().length >= 2 && name.trim() != user.displayName,
                    onClick = {
                        scope.launch {
                            saving = true
                            error = null
                            message = null
                            try {
                                repo.updateDisplayName(name.trim())
                                message = "Имя обновлено"
                            } catch (e: Exception) {
                                error = e.message ?: "Не удалось сохранить имя"
                            } finally {
                                saving = false
                            }
                        }
                    },
                    modifier = Modifier.padding(top = 8.dp)
                ) { Text(if (saving) "Сохраняю…" else "Сохранить") }

                message?.let {
                    Text(it, style = MaterialTheme.typography.labelSmall, color = OliCyan,
                        modifier = Modifier.padding(top = 6.dp))
                }
                error?.let {
                    Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.error,
                        modifier = Modifier.padding(top = 6.dp))
                }

                Spacer(Modifier.height(20.dp))
            }

            // --- stats
            ProfileCard {
                StatRow("Хранилище файлов",
                    "${OliApi.prettySize(filesUsedBytes)} из ${OliApi.prettySize(filesQuotaBytes)}",
                    onClick = onOpenStorage)
                DividerLine()
                StatRow("Токены за сутки",
                    usage?.let { "${compact(it.dayUsed)} из ${compact(it.dayLimit.toLong())}" } ?: "—")
                DividerLine()
                StatRow("Токены за час",
                    usage?.let { "${compact(it.hourUsed)} из ${compact(it.hourLimit.toLong())}" } ?: "—")
                DividerLine()
                StatRow("Пользователей онлайн", usage?.activeUsers?.toString() ?: "—")
            }

            Spacer(Modifier.height(14.dp))
            ProfileCard {
                StatRow("Сервер", repo.serverEndpoint.replace("http://", "").replace("https://", ""))
                DividerLine()
                StatRow("Версия приложения", BuildConfig.VERSION_NAME)
            }

            Spacer(Modifier.height(18.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Button(onClick = onOpenSettings, modifier = Modifier.weight(1f)) { Text("Настройки") }
                Button(onClick = onOpenStorage, modifier = Modifier.weight(1f)) { Text("Хранилище") }
            }

            if (!demo) {
                Spacer(Modifier.height(10.dp))
                Box(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(16.dp))
                        .background(OliGlass)
                        .border(1.dp, OliOutline.copy(alpha = 0.8f), RoundedCornerShape(16.dp))
                        .clickable(onClick = onLogout)
                        .padding(16.dp)
                ) {
                    Text("Выйти из аккаунта", style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.error)
                }
            }

            Spacer(Modifier.height(24.dp))
        }
    }
}

@Composable
private fun ProfileCard(content: @Composable () -> Unit) {
    Column(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .background(OliGlass)
            .border(1.dp, OliOutline.copy(alpha = 0.75f), RoundedCornerShape(18.dp))
            .padding(vertical = 4.dp)
    ) { content() }
}

@Composable
private fun StatRow(label: String, value: String, onClick: (() -> Unit)? = null) {
    Row(
        Modifier
            .fillMaxWidth()
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(horizontal = 16.dp, vertical = 13.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(label, style = MaterialTheme.typography.bodyMedium, color = OliText, modifier = Modifier.weight(1f))
        Text(value, style = MaterialTheme.typography.labelMedium, color = OliTextMuted)
        if (onClick != null) {
            Spacer(Modifier.width(8.dp))
            Text("›", style = MaterialTheme.typography.bodyLarge, color = OliTextMuted)
        }
    }
}

@Composable
private fun DividerLine() {
    Box(Modifier.fillMaxWidth().height(1.dp).background(OliOutline.copy(alpha = 0.6f)))
}

/** Round gradient avatar with the first letter of the name. */
@Composable
fun Avatar(initial: String, size: Int = 40, ring: Boolean = true) {
    Box(
        Modifier
            .size(size.dp)
            .clip(RoundedCornerShape(percent = 50))
            .background(Brush.linearGradient(listOf(OliBlue, OliViolet)))
            .then(
                if (ring) Modifier.border(1.dp, OliBlueBright.copy(alpha = 0.5f), RoundedCornerShape(percent = 50))
                else Modifier
            ),
        contentAlignment = Alignment.Center
    ) {
        Text(
            initial,
            color = androidx.compose.ui.graphics.Color.White,
            fontWeight = FontWeight.SemiBold,
            style = MaterialTheme.typography.titleMedium
        )
    }
}
