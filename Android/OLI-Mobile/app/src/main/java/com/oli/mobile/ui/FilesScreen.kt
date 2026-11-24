package com.oli.mobile.ui

import android.content.Intent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.background
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
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Archive
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Upload
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.oli.mobile.data.FileEntry
import com.oli.mobile.data.OliApi
import com.oli.mobile.data.OliRepository
import com.oli.mobile.data.Usage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

@Composable
fun FilesScreen(
    repo: OliRepository,
    usage: Usage?,
    demo: Boolean,
    path: String,
    onPathChange: (String) -> Unit,
    onBack: () -> Unit
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var entries by remember { mutableStateOf<List<FileEntry>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    var message by remember { mutableStateOf<String?>(null) }
    var pendingDelete by remember { mutableStateOf<FileEntry?>(null) }

    suspend fun reload() {
        loading = true
        repo.loadFiles(path).onFailure { message = it.message }.onSuccess { entries = it }
        loading = false
    }

    LaunchedEffect(path) { reload() }

    // Upload: read the picked document and stream it to the server.
    val pickDocument = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch {
            loading = true
            val result = runCatching {
                withContext(Dispatchers.IO) {
                    val resolver = context.contentResolver
                    val name = resolveName(context, uri) ?: "upload.bin"
                    val mime = resolver.getType(uri) ?: "application/octet-stream"
                    val size = resolver.openAssetFileDescriptor(uri, "r")?.use { it.length } ?: 0L
                    repo.upload(path, name, mime, size) { resolver.openInputStream(uri)!! }.getOrThrow()
                }
            }
            message = result.getOrElse { "Ошибка загрузки: ${it.message}" }
            reload()
        }
    }

    suspend fun saveToDevice(entry: FileEntry) {
        val targetDir = File(context.getExternalFilesDir(null), "downloads").apply { mkdirs() }
        val target = File(targetDir, entry.name)
        val result = repo.download(entry.path) { input ->
            target.outputStream().use { output -> input.copyTo(output) }
            target.length()
        }
        message = result.fold(
            onSuccess = { "Скачано: ${target.absolutePath}" },
            onFailure = { "Ошибка: ${it.message}" }
        )
    }

    Column(
        Modifier
            .fillMaxSize()
            .background(OliInk)
            .statusBarsPadding()
    ) {
        // --- header + breadcrumbs
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = {
                if (path.isEmpty()) onBack()
                else onPathChange(path.substringBeforeLast('/', ""))
            }) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Назад")
            }
            Column(Modifier.weight(1f)) {
                Text("Хранилище", style = MaterialTheme.typography.titleMedium)
                Text(
                    "/" + path,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            IconButton(
                enabled = !demo,
                onClick = {
                    scope.launch {
                        val name = "folder-" + System.currentTimeMillis().toString().takeLast(6)
                        repo.makeDir(if (path.isEmpty()) name else "$path/$name")
                        reload()
                    }
                }
            ) { Icon(Icons.Filled.Folder, contentDescription = "Создать папку") }
            IconButton(enabled = !demo, onClick = { pickDocument.launch(arrayOf("*/*")) }) {
                Icon(Icons.Filled.Upload, contentDescription = "Загрузить файл")
            }
        }

        if (demo) {
            Surface(
                color = MaterialTheme.colorScheme.secondaryContainer,
                shape = RoundedCornerShape(12.dp),
                modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp)
            ) {
                Text(
                    "👁 Режим просмотра: здесь только примеры файлов, загрузка и удаление недоступны.",
                    style = MaterialTheme.typography.labelMedium,
                    modifier = Modifier.padding(10.dp)
                )
            }
        }

        // --- quota bar
        Card(
            shape = RoundedCornerShape(16.dp),
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
            modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp)
        ) {
            Column(Modifier.padding(12.dp)) {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text("Ваше место", style = MaterialTheme.typography.labelMedium)
                    Text(
                        if (demo) "1 ГБ в аккаунте"
                        else "${OliApi.prettySize(usage?.used ?: 0)} из ${OliApi.prettySize(usage?.quota ?: 0)}",
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                Spacer(Modifier.height(6.dp))
                val fraction = (((usage?.used ?: 0L).toFloat()) / ((usage?.quota ?: 1L).coerceAtLeast(1L))).coerceIn(0f, 1f)
                LinearProgressIndicator(
                    progress = { fraction },
                    modifier = Modifier.fillMaxWidth().height(6.dp)
                )
            }
        }

        if (loading) {
            LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 8.dp))
        }

        message?.let {
            Surface(
                color = MaterialTheme.colorScheme.secondaryContainer,
                shape = RoundedCornerShape(12.dp),
                modifier = Modifier.fillMaxWidth().padding(12.dp)
            ) {
                Row(Modifier.padding(10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(it, style = MaterialTheme.typography.labelMedium, modifier = Modifier.weight(1f))
                    TextButton(onClick = { message = null }) { Text("OK") }
                }
            }
        }

        if (entries.isEmpty() && !loading) {
            Box(Modifier.fillMaxSize().padding(32.dp), contentAlignment = Alignment.Center) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text("Пусто", style = MaterialTheme.typography.titleMedium)
                    Spacer(Modifier.height(6.dp))
                    Text(
                        "Загрузите файл кнопкой ⬆ или попросите OLI сохранить что-нибудь в чате.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
        } else {
            LazyColumn(Modifier.fillMaxSize().padding(horizontal = 8.dp)) {
                items(entries, key = { it.path }) { entry ->
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .clickable {
                                if (entry.isDirectory) onPathChange(entry.path)
                                else scope.launch { saveToDevice(entry) }
                            }
                            .padding(vertical = 10.dp, horizontal = 6.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Icon(
                            when {
                                entry.isDirectory -> Icons.Filled.Folder
                                entry.name.lowercase().endsWith(".png") ||
                                    entry.name.lowercase().endsWith(".jpg") ||
                                    entry.name.lowercase().endsWith(".jpeg") ||
                                    entry.name.lowercase().endsWith(".webp") -> Icons.Filled.Image
                                else -> Icons.Filled.Description
                            },
                            contentDescription = null,
                            tint = if (entry.isDirectory) MaterialTheme.colorScheme.primary
                            else MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.size(26.dp)
                        )
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f)) {
                            Text(entry.name, fontWeight = FontWeight.Medium, fontSize = 15.sp)
                            Text(
                                if (entry.isDirectory) "папка" else "${OliApi.prettySize(entry.size)} · ${entry.modified}",
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        if (!entry.isDirectory) {
                            IconButton(onClick = { scope.launch { saveToDevice(entry) } }) {
                                Icon(Icons.Filled.Download, contentDescription = "Скачать")
                            }
                        }
                        IconButton(onClick = { pendingDelete = entry }) {
                            Icon(Icons.Filled.Delete, contentDescription = "Удалить",
                                tint = MaterialTheme.colorScheme.error)
                        }
                    }
                }
                item {
                    Row(
                        Modifier.fillMaxWidth().padding(vertical = 14.dp),
                        horizontalArrangement = Arrangement.Center
                    ) {
                        TextButton(onClick = {
                            scope.launch {
                                loading = true
                                val targetDir = File(context.getExternalFilesDir(null), "downloads").apply { mkdirs() }
                                val target = File(targetDir, "workspace-${System.currentTimeMillis()}.zip")
                                val result = repo.downloadArchive("workspace.zip") { input ->
                                    target.outputStream().use { output -> input.copyTo(output) }
                                    target.length()
                                }
                                message = result.fold(
                                    onSuccess = { "Архив сохранён: ${target.absolutePath} (${OliApi.prettySize(it)})" },
                                    onFailure = { "Ошибка: ${it.message}" }
                                )
                                loading = false
                            }
                        }) {
                            Icon(Icons.Filled.Archive, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(6.dp))
                            Text("Скачать всё архивом")
                        }
                    }
                }
            }
        }
    }

    pendingDelete?.let { entry ->
        AlertDialog(
            onDismissRequest = { pendingDelete = null },
            title = { Text("Удалить?") },
            text = { Text("«${entry.name}» будет удалён с сервера без возможности восстановить.") },
            confirmButton = {
                TextButton(onClick = {
                    scope.launch {
                        pendingDelete = null
                        repo.deleteFile(entry.path).onFailure { message = it.message }
                        reload()
                    }
                }) { Text("Удалить", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { pendingDelete = null }) { Text("Отмена") } }
        )
    }
}

private fun resolveName(context: android.content.Context, uri: android.net.Uri): String? {
    val projection = arrayOf(android.provider.OpenableColumns.DISPLAY_NAME)
    context.contentResolver.query(uri, projection, null, null, null)?.use { cursor ->
        if (cursor.moveToFirst()) {
            val index = cursor.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
            if (index >= 0) return cursor.getString(index)
        }
    }
    return uri.lastPathSegment
}
