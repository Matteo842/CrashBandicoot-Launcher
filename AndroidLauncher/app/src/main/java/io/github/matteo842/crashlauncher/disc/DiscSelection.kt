package io.github.matteo842.crashlauncher.disc

import android.net.Uri

data class DiscSelection(
    val folderUri: Uri,
    val cueName: String? = null,
    val binName: String? = null,
    val binSizeBytes: Long = 0,
    val isReady: Boolean,
    val title: String,
    val detail: String,
) {
    companion object {
        fun error(folderUri: Uri, title: String, detail: String) = DiscSelection(
            folderUri = folderUri,
            isReady = false,
            title = title,
            detail = detail,
        )
    }
}
