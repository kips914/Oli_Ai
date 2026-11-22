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
        versionCode = 2
        versionName = "1.9.0"

        // Optional default server baked into the build (Settings screen still overrides it):
        //   ./gradlew assembleRelease -POLI_DEFAULT_SERVER=http://192.168.1.10:8765
        val defaultServer = (project.findProperty("OLI_DEFAULT_SERVER") as String?) ?: ""
        buildConfigField("String", "DEFAULT_SERVER", "\"$defaultServer\"")
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
