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

    public override string ModuleVersion => "0.0.1";
    public override string ModuleAuthor => "Conch"; 
    string? configPath;

    private static readonly HttpClient httpClient = new ();
    public ConchPluginManagerConfig Config { get; set; } = new();

    public void OnConfigParsed(ConchPluginManagerConfig config)
    {
        Config = config;
    }

    [ConsoleCommand("css_update", "Update plugins")]
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
        Config.PluginsInstalled.Add(newPlugin);

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "connercsbn/ConchPluginManager");
        await CheckForUpdate(newPlugin, httpClient);
        File.WriteAllText(configPath!, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
        Logger.LogInformation("loading plugin {plugin}", newPlugin.Directory);
        Server.NextFrame(() => Server.ExecuteCommand($"css_plugins load {newPlugin.Directory}"));
    }

    [ConsoleCommand("css_remove", "Remove plugins")]
    [CommandHelper(minArgs: 1, usage: "<Plugin>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandRemove(CCSPlayerController? _, CommandInfo command)
    {
        var arg = command.GetArg(1);
        var pluginToRemove = Config.PluginsInstalled.Find((plugin) => plugin.Directory == arg);
        if (pluginToRemove == null)
        { 
            Logger.LogInformation("Plugin {plugin} not found", arg);
            Logger.LogInformation("available plugins: ");
            foreach (var plugin in Config.PluginsInstalled)
            {
                Logger.LogInformation(plugin.Directory);
            }
            return;
        }
        Logger.LogInformation("Removing plugin {plugin}", arg);
        var pluginsDir = new DirectoryInfo(ModulePath).Parent!.Parent!;
        var pluginToRemovePath = Path.Join(pluginsDir.FullName, pluginToRemove.Directory);
        if (Directory.Exists(pluginToRemovePath))
        {
            Logger.LogInformation("Deleting directory {dir}", pluginToRemovePath);
            Directory.Delete(pluginToRemovePath, true);
            Config.PluginsInstalled.Remove(pluginToRemove);
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
    }
    private async Task CheckForUpdate(PluginInfo plugin, HttpClient httpClient)
    {
        try
        {
            // for now, just download from latest release. TODO handle plugins that are released through the repository 
            Logger.LogInformation("querying https://api.github.com/repos/{plugin}/releases/latest", plugin.DownloadString);
            HttpResponseMessage response = await httpClient.GetAsync($"https://api.github.com/repos/{plugin.DownloadString}/releases/latest");
            try
            { 
                var res = JsonSerializer.Deserialize<GithubApiResponse>(await response.Content.ReadAsStringAsync());
                if (String.IsNullOrEmpty(res!.TagName)) return;
                if (res.TagName == plugin.TagName)
                {
                    Logger.LogInformation("Plugin {plugin} up to date", plugin.DownloadString);
                    return;
                }
                Logger.LogInformation("Plugin {plugin} is not up to date! \"Downloading new release {tag_name} from {url}\"", plugin.DownloadString, res!.TagName, res!.Assets[0].BrowserDownloadUrl);
                var extractPath = await Download(plugin, res);
                try
                {
                    plugin.Directory = Path.GetFileName(GetPluginDir(extractPath));
                    Merge(extractPath);
                    Logger.LogInformation("updating tag name from {previousTag} to {nextTag}", plugin.TagName, res!.TagName);
                    plugin.TagName = res!.TagName;
                    File.WriteAllText(configPath!, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    Logger.LogInformation(ex.Message);
                }
            }
            catch
            {
                Logger.LogInformation(await response.Content.ReadAsStringAsync());
            }
        }
        catch (Exception ex)
        {
            Logger.LogInformation("An error occurred: {message}", ex.Message);
        }
    }
    private void Merge(string extractPath)
    // sync to server files
    // any conflicting files will be overwritten, could this go wrong?
    // TODO make sure the structure is counterstrikesharp/plugins/<PluginName>/<PluginName.dll> before merging
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
        var pluginDir = GetPluginDir(extractPath);
        Logger.LogInformation("{dir} ----> {plugins_dir}", pluginDir, plugins);
        Move(pluginDir, Path.Join(plugins.FullName, new DirectoryInfo(pluginDir).Name));
    }

    public string GetPluginDir(string extractPath)
    { 
        // there are no common directories, so we find the plugin and put it in plugins dir
        var dlls = Directory.GetFiles(extractPath, "*.dll", SearchOption.AllDirectories);
        Logger.LogInformation("dlls found: {dlls}", string.Join(", ", dlls)); 
        // assuming all dlls are in the same directory...
        var pluginDir = Path.GetDirectoryName(dlls[0])!;
        return pluginDir;
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
            file.CopyTo(tempPath, false);
            file.Delete();
        } 
        foreach (DirectoryInfo subdir in dirs)
        {
            string tempPath = Path.Combine(destinationDirectory, subdir.Name);
            Move(subdir.FullName, tempPath);
        }
        Directory.Delete(sourceDirectory);
    } 


    public async Task<string> Download(PluginInfo plugin, GithubApiResponse ghRes)
    {
        using HttpClient httpClient = new();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "connercsbn/ConchPluginManager");
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "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");
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
        ZipFile.ExtractToDirectory(filePath, extractPath);
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
    public string DownloadString { get; set; } = "connercsbn/ConchPluginManager";
    public string? Directory { get; set; }
    public string? TagName { get; set; }
    public bool AutoUpdate { get; set; } = true;
}

public class ConchPluginManagerConfig : BasePluginConfig
{
    // examples for testing
    public List<PluginInfo> PluginsInstalled { get; set; } = new ()
    { };
} 