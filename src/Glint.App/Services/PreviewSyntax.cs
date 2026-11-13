using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Glint.App.Services;

internal static class PreviewSyntax
{
    private static readonly Lazy<IHighlightingDefinition> Config = new(() => Load("Config"));
    private static readonly Lazy<IHighlightingDefinition> Go = new(() => Load("Go"));
    private static readonly Lazy<IHighlightingDefinition> Rust = new(() => Load("Rust"));
    private static readonly Lazy<IHighlightingDefinition> Docker = new(() => Load("Docker"));

    public static IHighlightingDefinition? GetDefinition(string path, bool isEnvironmentFile)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (extension == ".go") return Go.Value;
        if (extension == ".rs") return Rust.Value;
        if (extension == ".dockerfile" || name is "dockerfile" or "containerfile") return Docker.Value;
        if (extension is ".md" or ".markdown") return HighlightingManager.Instance.GetDefinition("MarkDown");
        if (isEnvironmentFile || extension is ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg" or ".conf" or ".properties" or ".editorconfig"
            || name is ".npmrc" or ".yarnrc" or ".gitconfig" or ".gitmodules" or "pipfile" or "cargo.lock" or "poetry.lock" or "uv.lock")
            return Config.Value;

        var grammarExtension = extension switch
        {
            ".ts" or ".tsx" or ".jsx" or ".mjs" or ".cjs" or ".mts" or ".cts" => ".js",
            ".cxx" or ".hh" or ".hxx" or ".inl" => ".cpp",
            ".pyi" => ".py",
            ".vue" or ".svelte" or ".astro" or ".cshtml" or ".razor" => ".html",
            ".svg" or ".axaml" or ".slnx" or ".fsproj" or ".vcxproj" or ".props" or ".resx" or ".plist" => ".xml",
            ".jsonc" or ".json5" or ".jsonl" or ".ndjson" => ".json",
            _ => extension
        };
        if (name is "pipfile.lock" or "composer.lock" or "package.resolved" or "flake.lock") grammarExtension = ".json";
        return HighlightingManager.Instance.GetDefinitionByExtension(grammarExtension);
    }

    private static IHighlightingDefinition Load(string name)
    {
        using var stream = typeof(PreviewSyntax).Assembly.GetManifestResourceStream($"Glint.App.Assets.Preview{name}.xshd")!;
        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
