using MedicalApp.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MedicalApp.Services
{
    /// <summary>
    /// B2C Profile Compare PDF — same visual layout as
    /// <see cref="CamComparePdfGenerator"/> but driven by a B2C
    /// <see cref="Profile"/> + already-built
    /// <see cref="CompareInterpretationsViewModel"/>.
    ///
    /// Intentionally a *separate* class with its own copy of the QuestPDF
    /// rendering so changes here cannot regress the CAM flow.
    /// </summary>
    public class ProfileComparePdfGenerator
    {
        public ProfileComparePdfGenerator()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        /// <summary>Renders the comparison as a landscape A4 PDF and returns the bytes.</summary>
        public byte[] Generate(Profile profile, CompareInterpretationsViewModel vm)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(vm);

            // Normalize every date label to YYYY-MM-DD.
            var dateLabels = vm.Columns
                .Select(c => c.EffectiveDate.ToLocalTime().ToString("yyyy-MM-dd"))
                .ToList();

            var doc = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(1.2f, Unit.Centimetre);
                    page.DefaultTextStyle(t => t.FontFamily("Arial").FontSize(9));

                    page.Header().Column(col =>
                    {
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Text(t =>
                            {
                                t.Span("Comparație profil: ").FontSize(15).Bold().FontColor(Colors.Blue.Darken3);
                                t.Span(profile.Name).FontSize(15).Bold().FontColor(Colors.Blue.Darken3);
                            });
                            r.ConstantItem(140)
                                .AlignRight()
                                .Background(Colors.Grey.Darken1)
                                .PaddingVertical(3).PaddingHorizontal(8)
                                .Text($"{vm.Columns.Count} interpretări")
                                .FontSize(9).FontColor(Colors.White).Bold();
                        });
                        col.Item().PaddingTop(4).PaddingBottom(2).Text(
                            "Coloanele sunt ordonate cronologic, de la stânga (mai vechi) la dreapta (mai recent), " +
                            "după data recoltării (sau, dacă lipsește, după data interpretării).")
                            .FontSize(8).Italic().FontColor(Colors.Grey.Darken1);
                    });

                    page.Content().Column(content =>
                    {
                        // ---------- Per-column mini cards ----------
                        var cardBorders = new[]
                        {
                            Colors.Blue.Medium, Colors.Cyan.Medium,
                            Colors.Orange.Medium, Colors.Green.Medium
                        };
                        content.Item().PaddingBottom(6).Row(row =>
                        {
                            for (int i = 0; i < vm.Columns.Count; i++)
                            {
                                var c = vm.Columns[i];
                                var border = cardBorders[i % cardBorders.Length];
                                row.RelativeItem().PaddingRight(i == vm.Columns.Count - 1 ? 0 : 4)
                                    .Border(0.7f).BorderColor(border)
                                    .Padding(6).Column(card =>
                                {
                                    card.Item().Text($"Interpretarea {i + 1}")
                                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                                    if (!string.IsNullOrWhiteSpace(c.PatientName))
                                    {
                                        card.Item().Text(t =>
                                        {
                                            t.Span("Pacient: ").FontSize(8).FontColor(Colors.Grey.Darken1);
                                            t.Span(c.PatientName).FontSize(9).SemiBold()
                                                .FontColor(Colors.Grey.Darken3);
                                        });
                                    }
                                    card.Item().Text(t =>
                                    {
                                        t.Span("Recoltare: ").FontSize(9).Bold();
                                        t.Span(c.EffectiveDate.ToLocalTime().ToString("yyyy-MM-dd"))
                                            .FontSize(9).Bold();
                                    });
                                    card.Item().Text(
                                        $"Interpretat: {c.CreatedAt.ToLocalTime():yyyy-MM-dd}")
                                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                                    card.Item().Text(
                                        $"{c.KeyResultsCount} parametri · {c.AbnormalFindingsCount} anormalități")
                                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                                });
                            }
                            for (int i = vm.Columns.Count; i < 4; i++)
                                row.RelativeItem();
                        });

                        // ---------- Summary badges row ----------
                        content.Item().PaddingBottom(8).Row(row =>
                        {
                            void Badge(string text, string bg, string fg)
                            {
                                row.AutoItem().PaddingRight(4).Background(bg)
                                    .PaddingVertical(3).PaddingHorizontal(8)
                                    .Text(text).FontSize(8).Bold().FontColor(fg);
                            }
                            Badge($"↗ Crescute: {vm.RisenCount}", Colors.Red.Medium, Colors.White);
                            Badge($"↘ Scăzute: {vm.FallenCount}", Colors.Blue.Lighten2, Colors.Black);
                            Badge($"= Neschimbate: {vm.UnchangedCount}", Colors.Green.Medium, Colors.White);
                            if (vm.PartialCount > 0)
                                Badge($"⚠ Doar parțial: {vm.PartialCount}",
                                    Colors.Yellow.Medium, Colors.Black);
                            row.RelativeItem();
                        });

                        // ---------- Main comparison table ----------
                        content.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(2.6f);
                                for (int i = 0; i < dateLabels.Count; i++)
                                    c.RelativeColumn(1.4f);
                                c.RelativeColumn(1.3f);
                            });

                            table.Header(h =>
                            {
                                h.Cell().Element(HeaderCellStyle).Text("Parametru").Bold();
                                foreach (var lbl in dateLabels)
                                    h.Cell().Element(HeaderCellStyle).AlignCenter().Text(lbl).Bold();
                                h.Cell().Element(HeaderCellStyle).Text("Referință")
                                    .FontSize(8).FontColor(Colors.Grey.Darken2).Bold();
                            });

                            int colSpanTotal = 2 + dateLabels.Count;
                            foreach (var row in vm.Rows)
                            {
                                if (row.IsFirstInClass)
                                {
                                    table.Cell().ColumnSpan((uint)colSpanTotal)
                                        .Background("#EEF3FF").PaddingVertical(4).PaddingHorizontal(6)
                                        .Text(t =>
                                        {
                                            t.Span((row.ClassDisplayLabel ?? string.Empty).ToUpperInvariant())
                                                .FontSize(9).Bold().FontColor(Colors.Grey.Darken4);
                                            if (!string.IsNullOrWhiteSpace(row.LoincClass))
                                            {
                                                t.Span("   ").FontSize(9);
                                                t.Span(row.LoincClass).FontSize(8)
                                                    .FontColor(Colors.Grey.Darken2);
                                            }
                                        });
                                }

                                table.Cell().Element(BodyCellStyle).Text(t =>
                                {
                                    t.Span(row.Parameter).SemiBold();
                                    if (!string.IsNullOrWhiteSpace(row.Unit))
                                    {
                                        t.Span($" ({row.Unit})").FontSize(8).FontColor(Colors.Grey.Darken1);
                                    }
                                    if (row.HasLoincDrift)
                                    {
                                        t.Span("  ⚠").FontColor(Colors.Orange.Darken2).Bold();
                                    }
                                    if (!string.IsNullOrWhiteSpace(row.LoincCode))
                                    {
                                        t.Span($"  LOINC {row.LoincCode}")
                                            .FontSize(7).FontColor(Colors.Grey.Darken2);
                                        t.Span(" ●")
                                            .FontSize(7)
                                            .FontColor(LoincSourceBadge.GetPdfColor(row.LoincSource));
                                        if (!LoincSourceBadge.IsVerified(row.LoincSource) && row.LoincScore.HasValue)
                                        {
                                            t.Span($" {(int)Math.Round(row.LoincScore.Value * 100)}%")
                                                .FontSize(7).FontColor(Colors.Grey.Darken1);
                                        }
                                    }
                                });

                                foreach (var cell in row.Cells)
                                {
                                    table.Cell().Element(c => CellBgFor(c, cell.CellDirection))
                                        .AlignCenter().Text(t =>
                                    {
                                        if (cell.CellDirection == "absent")
                                        {
                                            t.Span("—").FontColor(Colors.Grey.Darken1);
                                        }
                                        else
                                        {
                                            t.Span(cell.Value ?? "-").SemiBold();
                                            var statusGlyph = StatusGlyph(cell.Status);
                                            if (statusGlyph != null)
                                            {
                                                t.Span("  " + statusGlyph.Value.glyph)
                                                    .FontColor(statusGlyph.Value.color).Bold();
                                            }
                                            var dirGlyph = DirectionGlyph(cell.CellDirection);
                                            if (dirGlyph != null)
                                            {
                                                t.Span("  " + dirGlyph.Value.glyph)
                                                    .FontColor(dirGlyph.Value.color).Bold();
                                            }
                                        }
                                    });
                                }

                                table.Cell().Element(BodyCellStyle).Text(row.ReferenceRange ?? "-")
                                    .FontSize(8).FontColor(Colors.Grey.Darken2);
                            }
                        });

                        content.Item().PaddingTop(10).Text(t =>
                        {
                            t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                            t.Line("Rândurile sunt grupate pe clase de analize (Hematologie, Coagulare, Biochimie serică, " +
                                "Endocrinologie, Serologie, Biochimie urinară etc.) folosind câmpul oficial LOINC CLASS. " +
                                "În interiorul fiecărei clase, rândurile sunt aliniate după codul LOINC când este disponibil.");
                            t.Line("Prima coloană (cea cu fundal gri) este referința. " +
                                "↗ roșu = valoare crescută față de prima coloană · " +
                                "↘ albastru = scăzută · " +
                                "= neschimbat · " +
                                "galben „—\" = parametru lipsă în interpretarea respectivă.");
                            t.Span("● ").FontColor("#198754");
                            t.Span("verificat (anchor) · ");
                            t.Span("● ").FontColor("#0d6efd");
                            t.Span("auto (semantic) · ");
                            t.Span("⚠ ").FontColor(Colors.Orange.Darken2).Bold();
                            t.Span("același nume de parametru a primit coduri LOINC diferite în interpretările comparate.");
                        });
                    });

                    page.Footer().AlignCenter().Text(t =>
                    {
                        t.Span("Generat automat de MedicalApp+ — ").FontSize(7).FontColor(Colors.Grey.Medium);
                        t.Span("medicalapp.ro").FontSize(7).FontColor(Colors.Blue.Medium);
                    });
                });
            });

            return doc.GeneratePdf();
        }

        // ============================================================
        //                  Styling helpers (private copy)
        // ============================================================

        private static IContainer HeaderCellStyle(IContainer c) =>
            c.Background(Colors.Grey.Lighten3).Padding(5)
             .BorderBottom(1).BorderColor(Colors.Grey.Darken1);

        private static IContainer BodyCellStyle(IContainer c) =>
            c.Padding(5).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);

        private static IContainer CellBgFor(IContainer c, string? direction)
        {
            string bg = direction switch
            {
                "first"  => "#F8F9FA",
                "risen"  => "#F8D7DA",
                "fallen" => "#CFF4FC",
                "absent" => "#FFF3CD",
                _ => "#FFFFFF"
            };
            return c.Background(bg).Padding(5)
                    .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);
        }

        private static (string glyph, string color)? StatusGlyph(string? status)
        {
            return (status ?? string.Empty).ToLowerInvariant() switch
            {
                "high"       => ("↑", Colors.Red.Darken2),
                "low"        => ("↓", Colors.Blue.Darken2),
                "borderline" => ("≈", Colors.Orange.Darken2),
                "normal"     => ("✓", Colors.Green.Darken2),
                _ => ((string, string)?)null
            };
        }

        private static (string glyph, string color)? DirectionGlyph(string? dir)
        {
            return (dir ?? string.Empty).ToLowerInvariant() switch
            {
                "risen"  => ("↗", Colors.Red.Darken2),
                "fallen" => ("↘", Colors.Blue.Darken2),
                _ => ((string, string)?)null
            };
        }
    }
}
