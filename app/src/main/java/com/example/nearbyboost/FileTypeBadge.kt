package com.example.nearbyboost

/** A small colored label standing in for a file-type icon (e.g. "IMG" on a blue chip). */
object FileTypeBadge {
    data class Badge(val label: String, val colorHex: String)

    private val imageExt = setOf("jpg", "jpeg", "png", "gif", "webp", "bmp", "heic", "svg")
    private val videoExt = setOf("mp4", "mkv", "mov", "avi", "webm", "m4v", "3gp")
    private val audioExt = setOf("mp3", "wav", "aac", "flac", "ogg", "m4a")
    private val docExt = setOf("doc", "docx", "txt", "rtf", "odt")
    private val pdfExt = setOf("pdf")
    private val sheetExt = setOf("xls", "xlsx", "csv")
    private val slideExt = setOf("ppt", "pptx")
    private val archiveExt = setOf("zip", "rar", "7z", "tar", "gz")
    private val codeExt = setOf("kt", "java", "cs", "py", "js", "ts", "json", "xml", "html", "css", "gradle", "kts")

    fun forFile(name: String): Badge {
        val ext = name.substringAfterLast('.', "").lowercase()
        return when (ext) {
            in imageExt -> Badge("IMG", "#2E86AB")
            in videoExt -> Badge("VID", "#EF476F")
            in audioExt -> Badge("AUD", "#06D6A0")
            in pdfExt -> Badge("PDF", "#E63946")
            in docExt -> Badge("DOC", "#118AB2")
            in sheetExt -> Badge("XLS", "#2A9D8F")
            in slideExt -> Badge("PPT", "#F4A261")
            in archiveExt -> Badge("ZIP", "#6A4C93")
            in codeExt -> Badge("DEV", "#264653")
            else -> Badge("FILE", "#6C757D")
        }
    }

    fun forFolder(): Badge = Badge("DIR", "#FFB703")
}
