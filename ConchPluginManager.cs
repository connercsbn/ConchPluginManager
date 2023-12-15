using System.Text.Json;
using System.IO.Compression;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using Microsoft.Extensions.Logging;

namespace ConchPluginManager;



[MinimumApiVersion(124)]
public class ConchPluginManager : BasePlugin, IPluginConfig<ConchPluginManagerConfig>
{
    public override string ModuleName => "Conch Plugin Manager";

    public override string ModuleVersion => "0.0.1";
    public override string ModuleAuthor => "Conch"; 
    string? configPath;

    public ConchPluginManagerConfig Config { get; set; } = new();

    public void OnConfigParsed(ConchPluginManagerConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        if (hotReload) Logger.LogInformation("Conch Plugin Manager reloaded");
            
        string pluginDirName = "ConchPluginManager";
        var counterStrikeSharpDir = (new DirectoryInfo(ModulePath).Parent!.Parent!.Parent!).FullName;
        configPath = Path.Combine(counterStrikeSharpDir, "configs", "plugins", pluginDirName, pluginDirName + ".json")!;
        Logger.LogInformation($"configPath: {configPath}");
        if (!File.Exists(configPath)) throw new Exception("config not found"); 
        CheckForUpdates();
    }
    public async void CheckForUpdates()
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "connercsbn/ConchPluginManager");
        foreach (var plugin in Config.PluginsInstalled)
        {
            if (!plugin.AutoUpdate) continue;
            try
            {
                // for now, just download from latest release. TODO handle plugins that are released through the repository 
                Logger.LogInformation($"querying https://api.github.com/repos/{plugin.Plugin}/releases/latest");
                HttpResponseMessage response = await httpClient.GetAsync($"https://api.github.com/repos/{plugin.Plugin}/releases/latest");
                var res = JsonSerializer.Deserialize<GithubApiResponse>(await response.Content.ReadAsStringAsync());
                if (String.IsNullOrEmpty(res!.TagName)) continue;
                if (res.TagName != plugin.TagName)
                {
                    Logger.LogInformation($"Plugin {plugin.Plugin} is not up to date! \"Downloading new release {res!.TagName} from {res!.Assets[0].BrowserDownloadUrl}\"");
                    var extractPath = await Download(plugin, res);
                    Merge(extractPath);
                    Directory.Delete(extractPath, true);
                    // TODO only update tag if merge successful?
                    plugin.TagName = res!.TagName;
                    File.WriteAllText(configPath!, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception ex)
            {
                Logger.LogInformation("An error occurred: {message}", ex.Message);
            }
        }
    }
    private void Merge(string extractPath)
    // sync to server files
    // any conflicting files will be overwritten, could this go wrong?
    // what if plugin author changes file/dir name and we end up with two versions of same plugin?
    {
        var csgo = new DirectoryInfo(ModulePath)!.Parent!.Parent!.Parent!.Parent!.Parent!;
        var plugins = new DirectoryInfo(ModulePath)!.Parent!.Parent!; 

        string[] targetSubDirs = { "csgo", "addons", "counterstrikesharp", "plugins" };
        for (int i = 0; i < targetSubDirs.Length; i++) 
        { 
            Logger.LogInformation("extract path: {ep}. searching for {td}", extractPath, targetSubDirs[i]);
            string[] matchedDirectories = Directory.GetDirectories(extractPath, targetSubDirs[i], SearchOption.AllDirectories);
            if (matchedDirectories.Length == 0) continue; 
            Logger.LogInformation("found common directory: {dir}", targetSubDirs[i]);
            foreach (var matchedDir in matchedDirectories)
            { 
                Logger.LogInformation(matchedDir);
                var pathToPluginsDir = Path.Combine(matchedDir, Path.Join(targetSubDirs.Skip(i + 1).ToArray())); 
                Logger.LogInformation(pathToPluginsDir);
                if (Directory.Exists(pathToPluginsDir))
                {
                    var parentOfMatchedDir = Path.GetDirectoryName(matchedDir);
                    Logger.LogInformation("parentOfMatchedDir: {dir}", parentOfMatchedDir);
                    foreach (var file in Directory.GetFileSystemEntries(parentOfMatchedDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        var destination = Path.Join(targetSubDirs.Skip(1).Take(i - 1).ToArray());
                        Logger.LogInformation("{file} ----> {destination_dir}", file, Path.Combine(csgo.FullName, destination));
                    }
                    return;
                } else
                {
                    Logger.LogInformation("directory {dir} doesn't exist", pathToPluginsDir);
                }
            }
        }
        // there are no common directories, so we find the plugin and put it in plugins dir
        var dlls = Directory.GetFiles(extractPath, "*.dll", SearchOption.AllDirectories);
        Logger.LogInformation("dlls found: {dlls}", string.Join(", ", dlls)); 
        // assuming all dlls are in the same directory...
        var pluginDir = Path.GetDirectoryName(dlls[0]);
        Logger.LogInformation("{dir} ----> {plugins_dir}", pluginDir, plugins);
    } 

    private async Task<string?> Download(PluginInfo plugin, GithubApiResponse ghRes)
    // check if enough size for download (overkill?)
    // download to temp
    {
        using HttpClient httpClient = new();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "connercsbn/ConchPluginManager");
        try
        {
            // for now, we problematically assume the download is in assets[0] and that it's a zip file 
            HttpResponseMessage res = await httpClient.GetAsync(ghRes.Assets[0].BrowserDownloadUrl); 
            if (res.IsSuccessStatusCode)
            {
                string fileName = res.Content.Headers.ContentDisposition?.FileName ?? $"{plugin.Plugin.Split('/')[1]}.zip";

                string tempDirectory = Path.Combine(Path.GetTempPath(), "ConchPluginManagerDownloads");
                Directory.CreateDirectory(tempDirectory); 
                string filePath = Path.Combine(tempDirectory, fileName);

                using (Stream contentStream = await res.Content.ReadAsStreamAsync(),
                              fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var buffer = new byte[1024 * 1024];
                    int bytesRead;
                    long totalBytesRead = 0;
                    long totalBytes = res.Content.Headers.ContentLength.GetValueOrDefault(); 

                    while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)); 
                        totalBytesRead += bytesRead; 
                        double percentage = ((double)totalBytesRead / totalBytes) * 100;
                        Logger.LogInformation("Downloaded: {totalBytesRead}/{totalBytes} bytes ({percentage}%)", totalBytesRead, totalBytes, percentage);
                    }
                }

                Logger.LogInformation("File downloaded successfully: {path}", filePath);
                var extractPath = Path.Combine(tempDirectory, Path.GetFileNameWithoutExtension(filePath));
                ZipFile.ExtractToDirectory(filePath, extractPath);
                Logger.LogInformation("Extracted file {path} to {dir}", filePath, extractPath);
                return extractPath;
            }
            else
            {
                Logger.LogInformation("Error downloading file while updating {plugin}. Status code: {status code}", plugin.Plugin, res.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.LogInformation("Error downloading file: {message}", ex.Message);
        }
        return null;
    } 
}

public class PluginInfo
{
    public string Plugin { get; set; } = "connercsbn/ConchPluginManager";
    public string? TagName { get; set; } = "v0.0.1";
    public bool AutoUpdate { get; set; } = true;
}

public class ConchPluginManagerConfig : BasePluginConfig
{
    // examples for testing
    public List<PluginInfo> PluginsInstalled { get; set; } = new List<PluginInfo>
    { 
        new PluginInfo
        {
            Plugin = "charliethomson/DeathmatchPlugin",
            TagName = "v0"
        },
        new PluginInfo
        {
            Plugin = "justinnobledev/cs2-mapchooser",
            TagName = "v0"
        },
        new PluginInfo
        {
            Plugin = "dran1x/CS2-AutoUpdater",
            TagName = "v0"
        }, 
        new PluginInfo
        {
            Plugin = "Iksix/NameChecker-cs2",
            TagName = "v0"
        }, 
        new PluginInfo
        { 
            Plugin = "NockyCZ/CS2_AntiVPN",
            TagName = "v0"
        },
        new PluginInfo
        { 
            Plugin = "onurcanertekin/cs2-simple-discord-report",
            TagName = "v0"
        },
        new PluginInfo
        { 
            Plugin = "daffyyyy/CS2-RecordAbuse",
            TagName = "v0"
        }
    };
} 