package com.isle.link;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Intent;
import android.os.Bundle;
import android.widget.Toast;

/** Invisible. Android 10+ only lets the focused app read the clipboard, so this takes focus for a blink, sends, and closes. */
public class ClipActivity extends Activity {
    private boolean done;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        Intent i = getIntent();
        if (i != null && Intent.ACTION_SEND.equals(i.getAction())) {
            CharSequence text = i.getCharSequenceExtra(Intent.EXTRA_TEXT);
            send(text != null ? text.toString() : null);
        }
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (!hasFocus || done) return;
        String text = null;
        try {
            ClipboardManager cm = getSystemService(ClipboardManager.class);
            ClipData clip = cm.getPrimaryClip();
            if (clip != null && clip.getItemCount() > 0) {
                CharSequence t = clip.getItemAt(0).coerceToText(this);
                if (t != null) text = t.toString();
            }
        } catch (Exception ignored) { }
        send(text);
    }

    private void send(final String text) {
        done = true;
        if (text == null || text.isEmpty()) {
            Toast.makeText(this, "Nothing copied yet", Toast.LENGTH_SHORT).show();
            finish();
            return;
        }
        if (LinkService.instance == null) startForegroundService(new Intent(this, LinkService.class));
        new Thread(() -> {
            boolean ok = LinkService.sendClip(text);
            runOnUiThread(() -> {
                Toast.makeText(this, ok ? "Sent to laptop — press Ctrl+V there" : "Laptop not connected yet", Toast.LENGTH_SHORT).show();
                finish();
            });
        }).start();
    }
}
