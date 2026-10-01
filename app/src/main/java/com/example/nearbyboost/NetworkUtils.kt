package com.example.nearbyboost

import java.net.Inet4Address
import java.net.NetworkInterface

object NetworkUtils {
    /**
     * This device's local IPv4 address on its WiFi/hotspot interface, or null if none
     * could be found. Works whether this device is a WiFi client connected to a router,
     * or is itself acting as a hotspot (access point) — WifiManager.connectionInfo only
     * covers the former.
     */
    fun getLocalIPv4(): String? {
        return try {
            val interfaces = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
            val up = interfaces.filter { it.isUp && !it.isLoopback }

            // Prefer WiFi/hotspot-looking interfaces over mobile data or other interfaces.
            val preferred = up.filter { it.name.contains("wlan", true) || it.name.contains("ap", true) }
            val candidates = preferred.ifEmpty { up }

            for (iface in candidates) {
                val addr = iface.inetAddresses?.toList().orEmpty()
                    .firstOrNull { it is Inet4Address && !it.isLoopbackAddress }
                if (addr != null) return addr.hostAddress
            }
            null
        } catch (e: Exception) {
            null
        }
    }
}
