using Microsoft.Win32;

namespace Shitie.Windows;

internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new IOException("无法找到程序路径。");
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new IOException("请使用 Shitie.exe 设置开机启动。");
            key.SetValue("Shitie", $"\"{executable}\" --background", RegistryValueKind.String);
        }
        else key.DeleteValue("Shitie", false);
    }
}
