using NebulaLauncher.Core.Java;

namespace NebulaLauncher.Tests;

/// <summary>
/// Mojang java-runtime 清单的解析用例。两个解析函数都是纯函数，
/// 这里用内置 JSON 片段覆盖「组件缺失 / 平台缺失 / 条目类型」几类真实分支。
/// </summary>
public sealed class CoreAssets_MojangRuntimeTests
{
    [Theory]
    [InlineData(8, "jre-legacy")]
    [InlineData(16, "java-runtime-alpha")]
    [InlineData(17, "java-runtime-beta")]
    [InlineData(21, "java-runtime-delta")]
    [InlineData(25, "java-runtime-epsilon")]
    [InlineData(11, "")]
    [InlineData(22, "")]
    public void ComponentFor_MapsOnlyTheFivePublishedComponents(int major, string expected)
        => Assert.Equal(expected, MojangRuntimeCatalog.ComponentFor(major));

    [Theory]
    [InlineData(true, false, true, false, "windows-x64")]
    [InlineData(true, false, false, false, "windows-x86")]
    [InlineData(true, false, true, true, "windows-arm64")]
    [InlineData(false, true, true, false, "mac-os")]
    [InlineData(false, true, true, true, "mac-os-arm64")]
    [InlineData(false, false, true, false, "linux")]
    [InlineData(false, false, false, false, "linux-i386")]
    public void PlatformKeyFor_MatchesMojangKeys(
        bool isWindows,
        bool isMacOs,
        bool is64Bit,
        bool isArm64,
        string expected)
        => Assert.Equal(expected, MojangRuntimeCatalog.PlatformKeyFor(isWindows, isMacOs, is64Bit, isArm64));

    [Fact]
    public void ParseManifestUrl_PicksFirstEntry()
    {
        const string json = """
        {
          "windows-x64": {
            "java-runtime-delta": [
              { "manifest": { "url": "https://piston-meta.mojang.com/first.json", "sha1": "a" } },
              { "manifest": { "url": "https://piston-meta.mojang.com/second.json", "sha1": "b" } }
            ]
          }
        }
        """;

        Assert.Equal(
            "https://piston-meta.mojang.com/first.json",
            MojangRuntimeCatalog.ParseManifestUrl(json, "windows-x64", "java-runtime-delta"));
    }

    [Fact]
    public void ParseManifestUrl_ReturnsNullWhenComponentOrPlatformMissing()
    {
        const string json = """{ "windows-x64": { "java-runtime-delta": [] } }""";

        Assert.Null(MojangRuntimeCatalog.ParseManifestUrl(json, "windows-x64", "java-runtime-delta"));
        Assert.Null(MojangRuntimeCatalog.ParseManifestUrl(json, "windows-x64", "java-runtime-epsilon"));
        Assert.Null(MojangRuntimeCatalog.ParseManifestUrl(json, "mac-os", "java-runtime-delta"));
        Assert.Null(MojangRuntimeCatalog.ParseManifestUrl(null, "windows-x64", "java-runtime-delta"));
    }

    [Fact]
    public void ParseManifestUrl_SurvivesBrokenJson()
    {
        Assert.Null(MojangRuntimeCatalog.ParseManifestUrl("{ not json", "windows-x64", "java-runtime-delta"));
    }

    [Fact]
    public void ParseManifest_ReadsFileDirectoryAndLinkEntries()
    {
        const string json = """
        {
          "files": {
            "bin": { "type": "directory" },
            "bin/java": {
              "type": "file",
              "executable": true,
              "downloads": {
                "raw": {
                  "url": "https://piston-meta.mojang.com/raw/java",
                  "sha1": "0123456789abcdef",
                  "size": 12345
                }
              }
            },
            "lib/libjli.dylib": { "type": "link", "target": "../bin/java" },
            "unknown": { "type": "mystery" }
          }
        }
        """;

        var files = MojangRuntimeCatalog.ParseManifest(json);

        // 未知 type 被忽略：清单里出现新类型时宁可少装一个文件，也不要按文件路径去下载它。
        Assert.Equal(3, files.Count);

        var directory = Assert.Single(files, file => file.IsDirectory);
        Assert.Equal("bin", directory.RelativePath);

        var executable = Assert.Single(files, file => file.IsExecutable);
        Assert.Equal("bin/java", executable.RelativePath);
        Assert.Equal("0123456789abcdef", executable.Sha1);
        Assert.Equal(12345, executable.Size);
        Assert.Equal("https://piston-meta.mojang.com/raw/java", executable.Url);

        var link = Assert.Single(files, file => file.LinkTarget is not null);
        Assert.Equal("../bin/java", link.LinkTarget);
        Assert.Null(link.Url);

        // 目录排在文件前面，调用方可以顺序建目录再下载。
        Assert.True(files[0].IsDirectory);
    }

    [Fact]
    public void ParseManifest_ReturnsEmptyOnMissingFilesNode()
    {
        Assert.Empty(MojangRuntimeCatalog.ParseManifest("{}"));
        Assert.Empty(MojangRuntimeCatalog.ParseManifest("not json"));
        Assert.Empty(MojangRuntimeCatalog.ParseManifest(null));
    }
}
