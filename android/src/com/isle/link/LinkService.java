package com.isle.link;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothSocket;
import android.content.BroadcastReceiver;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.ServiceInfo;
import android.graphics.drawable.Icon;
import android.media.AudioAttributes;
import android.media.AudioManager;
import android.media.Ringtone;
import android.media.RingtoneManager;
import android.net.Uri;
import android.os.BatteryManager;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.provider.Settings;
import android.util.Base64;
import android.widget.Toast;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.UUID;

/** Keeps a Bluetooth (RFCOMM) connection to Isle on the laptop and reconnects by itself. */
public class LinkService extends Service {
    static final UUID SERVICE = UUID.fromString("6b1f7c2e-3d4a-4e8b-9c5f-2a7d1e0b8c41");
    static final String CHANNEL = "isle_link";
    static final int NOTE_ID = 1;
    static final String ACTION_STOP_RING = "com.isle.link.STOP_RING";
    static final String ACTION_RECONNECT = "com.isle.link.RECONNECT";

    static volatile LinkService instance;
    static volatile String status = "Not running";
    static volatile boolean connected;

    private volatile boolean running;
    private Thread worker;
    private BluetoothSocket socket;
    private OutputStream out;
    private final Object lock = new Object();
    private final Handler main = new Handler(Looper.getMainLooper());
    private Ringtone ringtone;
    private int savedAlarmVolume = -1;
    private int lastPct = -1;
    private boolean lastCharging;
    private final Runnable autoStopRing = this::stopRing;

    @Override
    public void onCreate() {
        super.onCreate();
        instance = this;
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Isle connection", NotificationManager.IMPORTANCE_LOW));
        startForeground(NOTE_ID, buildNote(), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
        registerReceiver(battery, new IntentFilter(Intent.ACTION_BATTERY_CHANGED));
        running = true;
        worker = new Thread(this::loop, "isle-link");
        worker.start();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null) {
            if (ACTION_STOP_RING.equals(intent.getAction())) stopRing();
            if (ACTION_RECONNECT.equals(intent.getAction()) && worker != null) { closeSocket(); worker.interrupt(); }
        }
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        running = false;
        if (worker != null) worker.interrupt();
        if (server != null) { try { server.close(); } catch (IOException ignored) { } }
        closeSocket();
        try { unregisterReceiver(battery); } catch (Exception ignored) { }
        stopRing();
        instance = null;
        connected = false;
        status = "Stopped";
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) { return null; }

    // ---------------- connection ----------------
    // Two ways in, whichever works first: this phone connects to Isle on the laptop (client loop),
    // and the phone also listens so Isle on the laptop can connect to it (server loop).

    static final UUID PHONE_SERVICE = UUID.fromString("6b1f7c2e-3d4a-4e8b-9c5f-2a7d1e0b8c42");
    private Thread serverThread;
    private android.bluetooth.BluetoothServerSocket server;
    private volatile String lastError = "";

    private void loop() {
        serverThread = new Thread(this::serverLoop, "isle-server");
        serverThread.start();
        int delay = 3000;
        while (running) {
            if (!Prefs.bluetoothOn(this)) { setStatus("Turn on Bluetooth", false); sleep(8000); continue; }
            if (connected) { sleep(2000); continue; }
            BluetoothDevice laptop = Prefs.laptop(this);
            if (laptop == null) { setStatus("Waiting for the laptop (pick it in Isle to connect faster)", false); sleep(8000); continue; }
            String name = Prefs.name(laptop);
            try {
                setStatus("Looking for " + name + "…", false);
                BluetoothSocket s = laptop.createRfcommSocketToServiceRecord(SERVICE);
                s.connect();
                serve(s, name);
                delay = 3000;
            } catch (SecurityException e) {
                lastError = "Bluetooth permission missing";
                setStatus("Allow Bluetooth access for Isle", false);
            } catch (IOException e) {
                lastError = e.getMessage() != null ? e.getMessage() : e.toString();
            }
            if (!running) break;
            if (!connected) {
                setStatus("Waiting for " + name + " — Isle running there, Bluetooth on" + (lastError.isEmpty() ? "" : "  [" + lastError + "]"), false);
                sleep(delay);
                delay = Math.min(delay * 2, 20000);
            }
        }
    }

    /** Lets Isle on the laptop connect to this phone. */
    private void serverLoop() {
        while (running) {
            try {
                if (!Prefs.bluetoothOn(this) || Prefs.adapter(this) == null) { sleep(8000); continue; }
                server = Prefs.adapter(this).listenUsingRfcommWithServiceRecord("Isle Phone", PHONE_SERVICE);
                while (running) {
                    BluetoothSocket s = server.accept();
                    if (connected) { try { s.close(); } catch (IOException ignored) { } continue; }
                    final BluetoothSocket accepted = s;
                    new Thread(() -> {
                        try { serve(accepted, Prefs.name(accepted.getRemoteDevice())); }
                        catch (IOException ignored) { }
                    }, "isle-accepted").start();
                }
            } catch (SecurityException e) {
                lastError = "Bluetooth permission missing";
                sleep(8000);
            } catch (IOException e) {
                sleep(4000);
            } finally {
                if (server != null) { try { server.close(); } catch (IOException ignored) { } server = null; }
            }
        }
    }

    /** Runs one connection until it drops. */
    private void serve(BluetoothSocket s, String name) throws IOException {
        synchronized (lock) {
            if (connected) { try { s.close(); } catch (IOException ignored) { } return; }
            socket = s;
            out = s.getOutputStream();
            connected = true;
        }
        lastError = "";
        setStatus("Connected to " + name, true);
        try {
            send("HELLO " + b64(deviceName()) + " 1");
            lastPct = -1;
            sendBattery(registerReceiver(null, new IntentFilter(Intent.ACTION_BATTERY_CHANGED)));
            BufferedReader reader = new BufferedReader(new InputStreamReader(s.getInputStream(), StandardCharsets.UTF_8));
            String line;
            while (running && (line = reader.readLine()) != null) handle(line.trim());
        } finally {
            closeSocket();
            setStatus("Disconnected from " + name, false);
        }
    }

    private void handle(String line) {
        if (line.isEmpty()) return;
        String[] p = line.split(" ");
        switch (p[0]) {
            case "CLIP":
                if (p.length > 1) {
                    final String text = unb64(p[1]);
                    main.post(() -> {
                        ClipboardManager cm = getSystemService(ClipboardManager.class);
                        cm.setPrimaryClip(ClipData.newPlainText("From laptop", text));
                        Toast.makeText(this, "Copied from laptop", Toast.LENGTH_SHORT).show();
                    });
                }
                break;
            case "FILE": beginFile(p.length > 1 ? unb64(p[1]) : "file"); break;
            case "DATA": writeFile(p.length > 1 ? p[1] : ""); break;
            case "FILEEND": endFile(); break;
            case "MIRRORREQ": main.post(this::askToMirror); break;
            case "MIRRORSTOP": stopService(new Intent(this, MirrorService.class)); break;
            case "RING": main.post(this::startRing); break;
            case "RINGSTOP": main.post(this::stopRing); break;
            case "PING": send("PONG"); break;
            default: break;
        }
    }

    boolean send(String line) {
        synchronized (lock) {
            if (out == null) return false;
            try {
                out.write((line + "\n").getBytes(StandardCharsets.UTF_8));
                out.flush();
                return true;
            } catch (IOException e) {
                closeSocketLocked();
                return false;
            }
        }
    }

    /** Sends a line to the laptop over Bluetooth (call off the main thread). */
    static boolean announce(String line) {
        LinkService s = instance;
        return s != null && s.send(line);
    }

    /** The laptop asked to show this screen: Android needs a tap from you to start screen capture. */
    private void askToMirror() {
        if (MirrorService.running) return;
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel("isle_alerts", "Requests from the laptop", NotificationManager.IMPORTANCE_HIGH));
        PendingIntent start = PendingIntent.getActivity(this, 6,
                new Intent(this, MirrorActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE);
        Notification n = new Notification.Builder(this, "isle_alerts")
                .setSmallIcon(R.drawable.ic_notify)
                .setContentTitle("Show your screen on the laptop?")
                .setContentText("Tap to start screen mirroring")
                .setContentIntent(start)
                .setAutoCancel(true)
                .setCategory(Notification.CATEGORY_REMINDER)
                .build();
        nm.notify(7, n);
    }

    /** Called from ClipActivity (off the main thread). */
    static boolean sendClip(String text) {
        LinkService s = instance;
        return s != null && text != null && !text.isEmpty() && s.send("CLIP " + b64(text));
    }

    private void closeSocket() { synchronized (lock) { closeSocketLocked(); } }

    private void closeSocketLocked() {
        if (socket != null) { try { socket.close(); } catch (IOException ignored) { } }
        socket = null;
        out = null;
        connected = false;
    }

    // ---------------- files from the laptop -> Downloads/Isle ----------------

    private java.io.OutputStream fileOut;
    private Uri fileUri;
    private String fileName;
    private int fileNote = 100;

    private void beginFile(String name) {
        abortFile();
        try {
            fileName = name.replaceAll("[\\\\/:*?\"<>|]", "_");
            android.content.ContentValues v = new android.content.ContentValues();
            v.put(android.provider.MediaStore.Downloads.DISPLAY_NAME, fileName);
            v.put(android.provider.MediaStore.Downloads.RELATIVE_PATH, android.os.Environment.DIRECTORY_DOWNLOADS + "/Isle");
            String ext = android.webkit.MimeTypeMap.getFileExtensionFromUrl(Uri.encode(fileName));
            String mime = ext != null ? android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext.toLowerCase()) : null;
            if (mime != null) v.put(android.provider.MediaStore.Downloads.MIME_TYPE, mime);
            v.put(android.provider.MediaStore.Downloads.IS_PENDING, 1);
            fileUri = getContentResolver().insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI, v);
            fileOut = fileUri != null ? getContentResolver().openOutputStream(fileUri) : null;
        } catch (Exception e) {
            abortFile();
        }
    }

    private void writeFile(String b64chunk) {
        if (fileOut == null) return;
        try { fileOut.write(Base64.decode(b64chunk, Base64.DEFAULT)); }
        catch (Exception e) { abortFile(); }
    }

    private void endFile() {
        if (fileOut == null) return;
        try {
            fileOut.close();
            android.content.ContentValues v = new android.content.ContentValues();
            v.put(android.provider.MediaStore.Downloads.IS_PENDING, 0);
            getContentResolver().update(fileUri, v, null, null);
            final Uri uri = fileUri;
            final String name = fileName;
            main.post(() -> fileReceivedNote(name, uri));
        } catch (Exception ignored) { }
        fileOut = null;
        fileUri = null;
    }

    private void abortFile() {
        if (fileOut != null) { try { fileOut.close(); } catch (Exception ignored) { } }
        if (fileUri != null) { try { getContentResolver().delete(fileUri, null, null); } catch (Exception ignored) { } }
        fileOut = null;
        fileUri = null;
    }

    private void fileReceivedNote(String name, Uri uri) {
        Intent view = new Intent(Intent.ACTION_VIEW).setDataAndType(uri, getContentResolver().getType(uri))
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_ACTIVITY_NEW_TASK);
        PendingIntent open = PendingIntent.getActivity(this, fileNote, view, PendingIntent.FLAG_IMMUTABLE);
        Notification n = new Notification.Builder(this, CHANNEL)
                .setSmallIcon(R.drawable.ic_notify)
                .setContentTitle("Received from laptop")
                .setContentText(name + "  ·  Downloads/Isle")
                .setContentIntent(open)
                .setAutoCancel(true)
                .build();
        getSystemService(NotificationManager.class).notify(fileNote++, n);
        Toast.makeText(this, "Saved to Downloads/Isle: " + name, Toast.LENGTH_SHORT).show();
    }

    // ---------------- battery ----------------

    private final BroadcastReceiver battery = new BroadcastReceiver() {
        @Override public void onReceive(Context c, Intent i) { sendBattery(i); }
    };

    private void sendBattery(Intent i) {
        if (i == null) return;
        int level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1);
        int scale = i.getIntExtra(BatteryManager.EXTRA_SCALE, 100);
        int st = i.getIntExtra(BatteryManager.EXTRA_STATUS, -1);
        final boolean charging = st == BatteryManager.BATTERY_STATUS_CHARGING || st == BatteryManager.BATTERY_STATUS_FULL;
        final int pct = scale > 0 ? Math.round(level * 100f / scale) : level;
        if (pct == lastPct && charging == lastCharging) return;
        lastPct = pct;
        lastCharging = charging;
        new Thread(() -> send("BATT " + pct + " " + (charging ? 1 : 0))).start();
    }

    // ---------------- find my phone ----------------

    private void startRing() {
        try {
            AudioManager am = getSystemService(AudioManager.class);
            if (savedAlarmVolume < 0) savedAlarmVolume = am.getStreamVolume(AudioManager.STREAM_ALARM);
            am.setStreamVolume(AudioManager.STREAM_ALARM, am.getStreamMaxVolume(AudioManager.STREAM_ALARM), 0);
            Uri uri = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM);
            if (uri == null) uri = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE);
            if (ringtone != null) ringtone.stop();
            ringtone = RingtoneManager.getRingtone(this, uri);
            ringtone.setAudioAttributes(new AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_ALARM)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION).build());
            ringtone.setLooping(true);
            ringtone.play();
            updateNote();
            main.removeCallbacks(autoStopRing);
            main.postDelayed(autoStopRing, 30000);
        } catch (Exception ignored) { }
    }

    private void stopRing() {
        main.removeCallbacks(autoStopRing);
        if (ringtone != null) { ringtone.stop(); ringtone = null; }
        if (savedAlarmVolume >= 0) {
            try { getSystemService(AudioManager.class).setStreamVolume(AudioManager.STREAM_ALARM, savedAlarmVolume, 0); } catch (Exception ignored) { }
            savedAlarmVolume = -1;
        }
        updateNote();
    }

    // ---------------- notification ----------------

    private Notification buildNote() {
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE);
        Intent clip = new Intent(this, ClipActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        PendingIntent clipPi = PendingIntent.getActivity(this, 1, clip, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        Icon icon = Icon.createWithResource(this, R.drawable.ic_notify);
        Notification.Builder b = new Notification.Builder(this, CHANNEL)
                .setSmallIcon(R.drawable.ic_notify)
                .setContentTitle(connected ? "Connected to your laptop" : "Isle")
                .setContentText(status)
                .setOngoing(true)
                .setShowWhen(false)
                .setContentIntent(open)
                .addAction(new Notification.Action.Builder(icon, "Copy to laptop", clipPi).build());
        if (ringtone != null) {
            PendingIntent stop = PendingIntent.getService(this, 2,
                    new Intent(this, LinkService.class).setAction(ACTION_STOP_RING), PendingIntent.FLAG_IMMUTABLE);
            b.addAction(new Notification.Action.Builder(icon, "Stop ringing", stop).build());
        }
        return b.build();
    }

    private void updateNote() {
        try { getSystemService(NotificationManager.class).notify(NOTE_ID, buildNote()); } catch (Exception ignored) { }
    }

    private void setStatus(String s, boolean c) {
        status = s;
        main.post(this::updateNote);
    }

    // ---------------- helpers ----------------

    private String deviceName() {
        String n = null;
        try { n = Settings.Global.getString(getContentResolver(), Settings.Global.DEVICE_NAME); } catch (Exception ignored) { }
        return n != null && !n.isEmpty() ? n : Build.MODEL;
    }

    static String b64(String s) { return Base64.encodeToString(s.getBytes(StandardCharsets.UTF_8), Base64.NO_WRAP); }

    static String unb64(String s) {
        try { return new String(Base64.decode(s, Base64.DEFAULT), StandardCharsets.UTF_8); }
        catch (Exception e) { return ""; }
    }

    private static void sleep(long ms) {
        try { Thread.sleep(ms); } catch (InterruptedException ignored) { }
    }
}
