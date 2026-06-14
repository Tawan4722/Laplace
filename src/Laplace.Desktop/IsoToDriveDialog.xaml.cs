using System.IO;
using System.Linq;
using System.Windows;

namespace Laplace.Desktop;

public partial class IsoToDriveDialog : Window
{
    public string SelectedDriveRoot => DrivesCombo.SelectedItem is DriveChoice choice ? choice.Root : string.Empty;
    public bool Overwrite => OverwriteCheck.IsChecked == true;

    public IsoToDriveDialog(string isoPath)
    {
        InitializeComponent();
        IsoInput.Text = isoPath;

        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Removable && x.IsReady))
        {
            DrivesCombo.Items.Add(new DriveChoice(drive));
        }

        if (DrivesCombo.Items.Count > 0)
        {
            DrivesCombo.SelectedIndex = 0;
        }
        else
        {
            DrivesCombo.Items.Add(new DriveChoice(null));
            DrivesCombo.SelectedIndex = 0;
            ExtractButton.IsEnabled = false;
        }
    }

    private void Extract_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:F2} {units[unit]}";
    }

    private sealed class DriveChoice
    {
        public DriveChoice(DriveInfo? drive)
        {
            if (drive == null)
            {
                Root = string.Empty;
                Display = "No ready removable drives found";
            }
            else
            {
                Root = drive.RootDirectory.FullName;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Removable drive" : drive.VolumeLabel;
                Display = $"{label} ({Root}) - {FormatBytes(drive.AvailableFreeSpace)} free";
            }
        }

        public string Root { get; }
        private string Display { get; }
        public override string ToString() => Display;
    }
}
