package io.github.matteo842.crashlauncher

import android.annotation.SuppressLint
import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.view.View
import android.widget.Toast
import io.github.matteo842.crashlauncher.disc.DiscFolderInspector
import io.github.matteo842.crashlauncher.disc.DiscSelection
import io.github.matteo842.crashlauncher.ui.LauncherScreen
import java.util.concurrent.Executors

class MainActivity : Activity() {
    private lateinit var screen: LauncherScreen
    private val worker = Executors.newSingleThreadExecutor()
    private var selectedDiscUri: Uri? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.statusBarColor = 0xFF061018.toInt()
        window.navigationBarColor = 0xFF061018.toInt()
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility =
            View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY

        screen = LauncherScreen(this).apply {
            onSelectDisc = { openDiscFolderPicker() }
            onStartGame = { launchRuntime() }
            onSettings = {
                Toast.makeText(
                    this@MainActivity,
                    "Settings will be connected after the runtime layer.",
                    Toast.LENGTH_SHORT,
                ).show()
            }
            onExit = { finish() }
        }
        setContentView(screen)

        getPreferences(MODE_PRIVATE)
            .getString(PREF_DISC_TREE_URI, null)
            ?.let(Uri::parse)
            ?.let(::inspectDiscFolder)
    }

    @Suppress("DEPRECATION")
    private fun openDiscFolderPicker() {
        val intent = Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).apply {
            addFlags(
                Intent.FLAG_GRANT_READ_URI_PERMISSION or
                    Intent.FLAG_GRANT_WRITE_URI_PERMISSION or
                    Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or
                    Intent.FLAG_GRANT_PREFIX_URI_PERMISSION,
            )
        }
        startActivityForResult(intent, REQUEST_DISC_FOLDER)
    }

    @SuppressLint("WrongConstant")
    @Deprecated("Kept intentionally to avoid an AndroidX dependency in the foundation build")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQUEST_DISC_FOLDER || resultCode != RESULT_OK) return

        val uri = data?.data ?: return
        // AGP 9.0 lint cannot infer that this mask contains only the two URI
        // permission constants accepted by takePersistableUriPermission.
        val takeFlags = data.flags and
            (Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        try {
            contentResolver.takePersistableUriPermission(uri, takeFlags)
        } catch (_: SecurityException) {
            // Some document providers grant only a session permission. Inspection can still run.
        }

        getPreferences(MODE_PRIVATE)
            .edit()
            .putString(PREF_DISC_TREE_URI, uri.toString())
            .apply()
        inspectDiscFolder(uri)
    }

    private fun inspectDiscFolder(uri: Uri) {
        screen.setBusy(true)
        worker.execute {
            val result = runCatching {
                DiscFolderInspector.inspect(contentResolver, uri)
            }.getOrElse { error ->
                DiscSelection.error(uri, "Cannot read this folder", error.message ?: "Unknown error")
            }

            runOnUiThread {
                if (!isFinishing && !isDestroyed) {
                    selectedDiscUri = result.folderUri.takeIf { result.isReady }
                    screen.setBusy(false)
                    screen.showDisc(result)
                }
            }
        }
    }

    private fun launchRuntime() {
        val discUri = selectedDiscUri ?: return
        val launch = packageManager.getLaunchIntentForPackage(RUNTIME_PACKAGE)
        if (launch == null) {
            Toast.makeText(
                this,
                "Install Crash Runtime Preview APK, then press Start Game again.",
                Toast.LENGTH_LONG,
            ).show()
            return
        }

        launch.data = discUri
        launch.addFlags(
            Intent.FLAG_GRANT_READ_URI_PERMISSION or
                Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or
                Intent.FLAG_GRANT_PREFIX_URI_PERMISSION,
        )
        startActivity(launch)
    }

    override fun onDestroy() {
        worker.shutdownNow()
        super.onDestroy()
    }

    private companion object {
        const val REQUEST_DISC_FOLDER = 401
        const val PREF_DISC_TREE_URI = "discTreeUri"
        const val RUNTIME_PACKAGE = "io.github.matteo842.crashlauncher.runtime"
    }
}
