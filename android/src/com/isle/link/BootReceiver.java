package com.isle.link;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** Reconnects to the laptop after the phone restarts. */
public class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        if (!Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction())) return;
        if (Prefs.laptopAddress(context) == null) return;
        try { context.startForegroundService(new Intent(context, LinkService.class)); } catch (Exception ignored) { }
    }
}
