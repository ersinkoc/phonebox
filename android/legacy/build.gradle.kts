plugins { id("com.android.application") }
android {
    namespace = "net.oxog.phonebox.legacy"
    compileSdk = 35
    defaultConfig {
        applicationId = "net.oxog.phonebox.legacy"
        minSdk = 16
        targetSdk = 35
        versionCode = 1
        versionName = "0.1.0-experimental"
    }
    lint { fatal += "NewApi" }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_1_8
        targetCompatibility = JavaVersion.VERSION_1_8
    }
}
