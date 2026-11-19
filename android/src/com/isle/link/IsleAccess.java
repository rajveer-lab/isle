package com.isle.link;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.graphics.Path;
import android.os.Bundle;
import android.util.DisplayMetrics;
import android.view.WindowManager;
import android.view.accessibility.AccessibilityEvent;
import android.view.accessibility.AccessibilityNodeInfo;

/** Performs taps, swipes, buttons and typing sent from the laptop's mirror window. */
public class IsleAccess extends AccessibilityService {
    static volatile IsleAccess instance;

    @Override protected void onServiceConnected() { instance = this; }
    @Override public void onAccessibilityEvent(AccessibilityEvent event) { }
    @Override public void onInterrupt() { }
    @Override public void onDestroy() { instance = null; super.onDestroy(); }

    /** TAP x y | LONG x y | SWIPE x1 y1 x2 y2 ms | BACK | HOME | RECENTS | TEXT b64 | BKSP | ENTER  (x/y are 0..1) */
    void command(String line) {
        String[] p = line.split(" ");
        try {
            switch (p[0]) {
                case "TAP": gesture(f(p[1]), f(p[2]), f(p[1]), f(p[2]), 60); break;
                case "LONG": gesture(f(p[1]), f(p[2]), f(p[1]), f(p[2]), 650); break;
                case "SWIPE": gesture(f(p[1]), f(p[2]), f(p[3]), f(p[4]), Math.max(80, Math.min(1500, Integer.parseInt(p[5])))); break;
                case "BACK": performGlobalAction(GLOBAL_ACTION_BACK); break;
                case "HOME": performGlobalAction(GLOBAL_ACTION_HOME); break;
                case "RECENTS": performGlobalAction(GLOBAL_ACTION_RECENTS); break;
                case "TEXT": type(LinkService.unb64(p[1]), false); break;
                case "BKSP": type("", true); break;
                case "ENTER": enter(); break;
                default: break;
            }
        } catch (Exception ignored) { }
    }

    private static float f(String s) { return Math.max(0f, Math.min(1f, Float.parseFloat(s))); }

    private void gesture(float x1, float y1, float x2, float y2, int ms) {
        DisplayMetrics m = new DisplayMetrics();
        getSystemService(WindowManager.class).getDefaultDisplay().getRealMetrics(m);
        Path path = new Path();
        path.moveTo(x1 * m.widthPixels, y1 * m.heightPixels);
        if (x1 != x2 || y1 != y2) path.lineTo(x2 * m.widthPixels, y2 * m.heightPixels);
        dispatchGesture(new GestureDescription.Builder()
                .addStroke(new GestureDescription.StrokeDescription(path, 0, ms)).build(), null, null);
    }

    private AccessibilityNodeInfo focusedInput() {
        AccessibilityNodeInfo root = getRootInActiveWindow();
        return root != null ? root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT) : null;
    }

    private void type(String add, boolean backspace) {
        AccessibilityNodeInfo node = focusedInput();
        if (node == null || !node.isEditable()) return;
        CharSequence current = node.getText();
        String text = current != null && !node.isShowingHintText() ? current.toString() : "";
        text = backspace ? (text.isEmpty() ? text : text.substring(0, text.length() - 1)) : text + add;
        Bundle args = new Bundle();
        args.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, text);
        node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args);
    }

    private void enter() {
        AccessibilityNodeInfo node = focusedInput();
        if (node != null) node.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.getId());
    }
}
