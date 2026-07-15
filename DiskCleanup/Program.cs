using DiskCleanup.Core;

var items = new List<CheckItem>();
var warnings = new List<string>();

items.AddRange(Scanners.RecycleBin(warnings));
items.AddRange(Scanners.TempFolders());
items.AddRange(Scanners.WindowsUpdateCache());
items.AddRange(Scanners.WindowsTempFolder(warnings));
items.AddRange(Scanners.VsCodeCache(warnings));
items.AddRange(Scanners.Wsl(warnings));
items.AddRange(Scanners.NativeBuildDirs(warnings));
items.AddRange(Scanners.Docker(warnings));
items.AddRange(Scanners.DockerVhdxBloat(warnings: warnings));
items.AddRange(Scanners.DownloadsTopFolders(warnings: warnings));
items.AddRange(Scanners.StalePackages(warnings: warnings));
items.AddRange(Scanners.AiFolders(warnings));
items.AddRange(Scanners.InstalledAppsBySize(warnings: warnings));
items.AddRange(Scanners.PersonalFolders(warnings: warnings));

Console.WriteLine("disk-cleanup — scan results");
Console.WriteLine("============================");
Console.WriteLine();

for (int i = 0; i < items.Count; i++)
{
    var item = items[i];
    Console.WriteLine($"[{i + 1}] {item.Label} — {item.FormattedSize} — {item.Risk} — {item.ActionDescription}");
}

Console.WriteLine();
Console.WriteLine($"{items.Count} items found.");

if (warnings.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Warnings (some categories may be incomplete):");
    foreach (var warning in warnings)
        Console.WriteLine($"  - {warning}");
}

Console.WriteLine();

Console.Write("Enter numbers to act on (e.g. 1,2,5 or all-safe), or press Enter to quit: ");
var input = Console.ReadLine() ?? "";

var risks = items.Select(i => i.Risk).ToList();
var selectedNumbers = SelectionParser.Parse(input, risks);

if (selectedNumbers.Count == 0)
{
    Console.WriteLine("Nothing selected. Exiting.");
    return;
}

var selectedItems = selectedNumbers.Select(n => items[n - 1]).ToList();

Console.WriteLine();
Console.WriteLine("The following items will be processed:");
foreach (var n in selectedNumbers)
{
    var item = items[n - 1];
    Console.WriteLine($"[{n}] {item.Label} — {item.FormattedSize} — {item.Risk}");
}

Console.Write("Proceed? (y/n): ");
var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
if (confirm != "y" && confirm != "yes")
{
    Console.WriteLine("Cancelled. Nothing was changed.");
    return;
}

var systemDrive = System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;
var freeBefore = new DriveInfo(systemDrive).AvailableFreeSpace;

Console.WriteLine();
Console.WriteLine("Results:");
foreach (var item in selectedItems)
{
    var result = ActionExecutor.Execute(item);
    var status = result.Success ? "OK" : "FAILED";
    Console.WriteLine($"[{status}] {item.Label}: {result.Message}");
}

var freeAfter = new DriveInfo(systemDrive).AvailableFreeSpace;

Console.WriteLine();
Console.WriteLine($"Free space on {systemDrive} before: {FormatBytes(freeBefore)}");
Console.WriteLine($"Free space on {systemDrive} after:  {FormatBytes(freeAfter)}");
Console.WriteLine($"Difference: {FormatBytes(freeAfter - freeBefore)}");

static string FormatBytes(long bytes)
{
    string[] units = { "B", "KB", "MB", "GB", "TB" };
    double size = bytes;
    int unit = 0;
    var sign = size < 0 ? "-" : "";
    size = Math.Abs(size);
    while (size >= 1024 && unit < units.Length - 1)
    {
        size /= 1024;
        unit++;
    }
    return $"{sign}{size:0.#}{units[unit]}";
}
