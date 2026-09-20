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
        versionCode = 5
        versionName = "0.3.2"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}


dependencies {
    testImplementation("junit:junit:4.13.2")
}
