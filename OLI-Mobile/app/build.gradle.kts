plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.olinox.oli_mobile"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.olinox.oli.mobile"
        minSdk = 26
        targetSdk = 35
        versionCode = 1
        versionName = "1.6.1"
    }

    buildTypes {
        release { isMinifyEnabled = false }
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("com.google.android.gms:play-services-code-scanner:16.1.0")
}
