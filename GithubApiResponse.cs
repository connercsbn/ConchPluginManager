using System.Text.Json.Serialization;

namespace ConchPluginManager
{
    public class GithubApiResponse
    {
        [JsonPropertyName("tag_name")]
        public required string TagName { get; set; } 

        [JsonPropertyName("zipball_url")]
        public required string ZipballUrl { get; set; } 

        [JsonPropertyName("tarball_url")]
        public required string TarballUrl { get; set; }
        [JsonPropertyName("assets")]
        public required List<Asset> Assets { get; set; }
    }
    public class Asset
    { 
        [JsonPropertyName("name")]
        public required string Name { get; set; } 
        [JsonPropertyName("browser_download_url")]
        public required string BrowserDownloadUrl { get; set; } 
    }
}

