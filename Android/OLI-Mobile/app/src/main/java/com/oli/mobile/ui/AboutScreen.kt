package com.oli.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.oli.mobile.BuildConfig

/** "Возможности" — a single place that explains what the app can do. */
@Composable
fun AboutScreen(onBack: () -> Unit) {
    val features = listOf(
        Triple("💬", "Чат без подписок", "Olim Pass и Olim Son работают на вашем ПК — запросы не уходят в чужое облако."),
        Triple("📁", "1 ГБ файлов", "Загрузка, просмотр и скачивание файлов в личном пространстве; квота видна на экране «Файлы»."),
        Triple("🗂", "История чатов", "Все разговоры сохраняются на сервере и открываются из меню слева."),
        Triple("⚡", "Модели", "Pass — максимум качества, Son — самая лёгкая. Выбор в верхней панели."),
        Triple("📊", "Лимиты по-честному", "Считаются токены «вопрос + ответ». Чем больше людей онлайн, тем меньше лимит на каждого."),
        Triple("🔒", "Приватность", "Пароль хранится только как хеш на сервере и зашифрован на телефоне (Keystore)."),
        Triple("🧠", "Свои файлы в ответах", "OLI читает документы из вашего workspace и складывает туда результаты."),
        Triple("🛡", "Безопасность", "Файлы изолированы по аккаунтам, команды на ПК для пользователей сервера выключены.")
    )

    Column(
        Modifier.fillMaxSize().background(OliInk).statusBarsPadding()
    ) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 10.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(Modifier.size(40.dp).clickable(onClick = onBack), contentAlignment = Alignment.Center) {
                Text("‹", style = MaterialTheme.typography.headlineSmall, color = OliText)
            }
            Spacer(Modifier.width(4.dp))
            OliMark(size = 26.dp)
            Spacer(Modifier.width(10.dp))
            Text("Возможности OLI", style = MaterialTheme.typography.titleMedium, color = OliText)
        }

        Column(
            Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp)
        ) {
            Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                OliMark(size = 96.dp)
            }
            Spacer(Modifier.height(4.dp))
            Text(
                "OLI Mobile ${BuildConfig.VERSION_NAME}",
                style = MaterialTheme.typography.labelMedium,
                color = OliTextMuted,
                modifier = Modifier.fillMaxWidth()
            )

            features.forEach { (icon, title, text) ->
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(16.dp))
                        .background(OliGlass)
                        .padding(14.dp),
                    verticalAlignment = Alignment.Top
                ) {
                    Text(icon)
                    Spacer(Modifier.width(12.dp))
                    Column {
                        Text(title, style = MaterialTheme.typography.bodyLarge, color = OliText,
                            fontWeight = FontWeight.SemiBold)
                        Text(text, style = MaterialTheme.typography.labelSmall, color = OliTextMuted)
                    }
                }
            }

            Spacer(Modifier.height(24.dp))
        }
    }
}
