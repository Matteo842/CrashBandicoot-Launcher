package io.github.matteo842.crashlauncher.disc

import android.content.ContentResolver
import android.net.Uri
import android.provider.DocumentsContract
import java.util.Locale

object DiscFolderInspector {
    private const val MIN_BIN_BYTES = 80L * 1024L * 1024L
    private const val MAX_CUE_BYTES = 256L * 1024L

    private val fileLine = Regex(
        pattern = """(?im)^\s*FILE\s+"([^"]+)"\s+(BINARY|MOTOROLA)\s*$""",
    )
    private val dataTrack = Regex(
        pattern = """(?im)^\s*TRACK\s+\d{1,2}\s+(MODE1/2048|MODE1/2352|MODE2/2336|MODE2/2352)\s*$""",
    )
    private val index01 = Regex(
        pattern = """(?im)^\s*INDEX\s+0?1\s+\d{2}:\d{2}:\d{2}\s*$""",
    )

    fun inspect(resolver: ContentResolver, folderUri: Uri): DiscSelection {
        val documents = listChildren(resolver, folderUri)
        val cue = documents
            .filter { it.displayName.endsWith(".cue", ignoreCase = true) }
            .sortedBy { it.displayName.lowercase(Locale.ROOT) }
            .firstOrNull()
            ?: return DiscSelection.error(
                folderUri,
                "No .cue file found",
                "Choose the folder containing your Crash Bandicoot .cue and matching .bin.",
            )

        if (cue.size > MAX_CUE_BYTES) {
            return DiscSelection.error(
                folderUri,
                "The .cue file looks invalid",
                "${cue.displayName} is too large to be a normal cue sheet.",
            )
        }

        val cueUri = DocumentsContract.buildDocumentUriUsingTree(folderUri, cue.documentId)
        val cueText = resolver.openInputStream(cueUri)
            ?.bufferedReader(Charsets.UTF_8)
            ?.use { it.readText() }
            ?: return DiscSelection.error(
                folderUri,
                "Cannot open ${cue.displayName}",
                "Android's document provider did not return a readable stream.",
            )

        val referencedPath = fileLine.find(cueText)?.groupValues?.get(1)
            ?: return DiscSelection.error(
                folderUri,
                "Broken .cue sheet",
                "The file has no FILE \"game.bin\" BINARY line.",
            )

        if (!dataTrack.containsMatchIn(cueText) || !index01.containsMatchIn(cueText)) {
            return DiscSelection.error(
                folderUri,
                "Incomplete .cue sheet",
                "A data TRACK and INDEX 01 are required.",
            )
        }

        val referencedName = referencedPath
            .replace('\\', '/')
            .substringAfterLast('/')
        val image = documents.firstOrNull {
            it.displayName.equals(referencedName, ignoreCase = true)
        } ?: return DiscSelection.error(
            folderUri,
            "Matching disc image not found",
            "${cue.displayName} points to $referencedName. Keep both files in the selected folder.",
        )

        if (image.size < MIN_BIN_BYTES) {
            val sizeMb = image.size.coerceAtLeast(0) / (1024 * 1024)
            return DiscSelection.error(
                folderUri,
                "Disc image looks incomplete",
                "${image.displayName} is only $sizeMb MB; a complete PS1 dump is much larger.",
            )
        }

        val sizeMb = image.size / (1024 * 1024)
        return DiscSelection(
            folderUri = folderUri,
            cueName = cue.displayName,
            binName = image.displayName,
            binSizeBytes = image.size,
            isReady = true,
            title = "Disc files ready",
            detail = "${cue.displayName}  •  ${image.displayName} ($sizeMb MB)\n" +
                "The full SCUS-94900 fingerprint check will be connected with the Android runtime.",
        )
    }

    private fun listChildren(resolver: ContentResolver, treeUri: Uri): List<Document> {
        val treeDocumentId = DocumentsContract.getTreeDocumentId(treeUri)
        val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, treeDocumentId)
        val projection = arrayOf(
            DocumentsContract.Document.COLUMN_DOCUMENT_ID,
            DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_MIME_TYPE,
            DocumentsContract.Document.COLUMN_SIZE,
        )
        val documents = mutableListOf<Document>()

        resolver.query(childrenUri, projection, null, null, null)?.use { cursor ->
            val idIndex = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
            val nameIndex = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
            val mimeIndex = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_MIME_TYPE)
            val sizeIndex = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_SIZE)
            while (cursor.moveToNext()) {
                documents += Document(
                    documentId = cursor.getString(idIndex),
                    displayName = cursor.getString(nameIndex) ?: "unnamed",
                    mimeType = cursor.getString(mimeIndex) ?: "application/octet-stream",
                    size = if (cursor.isNull(sizeIndex)) -1 else cursor.getLong(sizeIndex),
                )
            }
        }

        return documents.filterNot {
            it.mimeType == DocumentsContract.Document.MIME_TYPE_DIR
        }
    }

    private data class Document(
        val documentId: String,
        val displayName: String,
        val mimeType: String,
        val size: Long,
    )
}
