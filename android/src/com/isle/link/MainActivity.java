package com.isle.link;

import android.Manifest;
import android.app.Activity;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothClass;
import android.bluetooth.BluetoothDevice;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;
import android.provider.Settings;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.util.ArrayList;
import java.util.List;
import java.util.Set;

/** Setup screen: permissions, pick the laptop, status. Everything else runs in LinkService. */
public class MainActivity extends Activity {
    private static final int BG = 0xFF0B0B0E, CARD = 0xFF17171A, MINT = 0xFF7CF2C2, GRAY = 0x99FFFFFF;
    private final Handler h = new Handler(Looper.getMainLooper());
    private TextView status;
    private LinearLayout laptops;
    private final Runnable tick = new Runnable() {
        @Override public void run() { refreshStatus(); h.postDelayed(this, 1000); }
    };

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setStatusBarColor(BG);
        getWindow().setNavigationBarColor(BG);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(22), dp(36), dp(22), dp(24));
        root.setBackgroundColor(BG);

        TextView title = text("Isle", 30, Color.WHITE);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        root.addView(title);
        root.addView(spaced(text("Your phone and laptop, over Bluetooth", 15, GRAY), 2));

        status = text("", 15, Color.WHITE);
        root.addView(spaced(card(status), 22));

        root.addView(spaced(header("Your laptop"), 22));
        laptops = new LinearLayout(this);
        laptops.setOrientation(LinearLayout.VERTICAL);
        root.addView(laptops);

        root.addView(spaced(header("Clipboard"), 22));
        Button send = button("Copy to laptop now", true);
        send.setOnClickListener(v -> sendClipboardNow());
        root.addView(spaced(send, 8));
        root.addView(spaced(text("After copying anything: tap “Copy to laptop” in Isle's notification, use the "
                + "“Copy to laptop” tile in Quick Settings, or Share → Isle. Then press Ctrl+V on the laptop. "
                + "Text copied on the laptop with “Send clipboard” lands on this phone automatically.", 13, GRAY), 8));

        root.addView(spaced(header("Screen on the laptop"), 22));
        Button mirror = button("Show my screen on the laptop", true);
        mirror.setOnClickListener(v -> startActivity(new Intent(this, MirrorActivity.class)));
        root.addView(spaced(mirror, 8));
        Button access = button("Let the laptop tap and type (Accessibility)", false);
        access.setOnClickListener(v -> {
            startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS));
            Toast.makeText(this, "Open “Isle (control from laptop)” and turn it on", Toast.LENGTH_LONG).show();
        });
        root.addView(spaced(access, 8));
        root.addView(spaced(text("Uses Wi-Fi (same network as the laptop) for the picture; Bluetooth does the handshake. "
                + "Android asks once each time you start.", 13, GRAY), 8));

        root.addView(spaced(header("Stay connected"), 22));
        Button battery = button("Let Isle run in the background", false);
        battery.setOnClickListener(v -> askBatteryExemption());
        root.addView(spaced(battery, 8));
        root.addView(spaced(text("The laptop runs Isle and has Bluetooth on; the phone and laptop are paired once in Bluetooth settings. "
                + "After that this reconnects by itself, even after a restart.", 13, GRAY), 8));

        ScrollView scroll = new ScrollView(this);
        scroll.setBackgroundColor(BG);
        scroll.addView(root);
        setContentView(scroll);

        askPermissions();
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (hasBluetoothPermission()) { listLaptops(); startLink(); }
        h.post(tick);
    }

    @Override
    protected void onPause() {
        super.onPause();
        h.removeCallbacks(tick);
    }

    // ---------------- permissions ----------------

    private boolean hasBluetoothPermission() {
        return Build.VERSION.SDK_INT < 31 || checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED;
    }

    private void askPermissions() {
        List<String> need = new ArrayList<>();
        if (!hasBluetoothPermission()) need.add(Manifest.permission.BLUETOOTH_CONNECT);
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            need.add(Manifest.permission.POST_NOTIFICATIONS);
        if (!need.isEmpty()) requestPermissions(need.toArray(new String[0]), 1);
    }

    @Override
    public void onRequestPermissionsResult(int code, String[] perms, int[] results) {
        if (hasBluetoothPermission()) { listLaptops(); startLink(); }
        else Toast.makeText(this, "Isle needs Bluetooth access to reach your laptop", Toast.LENGTH_LONG).show();
    }

    private void askBatteryExemption() {
        PowerManager pm = getSystemService(PowerManager.class);
        if (pm != null && pm.isIgnoringBatteryOptimizations(getPackageName())) {
            Toast.makeText(this, "Already allowed", Toast.LENGTH_SHORT).show();
            return;
        }
        try {
            startActivity(new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:" + getPackageName())));
        } catch (Exception e) {
            startActivity(new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS));
        }
    }

    // ---------------- laptop ----------------

    private void listLaptops() {
        laptops.removeAllViews();
        BluetoothAdapter adapter = Prefs.adapter(this);
        if (adapter == null || !adapter.isEnabled()) {
            laptops.addView(text("Turn on Bluetooth to see your laptop.", 14, GRAY));
            return;
        }
        Set<BluetoothDevice> bonded;
        try { bonded = adapter.getBondedDevices(); } catch (SecurityException e) { return; }
        String chosen = Prefs.laptopAddress(this);
        List<BluetoothDevice> computers = new ArrayList<>(), others = new ArrayList<>();
        for (BluetoothDevice d : bonded) {
            int major = d.getBluetoothClass() != null ? d.getBluetoothClass().getMajorDeviceClass() : -1;
            if (major == BluetoothClass.Device.Major.COMPUTER) computers.add(d); else others.add(d);
        }
        if (chosen == null && computers.size() == 1) {
            chosen = computers.get(0).getAddress();
            Prefs.setLaptop(this, chosen);
        }
        if (computers.isEmpty() && others.isEmpty())
            laptops.addView(text("Pair this phone with your laptop in Bluetooth settings first.", 14, GRAY));
        List<BluetoothDevice> all = new ArrayList<>(computers);
        all.addAll(others);
        for (BluetoothDevice d : all) {
            final String address = d.getAddress();
            boolean isChosen = address.equals(chosen);
            Button b = button((isChosen ? "✓  " : "") + Prefs.name(d), isChosen);
            b.setOnClickListener(v -> {
                Prefs.setLaptop(this, address);
                listLaptops();
                startLink();
                startService(new Intent(this, LinkService.class).setAction(LinkService.ACTION_RECONNECT));
            });
            laptops.addView(spaced(b, 6));
        }
    }

    private void startLink() {
        if (Prefs.laptopAddress(this) == null) return;
        try { startForegroundService(new Intent(this, LinkService.class)); }
        catch (Exception e) { Toast.makeText(this, "Couldn't start: " + e.getMessage(), Toast.LENGTH_LONG).show(); }
    }

    private void refreshStatus() {
        String s = LinkService.status;
        status.setText((LinkService.connected ? "●  " : "○  ") + s);
        status.setTextColor(LinkService.connected ? MINT : Color.WHITE);
    }

    private void sendClipboardNow() {
        String text = null;
        ClipboardManager cm = getSystemService(ClipboardManager.class);
        ClipData clip = cm.getPrimaryClip();
        if (clip != null && clip.getItemCount() > 0) {
            CharSequence t = clip.getItemAt(0).coerceToText(this);
            if (t != null) text = t.toString();
        }
        if (text == null || text.isEmpty()) { Toast.makeText(this, "Nothing copied yet", Toast.LENGTH_SHORT).show(); return; }
        final String payload = text;
        new Thread(() -> {
            boolean ok = LinkService.sendClip(payload);
            runOnUiThread(() -> Toast.makeText(this, ok ? "Sent — press Ctrl+V on the laptop" : "Laptop not connected yet", Toast.LENGTH_SHORT).show());
        }).start();
    }

    // ---------------- tiny UI kit ----------------

    private int dp(int v) { return Math.round(v * getResources().getDisplayMetrics().density); }

    private TextView text(String s, int sp, int color) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(sp);
        t.setTextColor(color);
        return t;
    }

    private TextView header(String s) {
        TextView t = text(s, 13, MINT);
        t.setTypeface(Typeface.DEFAULT_BOLD);
        t.setAllCaps(true);
        return t;
    }

    private View card(View child) {
        LinearLayout box = new LinearLayout(this);
        box.setPadding(dp(16), dp(16), dp(16), dp(16));
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(CARD);
        bg.setCornerRadius(dp(18));
        box.setBackground(bg);
        box.addView(child);
        return box;
    }

    private Button button(String label, boolean primary) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(15);
        b.setGravity(Gravity.CENTER);
        b.setTextColor(primary ? 0xFF0A0A0C : Color.WHITE);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(primary ? MINT : CARD);
        bg.setCornerRadius(dp(24));
        b.setBackground(bg);
        b.setPadding(dp(18), dp(12), dp(18), dp(12));
        return b;
    }

    private View spaced(View v, int topDp) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.topMargin = dp(topDp);
        v.setLayoutParams(lp);
        return v;
    }
}
