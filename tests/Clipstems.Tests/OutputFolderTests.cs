using Clipstems.Core;

namespace Clipstems.Tests;

public class OutputFolderTests
{
    private static readonly string Temp = Path.Combine(Path.GetTempPath(), "clipstems-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_new_folder_under_an_existing_one_is_usable_and_is_not_created_by_checking()
    {
        var folder = Path.Combine(Temp, "Clips", "Console", "COM10"); // look like reserved names but aren't
        Assert.True(ClipstemsPaths.IsUsableOutputFolder(folder));
        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData(@"Videos\Clipstems")] // relative
    [InlineData(@"C:\Clips\bad*name")] // valid in a path, not in a folder name
    [InlineData(@"C:\Clips\what?")]
    [InlineData(@"C:\Clips\trailing.")]
    [InlineData(@"C:\NotYetCreated\bad*name")] // checked past the first folder that doesn't exist yet
    [InlineData(@"C:\Clips\CON\Highlights")] // reserved device names, any case, with or without an extension
    [InlineData(@"C:\Clips\nul.txt")]
    [InlineData(@"C:\Clips\Lpt3")]
    public void Paths_windows_cannot_create_are_refused(string path) =>
        Assert.False(ClipstemsPaths.IsUsableOutputFolder(path));

    [Fact]
    public void A_file_or_a_folder_inside_a_file_is_refused()
    {
        Directory.CreateDirectory(Temp);
        var file = Path.Combine(Temp, "notes.txt");
        File.WriteAllText(file, "x");

        Assert.False(ClipstemsPaths.IsUsableOutputFolder(file));
        Assert.False(ClipstemsPaths.IsUsableOutputFolder(Path.Combine(file, "Clips")));
    }

    [Fact]
    public void A_drive_that_does_not_exist_is_refused()
    {
        var missing = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\")
            .FirstOrDefault(d => !Directory.Exists(d));
        if (missing is null) return; // every letter mapped; nothing to test on this machine
        Assert.False(ClipstemsPaths.IsUsableOutputFolder(Path.Combine(missing, "Clips")));
    }
}
