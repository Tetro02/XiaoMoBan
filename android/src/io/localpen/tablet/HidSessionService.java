package io.localpen.tablet;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Binder;
import android.os.Build;
import android.os.IBinder;
import android.util.Log;

public final class HidSessionService extends Service {
    private static final String CHANNEL = "tablet_hid";
    private static final int NOTIFICATION_ID = 17;

    @Override public int onStartCommand(Intent intent, int flags, int startId) { return START_NOT_STICKY; }

    @Override public IBinder onBind(Intent intent) {
        try {
            getSystemService(NotificationManager.class).createNotificationChannel(
                    new NotificationChannel(CHANNEL, "蓝牙 HID", NotificationManager.IMPORTANCE_LOW));
            Notification notification = new Notification.Builder(this, CHANNEL)
                    .setSmallIcon(android.R.drawable.stat_notify_sync_noanim)
                    .setContentTitle("小墨板")
                    .setContentText("蓝牙 HID 直连已开启")
                    .setOngoing(true)
                    .build();
            if (Build.VERSION.SDK_INT >= 29) {
                startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
            } else {
                startForeground(NOTIFICATION_ID, notification);
            }
            return new Binder();
        } catch (IllegalStateException | SecurityException error) {
            Log.w("PenNoteHID", "Foreground service unavailable", error);
            return null;
        }
    }

    @Override public boolean onUnbind(Intent intent) {
        stopForeground(STOP_FOREGROUND_REMOVE);
        return false;
    }
}
