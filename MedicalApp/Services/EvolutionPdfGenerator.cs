using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Renders the multi-LOINC time-series ("Evoluție în timp") page into a
    /// PDF report that the user can attach to their medical file or email to a
    /// doctor. The PDF embeds:
    ///   - one PNG snapshot of the Chart.js graph (rendered client-side via
    ///     <c>chart.toBase64Image()</c> and posted to the server),
    ///   - one table per LOINC series with patient / lab / date / value /
    ///     status / unit / reference (same columns as the on-screen view).
    /// </summary>
    public class EvolutionPdfGenerator
    {
        public EvolutionPdfGenerator()
        {
            // QuestPDF Community license — same as PdfReportGenerator.
            QuestPDF.Settings.License = LicenseType.Community;
        }

        /// <summary>
        /// Build the PDF bytes.
        /// </summary>
        /// <param name="vm">Pre-built evolution view model.</param>
        /// <param name="chartPngBytes">
        /// PNG image of the Chart.js canvas as bytes. Pass null/empty to skip
        /// the image (the PDF will then contain only the tables and a note).
        /// </param>
        public byte[] Generate(EvolutionViewModel vm, byte[]? chartPngBytes)
        {
            var doc = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.DefaultTextStyle(x => x.FontSize(10).FontFamily(Fonts.Calibri));

                    page.Header().Element(h => ComposeHeader(h, vm));
                    page.Content().Element(c => ComposeContent(c, vm, chartPngBytes));
                    page.Footer().AlignCenter().Text(t =>
                    {
                        t.Span("MedicalApp — Evoluție în timp · ");
                        t.Span($"generat la {DateTime.Now:yyyy-MM-dd HH:mm}").FontColor(Colors.Grey.Medium);
                        t.Span("  ·  pagina ");
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            });

            return doc.GeneratePdf();
        }

        private static void ComposeHeader(IContainer container, EvolutionViewModel vm)
        {
            container.Column(col =>
            {
                col.Item().Text(t =>
                {
                    t.Span("Evoluție în timp — ").FontSize(16).Bold().FontColor(Colors.Blue.Darken2);
                    t.Span(vm.ProfileName).FontSize(16).Bold();
                });
                col.Item().Text($"{vm.Series.Count} analize · " +
                                $"{vm.Series.Sum(s => s.Points.Count)} măsurători")
                    .FontSize(10).FontColor(Colors.Grey.Darken1);
                col.Item().PaddingTop(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
            });
        }

        private static void ComposeContent(IContainer container, EvolutionViewModel vm, byte[]? chartPng)
        {
            container.PaddingVertical(8).Column(col =>
            {
                col.Spacing(12);

                // ---- 1) Chart image (if provided) ----
                if (chartPng != null && chartPng.Length > 0)
                {
                    col.Item().Text("Grafic combinat")
                        .FontSize(12).SemiBold().FontColor(Colors.Grey.Darken2);
                    col.Item().Image(chartPng).FitWidth();
                    col.Item().Text("Punctele sunt colorate după status (verde=normal, " +
                                    "roșu=ridicat, albastru=scăzut, galben=la limită). " +
                                    "Liniile punctate marchează intervalul de referință al primei analize.")
                        .FontSize(8).Italic().FontColor(Colors.Grey.Medium);
                }
                else
                {
                    col.Item().Text("Graficul nu a putut fi inclus în PDF.")
                        .FontSize(9).Italic().FontColor(Colors.Grey.Medium);
                }

                // ---- 2) One table per series ----
                foreach (var s in vm.Series)
                {
                    col.Item().PaddingTop(6).Column(seriesCol =>
                    {
                        seriesCol.Item().Text(t =>
                        {
                            t.Span($"{s.DisplayParameter}  ").FontSize(12).SemiBold()
                                .FontColor(ToQpdfColor(s.ColorHex));
                            t.Span($"(LOINC {s.LoincCode})").FontSize(9).FontColor(Colors.Grey.Darken1);
                            // Verification badge (matches the on-screen badge):
                            // green ✓ for anchor-mapped codes, yellow ~ for semantic.
                            t.Span("   ").FontSize(9);
                            t.Span(LoincSourceBadge.GetGlyph(s.LoincSource) + " " +
                                   LoincSourceBadge.GetLabel(s.LoincSource))
                                .FontSize(9).SemiBold()
                                .FontColor(LoincSourceBadge.GetPdfColor(s.LoincSource));
                            t.Span($"   ·   {s.ClassDisplayLabel}").FontSize(9).FontColor(Colors.Grey.Medium);
                        });

                        if (!string.IsNullOrWhiteSpace(s.LoincLongName))
                        {
                            seriesCol.Item().Text(s.LoincLongName!)
                                .FontSize(8).Italic().FontColor(Colors.Grey.Darken1);
                        }

                        seriesCol.Item().PaddingTop(4).Table(t =>
                        {
                            t.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(2.5f); // patient
                                c.RelativeColumn(2.5f); // lab
                                c.RelativeColumn(1.5f); // date
                                c.RelativeColumn(1.5f); // value
                                c.RelativeColumn(1.0f); // status
                                c.RelativeColumn(1.0f); // unit
                                c.RelativeColumn(2.0f); // reference
                            });

                            // Header
                            t.Header(h =>
                            {
                                static IContainer Hdr(IContainer c) => c
                                    .DefaultTextStyle(x => x.SemiBold().FontSize(9))
                                    .Background(Colors.Grey.Lighten3)
                                    .PaddingVertical(4).PaddingHorizontal(3);
                                h.Cell().Element(Hdr).Text("Pacient");
                                h.Cell().Element(Hdr).Text("Clinică / Laborator");
                                h.Cell().Element(Hdr).Text("Data");
                                h.Cell().Element(Hdr).AlignRight().Text("Valoare");
                                h.Cell().Element(Hdr).AlignCenter().Text("Status");
                                h.Cell().Element(Hdr).Text("Unit.");
                                h.Cell().Element(Hdr).Text("Interval");
                            });

                            foreach (var p in s.Points)
                            {
                                static IContainer Cell(IContainer c) => c
                                    .BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2)
                                    .PaddingVertical(3).PaddingHorizontal(3);
                                t.Cell().Element(Cell).Text(p.PatientName ?? "-").FontSize(9);
                                t.Cell().Element(Cell).Text(p.Laboratory ?? "-").FontSize(9);
                                t.Cell().Element(Cell).Text(p.DateLabel).FontSize(9);
                                t.Cell().Element(Cell).AlignRight().Text(p.Value ?? "-").FontSize(9).SemiBold();
                                t.Cell().Element(Cell).AlignCenter().Text(StatusGlyph(p.Status))
                                    .FontSize(10).FontColor(StatusColor(p.Status));
                                t.Cell().Element(Cell).Text(p.Unit ?? "-").FontSize(8).FontColor(Colors.Grey.Darken1);
                                t.Cell().Element(Cell).Text(p.ReferenceRange ?? "-").FontSize(8).FontColor(Colors.Grey.Darken1);
                            }
                        });
                    });
                }

                // ---- 3) Codes not found, if any ----
                if (vm.CodesNotFound.Any())
                {
                    col.Item().PaddingTop(8).Background(Colors.Yellow.Lighten4)
                        .Padding(6).Text(t =>
                        {
                            t.Span("⚠ Coduri LOINC negăsite în interpretările profilului: ")
                                .FontSize(8).SemiBold();
                            t.Span(string.Join(", ", vm.CodesNotFound)).FontSize(8);
                        });
                }
            });
        }

        private static string StatusGlyph(string? status) => status?.ToLowerInvariant() switch
        {
            "high"       => "↑",
            "low"        => "↓",
            "borderline" => "≈",
            "normal"     => "✓",
            _ => "-"
        };

        private static string StatusColor(string? status) => status?.ToLowerInvariant() switch
        {
            "high"       => Colors.Red.Darken1,
            "low"        => Colors.Blue.Darken1,
            "borderline" => Colors.Orange.Darken1,
            "normal"     => Colors.Green.Darken1,
            _ => Colors.Grey.Medium
        };

        /// <summary>Best-effort: pass hex strings like "#0d6efd" through to QuestPDF.</summary>
        private static string ToQpdfColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Colors.Black;
            return hex.StartsWith("#") ? hex : "#" + hex;
        }
    }
}
