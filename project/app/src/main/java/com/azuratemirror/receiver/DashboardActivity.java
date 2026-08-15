package com.azuratemirror.receiver;

import android.app.AlertDialog;
import android.content.Intent;
import android.os.Bundle;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.RadioButton;

import androidx.appcompat.app.AppCompatActivity;

public class DashboardActivity extends AppCompatActivity {

    public static final int DEFAULT_PORT = 47632;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_dashboard);

        EditText edtHost = findViewById(R.id.edtHost);
        Button btnConnect = findViewById(R.id.btnConnect);
        Button btnReadme = findViewById(R.id.btnReadme);
        RadioButton rbMirror = findViewById(R.id.rbMirror);
        CheckBox chkTouchpad = findViewById(R.id.chkTouchpad);

        btnConnect.setOnClickListener(v -> {
            String host = edtHost.getText().toString().trim();
            if (host.isEmpty()) return;
            int mode = rbMirror.isChecked() ? MirrorActivity.MODE_MIRROR : MirrorActivity.MODE_EXTEND;
            Intent intent = new Intent(this, MirrorActivity.class);
            intent.putExtra(MirrorActivity.EXTRA_HOST, host);
            intent.putExtra(MirrorActivity.EXTRA_PORT, DEFAULT_PORT);
            intent.putExtra(MirrorActivity.EXTRA_MODE, mode);
            intent.putExtra(MirrorActivity.EXTRA_TOUCHPAD_ENABLED, chkTouchpad.isChecked());
            startActivity(intent);
        });

        btnReadme.setOnClickListener(v -> showReadme());
    }

    private void showReadme() {
        String msg =
                "1. On the PC app, pick USB or WiFi BEFORE clicking Start (there's a picker there now).\n\n" +
                "2. USB mode (recommended): plug in a data cable, enable USB debugging on this tablet, " +
                "pick \"USB\" on the PC and Start. The PC app runs adb reverse for you automatically now - " +
                "just leave this screen's IP field as 127.0.0.1, nothing else to set up.\n\n" +
                "3. WiFi mode: pick \"WiFi\" on the PC and Start - its LAN IP is shown right there in the PC " +
                "app's window. Type that exact IP into the field above, port stays 47632. Both devices need " +
                "to be on the same network, and the PC's WiFi must be set to Private (Settings > Network & " +
                "internet > Wi-Fi > network name > Network profile type) or the connection will just time out.\n\n" +
                "4. First time only: if a Windows Firewall popup appears on the PC when it first Starts, " +
                "tick only Private then Allow access.\n\n" +
                "5. Extend (default) = this tablet becomes a real second Windows monitor (\"VDD by MTT\") - " +
                "drag any app onto it on the PC and it disappears from the PC's own screen, showing here instead. " +
                "Mirror = shows the PC's whole main screen instead.\n\n" +
                "6. Enable touchpad (optional, tick BEFORE connecting) = tapping/dragging on this screen moves " +
                "and clicks the PC's real mouse. The PC has its own matching checkbox too - BOTH need to be " +
                "checked before touch control works, either side can turn it off. Default OFF (view-only).\n\n" +
                "7. Port is always 47632 unless changed on the PC side too.";
        new AlertDialog.Builder(this)
                .setTitle("Readme — how to connect")
                .setMessage(msg)
                .setPositiveButton("OK", null)
                .show();
    }
}
