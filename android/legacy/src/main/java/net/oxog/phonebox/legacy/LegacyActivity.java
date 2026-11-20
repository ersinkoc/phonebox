package net.oxog.phonebox.legacy;

import android.app.Activity;
import android.os.Build;
import android.os.Bundle;
import android.net.Uri;
import android.view.View;
import android.view.WindowManager;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

/** Separate API-16 launcher. No native JavaScript bridge or certificate bypass. */
public final class LegacyActivity extends Activity {
    private WebView web;
    private String origin;

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        showConnect();
    }

    private void showConnect() {
        if (web != null) { web.destroy(); web = null; }
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        layout.setPadding(24, 32, 24, 24);
        TextView title = new TextView(this);
        title.setText("PhoneBox Legacy\nEnter the bridge address shown on your computer.\nUse only your trusted home Wi-Fi. Press Back to change address.");
        title.setTextSize(19);
        layout.addView(title);
        final EditText address = new EditText(this);
        address.setSingleLine(true);
        address.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_VARIATION_URI);
        address.setText(getPreferences(MODE_PRIVATE).getString("address", "http://192.168.0.1:8789/"));
        layout.addView(address);
        Button connect = new Button(this);
        connect.setText("Connect");
        layout.addView(connect);
        setContentView(layout);
        connect.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) {
                String url = address.getText().toString().trim();
                Uri uri = Uri.parse(url);
                if (!("http".equals(uri.getScheme()) || "https".equals(uri.getScheme())) || uri.getHost() == null || uri.getUserInfo() != null) {
                    address.setError("Enter a full http:// or https:// address without credentials");
                    return;
                }
                origin = uri.getScheme() + "://" + uri.getEncodedAuthority();
                getPreferences(MODE_PRIVATE).edit().putString("address", url).apply();
                openPanel(url);
            }
        });
    }

    private void openPanel(String url) {
        web = new WebView(this);
        web.getSettings().setJavaScriptEnabled(true);
        web.getSettings().setDomStorageEnabled(true);
        web.getSettings().setAllowFileAccess(false);
        web.getSettings().setAllowContentAccess(false);
        web.getSettings().setAllowFileAccessFromFileURLs(false);
        web.getSettings().setAllowUniversalAccessFromFileURLs(false);
        web.removeJavascriptInterface("searchBoxJavaBridge_");
        web.removeJavascriptInterface("accessibility");
        web.removeJavascriptInterface("accessibilityTraversal");
        web.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView view, String url) {
                Uri uri = Uri.parse(url);
                return !origin.equals(uri.getScheme() + "://" + uri.getEncodedAuthority());
            }
        });
        int flags = View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION;
        if (Build.VERSION.SDK_INT >= 19) flags |= View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY;
        web.setSystemUiVisibility(flags);
        setContentView(web);
        web.loadUrl(url);
    }

    @Override public void onBackPressed() { if (web != null) showConnect(); else super.onBackPressed(); }
    @Override protected void onDestroy() { if (web != null) web.destroy(); super.onDestroy(); }
}
