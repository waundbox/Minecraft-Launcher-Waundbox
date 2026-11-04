namespace Launcher.Tests;

internal static class TestData
{
    public const string ManifestJson = """
    {
      "latest": { "release": "1.21.1", "snapshot": "24w40a" },
      "versions": [
        { "id": "24w40a", "type": "snapshot", "url": "https://piston-meta.mojang.com/v1/packages/aaa/24w40a.json",
          "time": "2024-10-01T12:00:00+00:00", "releaseTime": "2024-10-01T12:00:00+00:00", "sha1": "aaa", "complianceLevel": 1 },
        { "id": "1.21.1", "type": "release", "url": "https://piston-meta.mojang.com/v1/packages/bbb/1.21.1.json",
          "time": "2024-08-08T12:00:00+00:00", "releaseTime": "2024-08-08T12:00:00+00:00", "sha1": "bbb", "complianceLevel": 1 },
        { "id": "b1.7.3", "type": "old_beta", "url": "https://piston-meta.mojang.com/v1/packages/ccc/b1.7.3.json",
          "time": "2011-07-08T00:00:00+00:00", "releaseTime": "2011-07-08T00:00:00+00:00", "sha1": "ccc", "complianceLevel": 0 }
      ]
    }
    """;
}

internal static class VersionTestData
{
    public static readonly string HashA = new('a', 40);
    public static readonly string HashB = new('b', 40);
    public static readonly string HashC = new('c', 40);

    public const string VersionJsonUrl = "https://piston-meta.mojang.com/v1/packages/bbb/1.21.1.json";
    public const string AssetIndexUrl = "https://piston-meta.mojang.com/v1/packages/x/17.json";
    public const string PlatformNativesWindowsUrl =
        "https://libraries.minecraft.net/org/lwjgl/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4-natives-windows.jar";

    /// <summary>Сокращённый JSON версии: обычная библиотека, natives нового стиля (по правилам) и natives старого стиля (classifiers).</summary>
    public static readonly string VersionJson = $$"""
    {
      "id": "1.21.1",
      "type": "release",
      "mainClass": "net.minecraft.client.main.Main",
      "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21 },
      "assetIndex": { "id": "17", "sha1": "{{HashA}}", "size": 100, "totalSize": 999, "url": "{{AssetIndexUrl}}" },
      "downloads": { "client": { "sha1": "{{HashB}}", "size": 5000, "url": "https://piston-data.mojang.com/v1/objects/c/client.jar" } },
      "libraries": [
        { "name": "com.mojang:logging:1.2.7",
          "downloads": { "artifact": { "path": "com/mojang/logging/1.2.7/logging-1.2.7.jar", "sha1": "{{HashA}}", "size": 100,
            "url": "https://libraries.minecraft.net/com/mojang/logging/1.2.7/logging-1.2.7.jar" } } },
        { "name": "org.lwjgl:lwjgl:3.3.3:natives-windows",
          "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar", "sha1": "{{HashA}}", "size": 100,
            "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar" } },
          "rules": [ { "action": "allow", "os": { "name": "windows" } } ] },
        { "name": "org.lwjgl:lwjgl:3.3.3:natives-linux",
          "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-linux.jar", "sha1": "{{HashA}}", "size": 100,
            "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-linux.jar" } },
          "rules": [ { "action": "allow", "os": { "name": "linux" } } ] },
        { "name": "org.lwjgl.lwjgl:lwjgl-platform:2.9.4",
          "downloads": {
            "artifact": { "path": "org/lwjgl/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4.jar", "sha1": "{{HashA}}", "size": 100,
              "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4.jar" },
            "classifiers": {
              "natives-windows": { "path": "org/lwjgl/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4-natives-windows.jar", "sha1": "{{HashA}}", "size": 100,
                "url": "{{PlatformNativesWindowsUrl}}" },
              "natives-linux": { "path": "org/lwjgl/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4-natives-linux.jar", "sha1": "{{HashA}}", "size": 100,
                "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4-natives-linux.jar" }
            } },
          "natives": { "windows": "natives-windows", "linux": "natives-linux" },
          "extract": { "exclude": [ "META-INF/" ] } }
      ]
    }
    """;

    /// <summary>Три имени ресурсов, два из них с одинаковым хешем.</summary>
    public static readonly string AssetIndexJson = $$"""
    {
      "objects": {
        "icons/icon_16x16.png": { "hash": "{{HashA}}", "size": 3665 },
        "icons/copy.png":       { "hash": "{{HashA}}", "size": 3665 },
        "minecraft/lang/ru_ru.json": { "hash": "{{HashC}}", "size": 120 }
      }
    }
    """;
}

internal static class LaunchTestData
{
    private const string MainClassLine = "\"mainClass\": \"net.minecraft.client.main.Main\",";

    /// <summary>Аргументы и конфигурация логирования в современном формате (как в JSON Mojang, в сокращённом виде).</summary>
    private static readonly string ArgumentsFragment = $$"""
      "arguments": {
        "game": [
          "--username", "${auth_player_name}", "--version", "${version_name}", "--gameDir", "${game_directory}",
          "--assetsDir", "${assets_root}", "--assetIndex", "${assets_index_name}", "--uuid", "${auth_uuid}",
          "--accessToken", "${auth_access_token}", "--userType", "${user_type}", "--versionType", "${version_type}",
          { "rules": [ { "action": "allow", "features": { "is_demo_user": true } } ], "value": "--demo" },
          { "rules": [ { "action": "allow", "features": { "has_custom_resolution": true } } ],
            "value": [ "--width", "${resolution_width}", "--height", "${resolution_height}" ] }
        ],
        "jvm": [
          { "rules": [ { "action": "allow", "os": { "name": "osx" } } ], "value": [ "-XstartOnFirstThread" ] },
          { "rules": [ { "action": "allow", "os": { "name": "windows" } } ], "value": "-XX:HeapDumpPath=MojangTricks.heapdump" },
          "-Djava.library.path=${natives_directory}",
          "-Dminecraft.launcher.brand=${launcher_name}",
          "-Dminecraft.launcher.version=${launcher_version}",
          "-cp", "${classpath}"
        ]
      },
      "logging": { "client": { "argument": "-Dlog4j.configurationFile=${path}",
        "file": { "id": "client-1.12.xml", "sha1": "{{VersionTestData.HashA}}", "size": 888,
                  "url": "https://piston-data.mojang.com/v1/objects/x/client-1.12.xml" },
        "type": "log4j2-xml" } },
    """;

    /// <summary>JSON версии 1.21.1 с аргументами запуска и логированием.</summary>
    public static readonly string VersionJson = VersionTestData.VersionJson.Replace(MainClassLine, MainClassLine + "\n" + ArgumentsFragment);

    public const string LegacyJson = """
    { "id": "1.7.10", "minecraftArguments": "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} --assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid} --accessToken ${auth_access_token} --userProperties ${user_properties} --userType ${user_type}" }
    """;
}

internal static class FabricTestData
{
    public const string ChildId = "fabric-loader-0.19.5-1.21.1";
    public const string AsmUrl = "https://maven.fabricmc.net/org/ow2/asm/asm/9.10.1/asm-9.10.1.jar";
    public const string LoaderUrl = "https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar";

    /// <summary>Профиль в формате meta.fabricmc.net (сокращённый): библиотеки заданы координатами Maven, у части нет sha1/size.</summary>
    public static readonly string ProfileJson = $$"""
    {
      "id": "{{ChildId}}",
      "inheritsFrom": "1.21.1",
      "releaseTime": "2026-10-14T05:28:22+0000",
      "type": "release",
      "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
      "arguments": { "game": [], "jvm": ["-DFabricMcEmu= net.minecraft.client.main.Main "] },
      "libraries": [
        { "name": "org.ow2.asm:asm:9.10.1", "url": "https://maven.fabricmc.net/", "sha1": "{{VersionTestData.HashA}}", "size": 40 },
        { "name": "com.mojang:logging:9.9.9", "url": "https://maven.fabricmc.net/", "size": 7 },
        { "name": "net.fabricmc:fabric-loader:0.19.5", "url": "https://maven.fabricmc.net/" }
      ]
    }
    """;

    public const string LoadersJson = """
    [
      { "loader": { "separator": ".", "build": 5, "maven": "x", "version": "0.19.5", "stable": true },
        "intermediary": { "version": "1.21.1", "stable": true }, "launcherMeta": {} },
      { "loader": { "version": "0.19.6-beta.1", "stable": false } },
      { "loader": { "version": "0.19.4", "stable": true } }
    ]
    """;
}

internal static class ForgeTestData
{
    public const string ForgeId = "1.21.1-forge-52.1.0";

    public const string PromotionsJson = """
    { "homepage": "https://files.minecraftforge.net/", "promos": {
        "1.21.1-latest": "52.1.19", "1.21.1-recommended": "52.1.0", "1.20.1-latest": "47.4.28" } }
    """;

    /// <summary>version.json внутри установщика (сокращённо): основной JAR Forge без адреса — его создаёт сам установщик.</summary>
    public static readonly string VersionJson = $$"""
    {
      "id": "{{ForgeId}}",
      "inheritsFrom": "1.21.1",
      "type": "release",
      "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
      "arguments": { "game": ["--launchTarget", "forgeclient"],
                     "jvm": ["-DlibraryDirectory=${library_directory}", "-p", "${library_directory}/a.jar${classpath_separator}${library_directory}/b.jar"] },
      "libraries": [
        { "name": "net.minecraftforge:forge:1.21.1-52.1.0:universal",
          "downloads": { "artifact": { "path": "net/minecraftforge/forge/1.21.1-52.1.0/forge-1.21.1-52.1.0-universal.jar",
                                       "url": "", "sha1": "{{VersionTestData.HashA}}", "size": 9 } } },
        { "name": "cpw.mods:bootstraplauncher:1.1.2",
          "downloads": { "artifact": { "path": "cpw/mods/bootstraplauncher/1.1.2/bootstraplauncher-1.1.2.jar",
                                       "url": "https://maven.minecraftforge.net/cpw/mods/bootstraplauncher/1.1.2/bootstraplauncher-1.1.2.jar",
                                       "sha1": "{{VersionTestData.HashB}}", "size": 5 } } }
      ]
    }
    """;
}
