using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Laplace.ShellIntegration;

[SupportedOSPlatform("windows")]
public sealed class ShellIntegrationManager
{
    private const string ClassesRoot = @"Software\Classes";
    private const string Extension = ".lpc";
    private const string ProgId = "Laplace.Archive";
    private const string MenuIconValue = "Laplace";
    private static readonly string[] ArchiveExtensions =
    [
        ".lpc",
        ".zip",
        ".7z",
        ".rar",
        ".tar",
        ".gz",
        ".tgz",
        ".bz2",
        ".xz",
        ".zst",
        ".lzip"
    ];

    public void Install(string cliExecutablePath, string? guiExecutablePath = null)
    {
        if (string.IsNullOrWhiteSpace(cliExecutablePath))
        {
            throw new ArgumentException("CLI executable path is required.", nameof(cliExecutablePath));
        }

        var fullCliPath = Path.GetFullPath(cliExecutablePath);
        var quotedCli = Quote(fullCliPath);
        var fullGuiPath = string.IsNullOrWhiteSpace(guiExecutablePath) ? ResolveSiblingGui(fullCliPath) : Path.GetFullPath(guiExecutablePath);
        var quotedGui = File.Exists(fullGuiPath) ? Quote(fullGuiPath) : quotedCli;
        using var classes = Registry.CurrentUser.CreateSubKey(ClassesRoot, writable: true)
            ?? throw new InvalidOperationException("Cannot open HKCU\\Software\\Classes.");

        using (var ext = classes.CreateSubKey(Extension, writable: true))
        {
            ext?.SetValue(string.Empty, ProgId);
            ext?.SetValue("PerceivedType", "compressed");
        }

        using (var prog = classes.CreateSubKey(ProgId, writable: true))
        {
            prog?.SetValue(string.Empty, "Laplace Archive");
            using var icon = prog?.CreateSubKey("DefaultIcon", writable: true);
            icon?.SetValue(string.Empty, $"{quotedGui},0");
        }

        RemoveLegacyFlatVerbs(classes);

        RegisterArchiveMenu(classes, $"{ProgId}\\shell\\laplace", quotedCli, quotedGui);
        foreach (var extension in ArchiveExtensions.Where(x => !string.Equals(x, Extension, StringComparison.OrdinalIgnoreCase)))
        {
            RegisterArchiveMenu(classes, $@"SystemFileAssociations\{extension}\shell\laplace", quotedCli, quotedGui);
        }

        RegisterCreateMenu(classes, @"*\shell\laplace", quotedCli, quotedGui, "%1", BuildNonArchiveFileFilter());
        RegisterCreateMenu(classes, @"Directory\shell\laplace", quotedCli, quotedGui, "%1");
        RegisterCreateMenu(classes, @"Directory\Background\shell\laplace", quotedCli, quotedGui, "%V");
    }

    public void Uninstall()
    {
        using var classes = Registry.CurrentUser.CreateSubKey(ClassesRoot, writable: true)
            ?? throw new InvalidOperationException("Cannot open HKCU\\Software\\Classes.");

        SafeDeleteTree(classes, ProgId);
        using (var ext = classes.OpenSubKey(Extension, writable: true))
        {
            var defaultValue = ext?.GetValue(string.Empty)?.ToString();
            if (string.Equals(defaultValue, ProgId, StringComparison.OrdinalIgnoreCase))
            {
                SafeDeleteTree(classes, Extension);
            }
        }

        SafeDeleteTree(classes, $"{ProgId}\\shell\\laplace");
        foreach (var extension in ArchiveExtensions.Where(x => !string.Equals(x, Extension, StringComparison.OrdinalIgnoreCase)))
        {
            SafeDeleteTree(classes, $@"SystemFileAssociations\{extension}\shell\laplace");
        }

        SafeDeleteTree(classes, @"*\shell\laplace");
        SafeDeleteTree(classes, @"Directory\shell\laplace");
        SafeDeleteTree(classes, @"Directory\Background\shell\laplace");
        RemoveLegacyFlatVerbs(classes);
    }

    public ShellIntegrationStatus GetStatus()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(ClassesRoot, writable: false);
        var extensionOk = classes?.OpenSubKey(Extension, writable: false)?.GetValue(string.Empty)?.ToString() == ProgId;
        var openCommand = classes?.OpenSubKey($"{ProgId}\\shell\\laplace\\shell\\open\\command", writable: false)?.GetValue(string.Empty)?.ToString()
            ?? classes?.OpenSubKey($"{ProgId}\\shell\\open\\command", writable: false)?.GetValue(string.Empty)?.ToString();
        var verbCount = 0;
        if (classes?.OpenSubKey($"{ProgId}\\shell\\laplace") != null) verbCount++;
        if (classes?.OpenSubKey(@"*\shell\laplace") != null) verbCount++;
        if (classes?.OpenSubKey(@"Directory\shell\laplace") != null) verbCount++;
        if (classes?.OpenSubKey(@"Directory\Background\shell\laplace") != null) verbCount++;
        foreach (var extension in ArchiveExtensions.Where(x => !string.Equals(x, Extension, StringComparison.OrdinalIgnoreCase)))
        {
            if (classes?.OpenSubKey($@"SystemFileAssociations\{extension}\shell\laplace") != null)
            {
                verbCount++;
            }
        }

        return new ShellIntegrationStatus
        {
            IsInstalled = extensionOk && !string.IsNullOrWhiteSpace(openCommand),
            ExtensionAssociated = extensionOk,
            OpenCommand = openCommand ?? string.Empty,
            RegisteredLaplaceVerbCount = verbCount
        };
    }

    private static void RegisterArchiveMenu(RegistryKey classes, string keyPath, string quotedCli, string quotedGui)
    {
        using var menuKey = classes.CreateSubKey(keyPath, writable: true);
        ConfigureCascadeRoot(menuKey, quotedGui);

        RegisterCascadeVerb(menuKey, "open", "Open in Laplace", $"{quotedGui} --open \"%1\"");
        RegisterCascadeVerb(menuKey, "extract_options", "Extract with options...", $"{quotedCli} extract-dialog \"%1\"");
        RegisterCascadeVerb(menuKey, "extract_here", "Extract here", $"{quotedCli} extract-here \"%1\"");
        RegisterCascadeVerb(menuKey, "extract_named", "Extract to archive-named folder", $"{quotedCli} extract-to-named-folder \"%1\"");
        RegisterCascadeVerb(menuKey, "test_archive", "Test integrity", $"{quotedCli} test \"%1\"");
        RegisterCascadeVerb(menuKey, "archive_info", "Show archive details", $"{quotedCli} info \"%1\"");
    }

    private static void RegisterCreateMenu(
        RegistryKey classes,
        string keyPath,
        string quotedCli,
        string quotedGui,
        string targetPlaceholder,
        string? appliesTo = null)
    {
        using var menuKey = classes.CreateSubKey(keyPath, writable: true);
        ConfigureCascadeRoot(menuKey, quotedGui);
        if (!string.IsNullOrWhiteSpace(appliesTo))
        {
            menuKey?.SetValue("AppliesTo", appliesTo);
        }

        RegisterCascadeVerb(menuKey, "create_options", "Create archive...", $"{quotedGui} --add \"{targetPlaceholder}\"");
        RegisterCascadeVerb(menuKey, "create_quick", "Create .lpc beside item", $"{quotedCli} compress-beside \"{targetPlaceholder}\" --mode balanced");
        RegisterCascadeVerb(menuKey, "create_quick_verified", "Create verified .lpc", $"{quotedCli} compress-beside \"{targetPlaceholder}\" --mode balanced --verify");
    }

    private static void ConfigureCascadeRoot(RegistryKey? menuKey, string quotedGui)
    {
        menuKey?.SetValue("MUIVerb", MenuIconValue);
        menuKey?.SetValue("Icon", quotedGui);
        menuKey?.SetValue("ExtendedSubCommandsKey", string.Empty);
        menuKey?.SetValue("MultiSelectModel", "Single");
        menuKey?.DeleteValue("SubCommands", throwOnMissingValue: false);
    }

    private static void RegisterCascadeVerb(RegistryKey? menuKey, string verbName, string title, string command)
    {
        using var shell = menuKey?.CreateSubKey($@"shell\{verbName}", writable: true);
        shell?.SetValue("MUIVerb", title);
        using var commandKey = shell?.CreateSubKey("command", writable: true);
        commandKey?.SetValue(string.Empty, command);
    }

    private static void RemoveLegacyFlatVerbs(RegistryKey classes)
    {
        SafeDeleteTree(classes, $"{ProgId}\\shell\\open");
        SafeDeleteTree(classes, $"{ProgId}\\shell\\extract_here");
        SafeDeleteTree(classes, $"{ProgId}\\shell\\extract_named");
        SafeDeleteTree(classes, $"{ProgId}\\shell\\extract_to");
        SafeDeleteTree(classes, $"{ProgId}\\shell\\test_archive");
        SafeDeleteTree(classes, $"{ProgId}\\shell\\archive_info");
        SafeDeleteTree(classes, @"*\shell\laplace_add_quick");
        SafeDeleteTree(classes, @"*\shell\laplace_add_dialog");
        SafeDeleteTree(classes, @"Directory\shell\laplace_add_quick");
        SafeDeleteTree(classes, @"Directory\shell\laplace_add_dialog");
    }

    private static void SafeDeleteTree(RegistryKey classes, string subkey)
    {
        try
        {
            classes.DeleteSubKeyTree(subkey, throwOnMissingSubKey: false);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static string Quote(string value) => $"\"{value}\"";

    private static string BuildNonArchiveFileFilter()
    {
        return string.Join(
            " AND ",
            ArchiveExtensions.Select(extension => $@"NOT System.FileName:""*{extension}"""));
    }

    private static string ResolveSiblingGui(string cliPath)
    {
        var directory = Path.GetDirectoryName(cliPath) ?? string.Empty;
        return Path.Combine(directory, "laplace-gui.exe");
    }
}

public sealed class ShellIntegrationStatus
{
    public bool IsInstalled { get; init; }
    public bool ExtensionAssociated { get; init; }
    public required string OpenCommand { get; init; }
    public int RegisteredLaplaceVerbCount { get; init; }
}
