plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.oli.mobile"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.oli.mobile"
        minSdk = 26
        targetSdk = 35
        versionCode = 18
        versionName = "1.12.6"

        // The server the app connects to by itself (the OLI cloud server behind the app).
        // Override at build time, e.g. -POLI_DEFAULT_SERVER=http://192.168.1.10:8765
        // (build-exe/apk CI passes OLI_DEFAULT_SERVER, falling back to the env var).
        val defaultServer = (project.findProperty("OLI_DEFAULT_SERVER") as String?)
            ?: System.getenv("OLI_DEFAULT_SERVER")
            ?: "https://oli-cloud.example.com"
        buildConfigField("String", "DEFAULT_SERVER", "\"$defaultServer\"")

        // Optional directory that resolves the current server address ({"server": "..."}).
        val directoryUrl = (project.findProperty("OLI_DIRECTORY_URL") as String?)
            ?: System.getenv("OLI_DIRECTORY_URL")
            ?: ""
        buildConfigField("String", "DIRECTORY_URL", "\"$directoryUrl\"")
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            // Signed with the debug key so the CI-built release APK installs without a keystore.
            // Provide OLI_KEYSTORE_PATH / OLI_KEYSTORE_PASSWORD / OLI_KEY_ALIAS to sign properly.
            val keystorePath = System.getenv("OLI_KEYSTORE_PATH")
            if (!keystorePath.isNullOrBlank()) {
                signingConfig = signingConfigs.create("oliRelease") {
                    storeFile = file(keystorePath)
                    storePassword = System.getenv("OLI_KEYSTORE_PASSWORD")
                    keyAlias = System.getenv("OLI_KEY_ALIAS") ?: "oli"
                    keyPassword = System.getenv("OLI_KEYSTORE_PASSWORD")
                }
            } else {
                signingConfig = signingConfigs.getByName("debug")
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        resources.excludes += setOf("/META-INF/{AL2.0,LGPL2.1}", "META-INF/DEPENDENCIES")
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.10.01")
    implementation(composeBom)

    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.7")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")

    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")

    implementation("androidx.security:security-crypto:1.1.0-alpha06")
    implementation("androidx.datastore:datastore-preferences:1.1.1")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")

    debugImplementation("androidx.compose.ui:ui-tooling")
}
