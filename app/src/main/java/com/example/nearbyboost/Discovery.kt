package com.example.nearbyboost

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketException
import java.net.SocketTimeoutException

data class DiscoveredDevice(val deviceId: String, val name: String, val ip: String, val port: Int)

/**
 * Lightweight LAN discovery over UDP, separate from the TCP transfer protocol.
 * A device in Receive mode runs [runResponder]; a device about to send calls [scan]
 * to find it. Requires both devices to already be on the same WiFi/hotspot network —
 * this does not set up that network connection itself.
 */
object Discovery {
    const val DISCOVERY_PORT = 52526
    private const val REQUEST_MAGIC = "NEARBYBOOST_DISCOVER"
    private const val RESPONSE_MAGIC = "NEARBYBOOST_HERE"

    /** Blocking — call from a background thread. Answers discovery broadcasts until [isActive] returns false. */
    fun runResponder(tcpPort: Int, deviceId: String, deviceName: String, isActive: () -> Boolean) {
        try {
            DatagramSocket(DISCOVERY_PORT).use { socket ->
                socket.soTimeout = 1000
                socket.broadcast = true
                val buffer = ByteArray(512)
                while (isActive()) {
                    val packet = DatagramPacket(buffer, buffer.size)
                    try {
                        socket.receive(packet)
                    } catch (e: SocketTimeoutException) {
                        continue
                    }
                    val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
                    if (text == REQUEST_MAGIC) {
                        val response = "$RESPONSE_MAGIC|$deviceId|$deviceName|$tcpPort".toByteArray(Charsets.UTF_8)
                        socket.send(DatagramPacket(response, response.size, packet.address, packet.port))
                    }
                }
            }
        } catch (e: SocketException) {
            // socket closed or port busy — responder simply stops
        }
    }

    /** Blocking — call from a background thread. Broadcasts a discovery request and collects replies for [timeoutMs]. */
    fun scan(timeoutMs: Int = 2000): List<DiscoveredDevice> {
        val found = LinkedHashMap<String, DiscoveredDevice>()
        try {
            DatagramSocket().use { socket ->
                socket.broadcast = true
                socket.soTimeout = 300

                val requestBytes = REQUEST_MAGIC.toByteArray(Charsets.UTF_8)
                val broadcastAddresses = getBroadcastAddresses()
                val deadline = System.currentTimeMillis() + timeoutMs

                while (System.currentTimeMillis() < deadline) {
                    for (addr in broadcastAddresses) {
                        try {
                            socket.send(DatagramPacket(requestBytes, requestBytes.size, addr, DISCOVERY_PORT))
                        } catch (e: Exception) { /* try the next address */ }
                    }
                    try {
                        val buffer = ByteArray(512)
                        val packet = DatagramPacket(buffer, buffer.size)
                        socket.receive(packet)
                        val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
                        val parts = text.split("|")
                        if (parts.size == 4 && parts[0] == RESPONSE_MAGIC) {
                            val ip = packet.address.hostAddress ?: continue
                            found[parts[1]] = DiscoveredDevice(parts[1], parts[2], ip, parts[3].toIntOrNull() ?: MainActivity.PORT)
                        }
                    } catch (e: SocketTimeoutException) {
                        // no reply this tick, keep looping until the deadline
                    }
                }
            }
        } catch (e: Exception) {
            // return whatever was found before the failure
        }
        return found.values.toList()
    }

    private fun getBroadcastAddresses(): List<InetAddress> {
        val result = mutableListOf<InetAddress>()
        try {
            val interfaces = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
            for (iface in interfaces) {
                if (!iface.isUp || iface.isLoopback) continue
                for (ifaceAddr in iface.interfaceAddresses) {
                    ifaceAddr.broadcast?.let { result.add(it) }
                }
            }
        } catch (e: Exception) { /* ignore */ }
        if (result.isEmpty()) {
            try { result.add(InetAddress.getByName("255.255.255.255")) } catch (e: Exception) { /* ignore */ }
        }
        return result
    }
}
