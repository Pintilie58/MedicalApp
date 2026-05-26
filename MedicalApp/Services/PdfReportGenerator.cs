using MedicalApp.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MedicalApp.Services
{
    /// <summary>
    /// Generates the A4 PDF report from an InterpretationResult.
    /// Layout rules: horizontal separator lines only (no boxes), clean vertical alignment,
    /// branded header/footer with www.MedicalApp.com and tagline.
    /// </summary>
    public class PdfReportGenerator
    {
        private const string BrandColor = "#0d47a1";   // deep blue
        private const string AccentRed = "#c62828";   // high values
        private const string AccentBlue = "#1565c0";  // low values
        private const string AccentGreen = "#2e7d32"; // normal
        private const string AccentYellow = "#f9a825";// borderline
        private const string MutedText = "#6c757d";

        static PdfReportGenerator()
        {
            // QuestPDF Community license is free for personal use and companies
            // with annual revenue under 1M USD. Replace if/when moving to a paid tier.
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public byte[] Generate(InterpretationResult result, LocalizedLabels labels)
        {
            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.PageColor(Colors.White);
                    // We pin the font to Arial (instead of the QuestPDF default
                    // Lato) to avoid OpenType ligatures for "ti", "fi", "fl"
                    // which the PDF text-extraction layer of most viewers
                    // can't reverse — when the user copy-pasted parameter
                    // names like "Prothrombin time" or "Coagulation assay"
                    // the "ti" sequence silently dropped from the clipboard.
                    page.DefaultTextStyle(x => x.FontSize(10).FontColor(Colors.Black).FontFamily(Fonts.Arial));

                    page.Header().Element(h => ComposeHeader(h, labels));
                    page.Content().Element(c => ComposeContent(c, result, labels));
                    page.Footer().Element(f => ComposeFooter(f, labels));
                });
            }).GeneratePdf();
        }

        // -------------------- Header --------------------
        private static void ComposeHeader(IContainer container, LocalizedLabels labels)
        {
            container.Column(col =>
            {
                col.Spacing(2);

                col.Item().AlignCenter().Text(labels.BrandTitle)
                    .FontSize(20).Bold().FontColor(BrandColor);

                col.Item().AlignCenter().Text(labels.BrandSubtitle)
                    .FontSize(10).FontColor(MutedText);

                col.Item().AlignCenter().Text("www.MedicalApp.com")
                    .FontSize(9).FontColor(BrandColor);

                col.Item().PaddingVertical(6).LineHorizontal(1).LineColor(BrandColor);
            });
        }

        // -------------------- Content --------------------
        private static void ComposeContent(IContainer container, InterpretationResult r, LocalizedLabels labels)
        {
            container.PaddingVertical(10).Column(col =>
            {
                col.Spacing(14);

                // Opening tagline
                col.Item().AlignCenter().Text(labels.Tagline)
                    .FontSize(11).Italic().FontColor(BrandColor);

                // Patient info
                if (r.PatientInfo != null)
                {
                    col.Item().Element(e => Section(e, labels.PatientInfo));
                    col.Item().Element(e => PatientInfoTable(e, r.PatientInfo, labels));
                }

                // Summary
                if (!string.IsNullOrWhiteSpace(r.Summary))
                {
                    col.Item().Element(e => Section(e, labels.Summary));
                    col.Item().Text(r.Summary!).FontSize(10);
                }

                // Key results table
                if (r.KeyResults != null && r.KeyResults.Count > 0)
                {
                    col.Item().Element(e => Section(e, labels.KeyResults));
                    col.Item().Element(e => KeyResultsTable(e, r.KeyResults, labels));
                }

                // Abnormal findings
                if (r.AbnormalFindings != null && r.AbnormalFindings.Count > 0)
                {
                    col.Item().Element(e => Section(e, labels.AbnormalFindings));
                    foreach (var f in r.AbnormalFindings)
                    {
                        col.Item().Element(e => AbnormalFindingBlock(e, f));
                    }
                }

                // Correlations
                if (!string.IsNullOrWhiteSpace(r.Correlations))
                {
                    col.Item().Element(e => Section(e, labels.Correlations));
                    col.Item().Text(r.Correlations!).FontSize(10);
                }

                // Recommendations
                if (!string.IsNullOrWhiteSpace(r.Recommendations))
                {
                    col.Item().Element(e => Section(e, labels.Recommendations));
                    col.Item().Text(r.Recommendations!).FontSize(10);
                }

                // LOINC source legend — small dotted explanation right
                // before the disclaimer so users understand the colored
                // dots next to each parameter. Only emitted when there's
                // actually a results table to legend.
                if (r.KeyResults != null && r.KeyResults.Count > 0)
                {
                    col.Item().PaddingTop(4).Text(t =>
                    {
                        t.Span("● ").FontSize(8).FontColor(LoincSourceBadge.GetPdfColor(LoincSourceBadge.AnchorSource));
                        t.Span(Loc.T("LoincSourceLegendVerified"))
                            .FontSize(8).FontColor(MutedText);
                        t.Span("   ·   ").FontSize(8).FontColor(MutedText);
                        t.Span("● ").FontSize(8).FontColor(LoincSourceBadge.GetPdfColor(LoincSourceBadge.SemanticSource));
                        t.Span(Loc.T("LoincSourceLegendAuto"))
                            .FontSize(8).FontColor(MutedText);
                    });
                }

                // Disclaimer
                if (!string.IsNullOrWhiteSpace(r.Disclaimer))
                {
                    col.Item().Element(e => Section(e, labels.Disclaimer));
                    col.Item().Text(r.Disclaimer!).FontSize(9).Italic().FontColor(MutedText);
                }
            });
        }

        // -------------------- Footer --------------------
        private static void ComposeFooter(IContainer container, LocalizedLabels labels)
        {
            container.Column(col =>
            {
                col.Spacing(3);
                col.Item().PaddingTop(6).LineHorizontal(1).LineColor(BrandColor);

                col.Item().AlignCenter().Text(labels.Tagline)
                    .FontSize(10).Italic().FontColor(BrandColor);

                col.Item().AlignCenter().Text("www.MedicalApp.com")
                    .FontSize(9).FontColor(BrandColor).Bold();

                col.Item().AlignCenter().Text(text =>
                {
                    text.Span($"{labels.GeneratedOn}: {DateTime.Now:yyyy-MM-dd HH:mm}   |   ")
                        .FontSize(8).FontColor(MutedText);
                    text.Span(labels.Page + " ").FontSize(8).FontColor(MutedText);
                    text.CurrentPageNumber().FontSize(8).FontColor(MutedText);
                    text.Span(" / ").FontSize(8).FontColor(MutedText);
                    text.TotalPages().FontSize(8).FontColor(MutedText);
                });

                // Optional processing-mode badge: tiny, discreet, italic. Helps the user
                // know whether digits in this report came from a literal text extraction
                // (rock-solid) or from a vision OCR pass (rare, only for scanned PDFs).
                if (!string.IsNullOrWhiteSpace(labels.ProcessingMode))
                {
                    col.Item().AlignCenter().Text(labels.ProcessingMode)
                        .FontSize(7).Italic().FontColor(MutedText);
                }
            });
        }

        // -------------------- Helper parts --------------------
        private static void Section(IContainer e, string title)
        {
            e.Column(col =>
            {
                col.Spacing(3);
                col.Item().Text(title).FontSize(12).Bold().FontColor(BrandColor);
                col.Item().LineHorizontal(0.5f).LineColor(BrandColor);
            });
        }

        private static void PatientInfoTable(IContainer e, PatientInfo p, LocalizedLabels labels)
        {
            e.PaddingVertical(4).Column(col =>
            {
                col.Spacing(3);
                InfoRow(col, labels.Name, p.Name);
                InfoRow(col, labels.Age, p.Age);
                InfoRow(col, labels.Sex, p.Sex);
                InfoRow(col, labels.DateTaken, p.DateTaken);
                InfoRow(col, labels.Laboratory, p.Laboratory);
                InfoRow(col, labels.DoctorRequesting, p.DoctorRequesting);
            });
        }

        private static void InfoRow(ColumnDescriptor col, string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            col.Item().Row(row =>
            {
                row.ConstantItem(140).Text(label + ":").SemiBold().FontColor(MutedText).FontSize(10);
                row.RelativeItem().Text(value).FontSize(10);
            });
        }

        private static void KeyResultsTable(IContainer e, List<KeyResult> results, LocalizedLabels labels)
        {
            e.PaddingVertical(4).Table(t =>
            {
                t.ColumnsDefinition(c =>
                {
                    // Wider parameter column so the explanation text below each
                    // parameter wraps less and uses fewer vertical lines.
                    // Previously: 3/8 (~37.5%) of the page width — explanations
                    // wrapped to many lines. Now: 5/10 (50%), with the other
                    // three columns shrunk proportionally. The value/reference
                    // columns stay readable because their content is short
                    // (numbers, units, "12-18 mg/dL", and an arrow).
                    c.RelativeColumn(5);   // parameter + explanation + LOINC
                    c.RelativeColumn(2);   // value + unit
                    c.RelativeColumn(2);   // reference
                    c.RelativeColumn(1);   // status (arrow)
                });

                // Header row with bottom line only
                t.Header(h =>
                {
                    h.Cell().PaddingBottom(4).Text(labels.Parameter).SemiBold().FontColor(BrandColor).FontSize(10);
                    h.Cell().PaddingBottom(4).AlignRight().Text(labels.Value).SemiBold().FontColor(BrandColor).FontSize(10);
                    h.Cell().PaddingBottom(4).AlignCenter().Text(labels.Reference).SemiBold().FontColor(BrandColor).FontSize(10);
                    h.Cell().PaddingBottom(4).AlignCenter().Text(labels.Status).SemiBold().FontColor(BrandColor).FontSize(10);
                });

                foreach (var r in results)
                {
                    var (arrow, color) = StatusArrow(r.Status);

                    t.Cell().PaddingVertical(4).BorderTop(0.25f).BorderColor(Colors.Grey.Lighten2)
                        .Column(c =>
                        {
                            c.Item().Text(r.Parameter).SemiBold().FontSize(10);
                            if (!string.IsNullOrWhiteSpace(r.Explanation))
                                c.Item().PaddingTop(1).Text(r.Explanation).FontSize(8).FontColor(MutedText);

                            // Show the official LOINC code + long common name in a
                            // small grey footer line under each parameter. This makes
                            // the report internationally recognizable: the same code
                            // identifies the same test in any hospital / EHR /
                            // research database worldwide. The block is only rendered
                            // when the matcher actually resolved a code (LoincCode
                            // is null for proprietary indices or low-confidence
                            // skips, in which case we just don't print it).
                            if (!string.IsNullOrWhiteSpace(r.LoincCode))
                            {
                                c.Item().PaddingTop(2).Text(text =>
                                {
                                    text.Span("LOINC ").FontSize(7).FontColor(MutedText);
                                    text.Span(r.LoincCode!).FontSize(7).SemiBold().FontColor(MutedText);
                                    if (!string.IsNullOrWhiteSpace(r.LoincLongName))
                                    {
                                        text.Span("  ·  ").FontSize(7).FontColor(MutedText);
                                        text.Span(r.LoincLongName!).FontSize(7).FontColor(MutedText);
                                    }
                                    // Compact colored dot (●) replaces the older
                                    // "verified/auto" badge — the codes are
                                    // correct in BOTH cases; the dot is just a
                                    // hint about provenance. A legend at the
                                    // end of the PDF explains the convention.
                                    text.Span("  ").FontSize(7);
                                    text.Span("●").FontSize(8)
                                        .FontColor(LoincSourceBadge.GetPdfColor(r.LoincSource));
                                });
                            }
                        });

                    t.Cell().PaddingVertical(4).BorderTop(0.25f).BorderColor(Colors.Grey.Lighten2)
                        .AlignRight().Text(text =>
                        {
                            text.Span(r.Value ?? "-").FontSize(10).SemiBold().FontColor(color);
                            if (!string.IsNullOrWhiteSpace(r.Unit))
                                text.Span(" " + r.Unit).FontSize(9).FontColor(MutedText);
                        });

                    t.Cell().PaddingVertical(4).BorderTop(0.25f).BorderColor(Colors.Grey.Lighten2)
                        .AlignCenter().Text(r.ReferenceRange ?? "-").FontSize(9).FontColor(MutedText);

                    t.Cell().PaddingVertical(4).BorderTop(0.25f).BorderColor(Colors.Grey.Lighten2)
                        .AlignCenter().Text(arrow).FontSize(12).Bold().FontColor(color);
                }
            });
        }

        private static void AbnormalFindingBlock(IContainer e, AbnormalFinding f)
        {
            var severityColor = f.Severity switch
            {
                "severe" => AccentRed,
                "moderate" => AccentYellow,
                _ => AccentBlue
            };
            e.PaddingVertical(3).Row(row =>
            {
                row.ConstantItem(6).Background(severityColor);
                row.ConstantItem(6);
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(f.Parameter).Bold().FontSize(10).FontColor(severityColor);
                    if (!string.IsNullOrWhiteSpace(f.Explanation))
                        c.Item().Text(f.Explanation).FontSize(9);
                });
            });
        }

        private static (string arrow, string color) StatusArrow(string status) => status?.ToLowerInvariant() switch
        {
            "high" => ("\u2191", AccentRed),       // ↑ red
            "low" => ("\u2193", AccentBlue),       // ↓ blue
            "borderline" => ("\u2248", AccentYellow), // ≈ yellow
            _ => ("\u2713", AccentGreen),          // ✓ green (normal)
        };
    }

    /// <summary>Labels for PDF text, localized per user's UI language.</summary>
    public class LocalizedLabels
    {
        public string BrandTitle { get; set; } = "";
        public string BrandSubtitle { get; set; } = "";
        public string Tagline { get; set; } = "";
        public string PatientInfo { get; set; } = "";
        public string Name { get; set; } = "";
        public string Age { get; set; } = "";
        public string Sex { get; set; } = "";
        public string DateTaken { get; set; } = "";
        public string Laboratory { get; set; } = "";
        public string DoctorRequesting { get; set; } = "";
        public string Summary { get; set; } = "";
        public string KeyResults { get; set; } = "";
        public string Parameter { get; set; } = "";
        public string Value { get; set; } = "";
        public string Reference { get; set; } = "";
        public string Status { get; set; } = "";
        public string AbnormalFindings { get; set; } = "";
        public string Correlations { get; set; } = "";
        public string Recommendations { get; set; } = "";
        public string Disclaimer { get; set; } = "";
        public string GeneratedOn { get; set; } = "";
        public string Page { get; set; } = "";

        /// <summary>
        /// Optional footer note describing how the PDF was processed: text-mode (literal
        /// extraction by PdfPig) vs vision-mode (Gemini visual OCR). Caller sets this
        /// per-interpretation; if left blank the footer line is omitted entirely.
        /// </summary>
        public string ProcessingMode { get; set; } = "";

        /// <summary>Builds the label set using the current UI culture's translations.</summary>
        public static LocalizedLabels ForCurrentUi() => new()
        {
            BrandTitle = "MedicalApp",
            BrandSubtitle = Loc.T("BrandSubtitle"),
            Tagline = Loc.T("Tagline"),
            PatientInfo = Loc.T("PatientInfoSection"),
            Name = Loc.T("PatientName"),
            Age = Loc.T("PatientAge"),
            Sex = Loc.T("PatientSex"),
            DateTaken = Loc.T("DateTaken"),
            Laboratory = Loc.T("Laboratory"),
            DoctorRequesting = Loc.T("DoctorRequesting"),
            Summary = Loc.T("SummarySection"),
            KeyResults = Loc.T("KeyResultsSection"),
            Parameter = Loc.T("Parameter"),
            Value = Loc.T("ValueLabel"),
            Reference = Loc.T("ReferenceRange"),
            Status = Loc.T("Status"),
            AbnormalFindings = Loc.T("AbnormalFindingsSection"),
            Correlations = Loc.T("CorrelationsSection"),
            Recommendations = Loc.T("RecommendationsSection"),
            Disclaimer = Loc.T("DisclaimerSection"),
            GeneratedOn = Loc.T("GeneratedOn"),
            Page = Loc.T("Page")
        };
    }
}
