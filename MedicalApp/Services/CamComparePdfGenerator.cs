using System.Text.Json;
using MedicalApp.Controllers;
using MedicalApp.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MedicalApp.Services
{
    /// <summary>
    /// CAM Compare PDF — pixel-for-pixel an attempt to mirror the B2C
    /// <c>Views/Profiles/Compare.cshtml</c> layout:
    ///   1. Header (title + patient + clinic) with a small "interpretări" badge
    ///   2. One mini-card per analysis (Interpretarea N · Recoltare · Interpretat · X parametri · Y anormalități)
    ///   3. Summary badges (Crescute / Scăzute / Neschimbate / Doar parțial)
    ///   4. Main table — Parametru | date1 | date2 | ... | Referință
    ///        - class header rows that span the whole width
    ///        - per-cell value + status arrow (↑/↓/↕/✓) + direction arrow (↗ ↘ — ✓)
    ///        - drift warning ⚠ on the row label when LOINC drift occurs
    ///   5. Footer legend explaining the icons + LOINC source dots
    /// </summary>
    public class CamComparePdfGenerator
    {
        public CamComparePdfGenerator()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public byte[]? GenerateIfPossible(Clinic clinic, ClinicPatient patient, List<ClinicAnalysis> analyses)
        {
            // ----- Take 2..4 analyses, oldest first (same as B2C compare) -----
            var sorted = analyses
                .OrderBy(a => a.SamplingDate ?? a.ProcessedAt)
                .ToList();
            if (sorted.Count < 2) return null;
            if (sorted.Count > 4) sorted = sorted.Skip(sorted.Count - 4).ToList();

            // ----- Synthesise the (History, Result) tuples BuildComparison expects -----
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

                var h = new InterpretationHistory
                {
                    OriginalFileName = a.OriginalFileName,
                    // CreatedAt models the timestamp at which the interpretation
                    // ran — that's ProcessedAt for CAM (NOT the sampling date).
                    // The sampling date flows through PatientInfo.DateTaken and
                    // is recovered downstream by BuildComparison's ParseSamplingDate.
                    CreatedAt = a.ProcessedAt,
                    UserEmail = clinic.UserEmail
                };
                feed.Add((h, parsed));
            }
            if (feed.Count < 2) return null;

            // ----- Reuse the exact same builder as B2C so all the LOINC grouping,
            //       drift detection, summary counters etc. line up perfectly. -----
            var fakeProfile = new Profile { UserEmail = clinic.UserEmail, Name = patient.Name };
            var vm = ProfilesController.BuildComparison(fakeProfile, feed);

            // Column header text — normalize EVERY date to a clean YYYY-MM-DD
            // format. EffectiveDate is the parsed sampling date when Gemini
            // gave us anything parseable (the SamplingDateParser handles
            // ISO, dd.MM.yyyy, "06.12.2023 - 10:27", labels, etc.), or
            // CreatedAt (ProcessedAt) as a last-resort fallback. Either way,
            // we never show the raw ugly "2025-04-30T08:50:00" or
            // "06.12.2023 - 10:27" strings in the report.
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
                                t.Span(Loc.T("CamCompareTitle")).FontSize(15).Bold().FontColor(Colors.Blue.Darken3);
                                t.Span(patient.Name).FontSize(15).Bold().FontColor(Colors.Blue.Darken3);
                            });
                            r.ConstantItem(140)
                                .AlignRight()
                                .Background(Colors.Grey.Darken1)
                                .PaddingVertical(3).PaddingHorizontal(8)
                                .Text(string.Format(Loc.T("CamCompareInterpretationsBadge"), vm.Columns.Count))
                                .FontSize(9).FontColor(Colors.White).Bold();
                        });
                        col.Item().PaddingTop(2).Text(t =>
                        {
                            t.Span(Loc.T("CamCompareClinicLabel")).FontColor(Colors.Grey.Darken1);
                            t.Span(clinic.Name).FontColor(Colors.Grey.Darken3).SemiBold();
                        });
                        col.Item().PaddingTop(4).PaddingBottom(2).Text(
                            Loc.T("CamCompareSubtitle"))
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
                                    card.Item().Text(string.Format(Loc.T("CamCompareCardTitle"), i + 1))
                                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                                    // Patient name as Gemini saw it inside THIS PDF.
                                    // Helps the operator catch wrong-file-to-patient mismatches.
                                    if (!string.IsNullOrWhiteSpace(c.PatientName))
                                    {
                                        card.Item().Text(t =>
                                        {
                                            t.Span(Loc.T("CamComparePatientLabel")).FontSize(8).FontColor(Colors.Grey.Darken1);
                                            t.Span(c.PatientName).FontSize(9).SemiBold()
                                                .FontColor(Colors.Grey.Darken3);
                                        });
                                    }
                                    card.Item().Text(t =>
                                    {
                                        t.Span(Loc.T("CamCompareSamplingLabel")).FontSize(9).Bold();
                                        t.Span(c.EffectiveDate.ToLocalTime().ToString("yyyy-MM-dd"))
                                            .FontSize(9).Bold();
                                    });
                                    card.Item().Text(
                                        string.Format(Loc.T("CamCompareInterpretedLabel"),
                                            c.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd")))
                                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                                    card.Item().Text(
                                        string.Format(Loc.T("CamCompareCardStats"),
                                            c.KeyResultsCount, c.AbnormalFindingsCount))
                                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                                });
                            }
                            // Pad empty card slots so the 4-card grid stays uniform.
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
                            Badge(string.Format("↗ " + Loc.T("CamCompareBadgeRisen"), vm.RisenCount), Colors.Red.Medium, Colors.White);
                            Badge(string.Format("↘ " + Loc.T("CamCompareBadgeFallen"), vm.FallenCount), Colors.Blue.Lighten2, Colors.Black);
                            Badge(string.Format("= " + Loc.T("CamCompareBadgeUnchanged"), vm.UnchangedCount), Colors.Green.Medium, Colors.White);
                            if (vm.PartialCount > 0)
                                Badge(string.Format("⚠ " + Loc.T("CamCompareBadgePartial"), vm.PartialCount),
                                    Colors.Yellow.Medium, Colors.Black);
                            row.RelativeItem(); // filler
                        });

                        // ---------- Main comparison table ----------
                        content.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(2.6f); // Parametru
                                for (int i = 0; i < dateLabels.Count; i++)
                                    c.RelativeColumn(1.4f);
                                c.RelativeColumn(1.3f); // Referință
                            });

                            // ---- Header row ----
                            table.Header(h =>
                            {
                                h.Cell().Element(HeaderCellStyle).Text(Loc.T("CamCompareColParameter")).Bold();
                                foreach (var lbl in dateLabels)
                                    h.Cell().Element(HeaderCellStyle).AlignCenter().Text(lbl).Bold();
                                h.Cell().Element(HeaderCellStyle).Text(Loc.T("CamCompareColReference"))
                                    .FontSize(8).FontColor(Colors.Grey.Darken2).Bold();
                            });

                            int colSpanTotal = 2 + dateLabels.Count; // Parametru + dates + Referință
                            foreach (var row in vm.Rows)
                            {
                                // Class section header — span entire width when a new LOINC class starts.
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

                                // ---- Parameter label cell ----
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

                                // ---- Value cells (per analysis date column) ----
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
                                            // Status arrow (↑ high / ↓ low / ≈ borderline / ✓ normal)
                                            var statusGlyph = StatusGlyph(cell.Status);
                                            if (statusGlyph != null)
                                            {
                                                t.Span("  " + statusGlyph.Value.glyph)
                                                    .FontColor(statusGlyph.Value.color).Bold();
                                            }
                                            // Direction arrow (↗ risen red / ↘ fallen blue)
                                            var dirGlyph = DirectionGlyph(cell.CellDirection);
                                            if (dirGlyph != null)
                                            {
                                                t.Span("  " + dirGlyph.Value.glyph)
                                                    .FontColor(dirGlyph.Value.color).Bold();
                                            }
                                        }
                                    });
                                }

                                // ---- Reference range cell ----
                                table.Cell().Element(BodyCellStyle).Text(row.ReferenceRange ?? "-")
                                    .FontSize(8).FontColor(Colors.Grey.Darken2);
                            }
                        });

                        // ---------- Footer legend ----------
                        content.Item().PaddingTop(10).Text(t =>
                        {
                            t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                            t.Line(Loc.T("CamCompareLegendLine1"));
                            t.Line(Loc.T("CamCompareLegendLine2"));
                            t.Span("● ").FontColor("#198754");
                            t.Span(Loc.T("CamCompareLegendVerified"));
                            t.Span("● ").FontColor("#0d6efd");
                            t.Span(Loc.T("CamCompareLegendAuto"));
                            t.Span("⚠ ").FontColor(Colors.Orange.Darken2).Bold();
                            t.Span(Loc.T("CamCompareLegendDrift"));
                        });
                    });

                    page.Footer().AlignCenter().Text(t =>
                    {
                        t.Span(Loc.T("CamCompareFooter")).FontSize(7).FontColor(Colors.Grey.Medium);
                        t.Span("medicalapp.ro").FontSize(7).FontColor(Colors.Blue.Medium);
                    });
                });
            });

            return doc.GeneratePdf();
        }

        // ============================================================
        //                      Styling helpers
        // ============================================================

        private static IContainer HeaderCellStyle(IContainer c) =>
            c.Background(Colors.Grey.Lighten3).Padding(5)
             .BorderBottom(1).BorderColor(Colors.Grey.Darken1);

        private static IContainer BodyCellStyle(IContainer c) =>
            c.Padding(5).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);

        /// <summary>
        /// Background tint per CellDirection, mirroring Bootstrap's table-light /
        /// table-danger / table-info / table-warning classes used by the B2C view.
        /// Borders stay consistent with <see cref="BodyCellStyle"/>.
        /// </summary>
        private static IContainer CellBgFor(IContainer c, string? direction)
        {
            string bg = direction switch
            {
                "first"  => "#F8F9FA", // table-light
                "risen"  => "#F8D7DA", // table-danger
                "fallen" => "#CFF4FC", // table-info
                "absent" => "#FFF3CD", // table-warning
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
