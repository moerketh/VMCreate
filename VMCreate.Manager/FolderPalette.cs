namespace VMCreate.Manager
{
    /// <summary>A choice in the folder "Color" menu; a null Hex clears the colour.</summary>
    public sealed record FolderColorOption(string Name, string? Hex);

    /// <summary>Folder icon colours, picked to stay readable on the dark theme.</summary>
    public static class FolderPalette
    {
        public static IReadOnlyList<FolderColorOption> Options { get; } = new[]
        {
            new FolderColorOption("Red", "#E5534B"),
            new FolderColorOption("Orange", "#E8912D"),
            new FolderColorOption("Yellow", "#E3C14B"),
            new FolderColorOption("Green", "#4CC24C"),
            new FolderColorOption("Teal", "#2BB5A6"),
            new FolderColorOption("Blue", "#5B9BE6"),
            new FolderColorOption("Purple", "#A371F7"),
            new FolderColorOption("Pink", "#DB61A2"),
            new FolderColorOption("No color", null),
        };
    }
}
