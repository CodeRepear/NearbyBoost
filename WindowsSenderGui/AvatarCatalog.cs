using System.Collections.Generic;

namespace NearbyBoostSenderGui;

public record AvatarOption(string Emoji, string ColorHex);

/// <summary>24 built-in avatar options — no photo upload, just pick one. Index is what gets persisted.</summary>
public static class AvatarCatalog
{
    public static readonly List<AvatarOption> Options = new()
    {
        new("\U0001F98A", "#FF6B6B"), // fox
        new("\U0001F431", "#4ECDC4"), // cat
        new("\U0001F436", "#FFD166"), // dog
        new("\U0001F43C", "#06D6A0"), // panda
        new("\U0001F981", "#118AB2"), // lion
        new("\U0001F428", "#EF476F"), // koala
        new("\U0001F438", "#8AC926"), // frog
        new("\U0001F989", "#7C5CFC"), // owl
        new("\U0001F680", "#FF9F1C"), // rocket
        new("\u2B50", "#2EC4B6"),     // star
        new("\U0001F319", "#E71D36"), // moon
        new("\U0001F525", "#FF4365"), // fire
        new("\U0001F308", "#00A8CC"), // rainbow
        new("\U0001F3A9", "#6A4C93"), // hat
        new("\U0001F3A7", "#1B998B"), // headphones
        new("\U0001F3AE", "#F46036"), // game
        new("\U0001F4F7", "#2E86AB"), // camera
        new("\U0001F355", "#C73E1D"), // pizza
        new("\U0001F369", "#F4A261"), // donut
        new("\U0001F427", "#264653"), // penguin
        new("\U0001F422", "#588157"), // turtle
        new("\U0001F984", "#B5838D"), // unicorn
        new("\U0001F419", "#6D597A"), // octopus
        new("\U0001F41D", "#EE9B00")  // bee
    };

    public static AvatarOption Get(int index)
    {
        if (index < 0 || index >= Options.Count) index = 0;
        return Options[index];
    }
}
