using MedicalApp.Areas.CAM.Models;
using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Services
{
    /// <summary>
    /// Builds the "Original" workbench (patient identity preview + email
    /// deliverability per PDF). Extracted from CheckPdfsController.Index so the
    /// same table can be rendered inside /CAM/Files → De trimis. Logic unchanged.
    /// </summary>
    public class CamCheckPdfsBuilder
    {
        private readonly AppDbContext _db;
        private readonly ICamFileStore _files;
        private readonly CamPdfMetadataExtractor _extractor;
        private readonly EmailDeliverabilityChecker _emailChecker;
        private readonly ILogger<CamCheckPdfsBuilder> _logger;

        public CamCheckPdfsBuilder(
            AppDbContext db,
            ICamFileStore files,
            CamPdfMetadataExtractor extractor,
            EmailDeliverabilityChecker emailChecker,
            ILogger<CamCheckPdfsBuilder> logger)
        {
            _db = db;
            _files = files;
            _extractor = extractor;
            _emailChecker = emailChecker;
            _logger = logger;
        }

        public async Task<CamCheckPdfsViewModel> BuildAsync(Clinic clinic, CancellationToken requestAborted = default)
        {
            var vm = new CamCheckPdfsViewModel
            {
                ClinicName = clinic.Name,
                OriginalFolder = _files.GetDisplayLocation(clinic, CamFolder.Original)
            };

            var pdfs = (await _files.ListAsync(clinic, CamFolder.Original, ".pdf", requestAborted))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (pdfs.Count == 0)
                return vm;

            // Preload all overrides for this clinic in ONE query.
            var fileNames = pdfs.Select(f => f.Name).ToList();
            var overrides = await _db.ClinicPdfOverrides.AsNoTracking()
                .Where(o => o.ClinicId == clinic.Id && fileNames.Contains(o.FileName))
                .ToDictionaryAsync(o => o.FileName, o => o, requestAborted);

            foreach (var entry in pdfs)
            {
                var row = new CamCheckPdfsViewModel.Row
                {
                    FileName = entry.Name,
                    SizeKb = (int)Math.Round(entry.SizeBytes / 1024.0)
                };

                // Check for an operator override FIRST.
                if (overrides.TryGetValue(row.FileName, out var ov))
                {
                    row.PatientName = ov.OverrideName;
                    row.PatientEmail = ov.OverrideEmail;
                    row.IsValid = true;
                    row.IsManualOverride = true;
                }
                else
                {
                    try
                    {
                        var bytes = await _files.ReadAsync(clinic, CamFolder.Original, entry.Name, requestAborted)
                                    ?? Array.Empty<byte>();
                        // In CAM we ONLY trust 3 sources of patient identity — operator
                        // override, explicit [MedicalApp] block, or Gemini at batch time.
                        // Heuristic guesses are deliberately hidden here.
                        var meta = _extractor.Extract(bytes, row.FileName, clinicDomainBlacklist: null);

                        if (meta.MatchedExplicitBlock && meta.IsValid)
                        {
                            row.PatientName = meta.PatientName;
                            row.PatientEmail = meta.PatientEmail;
                            row.IsValid = true;
                            row.MatchedExplicitBlock = true;
                        }
                        else if (!meta.IsMedicalLabReport)
                        {
                            row.IsValid = false;
                            row.Reason = meta.Reason ?? "PDF does not look like a medical lab report.";
                        }
                        else
                        {
                            row.IsValid = false;
                            row.Reason = Loc.T("CamCheckRowReasonNoBlock");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "CheckPdfs: failed reading {File}", entry.Name);
                        row.IsValid = false;
                        row.Reason = "I/O error: " + ex.Message;
                    }
                }
                vm.Items.Add(row);
            }

            // Email deliverability check (syntax + DNS, cached per domain), capped at 6 s.
            using (var ctsAll = CancellationTokenSource.CreateLinkedTokenSource(requestAborted))
            {
                ctsAll.CancelAfter(TimeSpan.FromSeconds(6));
                var deliverabilityTasks = vm.Items
                    .Where(r => r.IsValid && !string.IsNullOrWhiteSpace(r.PatientEmail))
                    .Select(async r =>
                    {
                        try
                        {
                            var res = await _emailChecker.ValidateAsync(r.PatientEmail, ctsAll.Token);
                            r.EmailValidity = res.Validity;
                            r.EmailValidityMessage = res.FriendlyMessage;
                            r.EmailDomainSuggestion = res.DomainSuggestion;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "EmailDeliverabilityChecker failed for {Email}", r.PatientEmail);
                            r.EmailValidity = EmailValidity.DnsUnknown;
                            r.EmailValidityMessage = Loc.T("CamEmailValidationDnsUnavailable");
                        }
                    })
                    .ToList();
                try { await Task.WhenAll(deliverabilityTasks); }
                catch (OperationCanceledException)
                {
                    // 6-second global cap reached — unfinished rows keep their defaults.
                }
            }

            return vm;
        }
    }
}
