using MedicalApp.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MedicalApp.Services
{
    /// <summary>
    /// Generates the per-batch "Sumar Lot" PDF — a branded 1-2 page summary
    /// the clinic operator can download from the CAM Dashboard for any
    /// finished batch. Mirrors the content of
    /// <see cref="CamBatchSumarWriter"/> (the .txt sibling) but in a polished
    /// PDF format suitable for archiving / forwarding to clinic management.
    /// </summary>
    public class CamBatchSumarPdfGenerator
    {
        public CamBatchSumarPdfGenerator()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public byte[] Generate(Clinic clinic, ClinicBatchRun batch, List<ClinicBatchError> errors)
        {
            var doc = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(1.6f, Unit.Centimetre);
                    page.DefaultTextStyle(t => t.FontFamily("Arial").FontSize(10));

                    page.Header().Column(col =>
                    {
                        col.Item().Text("Sumar Lot — Procesare Buletine")
                            .FontSize(16).Bold().FontColor(Colors.Blue.Darken3);
                        col.Item().Text(clinic.Name).FontSize(11).SemiBold();
                        col.Item().Text($"{clinic.City} · {clinic.Address}")
                            .FontSize(8).FontColor(Colors.Grey.Darken1);
                        col.Item().PaddingTop(4)
                            .LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    });

                    page.Content().PaddingTop(10).Column(content =>
                    {
                        // ---------- Identitate lot ----------
                        content.Item().PaddingBottom(8).Row(r =>
                        {
                            r.RelativeItem().Column(c =>
                            {
                                c.Item().Text(t =>
                                {
                                    t.Span("Lot #").FontSize(9).FontColor(Colors.Grey.Darken1);
                                    t.Span(batch.Id.ToString()).FontSize(13).Bold();
                                });
                                c.Item().Text(t =>
                                {
                                    t.Span("Pornit: ").FontSize(9).FontColor(Colors.Grey.Darken1);
                                    t.Span(batch.StartedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss"))
                                        .FontSize(10).SemiBold();
                                });
                                c.Item().Text(t =>
                                {
                                    t.Span("Finalizat: ").FontSize(9).FontColor(Colors.Grey.Darken1);
                                    t.Span(batch.FinishedAt?.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss") ?? "-")
                                        .FontSize(10).SemiBold();
                                });
                                c.Item().Text(t =>
                                {
                                    t.Span("Durată: ").FontSize(9).FontColor(Colors.Grey.Darken1);
                                    var dur = (batch.FinishedAt - batch.StartedAt)?.ToString(@"hh\:mm\:ss") ?? "-";
                                    t.Span(dur).FontSize(10).SemiBold();
                                });
                            });

                            r.ConstantItem(140).AlignRight().AlignTop().Column(c =>
                            {
                                var (bg, fg) = StatusColors(batch.Status);
                                c.Item().AlignRight()
                                    .Background(bg).PaddingVertical(4).PaddingHorizontal(10)
                                    .Text(batch.Status.ToUpperInvariant())
                                    .FontSize(10).Bold().FontColor(fg);
                            });
                        });

                        // ---------- Statistici (4 KPI mini-cards) ----------
                        content.Item().PaddingBottom(10).Row(r =>
                        {
                            void Kpi(string label, int value, string bg, string fg)
                            {
                                r.RelativeItem().PaddingRight(6)
                                    .Background(bg).Padding(8).Column(c =>
                                {
                                    c.Item().Text(value.ToString())
                                        .FontSize(20).Bold().FontColor(fg);
                                    c.Item().Text(label)
                                        .FontSize(8).FontColor(fg);
                                });
                            }
                            Kpi("Procesate cu succes", batch.FilesInterpreted, "#E7F1FF", Colors.Blue.Darken3);
                            Kpi("Emailuri trimise",    batch.FilesSent,        "#D1E7DD", Colors.Green.Darken2);
                            Kpi("Comparații atașate",  batch.FilesCompared,    "#CFF4FC", Colors.Cyan.Darken2);
                            Kpi("NotSends (erori)",    batch.NotSends,         "#FFF3CD", Colors.Orange.Darken2);
                        });

                        content.Item().PaddingBottom(8).Text(t =>
                        {
                            t.Span("Total fișiere în lot: ").FontSize(10).FontColor(Colors.Grey.Darken2);
                            t.Span(batch.TotalFiles.ToString()).FontSize(11).Bold();
                            if (batch.TotalFiles > 0)
                            {
                                double successRate = 100.0 * batch.FilesSent / batch.TotalFiles;
                                t.Span($"  ·  Rată succes: {successRate:F1}%")
                                    .FontSize(10).FontColor(Colors.Grey.Darken2);
                            }
                        });

                        // ---------- Tabel erori ----------
                        if (errors.Count > 0)
                        {
                            content.Item().PaddingTop(8).PaddingBottom(4)
                                .Text("Fișiere NEtrimise (motive)")
                                .FontSize(12).Bold().FontColor(Colors.Orange.Darken2);

                            content.Item().Table(table =>
                            {
                                table.ColumnsDefinition(c =>
                                {
                                    c.ConstantColumn(70);   // ora
                                    c.RelativeColumn(2.4f); // fișier
                                    c.RelativeColumn(2);    // pacient
                                    c.RelativeColumn(3.2f); // motiv
                                    c.ConstantColumn(50);   // retries
                                });

                                table.Header(h =>
                                {
                                    h.Cell().Element(ErrHeaderCell).Text("Ora").Bold();
                                    h.Cell().Element(ErrHeaderCell).Text("Fișier").Bold();
                                    h.Cell().Element(ErrHeaderCell).Text("Pacient").Bold();
                                    h.Cell().Element(ErrHeaderCell).Text("Motiv").Bold();
                                    h.Cell().Element(ErrHeaderCell).AlignRight().Text("Try").Bold();
                                });

                                foreach (var e in errors)
                                {
                                    table.Cell().Element(ErrBodyCell)
                                        .Text(e.OccurredAt.ToLocalTime().ToString("HH:mm:ss"))
                                        .FontSize(8).FontColor(Colors.Grey.Darken2);
                                    table.Cell().Element(ErrBodyCell)
                                        .Text(e.FileName).FontSize(8);
                                    table.Cell().Element(ErrBodyCell)
                                        .Text(string.IsNullOrWhiteSpace(e.PatientName) ? "—" : e.PatientName!)
                                        .FontSize(8);
                                    table.Cell().Element(ErrBodyCell)
                                        .Text(e.Reason).FontSize(8).FontColor(Colors.Orange.Darken2);
                                    table.Cell().Element(ErrBodyCell).AlignRight()
                                        .Text(e.RetryCount.ToString()).FontSize(8);
                                }
                            });
                        }
                        else
                        {
                            content.Item().PaddingTop(8)
                                .Background("#D1E7DD").Padding(8)
                                .Text("Toate fișierele din lot au fost procesate cu succes.")
                                .FontSize(10).SemiBold().FontColor(Colors.Green.Darken2);
                        }
                    });

                    page.Footer().AlignCenter().Text(t =>
                    {
                        t.Span("Generat automat de MedicalApp+ — ").FontSize(8).FontColor(Colors.Grey.Medium);
                        t.Span("medicalapp.ro").FontSize(8).FontColor(Colors.Blue.Medium);
                        t.Span($"  ·  {DateTime.Now:dd MMM yyyy HH:mm}").FontSize(8).FontColor(Colors.Grey.Medium);
                    });
                });
            });

            return doc.GeneratePdf();
        }

        // ============================================================
        //                       Styling helpers
        // ============================================================

        private static IContainer ErrHeaderCell(IContainer c) =>
            c.Background(Colors.Grey.Lighten3).Padding(4)
             .BorderBottom(1).BorderColor(Colors.Grey.Darken1);

        private static IContainer ErrBodyCell(IContainer c) =>
            c.Padding(4).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);

        private static (string bg, string fg) StatusColors(string status)
        {
            return (status ?? string.Empty).ToLowerInvariant() switch
            {
                "completed" => ("#198754", Colors.White),
                "running"   => ("#0d6efd", Colors.White),
                "cancelled" => ("#ffc107", Colors.Black),
                "failed"    => ("#dc3545", Colors.White),
                _ => (Colors.Grey.Medium, Colors.White)
            };
        }
    }
}
