using System.Text.Json;
using System.IO.Compression;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API;

namespace ConchPluginManager;


 [MinimumApiVersion(130)]
public class ConchPluginManager : BasePlugin, IPluginConfig<ConchPluginManagerConfig>
{
    public override string ModuleName => "Conch Plugin Manager";

    public override string ModuleVersion => "0.0.3";
    public override string ModuleAuthor => "Conch"; 
    string? configPath;

    private static readonly HttpClient httpClient = new ();
    public ConchPluginManagerConfig Config { get; set; } = new();

    public void OnConfigParsed(ConchPluginManagerConfig config)
    {
        Config = config;
    }

    [ConsoleCommand("css_list", "List installed plugins")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandList(CCSPlayerController? _, CommandInfo command)
    {
        foreach (var plugin in Config.PluginsInstalled)
        {
            command.ReplyToCommand(plugin.ToString());
        }
    }

    [ConsoleCommand("css_update", "Update all")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public async void OnCommandUpdate(CCSPlayerController? _, CommandInfo command)
    {
        await CheckForUpdates();
    }

    [ConsoleCommand("css_install", "Install a plugin")]
    [CommandHelper(minArgs: 1, usage: "<github_author>/<repository_name>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public async void OnCommandInstall(CCSPlayerController? _, CommandInfo command)
    {
        var downloadString = command.GetArg(1);
        if (Config.PluginsInstalled.Exists(plugin => plugin.DownloadString == downloadString))
        {
            command.ReplyToCommand($"Plugin {downloadString} already installed");
            return;
        }
        var newPlugin = new PluginInfo(downloadString); 

        if (!await CheckForUpdate(newPlugin, httpClient))
        {
            Server.NextFrame(() =>
            {
                command.ReplyToCommand("Failed to install plugin.");
            });
            return;
        }
        Config.PluginsInstalled.Add(newPlugin);
        WriteConfig();
        Logger.LogInformation("loading plugin {plugin}", newPlugin.Directory);
        Server.NextFrame(() => Server.ExecuteCommand($"css_plugins load {newPlugin.Directory}"));
    }

    [ConsoleCommand("css_remove", "Remove a plugin")]
    [CommandHelper(minArgs: 1, usage: "<Plugin download_string/directory>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandRemove(CCSPlayerController? _, CommandInfo command)
    {
        var arg = command.GetArg(1);
        var pluginToRemove = Config.PluginsInstalled.Find((plugin) => plugin.DownloadString == arg);
        pluginToRemove ??= Config.PluginsInstalled.Find((plugin) => plugin.Directory == arg);
        if (pluginToRemove == null)
        { 
            Logger.LogInformation("Plugin {plugin} not found", arg);
            return;
        }
        Logger.LogInformation("Removing plugin {plugin}", arg);
        var pluginsDir = new DirectoryInfo(ModulePath).Parent!.Parent!;
        var pluginToRemovePath = Path.Join(pluginsDir.FullName, pluginToRemove.Directory);
        if (Directory.Exists(pluginToRemovePath))
        {
            Logger.LogInformation("Deleting directory {dir}", pluginToRemovePath);
            Directory.Delete(pluginToRemovePath, true);
            Logger.LogInformation("Plugin directory {dir} has been deleted. You may need to unload the plugin with css_plugins unload <plugin_name>", pluginToRemove.Directory);
            Config.PluginsInstalled.Remove(pluginToRemove);
            WriteConfig();
        }
        else
        {
            Logger.LogInformation("couldn't find directory {pluginsDir} in {pluginToRemove}", pluginToRemove.Directory, pluginsDir.FullName);
        }
    }

    public override void Load(bool hotReload)
    {
        if (hotReload) Logger.LogInformation("Conch Plugin Manager reloaded");
        httpClient.DefaultRequestHeaders.Add("User-Agent", "connercsbn/ConchPluginManager");
            
        string pluginDirName = "ConchPluginManager";
        var counterStrikeSharpDir = (new DirectoryInfo(ModulePath).Parent!.Parent!.Parent!).FullName;
        configPath = Path.Combine(counterStrikeSharpDir, "configs", "plugins", pluginDirName, pluginDirName + ".json")!;
        Logger.LogInformation($"configPath: {configPath}");
        if (!File.Exists(configPath)) throw new Exception("config not found"); 

        CheckForUpdates();
    }
    public async Task CheckForUpdates()
    {
        foreach (var plugin in Config.PluginsInstalled)
        {
            if (!plugin.AutoUpdate) continue;
            await CheckForUpdate(plugin, httpClient); 
        }
        WriteConfig();
    }
    private void WriteConfig()
    { 
        if (configPath != null)
        { 
            File.WriteAllText(configPath!, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private async Task<bool> CheckForUpdate(PluginInfo plugin, HttpClient httpClient)
    {
        var githubLink = $"https://api.github.com/repos/{plugin.DownloadString}/releases/latest";
        try
        {
            // for now, just download from latest release. TODO handle plugins that are released through the repository 
            Logger.LogInformation("querying https://api.github.com/repos/{plugin}/releases/latest", plugin.DownloadString);
            HttpResponseMessage response = await httpClient.GetAsync(githubLink);
            try
            { 
                var res = JsonSerializer.Deserialize<GithubApiResponse>(await response.Content.ReadAsStringAsync());
                if (res == null || String.IsNullOrEmpty(res!.TagName))
                { 
                    Logger.LogInformation("Couldn't retrieve Github release information after querying {download_string}", plugin.DownloadString);
                    return false;
                }
                if (res.TagName == plugin.TagName)
                {
                    Logger.LogInformation("Plugin {plugin} up to date", plugin.DownloadString);
                    return false;
                }
                Logger.LogInformation("Plugin {plugin} is not up to date! Downloading new release {tag_name} from {url}", plugin.DownloadString, res!.TagName, res!.Assets[0].BrowserDownloadUrl);
                var extractPath = await Download(plugin, res);
                try
                {
                    var pluginDir = GetPluginDir(extractPath);
                    if (pluginDir == null)
                    {
                        Logger.LogInformation("Couldn't find plugin directory with matching dll. Aborting installation.");
                        return false;
                    }
                    plugin.Directory = new DirectoryInfo(pluginDir).Name; 
                    if (plugin.Directory == null)
                    {
                        Logger.LogInformation("TODO write this error message not sure why this would ever happen but probably will at some point, right?");
                        return false;
                    }
                    Merge(extractPath, pluginDir);
                    Logger.LogInformation("updating tag name from {previous_tag} to {next_tag}", plugin.TagName, res.TagName);
                    plugin.TagName = res.TagName;
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogInformation("{error_message}", ex.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogInformation("{error_message}", ex.Message);
                Logger.LogInformation("{response_content}", await response.Content.ReadAsStringAsync());
            }
        }
        catch (Exception ex)
        {
            Logger.LogInformation("An error occurred while querying {github_link}:\n {message}", githubLink, ex.Message);
        }
        return false;
    }
    private void Merge(string extractPath, string pluginDir)
    // sync to server files
    // any conflicting files will be overwritten, could this go wrong?
    {
        Logger.LogInformation("Merging");
        var csgo = new DirectoryInfo(ModulePath).Parent!.Parent!.Parent!.Parent!.Parent!;
        var plugins = new DirectoryInfo(ModulePath).Parent!.Parent!; 

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
                    var parentOfMatchedDir = Path.GetDirectoryName(matchedDir)!;
                    Logger.LogInformation("parentOfMatchedDir: {dir}", parentOfMatchedDir);
                    foreach (var fileSystemEntry in Directory.GetFileSystemEntries(parentOfMatchedDir, "*", SearchOption.TopDirectoryOnly))
                    { 
                        var destination = Path.Join(targetSubDirs.Skip(1).Take(i - 1).ToArray());
                        Logger.LogInformation("{file} ----> {destination_dir}", fileSystemEntry, Path.Join(csgo.FullName, destination));
                        Move(fileSystemEntry, Path.Combine(csgo.FullName, destination, Path.GetFileName(fileSystemEntry)));
                    }
                    return;
                } else
                {
                    Logger.LogInformation("directory {dir} doesn't exist", pathToPluginsDir);
                }
            }
        }
        Logger.LogInformation("{dir} ----> {plugins_dir}", pluginDir, plugins);
        Move(pluginDir, Path.Join(plugins.FullName, Path.GetFileName(pluginDir)));
    }

    public string? GetPluginDir(string extractPath)
    {
        // there are no common directories, so we find the plugin and put it in plugins dir 
        foreach (var dir in new DirectoryInfo(extractPath).GetDirectories())
        {
            var pluginDir = GetPluginDir(dir.FullName);
            if (pluginDir != null)
            {
                return pluginDir;
            }
            Logger.LogInformation("looking through {dir} for a matching .dll", dir.FullName);
            var matchingDlls = Directory.GetFiles(dir.FullName, $"{dir.Name}.dll", SearchOption.TopDirectoryOnly);
            Logger.LogInformation("Done...");
            if (matchingDlls.Length == 1)
            { 
                return Path.GetDirectoryName(matchingDlls[0]);
            }
        }
        return null;
    }

    static void Move(string sourceDirectory, string destinationDirectory)
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
            file.Delete();
        } 
        foreach (DirectoryInfo subdir in dirs)
        {
            string tempPath = Path.Combine(destinationDirectory, subdir.Name);
            Move(subdir.FullName, tempPath);
        }
        Directory.Delete(sourceDirectory, true);
    } 


    public async Task<string> Download(PluginInfo plugin, GithubApiResponse ghRes)
    {
        // for now, we problematically assume the download is in assets[0] and that it's a zip file 
        // TODO figure out a way to either automatically get the correct asset or allow the user to decide which one they want in the form of a chat menu thing
        HttpResponseMessage res = await httpClient.GetAsync(ghRes.Assets[0].BrowserDownloadUrl); 
        string fileName = res.Content.Headers.ContentDisposition?.FileName ?? $"{plugin.DownloadString.Split('/')[1]}.zip";

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

public class PluginInfo
{
    public PluginInfo (string downloadString) 
    {
        DownloadString = downloadString;
    }
    public string DownloadString { get; set; }
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
    public List<PluginInfo> PluginsInstalled { get; set; } = new() { new ("connercsbn/ConchPluginManager") };
} 