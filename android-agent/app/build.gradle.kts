plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }
android {
    namespace = "com.tridi.fleet.agent"
    compileSdk = 35
    defaultConfig {
        applicationId = "com.tridi.fleet.agent"
        minSdk = 28
        targetSdk = 35
        versionCode = 1
        versionName = "0.1.0"
    }
}
dependencies { implementation("com.squareup.okhttp3:okhttp:4.12.0") }