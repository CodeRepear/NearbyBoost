package com.example.nearbyboost

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.io.IOException
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.max
import kotlin.math.min

/** Thrown when the user taps Stop mid-transfer; caught and reported as "Cancelled". */
class TransferCancelledException(message: String = "Cancelled") : IOException(message)

/**
 * A small, high-throughput, parallel-capable file/folder receiver.
 *
 * Protocol on the wire (all integers big-endian / network order):
 *   transferType 0, standalone (no batch currently active on this server):
 *     C->S: [int32 nameLen][name][int64 fileSize][int32 senderIdLen][senderId][int32 senderNameLen][senderName]
 *     S->C: [1 accept/reject byte] + if accepted: [int32 idLen][myDeviceId][int32 nameLen][myDisplayName]
 *   transferType 0, batch member (a batch is currently active) — unchanged, no identity:
 *     C->S: [int32 nameLen][name][int64 fileSize]   S->C: [1 byte, always 1]
 *   transferType 2, batch announcement (one accept decision covers the whole batch):
 *     C->S: [int32 batchNameLen][batchName][int32 fileCount][int64 totalBytes][int32 senderIdLen][senderId][int32 senderNameLen][senderName]
 *     S->C: [1 accept/reject byte] + if accepted: [int32 idLen][myDeviceId][int32 nameLen][myDisplayName]
 *
 * Identity is exchanged only on the two "entry point" messages, not on every batch-member
 * file connection, so a large batch doesn't pay that cost per file. A device only gets
 * remembered as "paired" by the caller once a transfer actually completes — that mutual
 * exchange, gated by the existing accept/reject prompt, IS the "permission" for pairing.
 *
 * Parallelism: after a batch is accepted, the sender opens several *additional* ordinary
 * transferType-0 connections concurrently and streams different files down each one. This
 * server assumes at most one batch is in flight at a time, a reasonable simplification for
 * a point-to-point personal tool.
 */
class TransferServer(
    private val port: Int,
    private val context: Context,
    private val myDeviceId: String,
    private val myDisplayName: String,
    private val listener: Listener
) {
    interface Listener {
        fun onWaiting()
        fun onIncomingRequest(name: String, totalSize: Long, fileCount: Int, senderAddress: String): Boolean
        fun onStarted(name: String, totalSize: Long)
        fun onProgress(bytesDone: Long, totalBytes: Long, mbPerSec: Double)
        /** Per-file progress within a batch; not called for a standalone single-file transfer. */
        fun onFileProgress(fileName: String, bytesDone: Long, fileSize: Long, isNewFile: Boolean)
        /** peerDeviceId/peerName are null only if the sender used an old build without identity. */
        fun onCompleted(name: String, totalBytes: Long, elapsedSeconds: Double, peerDeviceId: String?, peerName: String?)
        /** A single transfer failed; the server itself keeps listening for the next connection. */
        fun onError(message: String)
        /** The server itself failed to bind or its accept loop broke — it is no longer listening. */
        fun onServerError(message: String)
        fun onCancelled(name: String)
    }

    companion object {
        private const val BUFFER_SIZE = 1 shl 20
        private const val PROGRESS_INTERVAL_MS = 250L
        private const val MAX_NAME_LEN = 4096
        private const val MAX_FILE_COUNT = 100_000
    }

    private class ActiveBatch(
        val destRoot: String,
        val name: String,
        val totalBytes: Long,
        val remainingFiles: AtomicInteger,
        val peerDeviceId: String?,
        val peerName: String?,
        val receivedBytes: AtomicLong = AtomicLong(0),
        val startTime: Long = System.currentTimeMillis(),
        val cancelled: AtomicBoolean = AtomicBoolean(false)
    ) {
        var lastReportTime: Long = System.currentTimeMillis()
        var lastReportBytes: Long = 0
    }

    @Volatile private var running = true
    private var serverSocket: ServerSocket? = null
    private val activeBatch = AtomicReference<ActiveBatch?>(null)
    private val activeCancelFlag = AtomicReference<AtomicBoolean?>(null)
    private var executor: ExecutorService? = null

    fun stop() {
        running = false
        try { serverSocket?.close() } catch (_: IOException) { /* ignore */ }
        executor?.shutdownNow()
    }

    /** Cancels whichever transfer is currently receiving (a batch or a standalone file), if any. */
    fun cancelCurrentTransfer() {
        activeCancelFlag.get()?.set(true)
    }

    /** Blocking — call from a background thread/coroutine, not the UI thread. */
    fun start() {
        val pool = Executors.newCachedThreadPool()
        executor = pool
        try {
            val localIp = NetworkUtils.getLocalIPv4()
            val ss = bindWithRetry(localIp) ?: return
            if (!running) {
                try { ss.close() } catch (_: IOException) { /* ignore */ }
                return
            }
            serverSocket = ss

            while (running) {
                listener.onWaiting()
                val socket = try {
                    ss.accept()
                } catch (e: IOException) {
                    if (running) throw e else break
                }
                pool.execute {
                    try {
                        handleClient(socket)
                    } catch (e: TransferCancelledException) {
                        // already reported via listener.onCancelled at the point of cancellation
                    } catch (e: Exception) {
                        listener.onError(e.message ?: "Transfer failed")
                    } finally {
                        try { socket.close() } catch (_: IOException) { /* ignore */ }
                    }
                }
            }
        } catch (e: SocketException) {
            if (running) listener.onServerError(e.message ?: "Socket error")
        } catch (e: IOException) {
            if (running) listener.onServerError(e.message ?: "Could not start server")
        }
    }

    /** A server just stopped a moment ago can leave the port briefly unreleased —
     *  retry a few times before giving up, instead of failing immediately with EADDRINUSE.
     *  Returns null (rather than throwing) if stop() was called while retrying. */
    private fun bindWithRetry(localIp: String?): ServerSocket? {
        var lastError: IOException? = null
        repeat(5) { attempt ->
            if (!running) return null
            try {
                val ss = ServerSocket()
                ss.reuseAddress = true
                ss.receiveBufferSize = BUFFER_SIZE
                if (localIp != null) ss.bind(InetSocketAddress(localIp, port)) else ss.bind(InetSocketAddress(port))
                return ss
            } catch (e: IOException) {
                lastError = e
                if (attempt < 4) Thread.sleep(300)
            }
        }
        if (!running) return null
        throw lastError ?: IOException("Could not bind to port $port")
    }

    private fun handleClient(socket: Socket) {
        socket.tcpNoDelay = true
        socket.receiveBufferSize = BUFFER_SIZE

        val dis = DataInputStream(BufferedInputStream(socket.getInputStream(), BUFFER_SIZE))
        val rawOut = socket.getOutputStream()
        val senderAddress = (socket.remoteSocketAddress as? InetSocketAddress)
            ?.address?.hostAddress ?: "unknown sender"

        when (val transferType = dis.readByte().toInt()) {
            0 -> handleSingleFileOrBatchMember(dis, rawOut, senderAddress)
            2 -> handleBatchAnnouncement(dis, rawOut, senderAddress)
            else -> throw IOException("Unknown transfer type: $transferType")
        }
    }

    private fun writeMyIdentity(rawOut: OutputStream) {
        val dos = java.io.DataOutputStream(rawOut)
        val idBytes = myDeviceId.toByteArray(Charsets.UTF_8)
        val nameBytes = myDisplayName.toByteArray(Charsets.UTF_8)
        dos.writeInt(idBytes.size)
        dos.write(idBytes)
        dos.writeInt(nameBytes.size)
        dos.write(nameBytes)
        dos.flush()
    }

    private fun handleSingleFileOrBatchMember(dis: DataInputStream, rawOut: OutputStream, senderAddress: String) {
        val nameLen = dis.readInt()
        require(nameLen in 1..MAX_NAME_LEN) { "Bad filename length: $nameLen" }
        val nameBytes = ByteArray(nameLen)
        dis.readFully(nameBytes)
        val rawName = String(nameBytes, Charsets.UTF_8)
        val fileSize = dis.readLong()
        require(fileSize >= 0) { "Bad file size" }

        val batch = activeBatch.get()
        if (batch != null) {
            // Auto-accepted batch member — the batch itself already got the one prompt, and
            // identity was already exchanged at the announcement. No identity fields here.
            rawOut.write(1)
            rawOut.flush()

            val cleanRelPath = sanitizeRelativePath(rawName)
            val segments = cleanRelPath.split('/')
            val fileName = segments.last().ifEmpty { "file" }
            val dirPart = segments.dropLast(1).joinToString("/")
            val subdir = if (dirPart.isEmpty()) batch.destRoot else "${batch.destRoot}/$dirPart"

            var wasCancelled = false
            try {
                var fileReceived = 0L
                var firstChunk = true
                writeIncomingFile(dis, subdir, fileName, fileSize, batch.cancelled) { delta ->
                    fileReceived += delta
                    listener.onFileProgress(cleanRelPath, fileReceived, fileSize, firstChunk)
                    firstChunk = false
                    val total = batch.receivedBytes.addAndGet(delta)
                    reportBatchProgress(batch, total)
                }
            } catch (e: TransferCancelledException) {
                wasCancelled = true
            } finally {
                if (batch.remainingFiles.decrementAndGet() <= 0) {
                    activeBatch.compareAndSet(batch, null)
                    activeCancelFlag.compareAndSet(batch.cancelled, null)
                    if (batch.cancelled.get()) {
                        listener.onCancelled(batch.name)
                    } else {
                        val elapsed = max(System.currentTimeMillis() - batch.startTime, 1L) / 1000.0
                        listener.onCompleted(batch.name, batch.totalBytes, elapsed, batch.peerDeviceId, batch.peerName)
                    }
                }
            }
            if (wasCancelled) throw TransferCancelledException()
            return
        }

        // Standalone single file — read the sender's identity, then the normal one-file prompt flow.
        val senderIdLen = dis.readInt()
        require(senderIdLen in 0..MAX_NAME_LEN) { "Bad sender id length" }
        val senderId = if (senderIdLen > 0) {
            val b = ByteArray(senderIdLen); dis.readFully(b); String(b, Charsets.UTF_8)
        } else ""
        val senderNameLen = dis.readInt()
        require(senderNameLen in 0..MAX_NAME_LEN) { "Bad sender name length" }
        val senderName = if (senderNameLen > 0) {
            val b = ByteArray(senderNameLen); dis.readFully(b); String(b, Charsets.UTF_8)
        } else ""

        val fileName = sanitizeFileName(rawName)
        val approved = listener.onIncomingRequest(fileName, fileSize, 1, senderAddress)
        rawOut.write(if (approved) 1 else 0)
        if (approved) writeMyIdentity(rawOut) else rawOut.flush()
        if (!approved) {
            listener.onError("Rejected: $fileName from $senderAddress")
            return
        }

        listener.onStarted(fileName, fileSize)
        val cancelFlag = AtomicBoolean(false)
        activeCancelFlag.set(cancelFlag)

        var received = 0L
        var lastReportTime = System.currentTimeMillis()
        var lastReportBytes = 0L
        val startTime = System.currentTimeMillis()

        try {
            writeIncomingFile(dis, null, fileName, fileSize, cancelFlag) { delta ->
                received += delta
                val now = System.currentTimeMillis()
                if (now - lastReportTime >= PROGRESS_INTERVAL_MS) {
                    val deltaSec = max(now - lastReportTime, 1L) / 1000.0
                    val mbPerSec = ((received - lastReportBytes) / (1024.0 * 1024.0)) / deltaSec
                    listener.onProgress(received, fileSize, mbPerSec)
                    lastReportTime = now
                    lastReportBytes = received
                }
            }
        } catch (e: TransferCancelledException) {
            listener.onCancelled(fileName)
            throw e
        } finally {
            activeCancelFlag.compareAndSet(cancelFlag, null)
        }

        val elapsed = max(System.currentTimeMillis() - startTime, 1L) / 1000.0
        listener.onCompleted(fileName, fileSize, elapsed, senderId.ifEmpty { null }, senderName.ifEmpty { null })
    }

    private fun handleBatchAnnouncement(dis: DataInputStream, rawOut: OutputStream, senderAddress: String) {
        val nameLen = dis.readInt()
        require(nameLen in 1..MAX_NAME_LEN) { "Bad batch name length" }
        val nameBytes = ByteArray(nameLen)
        dis.readFully(nameBytes)
        val batchName = sanitizeFileName(String(nameBytes, Charsets.UTF_8))
        val fileCount = dis.readInt()
        require(fileCount in 1..MAX_FILE_COUNT) { "Bad file count" }
        val totalBytes = dis.readLong()
        require(totalBytes >= 0) { "Bad total size" }

        if (activeBatch.get() != null) {
            // Another batch is already in flight (single-batch-at-a-time is a deliberate
            // simplification for this point-to-point tool) — reject, sender can retry shortly.
            rawOut.write(0)
            rawOut.flush()
            return
        }

        val senderIdLen = dis.readInt()
        require(senderIdLen in 0..MAX_NAME_LEN) { "Bad sender id length" }
        val senderId = if (senderIdLen > 0) {
            val b = ByteArray(senderIdLen); dis.readFully(b); String(b, Charsets.UTF_8)
        } else ""
        val senderNameLen = dis.readInt()
        require(senderNameLen in 0..MAX_NAME_LEN) { "Bad sender name length" }
        val senderName = if (senderNameLen > 0) {
            val b = ByteArray(senderNameLen); dis.readFully(b); String(b, Charsets.UTF_8)
        } else ""

        val approved = listener.onIncomingRequest(batchName, totalBytes, fileCount, senderAddress)
        rawOut.write(if (approved) 1 else 0)
        if (approved) writeMyIdentity(rawOut) else rawOut.flush()
        if (!approved) {
            listener.onError("Rejected: $batchName from $senderAddress")
            return
        }

        val destRoot = "NearbyBoost/$batchName"
        val newBatch = ActiveBatch(
            destRoot, batchName, totalBytes, AtomicInteger(fileCount),
            senderId.ifEmpty { null }, senderName.ifEmpty { null }
        )
        if (!activeBatch.compareAndSet(null, newBatch)) {
            return
        }
        activeCancelFlag.set(newBatch.cancelled)
        listener.onStarted(batchName, totalBytes)
        // File data arrives on separate transferType=0 connections the sender opens right after this.
    }

    private fun reportBatchProgress(batch: ActiveBatch, totalReceived: Long) {
        synchronized(batch) {
            val now = System.currentTimeMillis()
            if (now - batch.lastReportTime >= PROGRESS_INTERVAL_MS) {
                val deltaBytes = totalReceived - batch.lastReportBytes
                val deltaSec = max(now - batch.lastReportTime, 1L) / 1000.0
                val mbPerSec = (deltaBytes / (1024.0 * 1024.0)) / deltaSec
                listener.onProgress(totalReceived, batch.totalBytes, mbPerSec)
                batch.lastReportTime = now
                batch.lastReportBytes = totalReceived
            }
        }
    }

    private fun writeIncomingFile(
        dis: DataInputStream,
        relativeSubdir: String?,
        fileName: String,
        fileSize: Long,
        cancelFlag: AtomicBoolean,
        onBytes: (delta: Long) -> Unit
    ) {
        val outUri = createDownloadEntry(relativeSubdir, fileName, fileSize)
        val output = context.contentResolver.openOutputStream(outUri)
            ?: throw IOException("Could not open output stream for $fileName")

        val buffer = ByteArray(BUFFER_SIZE)
        var received = 0L
        output.use { out ->
            while (received < fileSize) {
                if (cancelFlag.get()) throw TransferCancelledException()
                val toRead = min(buffer.size.toLong(), fileSize - received).toInt()
                val n = dis.read(buffer, 0, toRead)
                if (n == -1) throw IOException("Connection closed before transfer finished")
                out.write(buffer, 0, n)
                received += n
                onBytes(n.toLong())
            }
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val done = ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }
            context.contentResolver.update(outUri, done, null, null)
        }
    }

    private fun createDownloadEntry(relativeSubdir: String?, fileName: String, fileSize: Long): Uri {
        val resolver = context.contentResolver
        val basePath = if (relativeSubdir != null) {
            Environment.DIRECTORY_DOWNLOADS + "/" + relativeSubdir
        } else {
            Environment.DIRECTORY_DOWNLOADS + "/NearbyBoost"
        }
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, fileName)
            put(MediaStore.MediaColumns.SIZE, fileSize)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                put(MediaStore.MediaColumns.RELATIVE_PATH, "$basePath/")
                put(MediaStore.MediaColumns.IS_PENDING, 1)
            }
        }
        return resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
            ?: throw IOException("Could not create a Downloads entry for $fileName")
    }

    private fun sanitizeFileName(name: String): String {
        val cleaned = name.replace(Regex("[/\\\\:*?\"<>|]"), "_").trim()
        return cleaned.ifEmpty { "received_file" }
    }

    private fun sanitizeRelativePath(path: String): String {
        val normalized = path.replace('\\', '/')
        val segments = normalized.split('/').filter { it.isNotBlank() && it != "." && it != ".." }
        val cleaned = segments.map { seg ->
            val c = seg.replace(Regex("[:*?\"<>|]"), "_").trim()
            c.ifEmpty { "_" }
        }
        return cleaned.joinToString("/").ifEmpty { "file" }
    }
}
