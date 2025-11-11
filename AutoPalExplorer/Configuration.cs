using Dalamud.Configuration;
using Dalamud.Plugin;

namespace AutoPalExplorer;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // 原有
    public bool EnabledByDefault { get; set; } = false;

    // 是否自动控制 BMRAI (/bmrai on/off)
    public bool UseBmrai { get; set; } = true;

    // 宝箱控制
    public bool OpenBronzeChests { get; set; } = true;
    public bool OpenSilverChests { get; set; } = true;
    public bool OpenGoldChests { get; set; } = false; // 默认金关掉，相对安全
    
    // 开发者模式
    public bool devMode { get; set; } = false;

    [System.NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
