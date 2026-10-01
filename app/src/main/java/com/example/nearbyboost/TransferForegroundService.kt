package com.example.nearbyboost

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat

/**
 * Keeps a low-priority "ongoing" notification showing transfer progress so the user can
 * check on a send/receive after minimizing the app. Content updates go straight through
 * NotificationManager (updateProgress can be called from anywhere in the app); this
 * service's own job is just to hold foreground priority so Android doesn't kill the
 * process mid-transfer.
 */
class TransferForegroundService : Service() {

    companion object {
        const val CHANNEL_ID = "nearbyboost_transfers"
        const val NOTIFICATION_ID = 1001
        const val ACTION_START = "com.example.nearbyboost.action.START"
        const val ACTION_STOP = "com.example.nearbyboost.action.STOP"

        fun start(context: Context) {
            val intent = Intent(context, TransferForegroundService::class.java).apply { action = ACTION_START }
            context.startForegroundService(intent)
        }

        fun stop(context: Context) {
            val intent = Intent(context, TransferForegroundService::class.java).apply { action = ACTION_STOP }
            context.startService(intent)
        }

        fun updateProgress(context: Context, title: String, text: String, percent: Int) {
            ensureChannel(context)
            val notification = buildNotification(context, title, text, percent)
            (context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager)
                .notify(NOTIFICATION_ID, notification)
        }

        private fun ensureChannel(context: Context) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
                if (manager.getNotificationChannel(CHANNEL_ID) == null) {
                    val channel = NotificationChannel(
                        CHANNEL_ID, "File transfers", NotificationManager.IMPORTANCE_LOW
                    ).apply { description = "Shows progress while NearbyBoost is sending or receiving" }
                    manager.createNotificationChannel(channel)
                }
            }
        }

        private fun buildNotification(context: Context, title: String, text: String, percent: Int): Notification {
            val openIntent = context.packageManager.getLaunchIntentForPackage(context.packageName)
            val pendingIntent = openIntent?.let {
                PendingIntent.getActivity(context, 0, it, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            }
            val builder = NotificationCompat.Builder(context, CHANNEL_ID)
                .setContentTitle(title)
                .setContentText(text)
                .setSmallIcon(android.R.drawable.stat_sys_download)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setProgress(100, percent.coerceIn(0, 100), percent <= 0)
            if (pendingIntent != null) builder.setContentIntent(pendingIntent)
            return builder.build()
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
            else -> {
                ensureChannel(this)
                startForeground(NOTIFICATION_ID, buildNotification(this, "NearbyBoost", "Starting\u2026", 0))
            }
        }
        return START_NOT_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
