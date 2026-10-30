using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ClaudePulse.Core;

public enum IconMetric { Session, Weekly, Context }

public enum IconStyle { Number, Bars }

public sealed class Settings
{
    public IconMetric IconMetric { get; set; } = IconMetric.Session;
    public IconStyle IconStyle { get; set; } = IconStyle.Number;
    public int RefreshSeconds { get; set; } = 120;
    public bool Notifications { get; set; } = true;
    public bool IncludeWsl { get; set; } = true;

    /// <summary>Conta fixada (chave "email|org"); null = automática (login mais recente).</summary>
    public string? PinnedAccount { get; set; }

    /// <summary>0 = automático (pelo modelo); senão força o tamanho da janela de contexto.</summary>
    public long ContextLimitOverride { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudePulse");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        }
        catch
        {
            // Arquivo corrompido: volta aos padrões.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Não é crítico.
        }
    }
}

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudePulse";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
