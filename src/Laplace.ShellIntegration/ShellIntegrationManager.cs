using Microsoft.Win32;

namespace Laplace.ShellIntegration;

public sealed class ShellIntegrationManager
{
    private const string ClassesRoot = @"Software\Classes";
    private const string Extension = ".lpc";
    private const string ProgId = "Laplace.Archive";

    public void Install(string cliExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(cliExecutablePath))
        {
            throw new ArgumentException("CLI executable path is required.", nameof(cliExecutablePath));
        }

        var fullCliPath = Path.GetFullPath(cliExecutablePath);
        var quotedCli = Quote(fullCliPath);
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
            icon?.SetValue(string.Empty, $"{quotedCli},0");
        }

        RegisterVerb(classes, $"{ProgId}\\shell\\open", "Open with Laplace", $"{quotedCli} open \"%1\"");
        RegisterVerb(classes, $"{ProgId}\\shell\\extract_here", "Extract Here", $"{quotedCli} extract-here \"%1\"");
        RegisterVerb(classes, $"{ProgId}\\shell\\extract_named", "Extract to <archive_name>\\", $"{quotedCli} extract-to-named-folder \"%1\"");
        RegisterVerb(classes, $"{ProgId}\\shell\\extract_to", "Extract to...", $"{quotedCli} extract-to-named-folder \"%1\"");
        RegisterVerb(classes, $"{ProgId}\\shell\\test_archive", "Test archive", $"{quotedCli} test \"%1\"");
        RegisterVerb(classes, $"{ProgId}\\shell\\archive_info", "Archive info", $"{quotedCli} info \"%1\"");

        RegisterVerb(classes, @"*\shell\laplace_add_quick", "Add to \"<name>.lpc\"", $"{quotedCli} compress \"%1\" \"%1.lpc\" --mode balanced");
        RegisterVerb(classes, @"*\shell\laplace_add_dialog", "Add to .lpc archive...", $"{quotedCli} compress-dialog \"%1\"");
        RegisterVerb(classes, @"Directory\shell\laplace_add_quick", "Add to \"<name>.lpc\"", $"{quotedCli} compress \"%1\" \"%1.lpc\" --mode balanced");
        RegisterVerb(classes, @"Directory\shell\laplace_add_dialog", "Add to .lpc archive...", $"{quotedCli} compress-dialog \"%1\"");
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

        SafeDeleteTree(classes, @"*\shell\laplace_add_quick");
        SafeDeleteTree(classes, @"*\shell\laplace_add_dialog");
        SafeDeleteTree(classes, @"Directory\shell\laplace_add_quick");
        SafeDeleteTree(classes, @"Directory\shell\laplace_add_dialog");
    }

    public ShellIntegrationStatus GetStatus()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(ClassesRoot, writable: false);
        var extensionOk = classes?.OpenSubKey(Extension, writable: false)?.GetValue(string.Empty)?.ToString() == ProgId;
        var openCommand = classes?.OpenSubKey($"{ProgId}\\shell\\open\\command", writable: false)?.GetValue(string.Empty)?.ToString();
        var verbCount = 0;
        if (classes?.OpenSubKey($"{ProgId}\\shell\\open") != null) verbCount++;
        if (classes?.OpenSubKey($"{ProgId}\\shell\\extract_here") != null) verbCount++;
        if (classes?.OpenSubKey($"{ProgId}\\shell\\extract_named") != null) verbCount++;
        if (classes?.OpenSubKey($"{ProgId}\\shell\\extract_to") != null) verbCount++;
        if (classes?.OpenSubKey($"{ProgId}\\shell\\test_archive") != null) verbCount++;
        if (classes?.OpenSubKey($"{ProgId}\\shell\\archive_info") != null) verbCount++;

        return new ShellIntegrationStatus
        {
            IsInstalled = extensionOk && !string.IsNullOrWhiteSpace(openCommand),
            ExtensionAssociated = extensionOk,
            OpenCommand = openCommand ?? string.Empty,
            RegisteredLaplaceVerbCount = verbCount
        };
    }

    private static void RegisterVerb(RegistryKey classes, string keyPath, string title, string command)
    {
        using var shellKey = classes.CreateSubKey(keyPath, writable: true);
        shellKey?.SetValue(string.Empty, title);
        using var commandKey = shellKey?.CreateSubKey("command", writable: true);
        commandKey?.SetValue(string.Empty, command);
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
}

public sealed class ShellIntegrationStatus
{
    public bool IsInstalled { get; init; }
    public bool ExtensionAssociated { get; init; }
    public required string OpenCommand { get; init; }
    public int RegisteredLaplaceVerbCount { get; init; }
}
