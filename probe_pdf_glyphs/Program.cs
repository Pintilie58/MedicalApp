using QuestPDF;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

Settings.License = LicenseType.Community;
// Throws when a glyph is missing in the whole font chain -> exactly the bug
// the user reported (empty box with a question mark instead of the symbol).
Settings.CheckIfAllTextGlyphsAreAvailable = true;

string[] chain = { "Arial", "Liberation Sans", "DejaVu Sans" };
var glyphs = new (string Glyph, string Name)[]
{
    ("\u2713", "U+2713 CHECK MARK"),
    ("\u26A0", "U+26A0 WARNING SIGN"),
    ("\u25AA", "U+25AA BLACK SMALL SQUARE (locked label)"),
    ("\u2191", "U+2191 UP"), ("\u2193", "U+2193 DOWN"),
    ("\u2197", "U+2197 NE"), ("\u2198", "U+2198 SE"), ("\u2195", "U+2195 UPDOWN"),
    ("\u2192", "U+2192 RIGHT"), ("\u2248", "U+2248 ALMOST EQUAL"),
    ("\u25CF", "U+25CF BLACK CIRCLE"), ("\u2588", "U+2588 FULL BLOCK"),
    ("\u00E2\u0103\u00EE\u0219\u021B", "RO diacritics placeholder"),
    ("ăâîșțĂÂÎȘȚ", "RO diacritics"),
    ("\U0001F512", "U+1F512 LOCK EMOJI (expected to FAIL)"),
};

int ok = 0, fail = 0;
foreach (var (glyph, name) in glyphs)
{
    try
    {
        Document.Create(c => c.Page(p =>
        {
            p.Size(PageSizes.A4);
            p.DefaultTextStyle(t => t.FontFamily(chain).FontSize(12));
            p.Content().Text(glyph);
        })).GeneratePdf();
        Console.WriteLine($"PASS  {name}");
        ok++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {name}  -> {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        fail++;
    }
}
Console.WriteLine($"\nPASS={ok} FAIL={fail}");
