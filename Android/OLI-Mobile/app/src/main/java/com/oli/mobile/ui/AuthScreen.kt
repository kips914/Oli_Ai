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
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
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
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.oli.mobile.BuildConfig
import com.oli.mobile.data.OliApi
import com.oli.mobile.data.OliRepository
import kotlinx.coroutines.launch

/**
 * Sign-in / registration against the OLI server running on the user's PC.
 * The password is sent once, then kept only in the Keystore-encrypted store.
 */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
fun AuthScreen(repo: OliRepository) {
    var registering by remember { mutableStateOf(false) }
    var server by remember { mutableStateOf(repo.serverUrl.ifBlank { BuildConfig.DEFAULT_SERVER }) }
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
    var advanced by remember { mutableStateOf(server.isBlank() || registering) }
    val scope = rememberCoroutineScope()

    fun submit() {
        error = null
        info = null
        if (server.isBlank()) { error = "Укажите адрес сервера, например 192.168.1.10:8765"; return }
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
                    repo.register(server, userName.trim(), password, displayName.trim(), invite.trim(), remember)
                } else {
                    repo.login(server, userName.trim(), password, remember)
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
            .background(
                Brush.verticalGradient(
                    listOf(
                        MaterialTheme.colorScheme.background,
                        MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.55f),
                        MaterialTheme.colorScheme.background
                    )
                )
            )
    ) {
        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .imePadding()
                .padding(horizontal = 22.dp, vertical = 36.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text("OLI", style = MaterialTheme.typography.headlineLarge, color = MaterialTheme.colorScheme.primary)
            Text(
                "Ваш ИИ на вашем компьютере",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 4.dp, bottom = 24.dp)
            )

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
                            onClick = { registering = true; error = null; advanced = true },
                            shape = SegmentedButtonDefaults.itemShape(index = 1, count = 2)
                        ) { Text("Регистрация") }
                    }

                    Spacer(Modifier.height(18.dp))

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
                        Modifier.fillMaxWidth().padding(top = 6.dp),
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
                            "На сервере остаётся только хеш — администратор не может его прочитать.",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )

                    TextButton(onClick = { advanced = !advanced }, modifier = Modifier.padding(top = 6.dp)) {
                        Text(if (advanced) "Скрыть адрес сервера" else "Указать адрес сервера")
                    }

                    if (advanced) {
                        OutlinedTextField(
                            value = server,
                            onValueChange = { server = it },
                            label = { Text("Сервер (IP компьютера:порт)") },
                            placeholder = { Text("192.168.1.10:8765") },
                            singleLine = true,
                            enabled = !busy,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Done),
                            modifier = Modifier.fillMaxWidth()
                        )
                        Text(
                            "Адрес показан в OLI на компьютере: «Пульт админа» → адрес для телефона.",
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(top = 4.dp)
                        )
                    }

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
                                modifier = Modifier.width(20.dp).height(20.dp),
                                strokeWidth = 2.dp,
                                color = MaterialTheme.colorScheme.onPrimary
                            )
                            Spacer(Modifier.width(10.dp))
                        }
                        Text(if (registering) "Создать аккаунт" else "Войти", style = MaterialTheme.typography.titleMedium)
                    }

                    TextButton(
                        onClick = {
                            if (server.isBlank()) { error = "Сначала укажите адрес сервера"; return@TextButton }
                            busy = true
                            error = null
                            scope.launch {
                                try {
                                    val health = repo.setServer(server)
                                    info = "Сервер доступен · версия ${health.version.ifBlank { "?" }} · " +
                                        if (health.registration) "регистрация открыта" else "регистрация закрыта"
                                } catch (e: Exception) {
                                    error = e.message
                                } finally {
                                    busy = false
                                }
                            }
                        },
                        modifier = Modifier.fillMaxWidth()
                    ) { Text("Проверить соединение", textAlign = TextAlign.Center) }
                }
            }

            Text(
                "Каждому пользователю — 1 ГБ личного пространства для файлов и работы ИИ.",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(top = 20.dp)
            )
        }
    }
}
