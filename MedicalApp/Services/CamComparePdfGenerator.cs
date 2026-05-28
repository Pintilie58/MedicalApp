using System.Text.Json;
using MedicalApp.Controllers;
using MedicalApp.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MedicalApp.Services
{
    /// <summary>
    /// Compare PDF pentru un pacient CAM, folosind EXACT aceeași logică de
    /// grupare LOINC (cu fallback pe nume) ca în B2C
    /// <see cref="ProfilesController.BuildComparison"/>. PDF-ul afișează:
    ///   * antet cu pacientul și clinica,
    ///   * un tabel side-by-side, grupat pe LOINC class (Hematology / Chemistry / ...),
    ///   * pentru fiecare rând: numele parametrului, unitatea, codul LOINC,
    ///     și valorile observate pe fiecare coloană (cea mai veche la stânga).
    /// Maxim 4 coloane (ultimele 4 analize ale pacientului, deja filtrate de service).
    /// </summary>
    public class CamComparePdfGenerator
    {
        public CamComparePdfGenerator()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public byte[]? GenerateIfPossible(Clinic clinic, ClinicPatient patient, List<ClinicAnalysis> analyses)
        {
            // Need at least 2 analyses to compare. Take ≤ 4, sorted OLDEST FIRST
            // to match the B2C compare expectation.
            var sorted = analyses
                .OrderBy(a => a.SamplingDate ?? a.ProcessedAt)
                .ToList();
            if (sorted.Count < 2) return null;
            if (sorted.Count > 4)
                sorted = sorted.Skip(sorted.Count - 4).ToList();

            // Build the (InterpretationHistory, InterpretationResult) tuple list
            // that BuildComparison expects. We synthesise lightweight History
            // rows from each ClinicAnalysis — only the Date and OriginalFileName
            // fields are read downstream.
            var feed = new List<(InterpretationHistory h, InterpretationResult r)>();
            foreach (var a in sorted)
            {
                if (string.IsNullOrWhiteSpace(a.RawJsonResult)) continue;
                InterpretationResult? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<InterpretationResult>(a.RawJsonResult,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch { continue; }
                if (parsed == null) continue;

                var when = a.SamplingDate ?? a.ProcessedAt;
                var h = new InterpretationHistory
                {
                    OriginalFileName = a.OriginalFileName,
                    CreatedAt = when,
                    UserEmail = clinic.UserEmail
                };
                feed.Add((h, parsed));
            }
            if (feed.Count < 2) return null;

            // Reuse the B2C compare builder so LOINC code grouping, class
            // grouping, drift detection, etc. are 100% identical between
            // personal-account and clinic-account compare PDFs.
            var fakeProfile = new Profile
            {
                UserEmail = clinic.UserEmail,
                Name = patient.Name
            };
            var vm = ProfilesController.BuildComparison(fakeProfile, feed);

            // ----- Render with QuestPDF -----
            var dateLabels = vm.Columns
                .Select(c => c.EffectiveDate.ToLocalTime().ToString("dd MMM yyyy"))
                .ToList();

            var doc = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(1.4f, Unit.Centimetre);
                    page.DefaultTextStyle(t => t.FontFamily("Arial").FontSize(9));

                    page.Header().Column(col =>
                    {
                        col.Item().Text("Comparație analize medicale")
                            .FontSize(14).Bold().FontColor(Colors.Blue.Darken3);
                        col.Item().Text($"Pacient: {patient.Name}").FontSize(10);
                        col.Item().Text($"Clinică: {clinic.Name}").FontSize(9).FontColor(Colors.Grey.Darken1);
                        col.Item().PaddingBottom(6);
                    });

                    page.Content().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(3); // parameter + unit
                            c.RelativeColumn(1.4f); // LOINC
                            for (int i = 0; i < dateLabels.Count; i++) c.RelativeColumn(1.8f);
                        });

                        table.Header(h =>
                        {
                            h.Cell().Element(HeaderCellStyle).Text("Parametru").Bold();
                            h.Cell().Element(HeaderCellStyle).Text("LOINC").Bold();
                            foreach (var lbl in dateLabels)
                                h.Cell().Element(HeaderCellStyle).AlignCenter().Text(lbl).Bold();
                        });

                        string? previousClass = null;
                        int colSpanTotal = 2 + dateLabels.Count;
                        foreach (var row in vm.Rows)
                        {
                            // Class section header (rendered once when class changes).
                            if (!string.Equals(row.ClassDisplayLabel, previousClass, StringComparison.OrdinalIgnoreCase))
                            {
                                table.Cell().ColumnSpan((uint)colSpanTotal)
                                    .Element(c => c.Background(Colors.Blue.Lighten5).PaddingVertical(3).PaddingHorizontal(4))
                                    .Text(row.ClassDisplayLabel)
                                    .Bold().FontColor(Colors.Blue.Darken2).FontSize(10);
                                previousClass = row.ClassDisplayLabel;
                            }

                            var paramLabel = string.IsNullOrWhiteSpace(row.Unit)
                                ? row.Parameter
                                : $"{row.Parameter} ({row.Unit})";

                            table.Cell().Element(BodyCellStyle).Text(t =>
                            {
                                t.Span(paramLabel);
                                if (row.HasLoincDrift)
                                    t.Span(" ⚠").FontColor(Colors.Orange.Darken2).Bold();
                            });
                            table.Cell().Element(BodyCellStyle).Text(row.LoincCode ?? "-")
                                .FontSize(8).FontColor(Colors.Grey.Darken2);

                            foreach (var cell in row.Cells)
                            {
                                table.Cell().Element(BodyCellStyle).AlignCenter().Text(t =>
                                {
                                    t.Span(cell.Value ?? "—");
                                    // Status is the lab's normal/high/low marker — bold red when abnormal.
                                    if (!string.IsNullOrWhiteSpace(cell.Status) &&
                                        !cell.Status.Equals("normal", StringComparison.OrdinalIgnoreCase) &&
                                        !cell.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                                    {
                                        t.Span("  !").FontColor(Colors.Red.Medium).Bold();
                                    }
                                });
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

        private static IContainer HeaderCellStyle(IContainer c) =>
            c.Background(Colors.Blue.Lighten4).Padding(4).BorderBottom(1).BorderColor(Colors.Blue.Darken1);

        private static IContainer BodyCellStyle(IContainer c) =>
            c.Padding(4).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);
    }
}
