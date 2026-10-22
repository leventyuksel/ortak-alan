using System.Reflection;

namespace OrtakAlan;

static class AppInfo
{
    public const string Owner = "leventyuksel";
    public const string Repo = "ortak-alan";

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static string Display => Current.ToString(3);

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrtakAlan",
        "uygulama");

    public static string InstalledExe => Path.Combine(InstallDir, "OrtakAlan.exe");

    public static string PreferredExe()
    {
        if (File.Exists(InstalledExe)) return InstalledExe;
        return Environment.ProcessPath ?? InstalledExe;
    }
}
