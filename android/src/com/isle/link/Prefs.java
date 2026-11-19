package com.isle.link;

import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothManager;
import android.content.Context;
import android.content.SharedPreferences;

final class Prefs {
    private Prefs() { }

    static SharedPreferences sp(Context c) { return c.getSharedPreferences("isle", Context.MODE_PRIVATE); }

    static String laptopAddress(Context c) { return sp(c).getString("laptop", null); }

    static void setLaptop(Context c, String address) { sp(c).edit().putString("laptop", address).apply(); }

    static BluetoothAdapter adapter(Context c) {
        BluetoothManager m = c.getSystemService(BluetoothManager.class);
        return m != null ? m.getAdapter() : null;
    }

    static boolean bluetoothOn(Context c) {
        BluetoothAdapter a = adapter(c);
        return a != null && a.isEnabled();
    }

    static BluetoothDevice laptop(Context c) {
        String address = laptopAddress(c);
        BluetoothAdapter a = adapter(c);
        if (address == null || a == null) return null;
        try { return a.getRemoteDevice(address); } catch (Exception e) { return null; }
    }

    static String name(BluetoothDevice d) {
        try {
            String n = d.getName();
            return n != null ? n : d.getAddress();
        } catch (SecurityException e) {
            return "your laptop";
        }
    }
}
