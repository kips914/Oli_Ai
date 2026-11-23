package com.oli.mobile.ui

import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.oli.mobile.data.OliApi
import com.oli.mobile.data.OliRepository
import com.oli.mobile.data.ServerHealth
import kotlinx.coroutines.launch

/**
 * Sign-in / registration against the OLI server. The app picks the server by itself
 * (the default OLI server, or the user's own PC when that mode is on in Settings);
 * the address field is hidden unless the user opens it explicitly.
 */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
fun AuthScreen(repo: OliRepository, defaultServerOffline: Boolean, onDemo: () -> Unit) {
    var registering by remember { mutableStateOf(false) }
    var userName by remember { mutableStateOf(repo.lastUserName) }
    var displayName by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var password2 by remember { mutableStateOf("") }
    var invite by remember { mutableStateOf("") }
    var remember by remember { mutableStateOf(true) }
    var showPassword by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var info by remember { mutableStateOf<String?>(null) }
    var health by remember { mutableStateOf<ServerHealth?>(null) }
    var checking by remember { mutableStateOf(true) }
    var ownServer by remember { mutableStateOf(repo.usingOwnServer) }
    var serverField by remember { mutableStateOf(repo.customServer) }
    var showServerField by remember { mutableStateOf(false) }
    var searching by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    // The app connects by itself on open; the user does not type an address.
    LaunchedEffect(Unit) {
        checking = true
        health = if (repo.usingOwnServer) {
            // The PC's IP may have changed: look for it on this Wi-Fi before giving up.
            val found = runCatching { repo.findOwnServer() }.getOrNull()
            if (found != null) {
                serverField = found
                runCatching { repo.useOwnServer(found) }.getOrNull()
            } else {
                runCatching { repo.useOwnServer(repo.customServer) }.getOrNull()
            }
        } else {
            repo.probeDefaultServer()
        }
        if (health == null) {
            showServerField = true
            ownServer = repo.usingOwnServer
            error = if (repo.usingOwnServer)
                "Свой сервер не отвечает. Нажмите «Найти мой ПК» или впишите адрес вручную."
            else
                "Сервер OLI сейчас недоступен. Можно посмотреть интерфейс без входа или подключить свой ПК."
        }
        checking = false
    }

    fun submit() {
        error = null
        info = null
        if (userName.isBlank()) { error = "Введите логин"; return }
        if (!OliApi.validPassword(password)) {
            error = "Пароль: минимум 8 символов, буквы и цифры"
            return
        }
        if (registering && password != password2) { error = "Пароли не совпадают"; return }

        busy = true
        scope.launch {
            try {
                if (registering) {
                    if (!OliApi.validUserName(userName)) {
                        error = "Логин: 3–32 символа, латиница, цифры, . _ -"
                        busy = false
                        return@launch
                    }
                    repo.register(repo.serverUrl, userName.trim(), password, displayName.trim(), invite.trim(), remember)
                } else {
                    repo.login(repo.serverUrl, userName.trim(), password, remember)
                }
            } catch (e: Exception) {
                error = e.message ?: "Не удалось войти"
            } finally {
                busy = false
            }
        }
    }

    Box(
        Modifier
            .fillMaxSize()
            .background(OliInk)
    ) {
        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .imePadding()
                .statusBarsPadding()
                .padding(horizontal = 22.dp, vertical = 22.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Box(contentAlignment = Alignment.Center) {
                OliBurst(size = 180.dp)
                OliMark(size = 92.dp)
            }
            Spacer(Modifier.height(14.dp))
            Text("OLI", style = MaterialTheme.typography.headlineLarge, color = OliText)
            Text(
                "Ваш ИИ на вашем компьютере",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 4.dp, bottom = 14.dp)
            )

            ServerBadge(
                checking = checking,
                health = health,
                ownServer = ownServer,
                address = repo.serverUrl,
                onClick = { showServerField = !showServerField }
            )

            Spacer(Modifier.height(16.dp))

            Card(
                shape = RoundedCornerShape(22.dp),
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
                elevation = CardDefaults.cardElevation(defaultElevation = 2.dp),
                modifier = Modifier.fillMaxWidth()
            ) {
                Column(Modifier.padding(20.dp)) {
                    SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                        SegmentedButton(
                            selected = !registering,
                            onClick = { registering = false; error = null },
                            shape = SegmentedButtonDefaults.itemShape(index = 0, count = 2)
                        ) { Text("Вход") }
                        SegmentedButton(
                            selected = registering,
                            onClick = { registering = true; error = null },
                            shape = SegmentedButtonDefaults.itemShape(index = 1, count = 2)
                        ) { Text("Регистрация") }
                    }

                    Spacer(Modifier.height(16.dp))

                    OutlinedTextField(
                        value = userName,
                        onValueChange = { userName = it.trim() },
                        label = { Text("Логин") },
                        singleLine = true,
                        enabled = !busy,
                        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Next),
                        modifier = Modifier.fillMaxWidth()
                    )

                    if (registering) {
                        Spacer(Modifier.height(10.dp))
                        OutlinedTextField(
                            value = displayName,
                            onValueChange = { displayName = it },
                            label = { Text("Как к вам обращаться (необязательно)") },
                            singleLine = true,
                            enabled = !busy,
                            modifier = Modifier.fillMaxWidth()
                        )
                    }

                    Spacer(Modifier.height(10.dp))
                    OutlinedTextField(
                        value = password,
                        onValueChange = { password = it },
                        label = { Text("Пароль") },
                        singleLine = true,
                        enabled = !busy,
                        visualTransformation = if (showPassword) VisualTransformation.None else PasswordVisualTransformation(),
                        keyboardOptions = KeyboardOptions(
                            keyboardType = KeyboardType.Password,
                            imeAction = if (registering) ImeAction.Next else ImeAction.Done
                        ),
                        trailingIcon = {
                            IconButton(onClick = { showPassword = !showPassword }) {
                                Icon(
                                    if (showPassword) Icons.Filled.VisibilityOff else Icons.Filled.Visibility,
                                    contentDescription = if (showPassword) "Скрыть пароль" else "Показать пароль"
                                )
                            }
                        },
                        modifier = Modifier.fillMaxWidth()
                    )

                    if (registering) {
                        Spacer(Modifier.height(10.dp))
                        OutlinedTextField(
                            value = password2,
                            onValueChange = { password2 = it },
                            label = { Text("Пароль ещё раз") },
                            singleLine = true,
                            enabled = !busy,
                            visualTransformation = if (showPassword) VisualTransformation.None else PasswordVisualTransformation(),
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Done),
                            modifier = Modifier.fillMaxWidth()
                        )
                        Spacer(Modifier.height(10.dp))
                        OutlinedTextField(
                            value = invite,
                            onValueChange = { invite = it },
                            label = { Text("Код приглашения (если нужен)") },
                            singleLine = true,
                            enabled = !busy,
                            modifier = Modifier.fillMaxWidth()
                        )
                    }

                    Row(
                        Modifier.fillMaxWidth().padding(top = 4.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Checkbox(checked = remember, onCheckedChange = { remember = it }, enabled = !busy)
                        Text(
                            "Запомнить меня на этом телефоне",
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(start = 2.dp)
                        )
                    }

                    Text(
                        "Пароль хранится в защищённом хранилище Android (шифрование ключом из Keystore). " +
                            "На сервере остаётся только хеш — его не может прочитать даже владелец сервера.",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )

                    if (error != null) {
                        Text(
                            error!!,
                            color = MaterialTheme.colorScheme.error,
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(top = 12.dp)
                        )
                    }
                    if (info != null) {
                        Text(
                            info!!,
                            color = MaterialTheme.colorScheme.secondary,
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(top = 12.dp)
                        )
                    }

                    Button(
                        onClick = ::submit,
                        enabled = !busy,
                        shape = RoundedCornerShape(14.dp),
                        modifier = Modifier.fillMaxWidth().padding(top = 16.dp).height(50.dp)
                    ) {
                        if (busy) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(20.dp),
                                strokeWidth = 2.dp,
                                color = MaterialTheme.colorScheme.onPrimary
                            )
                            Spacer(Modifier.width(10.dp))
                        }
                        Text(if (registering) "Создать аккаунт" else "Войти", style = MaterialTheme.typography.titleMedium)
                    }

                    OutlinedButton(
                        onClick = onDemo,
                        shape = RoundedCornerShape(14.dp),
                        modifier = Modifier.fillMaxWidth().padding(top = 10.dp).height(48.dp)
                    ) {
                        Text("👁 Посмотреть без входа")
                    }
                    Text(
                        "Режим просмотра: интерфейс и кнопки работают, но запросы никуда не уходят.",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        textAlign = TextAlign.Center,
                        modifier = Modifier.fillMaxWidth().padding(top = 4.dp)
                    )
                }
            }

            if (showServerField) {
                Spacer(Modifier.height(14.dp))
                Card(
                    shape = RoundedCornerShape(18.dp),
                    colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Column(Modifier.padding(16.dp)) {
                        Text("Сервер", style = MaterialTheme.typography.titleMedium)
                        Text(
                            "По умолчанию приложение подключается к серверу OLI само. " +
                                "Свой ПК включается здесь или в настройках.",
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(top = 4.dp)
                        )

                        Row(Modifier.padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(
                                checked = ownServer,
                                onCheckedChange = { enabled ->
                                    ownServer = enabled
                                    scope.launch {
                                        checking = true
                                        error = null
                                        health = if (enabled && serverField.isNotBlank()) {
                                            runCatching { repo.useOwnServer(serverField) }
                                                .onFailure { error = it.message }
                                                .getOrNull()
                                        } else {
                                            runCatching { repo.useDefaultServer() }.getOrNull()
                                        }
                                        checking = false
                                    }
                                }
                            )
                            Text("Свой ПК как сервер", style = MaterialTheme.typography.bodyMedium)
                        }

                        if (ownServer) {
                            OutlinedTextField(
                                value = serverField,
                                onValueChange = { serverField = it },
                                label = { Text("IP компьютера:порт") },
                                placeholder = { Text("192.168.1.10:8765") },
                                singleLine = true,
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Done),
                                modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
                            )
                            Row(
                                Modifier.padding(top = 8.dp),
                                horizontalArrangement = Arrangement.spacedBy(8.dp)
                            ) {
                                Button(
                                    enabled = serverField.isNotBlank() && !searching,
                                    onClick = {
                                        scope.launch {
                                            checking = true
                                            error = null
                                            try {
                                                health = repo.useOwnServer(serverField)
                                                info = "Свой сервер доступен"
                                            } catch (e: Exception) {
                                                health = null
                                                error = "Свой сервер не отвечает: ${e.message}"
                                            } finally {
                                                checking = false
                                            }
                                        }
                                    }
                                ) { Text("Подключиться") }
                                OutlinedButton(
                                    enabled = !searching,
                                    onClick = {
                                        scope.launch {
                                            searching = true
                                            error = null
                                            info = null
                                            try {
                                                val found = repo.findOwnServer()
                                                if (found != null) {
                                                    serverField = found
                                                    ownServer = true
                                                    health = runCatching { repo.useOwnServer(found) }.getOrNull()
                                                    info = "Найден компьютер: " +
                                                        found.replace("http://", "").replace("https://", "")
                                                } else {
                                                    health = null
                                                    error = "Не нашёл сервер в этой сети. Проверьте, что OLI " +
                                                        "на ПК запущен и порт открыт в брандмауэре."
                                                }
                                            } catch (e: Exception) {
                                                error = "Поиск не удался: ${e.message}"
                                            } finally {
                                                searching = false
                                                checking = false
                                            }
                                        }
                                    }
                                ) { Text(if (searching) "Ищу…" else "Найти мой ПК") }
                                TextButton(onClick = { showServerField = false }) { Text("Скрыть") }
                            }
                            if (searching) {
                                Text(
                                    "Ищу сервер OLI в вашей сети…",
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    modifier = Modifier.padding(top = 4.dp)
                                )
                            }
                            Text(
                                "Адрес показан в OLI на компьютере: «Пульт админа» → адрес для телефона. " +
                                    "Телефон и ПК должны быть в одной Wi-Fi-сети.",
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(top = 4.dp)
                            )
                        } else {
                            TextButton(onClick = { showServerField = false }) { Text("Скрыть") }
                        }
                    }
                }
            } else {
                TextButton(onClick = { showServerField = true }) { Text("Настроить сервер") }
            }

            Text(
                "Каждому пользователю — 1 ГБ личного пространства для файлов и работы ИИ.",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(top = 14.dp)
            )

            if (defaultServerOffline && !showServerField) {
                Text(
                    "Сервер OLI недоступен — можно посмотреть интерфейс или подключить свой ПК.",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.error,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.padding(top = 6.dp)
                )
            }
        }
    }
}

@Composable
private fun ServerBadge(
    checking: Boolean,
    health: ServerHealth?,
    ownServer: Boolean,
    address: String,
    onClick: () -> Unit
) {
    val (dot, text) = when {
        checking -> MaterialTheme.colorScheme.onSurfaceVariant to "Подключаюсь к серверу…"
        health != null && ownServer -> MaterialTheme.colorScheme.secondary to "Свой ПК · подключено"
        health != null -> MaterialTheme.colorScheme.secondary to "Сервер OLI · подключено"
        else -> MaterialTheme.colorScheme.error to "Сервер не отвечает"
    }

    Card(
        onClick = onClick,
        shape = RoundedCornerShape(14.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
        modifier = Modifier.fillMaxWidth()
    ) {
        Row(Modifier.padding(horizontal = 14.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(9.dp).background(dot, RoundedCornerShape(50)))
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Text(text, style = MaterialTheme.typography.labelMedium)
                if (address.isNotBlank()) {
                    Text(
                        address.replace("http://", "").replace("https://", ""),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
            if (checking) CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
            else Text("изменить", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary)
        }
    }
}
