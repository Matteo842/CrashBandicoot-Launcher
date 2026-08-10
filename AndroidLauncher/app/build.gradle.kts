import org.gradle.api.tasks.Sync

plugins {
    id("com.android.application")
}

val repoRoot = rootProject.projectDir.parentFile
val generatedSharedAssets = layout.buildDirectory.dir("generated/sharedAssets")
val generatedSharedAssetsPath = layout.buildDirectory.get().dir("generated/sharedAssets").asFile

// Keep one authoritative copy of desktop/mobile shared data in the repository.
// These files are copied only into build output and are never duplicated in Git.
val syncSharedAssets by tasks.registering(Sync::class) {
    into(generatedSharedAssets)

    from(repoRoot.resolve("icon.png")) {
        into("brand")
    }
    from(repoRoot.resolve("CrashBandicoot.Launcher/Ui/world_map.png")) {
        into("brand")
    }
    from(repoRoot.resolve("CrashBandicoot.Launcher/Ui/fonts")) {
        into("fonts")
    }
    from(repoRoot.resolve("CrashBandicoot.Launcher/Recomp/CrashBandicoot.json")) {
        into("pipeline")
    }
    from(repoRoot.resolve("CrashBandicoot.Launcher/Recomp/Patches/main.cs.patch")) {
        into("pipeline")
    }
    from(repoRoot.resolve("RecompOne.Runtime/Catalog/data")) {
        into("catalog")
    }
}

android {
    namespace = "io.github.matteo842.crashlauncher"
    compileSdk = 36

    defaultConfig {
        applicationId = "io.github.matteo842.crashlauncher"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0-dev"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    // AGP 9 rejects Provider instances in the legacy SourceSet API. Use the
    // resolved build-directory path and keep the task dependency on preBuild.
    sourceSets.getByName("main").assets.setSrcDirs(
        listOf("src/main/assets", generatedSharedAssetsPath),
    )
}

tasks.named("preBuild").configure {
    dependsOn(syncSharedAssets)
}
