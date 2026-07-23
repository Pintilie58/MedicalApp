using MedicalApp.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Text.RegularExpressions;

namespace MedicalApp.Services
{
    /// <summary>
    /// Generates the A4 PDF report from an InterpretationResult.
    /// Layout rules: horizontal separator lines only (no boxes), clean vertical alignment,
    /// branded header/footer with www.mymedicalapp.net and tagline.
    /// </summary>
    public class PdfReportGenerator
    {
        private const string BrandColor = "#0d47a1";   // deep blue
        private const string AccentRed = "#c62828";   // high values
        private const string AccentBlue = "#1565c0";  // low values
        private const string AccentGreen = "#2e7d32"; // normal
        private const string AccentYellow = "#f9a825";// borderline
        private const string MutedText = "#6c757d";

        // Freemium blur palette — kept here so the blur visually reads as a single
        // "redacted" effect across the whole report.
        private const string BlurBlockColor = "#dadce0";   // text replaced by gray bars
        private const string BlurRowBackground = "#f5f6f7"; // subtle background tint
        private const string WatermarkColor = "#eef0f2";   // very light gray "DEMO"

        static PdfReportGenerator()
        {
            // QuestPDF Community license is free for personal use and companies
            // with annual revenue under 1M USD. Replace if/when moving to a paid tier.
            QuestPDF.Settings.License = LicenseType.Community;
        }

        /// <summary>Legacy entry point — generates an unblurred (paid) report.</summary>
        public byte[] Generate(InterpretationResult result, LocalizedLabels labels)
            => Generate(result, labels, isFreemium: false);

        /// <summary>
        /// Generates the PDF. When <paramref name="isFreemium"/> is true, ~60% of the
        /// data-heavy sections (results table, abnormal findings, correlations,
        /// recommendations, risk factors) are replaced with redacted gray blocks
        /// and a large "DEMO" watermark is painted across every page.
        /// The summary stays visible so the user can taste the value before paying.
        /// </summary>
        public byte[] Generate(InterpretationResult result, LocalizedLabels labels, bool isFreemium)
        {
            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(10).FontColor(Colors.Black).FontFamily(Fonts.Arial));

                    if (isFreemium)
                    {
                        // Big light "DEMO" diagonal-ish watermark behind every page.
                        // We use AlignCenter+Middle for a centered banner; QuestPDF
                        // doesn't expose arbitrary-angle rotation in a portable way,
                        // so we ship with a horizontal large-font version which is
                        // perfectly readable and works on all renderers.
                        page.Background().AlignCenter().AlignMiddle()
                            .Text(labels.FreemiumWatermarkText)
                            .FontSize(140).Bold().FontColor(WatermarkColor);
                    }

                    page.Header().Element(h => ComposeHeader(h, labels));
                    page.Content().Element(c => ComposeContent(c, result, labels, isFreemium));
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

                col.Item().AlignCenter().Text("www.mymedicalapp.net")
                    .FontSize(9).FontColor(BrandColor);

                col.Item().PaddingVertical(6).LineHorizontal(1).LineColor(BrandColor);
            });
        }

        // -------------------- Content --------------------
        private static void ComposeContent(IContainer container, InterpretationResult r, LocalizedLabels labels, bool isFreemium)
        {
            container.PaddingVertical(10).Column(col =>
            {
                col.Spacing(14);

                // Freemium banner — sits right at the top so the user can never
                // miss why parts of the report are redacted.
                if (isFreemium)
                {
                    col.Item().Background("#FFF8E1").BorderLeft(4).BorderColor("#F9A825")
                        .Padding(10).Column(b =>
                        {
                            b.Item().Text(labels.FreemiumBannerTitle)
                                .FontSize(11).Bold().FontColor("#7A5700");
                            b.Item().PaddingTop(2).Text(labels.FreemiumBannerBody)
                                .FontSize(9).FontColor("#7A5700");
                        });
                }

                // Opening tagline
                col.Item().AlignCenter().Text(labels.Tagline)
                    .FontSize(11).Italic().FontColor(BrandColor);

                // Patient info — never blurred (the user already has this info)
                if (r.PatientInfo != null)
                {
                    col.Item().Element(e => Section(e, labels.PatientInfo));
                    col.Item().Element(e => PatientInfoTable(e, r.PatientInfo, labels));
                }

                // Summary — never blurred (teaser to lure the upgrade)
                if (!string.IsNullOrWhiteSpace(r.Summary))
                {
                    col.Item().Element(e => Section(e, labels.Summary));
                    col.Item().Text(r.Summary!).FontSize(10);
                }

                // Risk Factors — list, intercalated blur in freemium
                if (r.RiskFactors != null && r.RiskFactors.Count > 0)
                {
                    col.Item().Element(e => Section(e, labels.RiskFactors));
                    var visibleRisks = r.RiskFactors.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                    for (int i = 0; i < visibleRisks.Count; i++)
                    {
                        var rf = visibleRisks[i];
                        bool blur = isFreemium && BlurAt(i);
                        col.Item().PaddingBottom(3).Row(row =>
                        {
                            row.AutoItem().PaddingRight(6).Text("⚠")
                                .FontColor(blur ? BlurBlockColor : AccentYellow).Bold().FontSize(11);
                            if (blur)
                                row.RelativeItem().Element(e => BlurifyTextBlock(e, rf, labels));
                            else
                                row.RelativeItem().Text(rf).FontSize(10);
                        });
                    }
                    // Mandatory short medical disclaimer right below the list.
                    col.Item().PaddingTop(2).Background("#FFF7E6")
                        .Padding(8).Text(labels.RiskFactorsDisclaimer)
                        .FontSize(8).Italic().FontColor(MutedText);
                }

                // Key results table — most data-heavy section, prime blur target
                if (r.KeyResults != null && r.KeyResults.Count > 0)
                {
                    col.Item().Element(e => Section(e, labels.KeyResults));
                    col.Item().Element(e => KeyResultsTable(e, r.KeyResults, labels, isFreemium));
                }

                // Abnormal findings — list, intercalated blur in freemium
                if (r.AbnormalFindings != null && r.AbnormalFindings.Count > 0)
                {
                    col.Item().Element(e => Section(e, labels.AbnormalFindings));
                    for (int i = 0; i < r.AbnormalFindings.Count; i++)
                    {
                        var f = r.AbnormalFindings[i];
                        bool blur = isFreemium && BlurAt(i);
                        col.Item().Element(e => AbnormalFindingBlock(e, f, blur, labels));
                    }
                }

                // Correlations — sentence-level intercalated blur
                if (!string.IsNullOrWhiteSpace(r.Correlations))
                {
                    col.Item().Element(e => Section(e, labels.Correlations));
                    if (isFreemium)
                        col.Item().Element(e => BlurifySentenceText(e, r.Correlations!, labels));
                    else
                        col.Item().Text(r.Correlations!).FontSize(10);
                }

                // Recommendations — sentence-level intercalated blur
                if (!string.IsNullOrWhiteSpace(r.Recommendations))
                {
                    col.Item().Element(e => Section(e, labels.Recommendations));
                    if (isFreemium)
                        col.Item().Element(e => BlurifySentenceText(e, r.Recommendations!, labels));
                    else
                        col.Item().Text(r.Recommendations!).FontSize(10);
                }

                // Doctor Questions — numbered list 1..N, intercalated blur in freemium.
                // Mirrors the Risk Factors pattern: graceful if null/empty (older
                // interpretations in the DB simply don't render this section).
                if (r.DoctorQuestions != null && r.DoctorQuestions.Count > 0)
                {
                    col.Item().Element(e => Section(e, labels.DoctorQuestions));
                    var visibleQuestions = r.DoctorQuestions
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                    for (int i = 0; i < visibleQuestions.Count; i++)
                    {
                        var q = visibleQuestions[i];
                        bool blur = isFreemium && BlurAt(i);
                        col.Item().PaddingBottom(3).Row(row =>
                        {
                            row.AutoItem().PaddingRight(6).Text($"{i + 1}.")
                                .FontColor(blur ? BlurBlockColor : AccentYellow).Bold().FontSize(11);
                            if (blur)
                                row.RelativeItem().Element(e => BlurifyTextBlock(e, q, labels));
                            else
                                row.RelativeItem().Text(q).FontSize(10);
                        });
                    }
                    // Mandatory short MyMedicalApp.NET disclaimer right below the list.
                    col.Item().PaddingTop(2).Background("#FFF7E6")
                        .Padding(8).Text(labels.DoctorQuestionsDisclaimer)
                        .FontSize(8).Italic().FontColor(MutedText);
                }

                // LOINC source legend
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

                // Final freemium CTA — restate the upgrade message at the bottom.
                if (isFreemium)
                {
                    col.Item().PaddingTop(12).Background("#E8F5E9").BorderLeft(4).BorderColor(AccentGreen)
                        .Padding(10).Column(b =>
                        {
                            b.Item().Text(labels.FreemiumCtaTitle)
                                .FontSize(11).Bold().FontColor("#1B5E20");
                            b.Item().PaddingTop(2).Text(labels.FreemiumCtaBody)
                                .FontSize(9).FontColor("#1B5E20");
                        });
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

                col.Item().AlignCenter().Text("www.mymedicalapp.net")
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

        private static void KeyResultsTable(IContainer e, List<KeyResult> results, LocalizedLabels labels, bool isFreemium)
        {
            e.PaddingVertical(4).Table(t =>
            {
                t.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(5);   // parameter + explanation + LOINC
                    c.RelativeColumn(2);   // value + unit
                    c.RelativeColumn(2);   // reference
                    c.RelativeColumn(1);   // status (arrow)
                });

                t.Header(h =>
                {
                    h.Cell().PaddingBottom(4).Text(labels.Parameter).SemiBold().FontColor(BrandColor).FontSize(10);
                    h.Cell().PaddingBottom(4).AlignRight().Text(labels.Value).SemiBold().FontColor(BrandColor).FontSize(10);
                    h.Cell().PaddingBottom(4).AlignCenter().Text(labels.Reference).SemiBold().FontColor(BrandColor).FontSize(10);
                    h.Cell().PaddingBottom(4).AlignCenter().Text(labels.Status).SemiBold().FontColor(BrandColor).FontSize(10);
                });

                // Track the panel header of the previous row so we only emit
                // the group header once per contiguous block of parameters.
                // Preserves Gemini's key_results order (== PDF section order).
                string? lastHeader = null;
                bool firstIteration = true;

                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    var (arrow, color) = StatusArrow(r.Status);
                    bool blur = isFreemium && BlurAt(i);

                    // Group header row (span all 4 columns) — emitted only when
                    // panel_header_raw changes vs. the previous row. Contains
                    // the verbatim panel/section header text copied by Gemini
                    // from the original PDF (e.g. "Hemoleucograma completa -
                    // Sange - Spectroscopie de impedanta ... (PENTRA ES 60)").
                    // Not blurred for freemium: it is not PHI, only metadata.
                    var currentHeader = string.IsNullOrWhiteSpace(r.PanelHeaderRaw)
                        ? null : r.PanelHeaderRaw!.Trim();
                    bool headerChanged = firstIteration ||
                        !string.Equals(currentHeader, lastHeader, StringComparison.Ordinal);
                    if (headerChanged && !string.IsNullOrWhiteSpace(currentHeader))
                    {
                        t.Cell().ColumnSpan(4).PaddingTop(6).PaddingBottom(2)
                            .Text(currentHeader)
                            .SemiBold().Italic().FontSize(9).FontColor(BrandColor);
                    }
                    lastHeader = currentHeader;
                    firstIteration = false;

                    // Parameter cell
                    t.Cell().PaddingVertical(4).BorderTop(0.25f).BorderColor(Colors.Grey.Lighten2)
                        .Background(blur ? BlurRowBackground : Colors.White)
                        .Column(c =>
                        {
                            if (blur)
                            {
                                c.Item().Text(BlockText(r.Parameter ?? "Parameter", 18))
                                    .SemiBold().FontSize(10).FontColor(BlurBlockColor);
                                c.Item().PaddingTop(1).Text(BlockText("explanation", 50))
                                    .FontSize(8).FontColor(BlurBlockColor);
                                c.Item().PaddingTop(2).Text(text =>
                                {
                                    text.Span("🔒 ").FontSize(8).FontColor(MutedText);
                                    text.Span(labels.FreemiumLockedLabel).FontSize(7).Italic().FontColor(MutedText);
                                });
                            }
                            else
                            {
                                c.Item().Text(r.Parameter).SemiBold().FontSize(10);
                                // Inline analyte metadata copied verbatim from the PDF row
                                // (specimen + method + analyzer, e.g.
                                // "-Ser - Turbidimetrie (ABX PENTRA C400 ISE)"). Displayed
                                // between the analyte name and the explanation so the user
                                // can see exactly which lab methodology produced the value
                                // and how the downstream LOINC axes were resolved.
                                if (!string.IsNullOrWhiteSpace(r.AnalyteLineRaw))
                                    c.Item().PaddingTop(1).Text(r.AnalyteLineRaw!.Trim())
                                        .Italic().FontSize(8).FontColor(MutedText);
                                if (!string.IsNullOrWhiteSpace(r.Explanation))
                                    c.Item().PaddingTop(1).Text(r.Explanation).FontSize(8).FontColor(MutedText);
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
                                        text.Span("  ").FontSize(7);
                                        text.Span("●").FontSize(8)
                                            .FontColor(LoincSourceBadge.GetPdfColor(r.LoincSource));
                                        if (!LoincSourceBadge.IsVerified(r.LoincSource) && r.LoincScore.HasValue)
                                        {
                                            text.Span($" {(int)System.Math.Round(r.LoincScore.Value * 100)}%")
                                                .FontSize(7).FontColor(MutedText);
                                        }
                                    });
                                }
                            }
                        });

                    // Value cell
                    t.Cell().PaddingVertical(4).BorderTop(0.25f).BorderColor(Colors.Grey.Lighten2)
                        .Background(blur ? BlurRowBackground : Colors.White)
                        .AlignRight().Text(text =>
                        {
                            if (blur)
                            {
                                text.Span("████").FontSize(10).SemiBold().FontColor(BlurBlockColor);
                            }
                            else
                            {
                                text.Span(r.Value ?? "-").FontSize(10).SemiBold().FontColor(color);
                                if (!string.IsNullOrWhiteSpace(r.Unit))
                                    text.Span(" " + r.Unit).FontSize(9).FontColor(MutedText);
                            }
                        });

                    // Reference cell
                    t.Cell().PaddingVertical(4).BorderTop(0.25f).BorderColor(Colors.Grey.Lighten2)
                        .Background(blur ? BlurRowBackground : Colors.White)
                        .AlignCenter().Text(blur ? "█████" : (r.ReferenceRange ?? "-"))
                        .FontSize(9).FontColor(blur ? BlurBlockColor : MutedText);

                    // Status cell
                    t.Cell().PaddingVertical(4).BorderTop(0.25f).BorderColor(Colors.Grey.Lighten2)
                        .Background(blur ? BlurRowBackground : Colors.White)
                        .AlignCenter().Text(blur ? "?" : arrow)
                        .FontSize(12).Bold().FontColor(blur ? BlurBlockColor : color);
                }
            });
        }

        private static void AbnormalFindingBlock(IContainer e, AbnormalFinding f, bool blur, LocalizedLabels labels)
        {
            var severityColor = f.Severity switch
            {
                "severe" => AccentRed,
                "moderate" => AccentYellow,
                _ => AccentBlue
            };
            e.PaddingVertical(3).Row(row =>
            {
                row.ConstantItem(6).Background(blur ? BlurBlockColor : severityColor);
                row.ConstantItem(6);
                row.RelativeItem().Column(c =>
                {
                    if (blur)
                    {
                        c.Item().Text(BlockText(f.Parameter ?? "Parameter", 22))
                            .Bold().FontSize(10).FontColor(BlurBlockColor);
                        c.Item().Text(BlockText(f.Explanation ?? "explanation explanation", 60))
                            .FontSize(9).FontColor(BlurBlockColor);
                        c.Item().PaddingTop(1).Text(t =>
                        {
                            t.Span("🔒 ").FontSize(8).FontColor(MutedText);
                            t.Span(labels.FreemiumLockedLabel).FontSize(7).Italic().FontColor(MutedText);
                        });
                    }
                    else
                    {
                        c.Item().Text(f.Parameter).Bold().FontSize(10).FontColor(severityColor);
                        if (!string.IsNullOrWhiteSpace(f.Explanation))
                            c.Item().Text(f.Explanation).FontSize(9);
                    }
                });
            });
        }

        // -------------------- Freemium helpers --------------------

        /// <summary>
        /// Intercalated blur pattern: hides ~60% of items in a list while keeping
        /// visible items distributed (positions 0 and 3 visible, 1, 2, 4 hidden,
        /// then the pattern repeats). Gives a natural "you see some, you miss
        /// most" feel rather than a clean truncation at the end.
        /// </summary>
        private static bool BlurAt(int index) => (index % 5) is 1 or 2 or 4;

        /// <summary>
        /// Replaces visible text characters with the Unicode full block "█"
        /// using approximately the same width as the original, so the line
        /// still looks like a real text row.
        /// </summary>
        private static string BlockText(string original, int approxLength)
        {
            int len = Math.Clamp(string.IsNullOrEmpty(original) ? approxLength : original.Length, 6, approxLength);
            return new string('█', len);
        }

        /// <summary>
        /// Renders a single text item as a gray redacted block — used inside
        /// risk-factor and abnormal-finding lists.
        /// </summary>
        private static void BlurifyTextBlock(IContainer e, string original, LocalizedLabels labels)
        {
            e.Column(c =>
            {
                c.Item().Text(BlockText(original, 60)).FontSize(10).FontColor(BlurBlockColor);
                c.Item().Text(t =>
                {
                    t.Span("🔒 ").FontSize(8).FontColor(MutedText);
                    t.Span(labels.FreemiumLockedLabel).FontSize(7).Italic().FontColor(MutedText);
                });
            });
        }

        /// <summary>
        /// Sentence-level intercalated blur for free-text fields (Correlations,
        /// Recommendations). Splits on sentence boundaries and replaces ~60%
        /// of sentences with gray block text, keeping the rest readable.
        /// </summary>
        private static void BlurifySentenceText(IContainer e, string original, LocalizedLabels labels)
        {
            _ = labels; // labels reserved for future per-sentence "[locked]" annotations
            var sentences = Regex.Split(original, @"(?<=[\.\!\?])\s+")
                                 .Where(s => !string.IsNullOrWhiteSpace(s))
                                 .ToList();

            if (sentences.Count == 0)
            {
                e.Text(original).FontSize(10);
                return;
            }

            e.Text(t =>
            {
                for (int i = 0; i < sentences.Count; i++)
                {
                    if (BlurAt(i))
                    {
                        t.Span(BlockText(sentences[i], Math.Min(sentences[i].Length, 90)))
                            .FontSize(10).FontColor(BlurBlockColor);
                        t.Span(" ");
                    }
                    else
                    {
                        t.Span(sentences[i]).FontSize(10);
                        t.Span(" ");
                    }
                }
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
        public string RiskFactors { get; set; } = "";
        public string RiskFactorsDisclaimer { get; set; } = "";
        public string KeyResults { get; set; } = "";
        public string Parameter { get; set; } = "";
        public string Value { get; set; } = "";
        public string Reference { get; set; } = "";
        public string Status { get; set; } = "";
        public string AbnormalFindings { get; set; } = "";
        public string Correlations { get; set; } = "";
        public string Recommendations { get; set; } = "";
        public string DoctorQuestions { get; set; } = "";
        public string DoctorQuestionsDisclaimer { get; set; } = "";
        public string Disclaimer { get; set; } = "";
        public string GeneratedOn { get; set; } = "";
        public string Page { get; set; } = "";

        // Freemium-only labels
        public string FreemiumBannerTitle { get; set; } = "";
        public string FreemiumBannerBody { get; set; } = "";
        public string FreemiumWatermarkText { get; set; } = "";
        public string FreemiumLockedLabel { get; set; } = "";
        public string FreemiumCtaTitle { get; set; } = "";
        public string FreemiumCtaBody { get; set; } = "";

        public string ProcessingMode { get; set; } = "";

        /// <summary>Builds the label set using the current UI culture's translations.</summary>
        public static LocalizedLabels ForCurrentUi() => new()
        {
            BrandTitle = "MyMedicalApp.NET",
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
            RiskFactors = Loc.T("RiskFactorsSection"),
            RiskFactorsDisclaimer = Loc.T("RiskFactorsDisclaimer"),
            KeyResults = Loc.T("KeyResultsSection"),
            Parameter = Loc.T("Parameter"),
            Value = Loc.T("ValueLabel"),
            Reference = Loc.T("ReferenceRange"),
            Status = Loc.T("Status"),
            AbnormalFindings = Loc.T("AbnormalFindingsSection"),
            Correlations = Loc.T("CorrelationsSection"),
            Recommendations = Loc.T("RecommendationsSection"),
            DoctorQuestions = Loc.T("DoctorQuestionsSection"),
            DoctorQuestionsDisclaimer = Loc.T("DoctorQuestionsDisclaimer"),
            Disclaimer = Loc.T("DisclaimerSection"),
            GeneratedOn = Loc.T("GeneratedOn"),
            Page = Loc.T("Page"),
            FreemiumBannerTitle = Loc.T("PdfFreemiumBannerTitle"),
            FreemiumBannerBody = Loc.T("PdfFreemiumBannerBody"),
            FreemiumWatermarkText = Loc.T("PdfFreemiumWatermark"),
            FreemiumLockedLabel = Loc.T("PdfFreemiumLockedLabel"),
            FreemiumCtaTitle = Loc.T("PdfFreemiumCtaTitle"),
            FreemiumCtaBody = Loc.T("PdfFreemiumCtaBody")
        };
    }
}
