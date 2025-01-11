using ExifLib;
using Spectre.Console;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

// Pick directory

var directory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
string[] imageFiles;
while (true)
{
    imageFiles = Directory.GetFiles(directory, "*.jpeg").Concat(Directory.GetFiles(directory, "*.jpg")).ToArray();

    List<(string Path, string Text)> options = [];
    if (imageFiles.Length > 0)
    {
        options.Add((directory, $"[green]This directory ({imageFiles.Length} images)[/]"));
    }
    var parent = Path.GetDirectoryName(directory);
    if (parent != null)
    {
        options.Add((parent, "[grey].. (up on level)[/]"));
    }
    options.AddRange(Directory.GetDirectories(directory).Select(d => (d, Path.GetFileName(d))));

    var option = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title($"Pick the directory to process\nCurrent directory: [{(imageFiles.Length > 0 ? "green" : "grey")}]{directory}[/]")
            .PageSize(10)
            .MoreChoicesText("[grey](Move up and down for more options)[/]")
            .AddChoices(options.Select(o => o.Text).ToArray()));
    var nextPath = options.Single(o => o.Text == option).Path;
    if (nextPath == directory) { break; }
    directory = nextPath;
}

var imageStats = imageFiles.AsParallel()
    .Select(f =>
    {
        DateTime? dateTaken = null, exifDate = null, exifDateDigitized = null;
        try
        {
            using var reader = new ExifReader(f);
            dateTaken = reader.GetTagValue<DateTime>(ExifTags.DateTimeOriginal, out var parsedOriginalDate)
                ? parsedOriginalDate
                : default(DateTime?);
            exifDate = reader.GetTagValue<DateTime>(ExifTags.DateTime, out var parsedDate)
                ? parsedDate
                : default(DateTime?);
            exifDateDigitized = reader.GetTagValue<DateTime>(ExifTags.DateTimeDigitized, out var parsedDateDigitized)
                ? parsedDateDigitized
                : default(DateTime?);
        }
        catch (ExifLibException) { }

        var dateCreated = TruncateMillis(File.GetCreationTime(f));
        var dateModified = TruncateMillis(File.GetLastWriteTime(f));

        return new
        {
            File = f,
            DateTaken = dateTaken,
            ExifDate = exifDate,
            ExifDateDigitized = exifDateDigitized,
            DateCreated = dateCreated,
            DateModified = dateModified,
            NeedsUpdate = new[] { dateTaken, exifDate ?? dateTaken, exifDateDigitized, dateCreated, dateModified }.Distinct().Count() > 1
        };
    })
    .ToArray();
Table table = new();
table.AddColumn("File");
table.AddColumn("EXIF Date Taken");
table.AddColumn("EXIF Date");
table.AddColumn("EXIF Date Digitized");
table.AddColumn("Date Created");
table.AddColumn("Date Modified");
table.AddColumn("Action");
foreach (var stat in imageStats)
{
    table.AddRow(new[] {
        Path.GetFileName(stat.File),
        stat.DateTaken.HasValue ? $"[green]{stat.DateTaken:yyyy-MM-dd}[/]" : "[red]<NOT SET>[/]",
        stat.ExifDate?.ToString("yyyy-MM-dd") ?? "[grey]<NOT SET>[/]",
        stat.ExifDateDigitized?.ToString("yyyy-MM-dd") ?? "[grey]<NOT SET>[/]",
        stat.DateCreated.ToString("yyyy-MM-dd"),
        stat.DateModified.ToString("yyyy-MM-dd"),
        !stat.NeedsUpdate ? "[grey]NONE[/]"
            : stat.DateTaken.HasValue ? "[green]FIX[/]" 
            : "[red]SKIP[/]"
    });
}
AnsiConsole.Write(table);

var toFix = imageStats.Where(i => i.NeedsUpdate && i.DateTaken.HasValue).ToArray();
if (toFix.Any())
{
    if (AnsiConsole.Prompt(new ConfirmationPrompt("Fix dates?")))
    {
        Parallel.ForEach(toFix, i =>
        {
            if (i.ExifDate != i.DateTaken)
            {
                var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + Path.GetExtension(i.File));
                {
                    var dateBytes = Encoding.ASCII.GetBytes(i.DateTaken!.Value.ToString("yyyy:MM:dd HH:mm:ss") + "\0");
                    using var image = Image.FromFile(i.File);
                    foreach (var tag in new[] { ExifTags.DateTime, ExifTags.DateTimeDigitized })
                    {
                        var item = (PropertyItem)Activator.CreateInstance(typeof(PropertyItem), nonPublic: true);
                        item.Id = (int)tag;
                        item.Type = 2; // ascii string
                        item.Value = dateBytes;
                        item.Len = dateBytes.Length;
                        image.SetPropertyItem(item);
                    }
                    image.Save(tempPath);
                }
                File.Copy(tempPath, i.File, overwrite: true);
                File.Delete(tempPath);
            }
            File.SetCreationTime(i.File, i.DateTaken!.Value);
            File.SetLastWriteTime(i.File, i.DateTaken!.Value);
        });
        AnsiConsole.WriteLine("Dates updated!");
        
    }
}
else
{
    AnsiConsole.WriteLine("Nothing to do!");
}

AnsiConsole.WriteLine("Press any key to exit.");
Console.ReadKey();

static DateTime TruncateMillis(DateTime date) => date.AddMilliseconds(-date.Millisecond);