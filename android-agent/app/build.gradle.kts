plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }

android {
    namespace = "com.tridi.fleet.agent"
    compileSdk = 35
    defaultConfig {
        applicationId = "com.tridi.fleet.agent"
        minSdk = 28
        targetSdk = 35
        versionCode = 4
        versionName = "0.4.1"
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin { jvmToolchain(17) }

dependencies {
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
}