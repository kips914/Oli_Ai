package com.oli.mobile.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Находит сервер OLI в той же локальной сети, что и телефон.
 *
 * Зачем: раньше в приложении нужно было вручную вписать IP компьютера, и если он
 * менялся (или пользователь не знал его), «сервер не виден». Теперь приложение само
 * проходит по своей подсети, опрашивая `GET /api/health` на порту 8765, и подключается
 * к первому ответившему серверу OLI.
 *
 * Сканирование идёт параллельно (64 соединения за раз, таймаут ~350 мс на адрес),
 * поэтому вся подсеть проверяется за 1–3 секунды и не нагружает батарею.
 */
object LanScanner {

    private val VIRTUAL_KEYS = listOf(
        "veth", "vmware", "virtualbox", "hyper-v", "loopback", "tunnel", "tap", "tailscale",
        "zerotier", "wireguard", "openvpn", "nordlynx", "radmin", "hamachi", "docker", "wsl", "vpn"
    )

    /** /24-префиксы всех своих Wi-Fi/LTE адресов, реальные интерфейсы — первыми. */
    fun localPrefixes(): List<String> = runCatching {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { runCatching { it.isUp }.getOrDefault(false) }
            .filter { runCatching { !it.isLoopback }.getOrDefault(false) }
            .flatMap { nic ->
                val name = (nic.displayName ?: nic.name ?: "").lowercase()
                val virtual = VIRTUAL_KEYS.any { name.contains(it) }
                nic.inetAddresses.toList()
                    .filterIsInstance<Inet4Address>()
                    .filter { !it.isLoopbackAddress && !it.isLinkLocalAddress }
                    .mapNotNull { addr ->
                        val bytes = addr.address
                        if (bytes.size != 4) null
                        else Triple(
                            "${bytes[0].toInt() and 0xff}.${bytes[1].toInt() and 0xff}.${bytes[2].toInt() and 0xff}.",
                            virtual,
                            addr.hostAddress ?: ""
                        )
                    }
            }
            .sortedBy { it.second } // не виртуальные интерфейсы первыми
            .map { it.first }
            .distinct()
    }.getOrElse { emptyList() }

    /**
     * Ищет сервер OLI: сначала в своей подсети, затем (если не нашёлся) в подсетях
     * виртуальных адаптеров (VPN, Hyper-V). Возвращает `http://ip:порт` или null.
     */
    suspend fun findServer(
        port: Int = 8765,
        timeoutMs: Int = 350,
        overallTimeoutMs: Long = 12_000,
    ): String? = withContext(Dispatchers.IO) {
        val prefixes = localPrefixes()
        if (prefixes.isEmpty()) return@withContext null

        for (prefix in prefixes.take(2)) {
            val found = withTimeoutOrNull(overallTimeoutMs) { scanPrefix(prefix, port, timeoutMs) }
            if (found != null) return@withContext found
        }
        null
    }

    private suspend fun scanPrefix(prefix: String, port: Int, timeoutMs: Int): String? = coroutineScope {
        val hit = AtomicBoolean(false)
        val jobs = (1..254).map { host ->
            async(Dispatchers.IO) {
                if (hit.get()) return@async null
                val ip = prefix + host
                val ok = probe(ip, port, timeoutMs)
                if (ok && hit.compareAndSet(false, true)) "http://$ip:$port" else null
            }
        }
        jobs.awaitAll().firstOrNull { it != null }
    }

    /** Быстрая проверка: принимает ли адрес TCP-соединение на порту сервера. */
    private fun probe(ip: String, port: Int, timeoutMs: Int): Boolean = runCatching {
        Socket().use { socket ->
            socket.connect(InetSocketAddress(ip, port), timeoutMs)
            true
        }
    }.getOrDefault(false)
}
