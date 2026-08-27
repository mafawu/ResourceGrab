using System.Reflection;
using System.Text.Json;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M12: 插件加载器 — 加载独立程序集中的 IVideoScrapeSource 实现
// ---------------------------------------------------------------------------

public sealed class PluginLoader
{
    private readonly string _pluginDir;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, LoadedPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, LoadedPlugin> Plugins => _plugins;

    public PluginLoader(string pluginDir, ILogger? logger = null)
    {
        _pluginDir = pluginDir;
        _logger = logger;
        Directory.CreateDirectory(_pluginDir);
    }

    /// <summary>扫描插件目录并加载所有有效插件。</summary>
    public int LoadAll()
    {
        var loaded = 0;
        foreach (var manifestPath in Directory.GetFiles(_pluginDir, "plugin.json", SearchOption.AllDirectories))
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<VideoScrapePluginManifest>(json);
                if (manifest is null) continue;

                if (!LoadPlugin(manifest, Path.GetDirectoryName(manifestPath)!))
                    continue;

                loaded++;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"[Plugin] 加载失败: {manifestPath} - {ex.Message}");
            }
        }
        return loaded;
    }

    /// <summary>加载单个插件。</summary>
    public bool LoadPlugin(VideoScrapePluginManifest manifest, string basePath)
    {
        // 校验 ID 命名空间
        if (!manifest.Id.Contains('.'))
        {
            _logger?.Warn($"[Plugin] 插件 ID 必须带命名空间: {manifest.Id}");
            return false;
        }

        // 与内置来源冲突检查
        if (_plugins.ContainsKey(manifest.Id))
        {
            _logger?.Warn($"[Plugin] 插件 ID 重复: {manifest.Id}");
            return false;
        }

        if (!manifest.Enabled)
        {
            _plugins[manifest.Id] = new LoadedPlugin { Manifest = manifest, Status = PluginLoadStatus.Disabled };
            return false;
        }

        try
        {
            var assemblyPath = Path.Combine(basePath, manifest.EntryPoint);
            if (!File.Exists(assemblyPath))
            {
                _plugins[manifest.Id] = new LoadedPlugin
                {
                    Manifest = manifest,
                    Status = PluginLoadStatus.Failed,
                    ErrorMessage = $"程序集不存在: {assemblyPath}"
                };
                return false;
            }

            var assembly = Assembly.LoadFrom(assemblyPath);
            var type = assembly.GetType(manifest.TypeName);
            if (type is null)
            {
                _plugins[manifest.Id] = new LoadedPlugin
                {
                    Manifest = manifest,
                    Status = PluginLoadStatus.Failed,
                    ErrorMessage = $"类型不存在: {manifest.TypeName}"
                };
                return false;
            }

            if (!typeof(IVideoScrapeSource).IsAssignableFrom(type))
            {
                _plugins[manifest.Id] = new LoadedPlugin
                {
                    Manifest = manifest,
                    Status = PluginLoadStatus.Failed,
                    ErrorMessage = $"类型未实现 IVideoScrapeSource: {manifest.TypeName}"
                };
                return false;
            }

            var source = (IVideoScrapeSource)Activator.CreateInstance(type)!;
            _plugins[manifest.Id] = new LoadedPlugin
            {
                Manifest = manifest,
                Status = PluginLoadStatus.Loaded,
                Source = source
            };

            _logger?.Info($"[Plugin] 已加载: {manifest.Id} v{manifest.Version}");
            return true;
        }
        catch (Exception ex)
        {
            _plugins[manifest.Id] = new LoadedPlugin
            {
                Manifest = manifest,
                Status = PluginLoadStatus.Failed,
                ErrorMessage = ex.Message
            };
            _logger?.Error($"[Plugin] 加载异常: {manifest.Id}", ex);
            return false;
        }
    }

    /// <summary>卸载指定插件。</summary>
    public bool Unload(string pluginId)
    {
        if (_plugins.TryGetValue(pluginId, out var plugin))
        {
            plugin.Status = PluginLoadStatus.Disabled;
            plugin.Source = null;
            _logger?.Info($"[Plugin] 已卸载: {pluginId}");
            return true;
        }
        return false;
    }

    /// <summary>获取所有已加载的有效来源。</summary>
    public IReadOnlyList<IVideoScrapeSource> GetSources() =>
        _plugins.Values
            .Where(p => p.Status == PluginLoadStatus.Loaded && p.Source is not null)
            .Select(p => p.Source!)
            .ToList();
}
