using System.Text.Json;
using MedicalApp.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MedicalApp.Services
{
    /// <summary>
    /// Generates a simple side-by-side comparison PDF for a CAM patient,
    /// across the LAST up-to-4 stored analyses (most recent on the right).
    /// Algorithm mirrors the B2C compare table but kept minimal:
    /// rows are grouped by LOINC code when available, else by normalized
    /// parameter name.
    /// </summary>
    public class CamComparePdfGenerator
    {
        public CamComparePdfGenerator()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public byte[]? GenerateIfPossible(Clinic clinic, ClinicPatient patient, List<ClinicAnalysis> analyses)
        {
            // Need at least 2 analyses to compare. Take last 4, oldest first.
            var sorted = analyses
                .OrderBy(a => a.SamplingDate ?? a.ProcessedAt)
                .Reverse()
                .Take(4)
                .Reverse()
                .ToList();
            if (sorted.Count < 2) return null;

            // Deserialize each.
            var cols = new List<(DateTime when, Dictionary<string, KeyResult> map, string label)>();
            foreach (var a in sorted)
            {
                if (string.IsNullOrWhiteSpace(a.RawJsonResult)) continue;
                InterpretationResult? r;
                try
                {
                    r = JsonSerializer.Deserialize<InterpretationResult>(a.RawJsonResult,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch { continue; }
                if (r?.KeyResults == null) continue;

                var map = r.KeyResults
                    .Where(k => !string.IsNullOrWhiteSpace(k.Parameter))
                    .GroupBy(k => !string.IsNullOrWhiteSpace(k.LoincCode)
                                  ? "loinc:" + k.LoincCode.Trim()
                                  : "name:" + k.Parameter.Trim().ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.First());

                var when = a.SamplingDate ?? a.ProcessedAt;
                cols.Add((when, map, when.ToLocalTime().ToString("dd MMM yyyy")));
            }
            if (cols.Count < 2) return null;

            // Union of all row keys.
            var allKeys = cols.SelectMany(c => c.map.Keys).Distinct().ToList();

            var doc = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(1.5f, Unit.Centimetre);
                    page.DefaultTextStyle(t => t.FontFamily("Arial").FontSize(9));

                    page.Header().Column(col =>
                    {
                        col.Item().Text("Comparație analize medicale").FontSize(14).Bold().FontColor(Colors.Blue.Darken3);
                        col.Item().Text($"Pacient: {patient.Name}").FontSize(10);
                        col.Item().Text($"Clinică: {clinic.Name}").FontSize(9).FontColor(Colors.Grey.Darken1);
                        col.Item().PaddingBottom(6);
                    });

                    page.Content().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(3); // parameter
                            for (int i = 0; i < cols.Count; i++) c.RelativeColumn(2);
                        });

                        table.Header(h =>
                        {
                            h.Cell().Element(HeaderCellStyle).Text("Parametru").Bold();
                            foreach (var c in cols)
                                h.Cell().Element(HeaderCellStyle).AlignCenter().Text(c.label).Bold();
                        });

                        foreach (var k in allKeys)
                        {
                            KeyResult? meta = null;
                            for (int i = cols.Count - 1; i >= 0 && meta == null; i--)
                                cols[i].map.TryGetValue(k, out meta);
                            if (meta == null) continue;

                            var label = meta.Parameter
                                + (string.IsNullOrWhiteSpace(meta.Unit) ? "" : $" ({meta.Unit})");

                            table.Cell().Element(BodyCellStyle).Text(label);
                            foreach (var c in cols)
                            {
                                if (c.map.TryGetValue(k, out var kr))
                                {
                                    table.Cell().Element(BodyCellStyle).AlignCenter().Text(kr.Value ?? "-");
                                }
                                else
                                {
                                    table.Cell().Element(BodyCellStyle).AlignCenter().Text("—")
                                        .FontColor(Colors.Grey.Medium);
                                }
                            }
                        }
                    });

                    page.Footer().AlignCenter().Text(t =>
                    {
                        t.Span("Generat automat de MedicalApp+ — ").FontSize(8).FontColor(Colors.Grey.Medium);
                        t.Span("medicalapp.ro").FontSize(8).FontColor(Colors.Blue.Medium);
                    });
                });
            });

            return doc.GeneratePdf();
        }

        private static QuestPDF.Infrastructure.IContainer HeaderCellStyle(QuestPDF.Infrastructure.IContainer c) =>
            c.Background(Colors.Blue.Lighten4).Padding(4).BorderBottom(1).BorderColor(Colors.Blue.Darken1);

        private static QuestPDF.Infrastructure.IContainer BodyCellStyle(QuestPDF.Infrastructure.IContainer c) =>
            c.Padding(4).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);
    }
}
