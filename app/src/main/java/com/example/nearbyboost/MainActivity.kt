package com.example.nearbyboost

import android.Manifest
import android.app.DownloadManager
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.OpenableColumns
import android.text.TextUtils
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.GridLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.app.AppCompatDelegate
import androidx.appcompat.widget.SwitchCompat
import androidx.core.content.ContextCompat
import androidx.documentfile.provider.DocumentFile
import androidx.lifecycle.lifecycleScope
import com.google.android.material.appbar.MaterialToolbar
import com.google.android.material.bottomnavigation.BottomNavigationView
import com.google.android.material.button.MaterialButton
import com.google.android.material.button.MaterialButtonToggleGroup
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.google.android.material.progressindicator.LinearProgressIndicator
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.io.BufferedOutputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.text.SimpleDateFormat
import java.util.Locale
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.max
import kotlin.math.min

class MainActivity : AppCompatActivity(), TransferServer.Listener {

    companion object {
        const val PORT = 52525
        private const val PREFS_NAME = "nearbyboost_prefs"
        private const val KEY_THEME = "theme_mode"
        private const val KEY_DEVICE_ID = "device_id"
        private const val KEY_DISPLAY_NAME = "display_name"
        private const val KEY_AVATAR_INDEX = "avatar_index"
        private const val KEY_PARALLEL_ENABLED = "parallel_enabled"
        private const val KEY_PAIRED_DEVICES = "paired_devices"
        private const val KEY_HISTORY = "history_entries"
        private const val REQUEST_TIMEOUT_SECONDS = 30L
        private const val BUFFER_SIZE = 1 shl 20 // 1 MB
        private const val MAX_PARALLEL_STREAMS = 4
    }

    // Navigation
    private lateinit var toolbar: MaterialToolbar
    private lateinit var bottomNav: BottomNavigationView
    private lateinit var transferPage: View
    private lateinit var devicesPage: View
    private lateinit var historyPage: View
    private lateinit var settingsPage: View

    // Transfer page
    private lateinit var modeToggleGroup: MaterialButtonToggleGroup
    private lateinit var receivePanel: View
    private lateinit var sendPanel: View
    private lateinit var statusText: TextView
    private lateinit var stopTransferButton: MaterialButton
    private lateinit var ipText: TextView
    private lateinit var progressBar: LinearProgressIndicator
    private lateinit var progressText: TextView
    private lateinit var fileProgressContainer: LinearLayout
    private lateinit var toggleButton: MaterialButton
    private lateinit var copyButton: MaterialButton
    private lateinit var targetIpInput: EditText
    private lateinit var targetPortInput: EditText
    private lateinit var queueEmptyText: TextView
    private lateinit var queueContainer: LinearLayout
    private lateinit var devicesContainer: LinearLayout
    private lateinit var pickFileButton: MaterialButton
    private lateinit var pickFolderButton: MaterialButton
    private lateinit var scanButton: MaterialButton
    private lateinit var sendButton: MaterialButton

    // Devices page
    private lateinit var scanRippleContainer: FrameLayout
    private lateinit var rippleCircle1: View
    private lateinit var rippleCircle2: View
    private lateinit var rippleCircle3: View
    private lateinit var scanCenterAvatarFrame: FrameLayout
    private lateinit var devicesScanButton: MaterialButton
    private lateinit var nearbyEmptyText: TextView
    private lateinit var nearbyContainer: LinearLayout
    private lateinit var pairedEmptyText: TextView
    private lateinit var pairedDevicesContainer: LinearLayout
    private var rippleAnimators: List<android.animation.Animator> = emptyList()

    // History page
    private lateinit var historyEmptyText: TextView
    private lateinit var historyListContainer: LinearLayout
    private lateinit var openDownloadsButton: MaterialButton

    // Settings page
    private lateinit var settingsAvatarFrame: FrameLayout
    private lateinit var settingsNameInput: EditText
    private lateinit var chooseAvatarButton: MaterialButton
    private lateinit var saveIdentityButton: MaterialButton
    private lateinit var settingsThemeToggle: MaterialButtonToggleGroup
    private lateinit var themeLightBtn: MaterialButton
    private lateinit var themeDarkBtn: MaterialButton
    private lateinit var themeSystemBtn: MaterialButton
    private lateinit var parallelTransferSwitch: SwitchCompat

    private var server: TransferServer? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var running = false
    private var serverTransitioning = false

    private data class QueueItem(val uri: Uri, val isFolder: Boolean, val displayName: String)
    private data class BatchFileEntry(val uri: Uri, val relativePath: String, val size: Long)
    private data class KnownDevice(val deviceId: String, val name: String, val ip: String, val port: Int, val lastSeen: Long)
    private data class HistoryEntry(
        val time: String, val direction: String, val name: String,
        val peer: String, val size: String, val avgSpeed: String, val status: String
    )

    private val sendQueue = mutableListOf<QueueItem>()
    private val pairedDevices = mutableListOf<KnownDevice>()
    private val nearbyDevices = mutableListOf<DiscoveredDevice>()
    private val historyEntries = mutableListOf<HistoryEntry>()
    private var sendCancelFlag: java.util.concurrent.atomic.AtomicBoolean? = null
    private val fileProgressRows = mutableMapOf<String, Pair<LinearProgressIndicator, TextView>>()

    private val timeFormat = SimpleDateFormat("HH:mm:ss", Locale.getDefault())

    private val notificationPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { /* best effort */ }

    private val pickFileLauncher = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        for (uri in uris) {
            sendQueue.add(QueueItem(uri, false, queryDisplayName(uri) ?: "file"))
        }
        refreshQueueUi()
    }

    private val pickFolderLauncher = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) {
            contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)
            sendQueue.add(QueueItem(uri, true, DocumentFile.fromTreeUri(this, uri)?.name ?: "folder"))
            refreshQueueUi()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        applySavedTheme()
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        bindViews()
        wireNavigation()
        wireTransferPage()
        wireDevicesPage()
        wireHistoryPage()
        wireSettingsPage()

        ipText.text = "${getLocalIpAddress()}:$PORT"
        statusText.text = "Stopped"

        ensureDisplayNameSet()
        refreshIdentityUi()
        pairedDevices.clear()
        pairedDevices.addAll(loadPairedDevices())
        refreshPairedRows()
        historyEntries.clear()
        historyEntries.addAll(loadHistory())
        refreshHistoryUi()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED
            ) {
                notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
    }

    private fun bindViews() {
        toolbar = findViewById(R.id.toolbar)
        bottomNav = findViewById(R.id.bottomNav)
        transferPage = findViewById(R.id.transferPage)
        devicesPage = findViewById(R.id.devicesPage)
        historyPage = findViewById(R.id.historyPage)
        settingsPage = findViewById(R.id.settingsPage)

        modeToggleGroup = findViewById(R.id.modeToggleGroup)
        receivePanel = findViewById(R.id.receivePanel)
        sendPanel = findViewById(R.id.sendPanel)
        statusText = findViewById(R.id.statusText)
        stopTransferButton = findViewById(R.id.stopTransferButton)
        ipText = findViewById(R.id.ipText)
        progressBar = findViewById(R.id.progressBar)
        progressText = findViewById(R.id.progressText)
        fileProgressContainer = findViewById(R.id.fileProgressContainer)
        toggleButton = findViewById(R.id.toggleButton)
        copyButton = findViewById(R.id.copyButton)
        targetIpInput = findViewById(R.id.targetIpInput)
        targetPortInput = findViewById(R.id.targetPortInput)
        queueEmptyText = findViewById(R.id.queueEmptyText)
        queueContainer = findViewById(R.id.queueContainer)
        devicesContainer = findViewById(R.id.devicesContainer)
        pickFileButton = findViewById(R.id.pickFileButton)
        pickFolderButton = findViewById(R.id.pickFolderButton)
        scanButton = findViewById(R.id.scanButton)
        sendButton = findViewById(R.id.sendButton)

        scanRippleContainer = findViewById(R.id.scanRippleContainer)
        rippleCircle1 = findViewById(R.id.rippleCircle1)
        rippleCircle2 = findViewById(R.id.rippleCircle2)
        rippleCircle3 = findViewById(R.id.rippleCircle3)
        scanCenterAvatarFrame = findViewById(R.id.scanCenterAvatarFrame)
        devicesScanButton = findViewById(R.id.devicesScanButton)
        nearbyEmptyText = findViewById(R.id.nearbyEmptyText)
        nearbyContainer = findViewById(R.id.nearbyContainer)
        pairedEmptyText = findViewById(R.id.pairedEmptyText)
        pairedDevicesContainer = findViewById(R.id.pairedDevicesContainer)

        historyEmptyText = findViewById(R.id.historyEmptyText)
        historyListContainer = findViewById(R.id.historyListContainer)
        openDownloadsButton = findViewById(R.id.openDownloadsButton)

        settingsAvatarFrame = findViewById(R.id.settingsAvatarFrame)
        settingsNameInput = findViewById(R.id.settingsNameInput)
        chooseAvatarButton = findViewById(R.id.chooseAvatarButton)
        saveIdentityButton = findViewById(R.id.saveIdentityButton)
        settingsThemeToggle = findViewById(R.id.settingsThemeToggle)
        themeLightBtn = findViewById(R.id.themeLightBtn)
        themeDarkBtn = findViewById(R.id.themeDarkBtn)
        themeSystemBtn = findViewById(R.id.themeSystemBtn)
        parallelTransferSwitch = findViewById(R.id.parallelTransferSwitch)
    }

    private fun wireNavigation() {
        bottomNav.setOnItemSelectedListener { item ->
            transferPage.visibility = View.GONE
            devicesPage.visibility = View.GONE
            historyPage.visibility = View.GONE
            settingsPage.visibility = View.GONE
            when (item.itemId) {
                R.id.nav_transfer -> transferPage.visibility = View.VISIBLE
                R.id.nav_devices -> {
                    devicesPage.visibility = View.VISIBLE
                    refreshDevicesPageUi()
                }
                R.id.nav_history -> {
                    historyPage.visibility = View.VISIBLE
                    refreshHistoryUi()
                }
                R.id.nav_settings -> settingsPage.visibility = View.VISIBLE
            }
            true
        }
    }

    // ================= TRANSFER PAGE =================

    private fun wireTransferPage() {
        modeToggleGroup.addOnButtonCheckedListener { _, checkedId, isChecked ->
            if (!isChecked) return@addOnButtonCheckedListener
            val showSend = checkedId == R.id.sendModeButton
            sendPanel.visibility = if (showSend) View.VISIBLE else View.GONE
            receivePanel.visibility = if (showSend) View.GONE else View.VISIBLE
        }

        toggleButton.setOnClickListener {
            if (serverTransitioning) return@setOnClickListener
            if (running) stopServer() else startServer()
        }

        copyButton.setOnClickListener {
            val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            clipboard.setPrimaryClip(ClipData.newPlainText("NearbyBoost address", ipText.text))
            Toast.makeText(this, "Copied", Toast.LENGTH_SHORT).show()
        }

        pickFileButton.setOnClickListener { pickFileLauncher.launch(arrayOf("*/*")) }
        pickFolderButton.setOnClickListener { pickFolderLauncher.launch(null) }
        scanButton.setOnClickListener { onScanClicked(fromDevicesPage = false) }
        sendButton.setOnClickListener { onSendClicked() }
        stopTransferButton.setOnClickListener {
            sendCancelFlag?.set(true)
            server?.cancelCurrentTransfer()
        }

        refreshQueueUi()
        refreshPairedRows()
    }

    private fun startServer() {
        serverTransitioning = true
        toggleButton.isEnabled = false
        toggleButton.text = "Starting\u2026"

        val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "nearbyboost:wifi").apply { acquire() }

        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "nearbyboost:wake").apply {
            acquire(30 * 60 * 1000L)
        }

        ipText.text = "${getLocalIpAddress()}:$PORT"

        val newServer = TransferServer(PORT, applicationContext, getOrCreateDeviceId(), currentDisplayName(), this)
        server = newServer
        lifecycleScope.launch(Dispatchers.IO) { newServer.start() }
        lifecycleScope.launch(Dispatchers.IO) {
            Discovery.runResponder(PORT, getOrCreateDeviceId(), currentDisplayName()) { running }
        }

        running = true
        serverTransitioning = false
        toggleButton.isEnabled = true
        toggleButton.text = "Stop Server"
        statusText.text = "Waiting for a connection\u2026"
    }

    private fun stopServer() {
        serverTransitioning = true
        toggleButton.isEnabled = false

        server?.stop()
        server = null
        releaseLocks()
        running = false
        serverTransitioning = false
        toggleButton.isEnabled = true
        toggleButton.text = "Start Server"
        statusText.text = "Stopped"
        TransferForegroundService.stop(this)
    }

    private fun releaseLocks() {
        if (wakeLock?.isHeld == true) wakeLock?.release()
        if (wifiLock?.isHeld == true) wifiLock?.release()
    }

    private fun getLocalIpAddress(): String =
        NetworkUtils.getLocalIPv4() ?: "Unavailable \u2014 check Wi-Fi/hotspot is on"

    private fun getThemeColor(attr: Int): Int {
        val typedValue = TypedValue()
        theme.resolveAttribute(attr, typedValue, true)
        return typedValue.data
    }

    // ================= QUEUE (send picker) =================

    private fun refreshQueueUi() {
        queueContainer.removeAllViews()
        val empty = sendQueue.isEmpty()
        queueEmptyText.visibility = if (empty) View.VISIBLE else View.GONE
        queueContainer.visibility = if (empty) View.GONE else View.VISIBLE
        val density = resources.displayMetrics.density

        for (index in sendQueue.indices) {
            val item = sendQueue[index]
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding((12 * density).toInt(), (8 * density).toInt(), (10 * density).toInt(), (8 * density).toInt())
                background = GradientDrawable().apply {
                    cornerRadius = 12 * density
                    setColor(getThemeColor(com.google.android.material.R.attr.colorSurfaceContainerHigh))
                }
                layoutParams = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    LinearLayout.LayoutParams.WRAP_CONTENT
                ).apply {
                    bottomMargin = (8 * density).toInt()
                }
            }
            val badge = item.let { if (it.isFolder) FileTypeBadge.forFolder() else FileTypeBadge.forFile(it.displayName) }
            row.addView(makeBadgeView(badge))
            val label = TextView(this).apply {
                text = item.displayName
                textSize = 13f
                ellipsize = TextUtils.TruncateAt.MIDDLE
                maxLines = 1
                setTextColor(getThemeColor(com.google.android.material.R.attr.colorOnSurface))
                setPadding((12 * density).toInt(), 0, (8 * density).toInt(), 0)
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            }
            val removeButton = ImageView(this).apply {
                setImageResource(R.drawable.ic_close)
                setColorFilter(getThemeColor(com.google.android.material.R.attr.colorOnSurfaceVariant))
                val size = (26 * density).toInt()
                val p = (5 * density).toInt()
                layoutParams = LinearLayout.LayoutParams(size, size)
                setPadding(p, p, p, p)
                val outValue = TypedValue()
                theme.resolveAttribute(android.R.attr.selectableItemBackgroundBorderless, outValue, true)
                setBackgroundResource(outValue.resourceId)
                isClickable = true
                isFocusable = true
                setOnClickListener {
                    sendQueue.removeAt(index)
                    refreshQueueUi()
                }
            }
            row.addView(label)
            row.addView(removeButton)
            queueContainer.addView(row)
        }
    }

    private fun makeBadgeView(badge: FileTypeBadge.Badge): TextView {
        val density = resources.displayMetrics.density
        val drawable = GradientDrawable().apply {
            shape = GradientDrawable.RECTANGLE
            cornerRadius = 6 * density
            setColor(Color.parseColor(badge.colorHex))
        }
        return TextView(this).apply {
            text = badge.label
            textSize = 10f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(Color.WHITE)
            background = drawable
            setPadding((8 * density).toInt(), (4 * density).toInt(), (8 * density).toInt(), (4 * density).toInt())
        }
    }

    // ================= DEVICES (nearby + paired) =================

    private fun onScanClicked(fromDevicesPage: Boolean) {
        scanButton.isEnabled = false
        devicesScanButton.isEnabled = false
        scanButton.text = "Scanning\u2026"
        devicesScanButton.text = "Scanning\u2026"
        startRippleAnimation()

        lifecycleScope.launch(Dispatchers.IO) {
            val found = Discovery.scan()
            runOnUiThread {
                scanButton.isEnabled = true
                devicesScanButton.isEnabled = true
                scanButton.text = "Scan"
                devicesScanButton.text = "Scan for devices"
                stopRippleAnimation()

                nearbyDevices.clear()
                nearbyDevices.addAll(found)
                refreshNearbyRows()

                if (found.isEmpty()) {
                    Toast.makeText(
                        this@MainActivity,
                        "No devices found \u2014 make sure the other device is on Receive and on the same network",
                        Toast.LENGTH_LONG
                    ).show()
                }
            }
        }
    }

    private fun refreshDevicesPageUi() {
        scanCenterAvatarFrame.removeAllViews()
        val avatar = AvatarCatalog.get(getAvatarIndex())
        renderEmojiAvatar(scanCenterAvatarFrame, avatar.colorHex, avatar.emoji)
        refreshNearbyRows()
        refreshPairedRows()
    }

    private fun refreshNearbyRows() {
        nearbyContainer.removeAllViews()
        nearbyEmptyText.visibility = if (nearbyDevices.isEmpty()) View.VISIBLE else View.GONE
        for (d in nearbyDevices) {
            nearbyContainer.addView(buildDeviceRow(d.deviceId, d.name, d.ip) {
                targetIpInput.setText(d.ip)
                targetPortInput.setText(d.port.toString())
                bottomNav.selectedItemId = R.id.nav_transfer
                modeToggleGroup.check(R.id.sendModeButton)
            })
        }
    }

    private fun refreshPairedRows() {
        devicesContainer.removeAllViews()
        for (d in pairedDevices) {
            devicesContainer.addView(buildDeviceRow(d.deviceId, d.name, d.ip) {
                targetIpInput.setText(d.ip)
                targetPortInput.setText(d.port.toString())
            })
        }
        if (::pairedDevicesContainer.isInitialized) {
            pairedDevicesContainer.removeAllViews()
            pairedEmptyText.visibility = if (pairedDevices.isEmpty()) View.VISIBLE else View.GONE
            for (d in pairedDevices) {
                pairedDevicesContainer.addView(buildDeviceRow(d.deviceId, d.name, d.ip) {
                    targetIpInput.setText(d.ip)
                    targetPortInput.setText(d.port.toString())
                    bottomNav.selectedItemId = R.id.nav_transfer
                    modeToggleGroup.check(R.id.sendModeButton)
                })
            }
        }
    }

    private fun buildDeviceRow(deviceId: String, name: String, ip: String, onClick: () -> Unit): LinearLayout {
        val density = resources.displayMetrics.density
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding((12 * density).toInt(), (10 * density).toInt(), (12 * density).toInt(), (10 * density).toInt())
            val outValue = TypedValue()
            theme.resolveAttribute(android.R.attr.selectableItemBackground, outValue, true)
            setBackgroundResource(outValue.resourceId)
            isClickable = true
            isFocusable = true
            setOnClickListener { onClick() }
        }
        row.addView(makeAvatarView(name, deviceId, 40))
        val textCol = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
                marginStart = (14 * density).toInt()
            }
        }
        textCol.addView(TextView(this).apply {
            text = name
            textSize = 15f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(getThemeColor(com.google.android.material.R.attr.colorOnSurface))
        })
        textCol.addView(TextView(this).apply {
            text = ip
            textSize = 12f
            setTextColor(getThemeColor(com.google.android.material.R.attr.colorOnSurfaceVariant))
            typeface = Typeface.MONOSPACE
        })
        row.addView(textCol)
        val chevron = ImageView(this).apply {
            setImageResource(R.drawable.ic_chevron_right)
            setColorFilter(getThemeColor(com.google.android.material.R.attr.colorOnSurfaceVariant))
            alpha = 0.5f
            val size = (20 * density).toInt()
            layoutParams = LinearLayout.LayoutParams(size, size)
        }
        row.addView(chevron)
        return row
    }

    private fun startRippleAnimation() {
        val circles = listOf(rippleCircle1, rippleCircle2, rippleCircle3)
        rippleAnimators = circles.mapIndexed { index, view ->
            view.visibility = View.VISIBLE
            view.scaleX = 0.3f
            view.scaleY = 0.3f
            view.alpha = 0.6f
            val scaleX = android.animation.ObjectAnimator.ofFloat(view, "scaleX", 0.3f, 1.4f)
            val scaleY = android.animation.ObjectAnimator.ofFloat(view, "scaleY", 0.3f, 1.4f)
            val alphaAnim = android.animation.ObjectAnimator.ofFloat(view, "alpha", 0.6f, 0f)
            val set = android.animation.AnimatorSet()
            set.playTogether(scaleX, scaleY, alphaAnim)
            set.duration = 1800
            set.startDelay = index * 500L
            set.interpolator = android.view.animation.LinearInterpolator()
            set.addListener(object : android.animation.AnimatorListenerAdapter() {
                override fun onAnimationEnd(animation: android.animation.Animator) {
                    if (view.visibility == View.VISIBLE) {
                        view.scaleX = 0.3f; view.scaleY = 0.3f; view.alpha = 0.6f
                        animation.start()
                    }
                }
            })
            set.start()
            set as android.animation.Animator
        }
    }

    private fun stopRippleAnimation() {
        rippleAnimators.forEach { it.cancel() }
        rippleAnimators = emptyList()
        listOf(rippleCircle1, rippleCircle2, rippleCircle3).forEach { it.visibility = View.INVISIBLE }
    }

    private fun renderEmojiAvatar(frame: FrameLayout, colorHex: String, emoji: String) {
        frame.removeAllViews()
        val bg = GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(Color.parseColor(colorHex))
        }
        val tv = TextView(this).apply {
            text = emoji
            textSize = 22f
            gravity = Gravity.CENTER
            background = bg
            layoutParams = FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT)
        }
        frame.addView(tv)
    }

    private fun avatarColorForKey(key: String): Int {
        val palette = intArrayOf(
            0xFF7C5CFC.toInt(), 0xFFFF6B6B.toInt(), 0xFF4ECDC4.toInt(), 0xFFFFD166.toInt(),
            0xFF06D6A0.toInt(), 0xFF118AB2.toInt(), 0xFFEF476F.toInt(), 0xFF8AC926.toInt()
        )
        var hash = 0
        for (c in key) hash = (hash * 31 + c.code) and 0x7FFFFFFF
        return palette[hash % palette.size]
    }

    private fun makeAvatarView(name: String, deviceId: String, sizeDp: Int = 32): TextView {
        val density = resources.displayMetrics.density
        val sizePx = (sizeDp * density).toInt()
        val color = avatarColorForKey(deviceId.ifEmpty { name })
        val drawable = GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(color)
        }
        return TextView(this).apply {
            text = (name.trim().firstOrNull()?.uppercaseChar() ?: '?').toString()
            setTextColor(Color.WHITE)
            textSize = 15f
            gravity = Gravity.CENTER
            background = drawable
            layoutParams = LinearLayout.LayoutParams(sizePx, sizePx)
            setTypeface(typeface, Typeface.BOLD)
        }
    }

    private fun wireDevicesPage() {
        devicesScanButton.setOnClickListener { onScanClicked(fromDevicesPage = true) }
    }

    // ================= PAIRED DEVICE PERSISTENCE =================

    private fun loadPairedDevices(): MutableList<KnownDevice> {
        val raw = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getString(KEY_PAIRED_DEVICES, null)
            ?: return mutableListOf()
        return try {
            val arr = org.json.JSONArray(raw)
            (0 until arr.length()).map { i ->
                val o = arr.getJSONObject(i)
                KnownDevice(o.getString("id"), o.getString("name"), o.getString("ip"), o.getInt("port"), o.optLong("lastSeen"))
            }.toMutableList()
        } catch (e: Exception) {
            mutableListOf()
        }
    }

    private fun savePairedDevices(devices: List<KnownDevice>) {
        val arr = org.json.JSONArray()
        for (d in devices) {
            val o = org.json.JSONObject()
            o.put("id", d.deviceId)
            o.put("name", d.name)
            o.put("ip", d.ip)
            o.put("port", d.port)
            o.put("lastSeen", d.lastSeen)
            arr.put(o)
        }
        getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit().putString(KEY_PAIRED_DEVICES, arr.toString()).apply()
    }

    /** Called only after a transfer actually succeeds — that mutual accept/reject exchange
     *  IS the "permission" that gates pairing. */
    private fun rememberPairedDevice(deviceId: String?, name: String?, ip: String, port: Int) {
        if (deviceId.isNullOrBlank()) return
        val idx = pairedDevices.indexOfFirst { it.deviceId == deviceId }
        val updated = KnownDevice(deviceId, name ?: "Device", ip, port, System.currentTimeMillis())
        if (idx >= 0) pairedDevices[idx] = updated else pairedDevices.add(updated)
        savePairedDevices(pairedDevices)
        refreshPairedRows()
    }

    // ================= HISTORY =================

    private fun loadHistory(): MutableList<HistoryEntry> {
        val raw = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getString(KEY_HISTORY, null)
            ?: return mutableListOf()
        return try {
            val arr = org.json.JSONArray(raw)
            (0 until arr.length()).map { i ->
                val o = arr.getJSONObject(i)
                HistoryEntry(
                    o.getString("time"), o.getString("dir"), o.getString("name"),
                    o.getString("peer"), o.getString("size"), o.getString("speed"), o.getString("status")
                )
            }.toMutableList()
        } catch (e: Exception) {
            mutableListOf()
        }
    }

    private fun saveHistory() {
        val arr = org.json.JSONArray()
        for (h in historyEntries.take(100)) {
            val o = org.json.JSONObject()
            o.put("time", h.time); o.put("dir", h.direction); o.put("name", h.name)
            o.put("peer", h.peer); o.put("size", h.size); o.put("speed", h.avgSpeed); o.put("status", h.status)
            arr.put(o)
        }
        getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit().putString(KEY_HISTORY, arr.toString()).apply()
    }

    private fun addHistoryEntry(direction: String, name: String, peer: String, size: String, speed: String, status: String) {
        historyEntries.add(0, HistoryEntry(timeFormat.format(java.util.Date()), direction, name, peer, size, speed, status))
        if (historyEntries.size > 100) historyEntries.removeAt(historyEntries.size - 1)
        saveHistory()
        refreshHistoryUi()
    }

    private fun refreshHistoryUi() {
        if (!::historyListContainer.isInitialized) return
        historyListContainer.removeAllViews()
        historyEmptyText.visibility = if (historyEntries.isEmpty()) View.VISIBLE else View.GONE
        val density = resources.displayMetrics.density

        for (entry in historyEntries) {
            val card = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding((16 * density).toInt(), (14 * density).toInt(), (16 * density).toInt(), (14 * density).toInt())
                background = GradientDrawable().apply {
                    cornerRadius = 16 * density
                    setColor(getThemeColor(com.google.android.material.R.attr.colorSurfaceContainer))
                    setStroke((1 * density).toInt(), getThemeColor(com.google.android.material.R.attr.colorOutlineVariant))
                }
                layoutParams = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    LinearLayout.LayoutParams.WRAP_CONTENT
                ).apply {
                    bottomMargin = (12 * density).toInt()
                }
            }
            val topRow = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            val isSent = entry.direction == "Sent"
            topRow.addView(makeBadgeView(if (isSent) FileTypeBadge.Badge("SENT", "#2E7D32") else FileTypeBadge.Badge("RECV", "#5E35B1")))
            topRow.addView(TextView(this).apply {
                text = entry.name
                textSize = 15f
                setTypeface(typeface, Typeface.BOLD)
                setTextColor(getThemeColor(com.google.android.material.R.attr.colorOnSurface))
                ellipsize = TextUtils.TruncateAt.MIDDLE
                maxLines = 1
                setPadding((12 * density).toInt(), 0, 0, 0)
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            })
            card.addView(topRow)

            val detailsText = TextView(this).apply {
                text = "${entry.peer} \u2022 ${entry.size} \u2022 ${entry.avgSpeed} \u2022 ${entry.status} \u2022 ${entry.time}"
                textSize = 12f
                setTextColor(getThemeColor(com.google.android.material.R.attr.colorOnSurfaceVariant))
                setPadding(0, (8 * density).toInt(), 0, 0)
            }
            card.addView(detailsText)

            if (entry.direction == "Received") {
                val openBtn = LinearLayout(this).apply {
                    orientation = LinearLayout.HORIZONTAL
                    gravity = Gravity.CENTER_VERTICAL
                    layoutParams = LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.WRAP_CONTENT,
                        LinearLayout.LayoutParams.WRAP_CONTENT
                    ).apply {
                        topMargin = (10 * density).toInt()
                    }
                    val outValue = TypedValue()
                    theme.resolveAttribute(android.R.attr.selectableItemBackground, outValue, true)
                    setBackgroundResource(outValue.resourceId)
                    isClickable = true
                    isFocusable = true
                    setOnClickListener { openDownloadsFolder() }
                }
                val folderIcon = ImageView(this).apply {
                    setImageResource(R.drawable.ic_folder_open)
                    setColorFilter(getThemeColor(com.google.android.material.R.attr.colorPrimary))
                    val size = (16 * density).toInt()
                    layoutParams = LinearLayout.LayoutParams(size, size)
                }
                val openLabel = TextView(this).apply {
                    text = "Open folder"
                    textSize = 12f
                    setTypeface(typeface, Typeface.BOLD)
                    setTextColor(getThemeColor(com.google.android.material.R.attr.colorPrimary))
                    setPadding((6 * density).toInt(), 0, 0, 0)
                }
                openBtn.addView(folderIcon)
                openBtn.addView(openLabel)
                card.addView(openBtn)
            }
            historyListContainer.addView(card)
        }
    }

    private fun openDownloadsFolder() {
        try {
            startActivity(Intent(DownloadManager.ACTION_VIEW_DOWNLOADS))
        } catch (e: Exception) {
            Toast.makeText(this, "Files are in Downloads/NearbyBoost", Toast.LENGTH_LONG).show()
        }
    }

    private fun wireHistoryPage() {
        openDownloadsButton.setOnClickListener { openDownloadsFolder() }
    }

    // ================= SETTINGS =================

    private fun wireSettingsPage() {
        settingsNameInput.setText(currentDisplayName())
        renderEmojiAvatar(settingsAvatarFrame, AvatarCatalog.get(getAvatarIndex()).colorHex, AvatarCatalog.get(getAvatarIndex()).emoji)
        settingsAvatarFrame.setOnClickListener { showAvatarPicker() }
        chooseAvatarButton.setOnClickListener { showAvatarPicker() }
        saveIdentityButton.setOnClickListener {
            val name = settingsNameInput.text?.toString()?.trim().takeUnless { it.isNullOrEmpty() }
                ?: (Build.MODEL ?: "Device")
            setDisplayName(name)
            refreshIdentityUi()
            Toast.makeText(this, "Saved", Toast.LENGTH_SHORT).show()
        }

        val savedTheme = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getString(KEY_THEME, "light") ?: "light"
        when (savedTheme) {
            "light" -> settingsThemeToggle.check(R.id.themeLightBtn)
            "dark" -> settingsThemeToggle.check(R.id.themeDarkBtn)
            else -> settingsThemeToggle.check(R.id.themeSystemBtn)
        }
        settingsThemeToggle.addOnButtonCheckedListener { _, checkedId, isChecked ->
            if (!isChecked) return@addOnButtonCheckedListener
            val mode = when (checkedId) {
                R.id.themeLightBtn -> "light"
                R.id.themeDarkBtn -> "dark"
                else -> "system"
            }
            getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit().putString(KEY_THEME, mode).apply()
            applyNightMode(mode)
        }

        parallelTransferSwitch.isChecked = getParallelEnabled()
        parallelTransferSwitch.setOnCheckedChangeListener { _, checked ->
            getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit().putBoolean(KEY_PARALLEL_ENABLED, checked).apply()
        }
    }

    private fun showAvatarPicker() {
        val density = resources.displayMetrics.density
        val grid = GridLayout(this).apply {
            columnCount = 6
            setPadding((12 * density).toInt(), (12 * density).toInt(), (12 * density).toInt(), (12 * density).toInt())
        }
        val dialog = MaterialAlertDialogBuilder(this)
            .setTitle("Choose an avatar")
            .setView(grid)
            .setNegativeButton("Cancel", null)
            .create()

        for ((index, avatar) in AvatarCatalog.options.withIndex()) {
            val cell = FrameLayout(this).apply {
                val sizePx = (48 * density).toInt()
                layoutParams = GridLayout.LayoutParams().apply {
                    width = sizePx; height = sizePx
                    setMargins(6, 6, 6, 6)
                }
            }
            renderEmojiAvatar(cell, avatar.colorHex, avatar.emoji)
            cell.setOnClickListener {
                setAvatarIndex(index)
                renderEmojiAvatar(settingsAvatarFrame, avatar.colorHex, avatar.emoji)
                dialog.dismiss()
            }
            grid.addView(cell)
        }
        dialog.show()
    }

    // ================= IDENTITY =================

    private fun getOrCreateDeviceId(): String {
        val prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        var id = prefs.getString(KEY_DEVICE_ID, null)
        if (id == null) {
            id = java.util.UUID.randomUUID().toString()
            prefs.edit().putString(KEY_DEVICE_ID, id).apply()
        }
        return id
    }

    private fun getDisplayName(): String =
        getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getString(KEY_DISPLAY_NAME, "") ?: ""

    private fun currentDisplayName(): String = getDisplayName().ifBlank { Build.MODEL ?: "Android" }

    private fun setDisplayName(name: String) {
        getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit().putString(KEY_DISPLAY_NAME, name).apply()
    }

    private fun getAvatarIndex(): Int =
        getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getInt(KEY_AVATAR_INDEX, 0)

    private fun setAvatarIndex(index: Int) {
        getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit().putInt(KEY_AVATAR_INDEX, index).apply()
    }

    private fun getParallelEnabled(): Boolean =
        getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getBoolean(KEY_PARALLEL_ENABLED, true)

    private fun ensureDisplayNameSet() {
        if (getDisplayName().isBlank()) {
            val density = resources.displayMetrics.density
            val input = EditText(this).apply {
                setText(Build.MODEL ?: "Android")
                selectAll()
            }
            val container = FrameLayout(this).apply {
                setPadding((24 * density).toInt(), (12 * density).toInt(), (24 * density).toInt(), 0)
                addView(input)
            }
            MaterialAlertDialogBuilder(this)
                .setTitle("What should other devices call this phone?")
                .setView(container)
                .setCancelable(false)
                .setPositiveButton("Continue") { _, _ ->
                    val name = input.text?.toString()?.trim().takeUnless { it.isNullOrEmpty() } ?: (Build.MODEL ?: "Android")
                    setDisplayName(name)
                    refreshIdentityUi()
                }
                .show()
        }
    }

    private fun refreshIdentityUi() {
        if (::settingsNameInput.isInitialized) settingsNameInput.setText(currentDisplayName())
        if (::settingsAvatarFrame.isInitialized) {
            val avatar = AvatarCatalog.get(getAvatarIndex())
            renderEmojiAvatar(settingsAvatarFrame, avatar.colorHex, avatar.emoji)
        }
    }

    private fun applySavedTheme() {
        val mode = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getString(KEY_THEME, "light") ?: "light"
        applyNightMode(mode)
    }

    private fun applyNightMode(mode: String) {
        val nightMode = when (mode) {
            "light" -> AppCompatDelegate.MODE_NIGHT_NO
            "dark" -> AppCompatDelegate.MODE_NIGHT_YES
            else -> AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM
        }
        AppCompatDelegate.setDefaultNightMode(nightMode)
    }

    private fun queryDisplayName(uri: Uri): String? {
        contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) {
                val idx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (idx >= 0) return cursor.getString(idx)
            }
        }
        return null
    }

    private fun queryFileSize(uri: Uri): Long {
        contentResolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) {
                val idx = cursor.getColumnIndex(OpenableColumns.SIZE)
                if (idx >= 0 && !cursor.isNull(idx)) return cursor.getLong(idx)
            }
        }
        return 0L
    }

    // ================= SEND =================

    private fun onSendClicked() {
        if (sendQueue.isEmpty()) {
            Toast.makeText(this, "Pick at least one file or folder first", Toast.LENGTH_SHORT).show()
            return
        }
        val ip = targetIpInput.text?.toString()?.trim().orEmpty()
        val port = targetPortInput.text?.toString()?.trim()?.toIntOrNull()
        if (ip.isEmpty() || port == null) {
            Toast.makeText(this, "Enter a valid IP and port", Toast.LENGTH_SHORT).show()
            return
        }

        sendButton.isEnabled = false
        pickFileButton.isEnabled = false
        pickFolderButton.isEnabled = false
        stopTransferButton.visibility = View.VISIBLE
        clearFileProgressRows()
        TransferForegroundService.start(this)
        val cancelFlag = java.util.concurrent.atomic.AtomicBoolean(false)
        sendCancelFlag = cancelFlag
        val queueSnapshot = sendQueue.toList()
        sendQueue.clear()
        refreshQueueUi()

        lifecycleScope.launch(Dispatchers.IO) {
            try {
                val entries = mutableListOf<BatchFileEntry>()
                for (item in queueSnapshot) {
                    if (item.isFolder) {
                        val root = DocumentFile.fromTreeUri(this@MainActivity, item.uri)
                        if (root != null) {
                            entries.addAll(collectFolderEntries(root).map {
                                BatchFileEntry(it.uri, "${item.displayName}/${it.relativePath}", it.size)
                            })
                        }
                    } else {
                        entries.add(BatchFileEntry(item.uri, item.displayName, queryFileSize(item.uri)))
                    }
                }
                if (entries.isEmpty()) throw IOException("Nothing to send")

                if (entries.size == 1) {
                    sendSingleEntry(ip, port, entries[0], cancelFlag)
                } else {
                    val batchName = if (queueSnapshot.size == 1 && queueSnapshot[0].isFolder) {
                        queueSnapshot[0].displayName
                    } else {
                        "${entries.size} files"
                    }
                    sendBatch(ip, port, batchName, entries, cancelFlag)
                }
            } catch (e: TransferCancelledException) {
                runOnUiThread {
                    statusText.text = "Cancelled"
                    addHistoryEntry("Sent", "(cancelled)", "$ip:$port", "-", "-", "Cancelled")
                    for (item in queueSnapshot) if (!sendQueue.contains(item)) sendQueue.add(item)
                    refreshQueueUi()
                }
            } catch (e: Exception) {
                runOnUiThread {
                    statusText.text = "Error: ${e.message}"
                    addHistoryEntry("Sent", "(failed)", "$ip:$port", "-", "-", "Failed: ${e.message}")
                    for (item in queueSnapshot) if (!sendQueue.contains(item)) sendQueue.add(item)
                    refreshQueueUi()
                }
            } finally {
                sendCancelFlag = null
                TransferForegroundService.stop(this@MainActivity)
                runOnUiThread {
                    sendButton.isEnabled = true
                    pickFileButton.isEnabled = true
                    pickFolderButton.isEnabled = true
                    stopTransferButton.visibility = View.GONE
                }
            }
        }
    }

    /** Reads [int32 idLen][id][int32 nameLen][name] from an accept response, if present. */
    private fun readPeerIdentity(input: java.io.InputStream): Pair<String, String>? {
        return try {
            val dis = java.io.DataInputStream(input)
            val idLen = dis.readInt()
            if (idLen !in 0..4096) return null
            val idBytes = ByteArray(idLen); dis.readFully(idBytes)
            val nameLen = dis.readInt()
            if (nameLen !in 0..4096) return null
            val nameBytes = ByteArray(nameLen); dis.readFully(nameBytes)
            Pair(String(idBytes, Charsets.UTF_8), String(nameBytes, Charsets.UTF_8))
        } catch (e: Exception) {
            null
        }
    }

    private fun sendSingleEntry(ip: String, port: Int, entry: BatchFileEntry, cancelFlag: java.util.concurrent.atomic.AtomicBoolean) {
        runOnUiThread {
            statusText.text = "Connecting..."
            progressBar.progress = 0
            progressText.text = ""
        }

        Socket().use { socket ->
            socket.tcpNoDelay = true
            socket.connect(InetSocketAddress(ip, port), 10000)

            val out = DataOutputStream(BufferedOutputStream(socket.getOutputStream(), BUFFER_SIZE))
            out.writeByte(0)
            val nameBytes = entry.relativePath.toByteArray(Charsets.UTF_8)
            out.writeInt(nameBytes.size)
            out.write(nameBytes)
            out.writeLong(entry.size)
            val myId = getOrCreateDeviceId().toByteArray(Charsets.UTF_8)
            val myName = currentDisplayName().toByteArray(Charsets.UTF_8)
            out.writeInt(myId.size); out.write(myId)
            out.writeInt(myName.size); out.write(myName)
            out.flush()

            socket.soTimeout = 35000
            val response = socket.getInputStream().read()
            if (response != 1) throw IOException("Rejected by receiver")
            val peer = readPeerIdentity(socket.getInputStream())
            socket.soTimeout = 0

            runOnUiThread { statusText.text = "Sending ${entry.relativePath}" }

            val input = contentResolver.openInputStream(entry.uri) ?: throw IOException("Could not open ${entry.relativePath}")
            val buffer = ByteArray(BUFFER_SIZE)
            var sent = 0L
            var lastReportTime = System.currentTimeMillis()
            var lastReportBytes = 0L
            val startTime = System.currentTimeMillis()

            input.use { ins ->
                while (true) {
                    if (cancelFlag.get()) throw TransferCancelledException()
                    val n = ins.read(buffer)
                    if (n == -1) break
                    out.write(buffer, 0, n)
                    sent += n

                    val now = System.currentTimeMillis()
                    if (now - lastReportTime >= 250) {
                        val deltaSec = max(now - lastReportTime, 1L) / 1000.0
                        val mbPerSec = ((sent - lastReportBytes) / (1024.0 * 1024.0)) / deltaSec
                        val pct = if (entry.size > 0) ((sent * 100) / entry.size).toInt() else 0
                        runOnUiThread {
                            progressBar.progress = pct
                            progressText.text = buildProgressText(mbPerSec, pct, sent, entry.size)
                        }
                        TransferForegroundService.updateProgress(this, "Sending ${entry.relativePath}", "$pct% \u2022 ${"%.1f".format(mbPerSec)} MB/s", pct)
                        lastReportTime = now
                        lastReportBytes = sent
                    }
                }
            }
            out.flush()

            val elapsed = max(System.currentTimeMillis() - startTime, 1L) / 1000.0
            val avgMbPerSec = (sent / (1024.0 * 1024.0)) / elapsed
            runOnUiThread {
                statusText.text = String.format("Sent: %s (%.1f MB/s avg)", entry.relativePath, avgMbPerSec)
                progressBar.progress = 100
                addHistoryEntry("Sent", entry.relativePath, "$ip:$port", formatSize(sent), String.format("%.1f MB/s", avgMbPerSec), "Done")
                if (peer != null) rememberPairedDevice(peer.first, peer.second, ip, port)
            }
        }
    }

    /** Announces the batch (one accept prompt), then streams files across up to
     *  [MAX_PARALLEL_STREAMS] concurrent connections (or 1 if parallel transfer is off). */
    private fun sendBatch(ip: String, port: Int, batchName: String, entries: List<BatchFileEntry>, cancelFlag: java.util.concurrent.atomic.AtomicBoolean) {
        val totalBytes = entries.sumOf { it.size }
        runOnUiThread {
            statusText.text = "Connecting..."
            progressBar.progress = 0
            progressText.text = ""
            clearFileProgressRows()
            for (e in entries) addFileProgressRow(e.relativePath)
        }

        var peer: Pair<String, String>? = null
        Socket().use { control ->
            control.tcpNoDelay = true
            control.connect(InetSocketAddress(ip, port), 10000)
            val out = DataOutputStream(BufferedOutputStream(control.getOutputStream(), BUFFER_SIZE))
            out.writeByte(2) // transferType = batch announcement
            val nameBytes = batchName.toByteArray(Charsets.UTF_8)
            out.writeInt(nameBytes.size)
            out.write(nameBytes)
            out.writeInt(entries.size)
            out.writeLong(totalBytes)
            val myId = getOrCreateDeviceId().toByteArray(Charsets.UTF_8)
            val myName = currentDisplayName().toByteArray(Charsets.UTF_8)
            out.writeInt(myId.size); out.write(myId)
            out.writeInt(myName.size); out.write(myName)
            out.flush()

            control.soTimeout = 35000
            val response = control.getInputStream().read()
            if (response != 1) throw IOException("Rejected by receiver")
            peer = readPeerIdentity(control.getInputStream())
        }

        runOnUiThread { statusText.text = "Sending $batchName (${entries.size} files)" }

        val streamCount = if (getParallelEnabled()) min(MAX_PARALLEL_STREAMS, entries.size) else 1
        val lanes = Array(streamCount) { mutableListOf<BatchFileEntry>() }
        entries.forEachIndexed { i, e -> lanes[i % streamCount].add(e) }

        val totalSent = AtomicLong(0)
        val startTime = System.currentTimeMillis()
        val progressLock = Object()
        var lastReportTime = System.currentTimeMillis()
        var lastReportBytes = 0L
        var laneError: Exception? = null

        val threads = lanes.filter { it.isNotEmpty() }.map { lane ->
            Thread {
                try {
                    for (entry in lane) {
                        if (cancelFlag.get()) throw TransferCancelledException()
                        Socket().use { socket ->
                            socket.tcpNoDelay = true
                            socket.connect(InetSocketAddress(ip, port), 10000)
                            val out = DataOutputStream(BufferedOutputStream(socket.getOutputStream(), BUFFER_SIZE))

                            out.writeByte(0)
                            val relBytes = entry.relativePath.toByteArray(Charsets.UTF_8)
                            out.writeInt(relBytes.size)
                            out.write(relBytes)
                            out.writeLong(entry.size)
                            out.flush()

                            socket.soTimeout = 35000
                            val ack = socket.getInputStream().read()
                            if (ack != 1) throw IOException("Receiver did not accept ${entry.relativePath}")
                            socket.soTimeout = 0

                            val input = contentResolver.openInputStream(entry.uri)
                                ?: throw IOException("Could not open ${entry.relativePath}")
                            val buffer = ByteArray(BUFFER_SIZE)
                            var fileSent = 0L
                            input.use { ins ->
                                while (true) {
                                    if (cancelFlag.get()) throw TransferCancelledException()
                                    val n = ins.read(buffer)
                                    if (n == -1) break
                                    out.write(buffer, 0, n)
                                    fileSent += n
                                    val total = totalSent.addAndGet(n.toLong())

                                    val filePct = if (entry.size > 0) ((fileSent * 100) / entry.size).toInt() else 100
                                    runOnUiThread { updateFileProgressRow(entry.relativePath, filePct) }

                                    synchronized(progressLock) {
                                        val now = System.currentTimeMillis()
                                        if (now - lastReportTime >= 250) {
                                            val deltaSec = max(now - lastReportTime, 1L) / 1000.0
                                            val mbPerSec = ((total - lastReportBytes) / (1024.0 * 1024.0)) / deltaSec
                                            val pct = if (totalBytes > 0) ((total * 100) / totalBytes).toInt() else 0
                                            runOnUiThread {
                                                progressBar.progress = pct
                                                progressText.text = buildProgressText(mbPerSec, pct, total, totalBytes)
                                            }
                                            TransferForegroundService.updateProgress(this@MainActivity, "Sending $batchName", "$pct% \u2022 ${"%.1f".format(mbPerSec)} MB/s", pct)
                                            lastReportTime = now
                                            lastReportBytes = total
                                        }
                                    }
                                }
                            }
                            out.flush()
                            runOnUiThread { updateFileProgressRow(entry.relativePath, 100) }
                        }
                    }
                } catch (e: Exception) {
                    synchronized(progressLock) { if (laneError == null) laneError = e }
                }
            }.apply { start() }
        }
        threads.forEach { it.join() }
        laneError?.let { throw it }

        val elapsed = max(System.currentTimeMillis() - startTime, 1L) / 1000.0
        val avgMbPerSec = (totalSent.get() / (1024.0 * 1024.0)) / elapsed
        runOnUiThread {
            statusText.text = String.format("Sent: %s (%.1f MB/s avg)", batchName, avgMbPerSec)
            progressBar.progress = 100
            addHistoryEntry("Sent", "$batchName (${entries.size} files)", "$ip:$port", formatSize(totalBytes), String.format("%.1f MB/s", avgMbPerSec), "Done")
            peer?.let { rememberPairedDevice(it.first, it.second, ip, port) }
        }
    }

    private data class CollectedEntry(val uri: Uri, val relativePath: String, val size: Long)

    private fun collectFolderEntries(root: DocumentFile, prefix: String = ""): List<CollectedEntry> {
        val result = mutableListOf<CollectedEntry>()
        for (child in root.listFiles()) {
            val name = child.name ?: continue
            val relPath = if (prefix.isEmpty()) name else "$prefix/$name"
            if (child.isDirectory) {
                result.addAll(collectFolderEntries(child, relPath))
            } else if (child.isFile) {
                result.add(CollectedEntry(child.uri, relPath, child.length()))
            }
        }
        return result
    }

    // ================= SHARED UI HELPERS =================

    private fun clearFileProgressRows() {
        fileProgressRows.clear()
        fileProgressContainer.removeAllViews()
        fileProgressContainer.visibility = View.GONE
    }

    private fun addFileProgressRow(name: String) {
        val density = resources.displayMetrics.density
        val rowContainer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding((10 * density).toInt(), (8 * density).toInt(), (10 * density).toInt(), (8 * density).toInt())
            background = GradientDrawable().apply {
                cornerRadius = 10 * density
                setColor(getThemeColor(com.google.android.material.R.attr.colorSurfaceContainerHigh))
            }
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply {
                topMargin = (4 * density).toInt()
                bottomMargin = (4 * density).toInt()
            }
        }
        val headerRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        val fileIcon = ImageView(this).apply {
            setImageResource(R.drawable.ic_file)
            setColorFilter(getThemeColor(com.google.android.material.R.attr.colorPrimary))
            val size = (16 * density).toInt()
            layoutParams = LinearLayout.LayoutParams(size, size).apply {
                marginEnd = (8 * density).toInt()
            }
        }
        val nameText = TextView(this).apply {
            text = name
            textSize = 12f
            setTextColor(getThemeColor(com.google.android.material.R.attr.colorOnSurface))
            ellipsize = TextUtils.TruncateAt.MIDDLE
            maxLines = 1
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        headerRow.addView(fileIcon)
        headerRow.addView(nameText)
        rowContainer.addView(headerRow)

        val bar = LinearProgressIndicator(this).apply {
            layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
                topMargin = (6 * density).toInt()
            }
            max = 100
            trackThickness = (4 * density).toInt()
            trackCornerRadius = (2 * density).toInt()
            setIndicatorColor(getThemeColor(com.google.android.material.R.attr.colorPrimary))
            trackColor = getThemeColor(com.google.android.material.R.attr.colorSurfaceContainer)
        }
        rowContainer.addView(bar)
        fileProgressContainer.addView(rowContainer)
        fileProgressContainer.visibility = View.VISIBLE
        fileProgressRows[name] = bar to nameText
    }

    private fun updateFileProgressRow(name: String, pct: Int) {
        fileProgressRows[name]?.first?.progress = pct
    }

    private fun formatSize(bytes: Long): String {
        val mb = bytes / (1024.0 * 1024.0)
        return if (mb >= 1024) String.format("%.2f GB", mb / 1024.0) else String.format("%.1f MB", mb)
    }

    private fun buildProgressText(mbPerSec: Double, pct: Int, bytesDone: Long, totalBytes: Long): String {
        val remainingBytes = max(totalBytes - bytesDone, 0L)
        val etaText = if (mbPerSec > 0.05) {
            formatEta((remainingBytes / (1024.0 * 1024.0)) / mbPerSec)
        } else {
            "calculating\u2026"
        }
        return String.format("%.1f MB/s \u2014 %d%% \u2014 %s left", mbPerSec, pct, etaText)
    }

    private fun formatEta(seconds: Double): String {
        val s = seconds.toInt().coerceAtLeast(0)
        return when {
            s < 60 -> "${s}s"
            s < 3600 -> "${s / 60}m ${s % 60}s"
            else -> "${s / 3600}h ${(s % 3600) / 60}m"
        }
    }

    // --- TransferServer.Listener callbacks (invoked from background threads) ---

    override fun onWaiting() = runOnUiThread {
        statusText.text = "Waiting for a connection\u2026"
    }

    override fun onIncomingRequest(name: String, totalSize: Long, fileCount: Int, senderAddress: String): Boolean {
        val resultQueue = ArrayBlockingQueue<Boolean>(1)
        val message = if (fileCount > 1) {
            "From $senderAddress\n\n$name\n$fileCount files, ${formatSize(totalSize)}"
        } else {
            "From $senderAddress\n\n$name\n${formatSize(totalSize)}"
        }
        runOnUiThread {
            MaterialAlertDialogBuilder(this)
                .setTitle(if (fileCount > 1) "Incoming files" else "Incoming file")
                .setMessage(message)
                .setCancelable(false)
                .setPositiveButton("Accept") { _, _ -> resultQueue.offer(true) }
                .setNegativeButton("Reject") { _, _ -> resultQueue.offer(false) }
                .show()
        }
        return try {
            resultQueue.poll(REQUEST_TIMEOUT_SECONDS, TimeUnit.SECONDS) ?: false
        } catch (e: InterruptedException) {
            false
        }
    }

    override fun onStarted(name: String, totalSize: Long) = runOnUiThread {
        statusText.text = "Receiving $name"
        progressBar.progress = 0
        progressText.text = ""
        stopTransferButton.visibility = View.VISIBLE
        clearFileProgressRows()
        TransferForegroundService.start(this)
        TransferForegroundService.updateProgress(this, "Receiving $name", "Starting\u2026", 0)
    }

    override fun onProgress(bytesDone: Long, totalBytes: Long, mbPerSec: Double) = runOnUiThread {
        val pct = if (totalBytes > 0) ((bytesDone * 100) / totalBytes).toInt() else 0
        progressBar.progress = pct
        progressText.text = buildProgressText(mbPerSec, pct, bytesDone, totalBytes)
        TransferForegroundService.updateProgress(this, "Receiving", "$pct% \u2022 ${"%.1f".format(mbPerSec)} MB/s", pct)
    }

    override fun onFileProgress(fileName: String, bytesDone: Long, fileSize: Long, isNewFile: Boolean) = runOnUiThread {
        if (isNewFile && !fileProgressRows.containsKey(fileName)) addFileProgressRow(fileName)
        val pct = if (fileSize > 0) ((bytesDone * 100) / fileSize).toInt() else 0
        updateFileProgressRow(fileName, pct)
    }

    override fun onCompleted(name: String, totalBytes: Long, elapsedSeconds: Double, peerDeviceId: String?, peerName: String?) = runOnUiThread {
        val avgMbPerSec = (totalBytes / (1024.0 * 1024.0)) / elapsedSeconds
        progressBar.progress = 100
        statusText.text = String.format("Received: %s (%.1f MB/s avg)", name, avgMbPerSec)
        progressText.text = "Saved to Downloads/NearbyBoost"
        stopTransferButton.visibility = View.GONE
        TransferForegroundService.stop(this)
        addHistoryEntry("Received", name, peerName ?: "Unknown", formatSize(totalBytes), String.format("%.1f MB/s", avgMbPerSec), "Done")
        if (peerDeviceId != null) rememberPairedDevice(peerDeviceId, peerName, "", PORT)
    }

    override fun onCancelled(name: String) = runOnUiThread {
        statusText.text = "Cancelled: $name"
        stopTransferButton.visibility = View.GONE
        TransferForegroundService.stop(this)
        addHistoryEntry("Received", name, "-", "-", "-", "Cancelled")
    }

    /** A single transfer failed; the server itself keeps listening for the next connection. */
    override fun onError(message: String) = runOnUiThread {
        statusText.text = "Error: $message"
        stopTransferButton.visibility = View.GONE
        TransferForegroundService.stop(this)
        addHistoryEntry("Received", "(failed)", "-", "-", "-", "Failed: $message")
    }

    /** The server itself failed to bind or its accept loop broke — it is no longer listening. */
    override fun onServerError(message: String) = runOnUiThread {
        statusText.text = "Error: $message"
        stopTransferButton.visibility = View.GONE
        addHistoryEntry("Received", "(server error)", "-", "-", "-", "Failed: $message")
        server = null
        releaseLocks()
        running = false
        toggleButton.isEnabled = true
        toggleButton.text = "Start Server"
        TransferForegroundService.stop(this)
    }

    override fun onDestroy() {
        super.onDestroy()
        server?.stop()
        releaseLocks()
    }
}
