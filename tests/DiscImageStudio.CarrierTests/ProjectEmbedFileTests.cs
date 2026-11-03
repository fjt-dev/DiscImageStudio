using System.Text.Json;
using DiscImageStudio.Core;

/// <summary>The embedded file travels with the project; older projects must keep loading.</summary>
internal static class ProjectEmbedFileTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "carrier-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ProjectDocument withFile = Document(new ProjectOutputs("out.iso", false, "data", "LABEL", false,
                FilesAtOuterRing: true, EmbedFile: "secret/日本語 file.zip"));
            string path = Path.Combine(directory, "with.discproj");
            ProjectJsonStore.Save(path, withFile);
            ProjectDocument loaded = ProjectJsonStore.Load(path);
            Check.Equal("secret/日本語 file.zip", loaded.Outputs.EmbedFile, "the embedded file path survives a save and load");
            Check.True(loaded.Outputs.FilesAtOuterRing && loaded.Outputs.DataDirectory == "data",
                "neighbouring output settings are untouched");

            // Without an embedded file the key is not written at all, so files saved by this
            // version stay readable by builds that do not know it.
            ProjectDocument without = Document(new ProjectOutputs("out.iso", false, "", "LABEL", false));
            string plain = Path.Combine(directory, "without.discproj");
            ProjectJsonStore.Save(plain, without);
            using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(plain)))
            {
                Check.True(!json.RootElement.GetProperty("outputs").EnumerateObject()
                    .Any(property => property.Name.Equals("embedFile", StringComparison.OrdinalIgnoreCase)),
                    "no embedFile key for projects that do not use it");
            }

            Check.True(ProjectJsonStore.Load(plain).Outputs.EmbedFile is null, "a project without the key loads with none");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("project-embed-file: passed (round trip, key omitted when unused)");
    }

    private static ProjectDocument Document(ProjectOutputs outputs)
        => new(ProjectJsonStore.CurrentSchemaVersion, "embed", ProjectDocument.DvdFamily,
            new ProjectGeometry(24.0, 58.0, 2_295_104, 0, 0, 0), [], outputs,
            CalibrationSessionPath: null, ThumbnailPngBase64: null,
            CreatedUtc: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            ModifiedUtc: new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));
}
