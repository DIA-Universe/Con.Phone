plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "universe.dia.conphone"
    compileSdk = 35

    defaultConfig {
        applicationId = "universe.dia.conphone"
        minSdk = 31
        targetSdk = 35
        versionCode = 13
        versionName = "1.0.3"
        testInstrumentationRunner = "universe.dia.conphone.ImageProviderSmoke"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures { aidl = true }
}


dependencies {
    implementation("dev.rikka.shizuku:api:13.1.5")
    implementation("dev.rikka.shizuku:provider:13.1.5")
    testImplementation("junit:junit:4.13.2")
}
