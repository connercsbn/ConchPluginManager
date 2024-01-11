using System.Text.Json;
using System.IO.Compression;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API;
using System.Text.Json.Serialization;

namespace ConchPluginManager;


 [MinimumApiVersion(143)]
public class ConchPluginManager : BasePlugin, IPluginConfig<ConchPluginManagerConfig>
{
    public override string ModuleName => "Conch Plugin Manager";

    public override string ModuleVersion => "0.1.1";
    public override string ModuleAuthor => "Conch"; 

    private static readonly HttpClient httpClient = new ();
    public ConchPluginManagerConfig Config { get; set; } = new ();
    public Manifest Manifest { get; set; } = new ();
    public string? manifestPath; 
    public DirectoryInfo? gameDir;
    public DirectoryInfo? pluginsDir;

    public void OnConfigParsed(ConchPluginManagerConfig config)
    { 
        if (config.Version < 2)
        {
            throw new Exception("Conch Plugin Manager config is outdated. Delete old config and restart the plugin");
        }
        Config = config;
    }

    [ConsoleCommand("css_cpm_list", "List installed plugins")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandList(CCSPlayerController? _, CommandInfo command)
    {
        foreach (var plugin in Manifest.PluginsInstalled)
        {
            command.ReplyToCommand(plugin.ToString());
        }
    }

    [ConsoleCommand("css_cpm_update_all", "Update all")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public async void OnCommandUpdateAll(CCSPlayerController? _, CommandInfo command)
    {
        await CheckForUpdates();
    }

    [ConsoleCommand("css_cpm_install", "Install a plugin")]
    [CommandHelper(minArgs: 1, usage: "<github_author>/<repository_name>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public async void OnCommandInstall(CCSPlayerController? _, CommandInfo command)
    {
        var downloadString = command.GetArg(1);
        if (Manifest.PluginsInstalled.Exists(plugin => plugin.DownloadString == downloadString))
        {
            command.ReplyToCommand($"Plugin {downloadString} already installed");
            return;
        }
        var newPlugin = new PluginInfo(downloadString); 

        if (!await CheckForUpdate(newPlugin, httpClient))
        {
            Logger.LogInformation("Failed to install plugin.");
            return;
        }
        Manifest.PluginsInstalled.Add(newPlugin);
        WriteManifest();
        Logger.LogInformation("loading plugin {plugin}", newPlugin.Directory);
        Server.NextFrame(() => Server.ExecuteCommand($"css_plugins load {newPlugin.Directory}"));
    }

    [ConsoleCommand("css_cpm_remove", "Remove a plugin")]
    [CommandHelper(minArgs: 1, usage: "<plugin download_string/directory>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandRemove(CCSPlayerController? _, CommandInfo command)
    {
        var arg = command.GetArg(1);
        var pluginToRemove = Manifest.PluginsInstalled.Find((plugin) => plugin.DownloadString == arg);
        pluginToRemove ??= Manifest.PluginsInstalled.Find((plugin) => plugin.Directory == arg);
        if (pluginToRemove == null)
        { 
            Logger.LogInformation("Plugin {plugin} not found", arg);
            return;
        }
        Logger.LogInformation("Removing plugin {plugin}", arg);
        var pluginToRemovePath = Path.Join(pluginsDir!.FullName, pluginToRemove.Directory);
        if (Directory.Exists(pluginToRemovePath))
        {
            Logger.LogInformation("Deleting directory {dir}", pluginToRemovePath);
            Directory.Delete(pluginToRemovePath, true);
            Logger.LogInformation("Plugin directory {dir} has been deleted. You may need to unload the plugin with css_plugins unload <plugin_name>", pluginToRemove.Directory);
        }
        else
        {
            Logger.LogInformation("couldn't find directory {plugin} in {plugins}", pluginToRemove.Directory, pluginsDir.FullName);
        }
        Manifest.PluginsInstalled.Remove(pluginToRemove);
        WriteManifest();
    } 


    public override async void Load(bool hotReload)
    {
        gameDir = new DirectoryInfo(ModulePath).Parent?.Parent?.Parent?.Parent?.Parent?.Parent;
        pluginsDir = new DirectoryInfo(ModulePath)?.Parent?.Parent; 
 
        if (gameDir == null) throw new Exception("'game' directory not found");
        if (pluginsDir == null) throw new Exception("counterstrikesharp/plugins directory not found");

        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ConchPluginManager", ModuleVersion)); 
        if (!String.IsNullOrEmpty(Config.GithubAuthToken))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Config.GithubAuthToken);
            if (!await TestAuth()) return;
        } 
        manifestPath = Path.Join(ModuleDirectory, "plugins.json");
        if (!LoadManifest()) return;

        var updateOnReload = Config?.UpdateOnReload ?? false;
        var updateOnServerStart = Config?.UpdateOnServerStart ?? false;
        var updateOnMapChange = Config?.UpdateOnMapChange ?? false;

        if ((updateOnServerStart && !hotReload) || (updateOnReload && hotReload))
            await CheckForUpdates();

        if (updateOnMapChange && !hotReload) RegisterListener<Listeners.OnMapEnd>(async () => await CheckForUpdates());
    }

    public async Task<bool> TestAuth()
    {
        var response = await httpClient.GetAsync("https://api.github.com/user");
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            Logger.LogError("{code}: Github auth token didn't work. Try restarting plugin with a different token or get rid of token in config.", response.StatusCode);
            return false;
        } 
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            Logger.LogError("{code}: Test Github reequest didn't work. Response: {response}", response.StatusCode, errorContent);
            return false;
        } 
        Logger.LogInformation("Github auth token successful!");
        return true;
    }


    public bool LoadManifest()
    {
        if (File.Exists(manifestPath)) {
            try
            {
                Manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions() { ReadCommentHandling = JsonCommentHandling.Skip })!;
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("{exception}\nFailed to parse manifest (plugins.json)", ex);
            }
        } else
        { 
            try
            {
                WriteManifest();
                return true;
            } catch (Exception ex)
            {
                Logger.LogError("{exception}\nFailed to generate manifest (plugins.json)", ex);
            }
        };
        return false;
    }

    public async Task CheckForUpdates()
    {
        foreach (var plugin in Manifest.PluginsInstalled)
        {
            if (!plugin.AutoUpdate) continue;
            await CheckForUpdate(plugin, httpClient); 
        }
        WriteManifest();
    }

    private void WriteManifest()
    { 
        File.WriteAllText(manifestPath!, JsonSerializer.Serialize(Manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<bool> CheckForUpdate(PluginInfo plugin, HttpClient httpClient)
    {
        var githubLink = $"https://api.github.com/repos/{plugin.DownloadString}/releases/latest";
        try
        {
            Logger.LogInformation("querying https://api.github.com/repos/{plugin}/releases/latest", plugin.DownloadString);
            HttpResponseMessage response = await httpClient.GetAsync(githubLink);
            response.EnsureSuccessStatusCode();
            try
            { 
                var res = JsonSerializer.Deserialize<GithubApiResponse>(await response.Content.ReadAsStringAsync());
                if (res == null || String.IsNullOrEmpty(res!.TagName))
                { 
                    Logger.LogError("Couldn't retrieve Github release information after querying {download_string}", plugin.DownloadString);
                    return false;
                }
                if (res.TagName == plugin.TagName)
                {
                    Logger.LogInformation("Plugin {plugin} up to date", plugin.DownloadString);
                    return false;
                }
                Logger.LogInformation("Plugin {plugin} is not up to date! Downloading new release {tag_name} from {url}", plugin.DownloadString, res!.TagName, res!.Assets.First().BrowserDownloadUrl);
                var extractPath = await Download(plugin, res);
                try
                {
                    var pluginDir = GetPluginDir(extractPath);
                    if (pluginDir == null)
                    {
                        Logger.LogError("Couldn't find plugin directory with matching dll. Aborting installation.");
                        return false;
                    }
                    var pluginDirName = new DirectoryInfo(pluginDir).Name;
                    if (!String.IsNullOrEmpty(plugin.Directory) && plugin.Directory != pluginDirName)
                    {
                        Logger.LogError("New version of {plugin} uses a different directory from before. OLD: {old_dir} | NEW: {new_dir}", plugin.TagName, plugin.Directory, pluginDirName);
                        return false;
                    }
                    Merge(extractPath, pluginDir);
                    plugin.Directory ??= pluginDirName;
                    Logger.LogInformation("updating tag name from {previous_tag} to {next_tag}", plugin.TagName, res.TagName);
                    plugin.TagName = res.TagName;
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogError("{error_message}", ex.Message);
                }
            }
            catch (HttpRequestException ex)
            { 
                Logger.LogError("{error_message}", ex.Message);
                Logger.LogError("{response_content}", await response.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                Logger.LogError("{error_message}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("An error occurred while querying {github_link}:\n {message}", githubLink, ex.Message);
        }
        return false;
    }
    private void Merge(string extractPath, string pluginDir)
    {
        Logger.LogInformation("Merging");

        // check for matching directory structure in download
        // if there's a match, merge on the outer-most matching directory
        string[] targetSubDirs = { "csgo", "addons", "counterstrikesharp", "plugins" };
        for (int i = 0; i < targetSubDirs.Length; i++) 
        { 
            Logger.LogInformation("Extract path: {ep}. searching for {td}", extractPath, targetSubDirs[i]);
            string[] matchedDirectories = Directory.GetDirectories(extractPath, targetSubDirs[i], SearchOption.AllDirectories);
            if (matchedDirectories.Length == 0) continue; 
            Logger.LogInformation("Found common directory: {dir}", targetSubDirs[i]);
            foreach (var matchedDir in matchedDirectories)
            { 
                Logger.LogInformation(matchedDir);
                var pathToPluginsDir = Path.Combine(matchedDir, Path.Join(targetSubDirs.Skip(i + 1).ToArray())); 
                Logger.LogInformation(pathToPluginsDir);
                if (Directory.Exists(pathToPluginsDir))
                {
                    var parentOfMatchedDir = Path.GetDirectoryName(matchedDir)!;
                    Logger.LogInformation("parentOfMatchedDir: {dir}", parentOfMatchedDir);
                    foreach (var fileSystemEntry in Directory.GetFileSystemEntries(parentOfMatchedDir, "*", SearchOption.TopDirectoryOnly))
                    { 
                        var destination = Path.Join(targetSubDirs.Take(i).ToArray());
                        Logger.LogInformation("Moving {file} into --> {destination_dir}", fileSystemEntry, Path.Join(gameDir!.FullName, destination));
                        Copy(fileSystemEntry, Path.Combine(gameDir.FullName, destination, Path.GetFileName(fileSystemEntry)));
                    }
                    Directory.Delete(extractPath, true);
                    return;
                } else
                {
                    Logger.LogInformation("Tried merging {matched_dir}, but directory {dir} doesn't exist", matchedDir, pathToPluginsDir);
                }
            }
        }
        // else, 
        Logger.LogInformation("Moving {dir} into --> {plugins_dir}", pluginDir, pluginsDir);
        Copy(pluginDir, Path.Join(pluginsDir!.FullName, Path.GetFileName(pluginDir)));
        Directory.Delete(extractPath, true);
    }

    public string? GetPluginDir(string extractPath)
    {
        // search extract path for directory and .dll that have the same name.
        foreach (var dir in new DirectoryInfo(extractPath).GetDirectories())
        {
            var pluginDir = GetPluginDir(dir.FullName);
            if (pluginDir != null)
            {
                return pluginDir;
            }
        }
        Logger.LogInformation("Looking through {dir} for a matching .dll", extractPath);
        var matchingDlls = Directory.GetFiles(extractPath, $"{new DirectoryInfo(extractPath).Name}.dll", SearchOption.TopDirectoryOnly);
        if (matchingDlls.Length == 1)
        { 
            return Path.GetDirectoryName(matchingDlls.First());
        }
        return null;
    }

    static void Copy(string sourceDirectory, string destinationDirectory)
    {
        DirectoryInfo dir = new (sourceDirectory);

        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory does not exist or could not be found: {sourceDirectory}");
        }
        Directory.CreateDirectory(destinationDirectory);
        DirectoryInfo[] dirs = dir.GetDirectories();
        FileInfo[] files = dir.GetFiles();
        foreach (FileInfo file in files)
        {
            string tempPath = Path.Combine(destinationDirectory, file.Name);
            file.CopyTo(tempPath, true);
        } 
        foreach (DirectoryInfo subdir in dirs)
        {
            string tempPath = Path.Combine(destinationDirectory, subdir.Name);
            Copy(subdir.FullName, tempPath);
        }
    } 


    public async Task<string> Download(PluginInfo plugin, GithubApiResponse ghRes)
    {
        // for now, we problematically assume the download is in assets[0] and that it's a zip file 
        // TODO figure out a way to either automatically get the correct asset or allow the user to decide which one they want in the form of a chat menu thing
        HttpResponseMessage res = await httpClient.GetAsync(ghRes.Assets[0].BrowserDownloadUrl); 
        var fileName = (res.Content.Headers.ContentDisposition?.FileName) ?? throw new Exception("File name not found in download");
        if (fileName.EndsWith(".rar"))
        {
            throw new Exception("Rar packages currently not supported");
        }

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
        Logger.LogInformation("unzipping...");
        ZipFile.ExtractToDirectory(filePath, extractPath, overwriteFiles: true);
        Logger.LogInformation("deleting...");
        File.Delete(filePath);
        Logger.LogInformation("Extracted file {path} to {dir}", filePath, extractPath);
        return extractPath;
    }
}

public class Manifest
{ 
    public List<PluginInfo> PluginsInstalled { get; set; } = new() { new ( "connercsbn/ConchPluginManager", "ConchPluginManager" ) };
}
public class PluginInfo
{
    public PluginInfo() { }
    public PluginInfo (string downloadString, string directory)
    {
        DownloadString = downloadString;
        Directory = directory;
    }
    public PluginInfo (string downloadString)
    {
        DownloadString = downloadString;
    }
    public string? DownloadString { get; set; }
    public string? Directory { get; set; }
    public string? TagName { get; set; }
    public bool AutoUpdate { get; set; } = true; 
    public override string ToString()
    {
        return $"{DownloadString} is installed in plugins/{Directory} with TagName {TagName}";
    }
}

public class ConchPluginManagerConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 2;

    public string? GithubAuthToken { get; set;  }
    public bool UpdateOnMapChange { get; set;  } = false;
    public bool UpdateOnServerStart { get; set;  } = true;
    public bool UpdateOnReload { get; set;  } = false;
} 