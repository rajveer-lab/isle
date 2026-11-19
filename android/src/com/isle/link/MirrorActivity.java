package com.isle.link;

import android.app.Activity;
import android.content.Intent;
import android.media.projection.MediaProjectionManager;
import android.os.Bundle;
import android.widget.Toast;

/** Asks Android's "start recording or casting?" once per mirroring session, then hands over to MirrorService. */
public class MirrorActivity extends Activity {
    private static final int REQUEST = 7;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        if (LinkService.instance == null || !LinkService.connected) {
            Toast.makeText(this, "Connect to your laptop first (Isle running there, Bluetooth on)", Toast.LENGTH_LONG).show();
            finish();
            return;
        }
        MediaProjectionManager mpm = getSystemService(MediaProjectionManager.class);
        startActivityForResult(mpm.createScreenCaptureIntent(), REQUEST);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        if (requestCode == REQUEST && resultCode == RESULT_OK && data != null) {
            Intent svc = new Intent(this, MirrorService.class)
                    .putExtra(MirrorService.EXTRA_CODE, resultCode)
                    .putExtra(MirrorService.EXTRA_DATA, data);
            startForegroundService(svc);
        } else {
            LinkService.announce("MIRRORERR " + LinkService.b64("Screen sharing was cancelled on the phone"));
        }
        finish();
    }
}
