package com.isle.link;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.graphics.Bitmap;
import android.graphics.PixelFormat;
import android.graphics.drawable.Icon;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.util.DisplayMetrics;
import android.view.WindowManager;

import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.io.InputStreamReader;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.Collections;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Screen mirroring: captures the screen (MediaProjection), sends JPEG frames over Wi-Fi to Isle on the laptop.
 * The laptop learns this phone's address + a one-time token over the Bluetooth link, then connects here.
 * Frames: [int32 length][jpeg]. Length -1 = "control is off (Accessibility)". Laptop -> phone: text lines (AUTH, TAP, SWIPE, ...).
 */
public class MirrorService extends Service {
    static final String EXTRA_CODE = "code", EXTRA_DATA = "data";
    static final String ACTION_STOP = "com.isle.link.STOP_MIRROR";
    static final String CHANNEL = "isle_mirror";
    static volatile boolean running;

    private MediaProjection projection;
    private VirtualDisplay display;
    private ImageReader reader;
    private HandlerThread thread;
    private Handler handler;
    private ServerSocket server;
    private volatile Socket client;
    private volatile DataOutputStream out;
    private volatile boolean sending;
    private volatile byte[] lastFrame;
    private final ExecutorService sender = Executors.newSingleThreadExecutor();
    private final ByteArrayOutputStream jpeg = new ByteArrayOutputStream(128 * 1024);
    private String token;
    private int width, height;
    private long lastFrameAt;

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && ACTION_STOP.equals(intent.getAction())) { stopSelf(); return START_NOT_STICKY; }
        getSystemService(NotificationManager.class).createNotificationChannel(
                new NotificationChannel(CHANNEL, "Screen mirroring", NotificationManager.IMPORTANCE_LOW));
        startForeground(2, note(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
        if (projection != null || intent == null) return START_NOT_STICKY;
        try {
            int code = intent.getIntExtra(EXTRA_CODE, 0);
            Intent data = intent.getParcelableExtra(EXTRA_DATA);
            projection = getSystemService(MediaProjectionManager.class).getMediaProjection(code, data);
            thread = new HandlerThread("isle-mirror");
            thread.start();
            handler = new Handler(thread.getLooper());
            projection.registerCallback(new MediaProjection.Callback() {
                @Override public void onStop() { stopSelf(); }
            }, handler);

            DisplayMetrics m = new DisplayMetrics();
            getSystemService(WindowManager.class).getDefaultDisplay().getRealMetrics(m);
            width = 640;
            height = Math.round(640f * m.heightPixels / m.widthPixels) & ~1;
            reader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 2);
            reader.setOnImageAvailableListener(r -> onFrame(), handler);
            display = projection.createVirtualDisplay("Isle", width, height, m.densityDpi * width / m.widthPixels,
                    DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, reader.getSurface(), null, handler);
            running = true;
            new Thread(this::serve, "isle-mirror-server").start();
        } catch (Exception e) {
            LinkService.announce("MIRRORERR " + LinkService.b64("Couldn't start screen capture: " + e.getMessage()));
            stopSelf();
        }
        return START_NOT_STICKY;
    }

    /** Opens a port, tells the laptop over Bluetooth where to connect, then streams to it. */
    private void serve() {
        try {
            String ip = wifiAddress();
            if (ip == null) {
                LinkService.announce("MIRRORERR " + LinkService.b64("Turn on Wi-Fi on the phone (same network as the laptop)"));
                stopSelf();
                return;
            }
            server = new ServerSocket(0);
            byte[] t = new byte[12];
            new SecureRandom().nextBytes(t);
            StringBuilder sb = new StringBuilder();
            for (byte b : t) sb.append(String.format("%02x", b));
            token = sb.toString();
            if (!LinkService.announce("MIRROR " + ip + " " + server.getLocalPort() + " " + token + " " + width + " " + height)) {
                stopSelf();
                return;
            }
            server.setSoTimeout(30000);
            while (running) {
                Socket s = server.accept();
                s.setTcpNoDelay(true);
                BufferedReader in = new BufferedReader(new InputStreamReader(s.getInputStream(), StandardCharsets.UTF_8));
                String auth = in.readLine();
                if (auth == null || !auth.equals("AUTH " + token)) { s.close(); continue; }
                client = s;
                out = new DataOutputStream(new java.io.BufferedOutputStream(s.getOutputStream(), 256 * 1024));
                if (IsleAccess.instance == null) sendRaw(-1, null);
                byte[] first = lastFrame;
                if (first != null) sendRaw(first.length, first);
                String line;
                while (running && (line = in.readLine()) != null) {
                    IsleAccess a = IsleAccess.instance;
                    if (a != null) a.command(line.trim());
                    else sendRaw(-1, null);
                }
                break;   // the laptop closed the mirror window: end the session
            }
        } catch (IOException ignored) {
            // timeout (laptop never connected) or the laptop closed the window
        }
        stopSelf();
    }

    private void onFrame() {
        Image img = null;
        try {
            img = reader.acquireLatestImage();
            if (img == null) return;
            long now = System.currentTimeMillis();
            if (sending || now - lastFrameAt < 45) return;   // ~20 fps cap, drop frames while busy
            lastFrameAt = now;
            Image.Plane plane = img.getPlanes()[0];
            ByteBuffer buf = plane.getBuffer();
            int pixelStride = plane.getPixelStride(), rowStride = plane.getRowStride();
            int padded = rowStride / pixelStride;
            Bitmap bmp = Bitmap.createBitmap(padded, height, Bitmap.Config.ARGB_8888);
            bmp.copyPixelsFromBuffer(buf);
            Bitmap frame = padded == width ? bmp : Bitmap.createBitmap(bmp, 0, 0, width, height);
            jpeg.reset();
            frame.compress(Bitmap.CompressFormat.JPEG, 62, jpeg);
            if (frame != bmp) frame.recycle();
            bmp.recycle();
            final byte[] bytes = jpeg.toByteArray();
            lastFrame = bytes;
            if (out == null) return;
            sending = true;
            sender.execute(() -> { sendRaw(bytes.length, bytes); sending = false; });
        } catch (Exception ignored) {
        } finally {
            if (img != null) img.close();
        }
    }

    private synchronized void sendRaw(int length, byte[] bytes) {
        DataOutputStream o = out;
        if (o == null) return;
        try {
            o.writeInt(length);
            if (bytes != null) o.write(bytes);
            o.flush();
        } catch (IOException e) {
            out = null;
            running = false;
            stopSelf();
        }
    }

    private static String wifiAddress() {
        try {
            String fallback = null;
            for (NetworkInterface ni : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!ni.isUp() || ni.isLoopback()) continue;
                for (InetAddress a : Collections.list(ni.getInetAddresses())) {
                    if (!(a instanceof Inet4Address) || a.isLinkLocalAddress()) continue;
                    if (ni.getName().startsWith("wlan")) return a.getHostAddress();
                    if (fallback == null && !ni.getName().startsWith("rmnet")) fallback = a.getHostAddress();
                }
            }
            return fallback;
        } catch (Exception e) {
            return null;
        }
    }

    private Notification note() {
        PendingIntent stop = PendingIntent.getService(this, 5, new Intent(this, MirrorService.class).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CHANNEL)
                .setSmallIcon(R.drawable.ic_notify)
                .setContentTitle("Showing your screen on the laptop")
                .setContentText("Isle screen mirroring")
                .setOngoing(true)
                .addAction(new Notification.Action.Builder(Icon.createWithResource(this, R.drawable.ic_notify), "Stop", stop).build())
                .build();
    }

    @Override
    public void onDestroy() {
        running = false;
        try { if (client != null) client.close(); } catch (IOException ignored) { }
        try { if (server != null) server.close(); } catch (IOException ignored) { }
        if (display != null) display.release();
        if (reader != null) reader.close();
        if (projection != null) { try { projection.stop(); } catch (Exception ignored) { } }
        if (thread != null) thread.quitSafely();
        sender.shutdownNow();
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) { return null; }
}
