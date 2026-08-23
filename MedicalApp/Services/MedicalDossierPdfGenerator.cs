using MedicalApp.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MedicalApp.Services
{
    /// <summary>
    /// B2C "Dosar medical" PDF — the printable sheet the patient takes to the
    /// doctor: only out-of-range results, grouped by medical specialty, each
    /// analyte with its own timeline and direction of change.
    ///
    /// Deliberately its own QuestPDF implementation (like
    /// <see cref="ProfileComparePdfGenerator"/>) so changes here cannot regress
    /// the interpretation report or the CAM flows.
    /// </summary>
    public class MedicalDossierPdfGenerator
    {
        private const string Brand = "#0d47a1";
        private const string High = "#c62828";
        private const string Low = "#1565c0";
        private const string Border = "#f9a825";
        private const string Muted = "#6c757d";

        public MedicalDossierPdfGenerator()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public byte[] Generate(MedicalDossierViewModel vm)
        {
            ArgumentNullException.ThrowIfNull(vm);

            var doc = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(1.2f, Unit.Centimetre);
                    page.DefaultTextStyle(t => t.FontFamily("Arial").FontSize(9));

                    page.Header().Column(h =>
                    {
                        h.Item().AlignCenter().Text("MyMedicalApp.NET")
                            .FontSize(16).Bold().FontColor(Brand);
                        h.Item().AlignCenter().Text(Loc.T("BrandSubtitle"))
                            .FontSize(8.5f).FontColor(Muted);
                        h.Item().AlignCenter().Text("www.mymedicalapp.net")
                            .FontSize(8).FontColor(Brand);
                        h.Item().PaddingTop(6).LineHorizontal(1.5f).LineColor(Brand);
                        h.Item().PaddingTop(6).Text(Loc.T("DossierTitle"))
                            .FontSize(14).Bold().FontColor(Brand);
                    });

                    page.Content().PaddingTop(8).Column(content =>
                    {
                        // ---------- Patient ----------
                        content.Item().Text(Loc.T("PatientInfoSection"))
                            .FontSize(10).Bold().FontColor(Brand);
                        content.Item().PaddingTop(2).Text(t =>
                        {
                            t.Span($"{Loc.T("PatientName")}: ").FontColor(Muted);
                            t.Span(vm.ProfileName).SemiBold();
                            if (vm.Age.HasValue)
                            {
                                t.Span($"    {Loc.T("PatientAge")}: ").FontColor(Muted);
                                t.Span(vm.Age.Value.ToString()).SemiBold();
                            }
                            if (!string.IsNullOrWhiteSpace(vm.Gender))
                            {
                                t.Span($"    {Loc.T("PatientSex")}: ").FontColor(Muted);
                                t.Span(vm.Gender!).SemiBold();
                            }
                        });

                        // ---------- Medical history (Profile.Notes) ----------
                        content.Item().PaddingTop(8).Text(Loc.T("DossierMedicalHistory"))
                            .FontSize(10).Bold().FontColor(Brand);
                        content.Item().PaddingTop(2).Text(
                            string.IsNullOrWhiteSpace(vm.MedicalHistory)
                                ? Loc.T("DossierNoNotes")
                                : vm.MedicalHistory!)
                            .FontSize(9)
                            .FontColor(string.IsNullOrWhiteSpace(vm.MedicalHistory) ? Muted : "#000000");

                        // ---------- Summary ----------
                        content.Item().PaddingTop(8).Background("#f4f7fb").Padding(6)
                            .Text(SummaryLine(vm)).FontSize(9).SemiBold().FontColor(Brand);

                        if (vm.AbnormalAnalyteCount == 0)
                        {
                            content.Item().PaddingTop(14).Background("#e8f5e9").Padding(10).Column(c =>
                            {
                                c.Item().Text(Loc.T("DossierEmptyTitle"))
                                    .FontSize(11).Bold().FontColor("#1b5e20");
                                c.Item().PaddingTop(2).Text(
                                    string.Format(Loc.T("DossierEmptyBody"), vm.SourceReportsCount))
                                    .FontSize(9).FontColor("#1b5e20");
                            });
                            return;
                        }

                        // ---------- Groups ----------
                        foreach (var group in vm.Groups)
                        {
                            content.Item().PaddingTop(12).BorderBottom(1).BorderColor(Brand)
                                .PaddingBottom(2)
                                .Text(group.Label).FontSize(10.5f).Bold().FontColor(Brand);

                            foreach (var a in group.Analytes)
                            {
                                // Analyte identity row
                                content.Item().PaddingTop(6).Text(t =>
                                {
                                    t.Span(a.Parameter).FontSize(10).Bold();
                                    if (!string.IsNullOrWhiteSpace(a.LoincCode))
                                    {
                                        t.Span($"   LOINC {a.LoincCode}").FontSize(8.5f).FontColor(Brand).SemiBold();
                                        if (!string.IsNullOrWhiteSpace(a.LoincLongName))
                                            t.Span($" · {a.LoincLongName}").FontSize(8).FontColor(Muted);
                                    }
                                    else
                                    {
                                        t.Span($"   ({Loc.T("DossierNoLoincCode")})").FontSize(8).Italic().FontColor(Muted);
                                    }
                                });

                                // Timeline table for this analyte
                                content.Item().PaddingTop(2).Table(table =>
                                {
                                    table.ColumnsDefinition(c =>
                                    {
                                        c.RelativeColumn(2.0f);  // date
                                        c.RelativeColumn(2.6f);  // laboratory
                                        c.RelativeColumn(1.8f);  // value
                                        c.RelativeColumn(2.6f);  // reference
                                        c.RelativeColumn(1.4f);  // status
                                        c.RelativeColumn(1.4f);  // trend
                                    });

                                    void Head(string text) =>
                                        table.Cell().Background("#eef1f5").Padding(3)
                                            .Text(text).FontSize(8).Bold().FontColor(Brand);

                                    Head(Loc.T("DossierColDate"));
                                    Head(Loc.T("DossierColLab"));
                                    Head(Loc.T("ValueLabel"));
                                    Head(Loc.T("ReferenceRange"));
                                    Head(Loc.T("Status"));
                                    Head(Loc.T("DossierColTrend"));

                                    foreach (var e in a.Entries)
                                    {
                                        var color = e.Status switch
                                        {
                                            "high" or "positive" => High,
                                            "low" => Low,
                                            "borderline" => Border,
                                            _ => "#000000"
                                        };

                                        table.Cell().BorderBottom(0.5f).BorderColor("#eceff3").Padding(3)
                                            .Text(t =>
                                            {
                                                t.Span(e.Date.ToLocalTime().ToString("yyyy-MM-dd")).FontSize(8.5f).SemiBold();
                                                if (!e.DateIsSampling)
                                                    t.Span($" ({Loc.T("DossierDateFromInterpretation")})").FontSize(7).FontColor(Muted);
                                            });

                                        table.Cell().BorderBottom(0.5f).BorderColor("#eceff3").Padding(3)
                                            .Text(e.Laboratory ?? "—").FontSize(8.5f).FontColor(Muted);

                                        table.Cell().BorderBottom(0.5f).BorderColor("#eceff3").Padding(3)
                                            .Text(t =>
                                            {
                                                t.Span(e.Value ?? "—").FontSize(9.5f).Bold().FontColor(color);
                                                if (!string.IsNullOrWhiteSpace(e.Unit))
                                                    t.Span($" {e.Unit}").FontSize(7.5f).FontColor(Muted);
                                            });

                                        table.Cell().BorderBottom(0.5f).BorderColor("#eceff3").Padding(3)
                                            .Text(e.ReferenceRange ?? "—").FontSize(8).FontColor(Muted);

                                        table.Cell().BorderBottom(0.5f).BorderColor("#eceff3").Padding(3)
                                            .Text(StatusLabel(e.Status)).FontSize(8).Bold().FontColor(color);

                                        table.Cell().BorderBottom(0.5f).BorderColor("#eceff3").Padding(3)
                                            .Text(TrendLabel(e.Trend)).FontSize(8)
                                            .FontColor(e.Trend == "up" ? High : e.Trend == "down" ? Low : Muted);
                                    }
                                });
                            }
                        }

                        content.Item().PaddingTop(14).Text(Loc.T("DossierDisclaimer"))
                            .FontSize(7.5f).Italic().FontColor(Muted);
                    });

                    page.Footer().AlignCenter().Text(t =>
                    {
                        t.Span("www.mymedicalapp.net  ").FontSize(8).FontColor(Brand);
                        t.CurrentPageNumber().FontSize(8).FontColor(Muted);
                        t.Span(" / ").FontSize(8).FontColor(Muted);
                        t.TotalPages().FontSize(8).FontColor(Muted);
                    });
                });
            });

            return doc.GeneratePdf();
        }

        private static string SummaryLine(MedicalDossierViewModel vm)
        {
            if (vm.FirstDate.HasValue && vm.LastDate.HasValue &&
                vm.FirstDate.Value.Date != vm.LastDate.Value.Date)
            {
                return string.Format(Loc.T("DossierSummaryFmt"),
                    vm.AbnormalAnalyteCount, vm.SourceReportsCount,
                    vm.FirstDate.Value.ToLocalTime().ToString("yyyy-MM-dd"),
                    vm.LastDate.Value.ToLocalTime().ToString("yyyy-MM-dd"));
            }

            return string.Format(Loc.T("DossierSummaryShortFmt"),
                vm.AbnormalAnalyteCount, vm.SourceReportsCount);
        }

        private static string StatusLabel(string status) => status switch
        {
            "high" => Loc.T("EvolutionPageStatusHigh"),
            "low" => Loc.T("EvolutionPageStatusLow"),
            "borderline" => Loc.T("EvolutionPageStatusBorderline"),
            "positive" => Loc.T("DossierStatusPositive"),
            _ => status
        };

        private static string TrendLabel(string trend) => trend switch
        {
            "up" => "\u2191 " + Loc.T("DossierTrendUp"),
            "down" => "\u2193 " + Loc.T("DossierTrendDown"),
            "same" => "= " + Loc.T("DossierTrendSame"),
            _ => "—"
        };
    }
}
