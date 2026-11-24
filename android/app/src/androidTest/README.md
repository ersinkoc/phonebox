# TLS connection reuse regression

Run `./gradlew connectedDebugAndroidTest` with an Android device or emulator attached. The tests use a loopback HTTPS server and isolated test preferences; no desktop agent or real pairing is needed.

`TlsConnectionReuseTest` sends 210 requests through the real Wi-Fi transport and checks that TLS connections are reused and descriptor counts stay bounded. It also checks that an incorrect certificate fingerprint is rejected. The reuse test fails on Android 6 with a per-request hostname-verifier lambda, even when the pinned socket factory is cached.

The certificate and unencrypted private key in `assets/tls-test-*.pem` are public test fixtures for this loopback server only. They are packaged exclusively in the test APK and must never be used by a real agent.
