package com.oli.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.oli.mobile.data.OliApi
import com.oli.mobile.data.OliRepository
import com.oli.mobile.data.UserProfile
import kotlinx.coroutines.launch

@Composable
fun SettingsScreen(
    repo: OliRepository,
    user: UserProfile,
    usagePercent: Double,
    demo: Boolean,
    onSignedOut: () -> Unit,
    onExitDemo: () -> Unit,
    onBack: () -> Unit = {},
    onOpenAbout: () -> Unit = {}
) {
    var ownServer by remember { mutableStateOf(repo.usingOwnServer) }
    var serverField by remember { mutableStateOf(repo.customServer) }
    var status by remember { mutableStateOf<String?>(null) }
    var confirmLogout by remember { mutableStateOf(false) }
    var confirmForget by remember { mutableStateOf(false) }
    var oldPassword by remember { mutableStateOf("") }
    var newPassword by remember { mutableStateOf("") }
    var passwordMessage by remember { mutableStateOf<String?>(null) }
    var changingPassword by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    Column(
        Modifier
            .fillMaxSize()
            .background(OliInk)
            .statusBarsPadding()
    ) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 10.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                Modifier
                    .size(40.dp)
                    .clickable(onClick = onBack),
                contentAlignment = Alignment.Center
            ) {
                Text("‹", style = MaterialTheme.typography.headlineSmall, color = OliText)
            }
            Spacer(Modifier.width(4.dp))
            OliMark(size = 26.dp)
            Spacer(Modifier.width(10.dp))
            Column {
                Text("Настройки", style = MaterialTheme.typography.titleMedium, color = OliText)
                Text("OLI Mobile", style = MaterialTheme.typography.labelSmall, color = OliTextMuted)
            }
        }
        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(16.dp)
        ) {
        if (demo) {
            Card(
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer),
                modifier = Modifier.fillMaxWidth().padding(bottom = 14.dp)
            ) {
                Column(Modifier.padding(14.dp)) {
                    Text("👁 Режим просмотра", style = MaterialTheme.typography.titleMedium)
                    Text(
                        "Вы смотрите интерфейс без аккаунта: настройки не сохраняются, запросы не уходят. " +
                            "Войдите или зарегистрируйтесь, чтобы пользоваться OLI по-настоящему.",
                        style = MaterialTheme.typography.labelSmall,
                        modifier = Modifier.padding(top = 4.dp)
                    )
                    OutlinedButton(
                        onClick = onExitDemo,
                        modifier = Modifier.fillMaxWidth().padding(top = 10.dp)
                    ) { Text("Войти или зарегистрироваться") }
                }
            }
        }

        if (!demo) {
            Text("Аккаунт", style = MaterialTheme.typography.titleMedium)
            Card(
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
                modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
            ) {
                Column(Modifier.padding(16.dp)) {
                    Text(user.displayName, style = MaterialTheme.typography.headlineSmall)
                    Text("@" + user.userName, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Spacer(Modifier.height(8.dp))
                    Text(
                        "Личное место: ${OliApi.prettySize(user.quota)} · занято ${"%.0f".format(usagePercent)}%",
                        style = MaterialTheme.typography.labelMedium
                    )
                }
            }
            Spacer(Modifier.height(20.dp))
        }

        // ------------------------------------------------------------ server
        Text("Сервер", style = MaterialTheme.typography.titleMedium)
        Text(
            if (ownServer)
                "Приложение работает с вашим компьютером."
            else
                "Приложение само подключается к серверу OLI.",
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(top = 4.dp)
        )

        Row(
            Modifier.fillMaxWidth().padding(top = 10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(Modifier.weight(1f)) {
                Text("Свой ПК как сервер", style = MaterialTheme.typography.bodyMedium)
                Text(
                    if (ownServer) repo.serverUrl else "по умолчанию: сервер OLI",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            Switch(
                checked = ownServer,
                enabled = !demo,
                onCheckedChange = { enabled ->
                    ownServer = enabled
                    scope.launch {
                        if (!enabled) {
                            status = try {
                                val health = repo.useDefaultServer()
                                "Сервер OLI доступен · версия ${health?.version?.ifBlank { "?" } ?: "?"}"
                            } catch (e: Exception) {
                                "Не удалось подключиться к серверу OLI: ${e.message}"
                            }
                        } else if (serverField.isNotBlank()) {
                            status = try {
                                repo.useOwnServer(serverField)
                                "Свой ПК доступен"
                            } catch (e: Exception) {
                                "Свой ПК не отвечает: ${e.message}"
                            }
                        } else {
                            status = "Укажите IP компьютера ниже."
                        }
                    }
                }
            )
        }

        if (ownServer) {
            OutlinedTextField(
                value = serverField,
                onValueChange = { serverField = it },
                label = { Text("IP компьютера:порт") },
                placeholder = { Text("192.168.1.10:8765") },
                singleLine = true,
                enabled = !demo,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
            )
            Button(
                enabled = !demo,
                onClick = {
                    scope.launch {
                        status = try {
                            repo.useOwnServer(serverField)
                            "Свой ПК доступен"
                        } catch (e: Exception) {
                            "Свой ПК не отвечает: ${e.message}"
                        }
                    }
                },
                modifier = Modifier.padding(top = 8.dp)
            ) { Text("Проверить и сохранить") }
            Text(
                "Адрес виден в OLI на ПК: раздел «Пульт админа» → адрес для телефона. " +
                    "Телефон и ПК должны быть в одной сети (или в вашем VPN).",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 6.dp)
            )
        }
        status?.let {
            Text(it, style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(top = 6.dp),
                color = MaterialTheme.colorScheme.onSurfaceVariant)
        }

        // ------------------------------------------------------------ security
        if (!demo) {
            Spacer(Modifier.height(20.dp))
            Text("Безопасность", style = MaterialTheme.typography.titleMedium)
            Text(
                "Логин и пароль хранятся в зашифрованном виде (AES-256-GCM, ключ в Android Keystore). " +
                    "На сервере хранится только PBKDF2-хеш пароля — его не может прочитать даже владелец сервера. " +
                    "Забытый пароль можно только сбросить через пульт администратора на ПК.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 6.dp)
            )

            if (!changingPassword) {
                OutlinedButton(
                    onClick = { changingPassword = true },
                    modifier = Modifier.fillMaxWidth().padding(top = 12.dp)
                ) { Text("Сменить пароль") }
            } else {
                OutlinedTextField(
                    value = oldPassword,
                    onValueChange = { oldPassword = it },
                    label = { Text("Текущий пароль") },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    modifier = Modifier.fillMaxWidth().padding(top = 10.dp)
                )
                OutlinedTextField(
                    value = newPassword,
                    onValueChange = { newPassword = it },
                    label = { Text("Новый пароль (8+, буквы и цифры)") },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
                )
                Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(
                        enabled = OliApi.validPassword(newPassword),
                        onClick = {
                            scope.launch {
                                passwordMessage = try {
                                    repo.changePassword(oldPassword, newPassword)
                                    oldPassword = ""
                                    newPassword = ""
                                    changingPassword = false
                                    "Пароль изменён — новый пароль сохранён на телефоне."
                                } catch (e: Exception) {
                                    "Ошибка: ${e.message}"
                                }
                            }
                        }
                    ) { Text("Сохранить") }
                    TextButton(onClick = { changingPassword = false }) { Text("Отмена") }
                }
            }
            passwordMessage?.let {
                Text(it, style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(top = 6.dp))
            }

            Spacer(Modifier.height(22.dp))
            HorizontalDivider()
            Spacer(Modifier.height(14.dp))
            OutlinedButton(onClick = { confirmLogout = true }, modifier = Modifier.fillMaxWidth()) {
                Text("Выйти (пароль останется сохранённым)")
            }
            TextButton(onClick = { confirmForget = true }, modifier = Modifier.fillMaxWidth().padding(top = 4.dp)) {
                Text("Выйти и удалить сохранённые данные", color = MaterialTheme.colorScheme.error)
            }
        }

        Spacer(Modifier.height(16.dp))
        OutlinedButton(onClick = onOpenAbout, modifier = Modifier.fillMaxWidth()) {
            Text("Возможности OLI")
        }
        Spacer(Modifier.height(20.dp))
        Text(
            "OLI Mobile · модель работает на компьютере, телефон — пульт. " +
                "В режиме просмотра данные никуда не отправляются.",
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        }
    }

    if (confirmLogout) {
        AlertDialog(
            onDismissRequest = { confirmLogout = false },
            title = { Text("Выйти из аккаунта?") },
            text = { Text("Логин и пароль сохранятся, чтобы войти снова в один тап.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmLogout = false
                    scope.launch {
                        repo.logout(forgetPassword = false)
                        onSignedOut()
                    }
                }) { Text("Выйти") }
            },
            dismissButton = { TextButton(onClick = { confirmLogout = false }) { Text("Отмена") } }
        )
    }

    if (confirmForget) {
        AlertDialog(
            onDismissRequest = { confirmForget = false },
            title = { Text("Выйти и забыть пароль?") },
            text = { Text("Сохранённые логин и пароль будут удалены с телефона. Файлы на сервере останутся.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmForget = false
                    scope.launch {
                        repo.logout(forgetPassword = true)
                        onSignedOut()
                    }
                }) { Text("Удалить и выйти", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { confirmForget = false }) { Text("Отмена") } }
        )
    }
}
