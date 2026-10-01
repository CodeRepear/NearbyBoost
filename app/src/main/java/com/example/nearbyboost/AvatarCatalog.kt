package com.example.nearbyboost

/** 24 built-in avatar options — no photo upload, just pick one. Index is what gets persisted. */
object AvatarCatalog {
    data class Avatar(val emoji: String, val colorHex: String)

    val options = listOf(
        Avatar("\uD83E\uDD8A", "#FF6B6B"), // fox
        Avatar("\uD83D\uDC31", "#4ECDC4"), // cat
        Avatar("\uD83D\uDC36", "#FFD166"), // dog
        Avatar("\uD83D\uDC3C", "#06D6A0"), // panda
        Avatar("\uD83E\uDD81", "#118AB2"), // lion
        Avatar("\uD83D\uDC28", "#EF476F"), // koala
        Avatar("\uD83D\uDC38", "#8AC926"), // frog
        Avatar("\uD83E\uDD89", "#7C5CFC"), // owl
        Avatar("\uD83D\uDE80", "#FF9F1C"), // rocket
        Avatar("\u2B50", "#2EC4B6"),       // star
        Avatar("\uD83C\uDF19", "#E71D36"), // moon
        Avatar("\uD83D\uDD25", "#FF4365"), // fire
        Avatar("\uD83C\uDF08", "#00A8CC"), // rainbow
        Avatar("\uD83C\uDFA9", "#6A4C93"), // hat
        Avatar("\uD83C\uDFA7", "#1B998B"), // headphones
        Avatar("\uD83C\uDFAE", "#F46036"), // game
        Avatar("\uD83D\uDCF7", "#2E86AB"), // camera
        Avatar("\uD83C\uDF55", "#C73E1D"), // pizza
        Avatar("\uD83C\uDF69", "#F4A261"), // donut
        Avatar("\uD83D\uDC27", "#264653"), // penguin
        Avatar("\uD83D\uDC22", "#588157"), // turtle
        Avatar("\uD83E\uDD84", "#B5838D"), // unicorn
        Avatar("\uD83D\uDC19", "#6D597A"), // octopus
        Avatar("\uD83D\uDC1D", "#EE9B00")  // bee
    )

    fun get(index: Int): Avatar = options[index.coerceIn(options.indices)]
}
