package com.oli.mobile.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

/**
 * Finds the OLI server by listening to its UDP beacon (port 52345).
 *
 * Why this replaces subnet scanning: the beacon carries the real port, so a PC running on
 * 6767 is found just as fast as one on 8765, and the whole search takes ~2 seconds instead
 * of probing 254 addresses. Scanning stays as a fallback for networks that drop broadcasts.
 *
 * Protocol: "OLI-BEACON/2|port|name|version", broadcast every 2 s by OLI on the PC.
 */
object BeaconScanner {

    private const val MAGIC = "OLI-BEACON/2"
    private const val PORT = 52345

    data class Found(val url: String, val name: String, val version: String)

    /** Listens for up to [timeoutMs]; returns every distinct server seen, best first. */
    suspend fun find(timeoutMs: Long = 2500): List<Found> = withContext(Dispatchers.IO) {
        val found = LinkedHashMap<String, Found>()
        var socket: DatagramSocket? = null
        try {
            socket = DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(java.net.InetSocketAddress(PORT))
                soTimeout = 600
            }
            val deadline = System.currentTimeMillis() + timeoutMs
            val buffer = ByteArray(512)
            while (System.currentTimeMillis() < deadline) {
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(packet)
                } catch (_: SocketTimeoutException) {
                    continue
                }
                val text = String(packet.data, 0, packet.length, Charsets.UTF_8).trimEnd('\u0000')
                val parts = text.split('|')
                if (parts.size < 2 || parts[0] != MAGIC) continue
                val port = parts[1].toIntOrNull() ?: continue
                if (port !in 1..65535) continue
                val address = packet.address?.hostAddress ?: continue
                if (address.startsWith("0.") || address == "255.255.255.255") continue
                val url = "http://$address:$port"
                if (found.containsKey(url)) continue
                found[url] = Found(url, parts.getOrElse(2) { "" }, parts.getOrElse(3) { "" })
            }
        } catch (_: Exception) {
            // Broadcast may be blocked (some enterprise Wi-Fi): the caller falls back.
        } finally {
            try { socket?.close() } catch (_: Exception) {}
        }
        found.values.toList()
    }
}
