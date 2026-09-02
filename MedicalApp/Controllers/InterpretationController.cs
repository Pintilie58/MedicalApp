using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Security.Cryptography;

namespace MedicalApp.Controllers
{
    public class InterpretationController : Controller
    {
        private readonly AppDbContext _db;
        private readonly GeminiSettings _geminiSettings;
        private readonly IMemoryCache _cache;
        private readonly LoincMatcherClient _loincMatcher;
        private readonly IAiUsageLogger _aiUsage;
        private readonly InterpretationProgressTracker _progress;
        private readonly InterpretationJobQueue _queue;
        private readonly ILogger<InterpretationController> _logger;

        private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB
        private const string DupCacheKeyPrefix = "dup_pdf:";
        private static readonly TimeSpan DupCacheLifetime = TimeSpan.FromMinutes(15);

        // The heavy pipeline lives in B2cInterpretationRunner and runs in the
        // background worker — this controller only validates, reserves the credit
        // and queues the job, so it no longer needs the AI/PDF/email services.
        public InterpretationController(
            AppDbContext db,
            IOptions<GeminiSettings> geminiOptions,
            IMemoryCache cache,
            LoincMatcherClient loincMatcher,
            IAiUsageLogger aiUsage,
            InterpretationProgressTracker progress,
            InterpretationJobQueue queue,
            ILogger<InterpretationController> logger)
        {
            _db = db;
            _geminiSettings = geminiOptions.Value;
            _cache = cache;
            _loincMatcher = loincMatcher;
            _aiUsage = aiUsage;
            _progress = progress;
            _queue = queue;
            _logger = logger;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        [HttpGet]
        public async Task<IActionResult> Upload([FromServices] ILoincHealthState loincHealth)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Home");
            }

            if (user.CreditRest <= 0 && user.BonusCreditsRemaining <= 0)
            {
                TempData["ErrorMessage"] = Loc.T("NoCreditsBody");
                return RedirectToAction("Buy", "Credits");
            }

            ViewBag.CreditRest = user.CreditRest;
            ViewBag.BonusCreditsRemaining = user.BonusCreditsRemaining;
            ViewBag.TotalAvailableCredits = user.TotalAvailableCredits;

            // Pre-flight check: the LOINC microservice is what turns analytes into
            // codes used by charts/comparisons. Read the cached snapshot (0 ms) and
            // warn BEFORE the user spends a credit. "unknown" (no probe yet) and
            // "disabled" are not warnings. A cached failure is NEVER trusted on its
            // own: the monitor records a "timeout" whenever the service is alive but
            // busy (a batch match saturates the single uvicorn worker), so we
            // confirm live before showing anything and refresh the snapshot.
            if (!loincHealth.IsUp
                && loincHealth.LastProbeUtc.HasValue
                && !string.Equals(loincHealth.Status, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                var liveUp = await _loincMatcher.IsReadyAsync(HttpContext.RequestAborted);
                if (liveUp)
                {
                    loincHealth.Update(true, "ok", null, 0, loincHealth.LoincCount, loincHealth.BaseUrl);
                }
                else
                {
                    ViewBag.LoincOffline = true;
                    ViewBag.LoincOfflineDetail = $"{loincHealth.Status} — {loincHealth.Message}";
                }
            }

            // Load user's profiles for the dropdown.
            var profiles = await _db.Profiles
                .AsNoTracking()
                .Where(p => p.UserEmail == user.Email)
                .OrderByDescending(p => p.IsDefault)
                .ThenBy(p => p.Name)
                .Select(p => new InterpretationUploadViewModel.ProfileOption
                {
                    Id = p.Id,
                    Name = p.Name,
                    IsDefault = p.IsDefault
                })
                .ToListAsync();

            // "+ Profil nou" gate — same rule as /Profiles: B2C users need paid
            // credits for extra family profiles. Passed to the view so it can
            // grey-out the button. Server-side enforcement lives in
            // ProfilesController.Create; this ViewBag is UI-only.
            ViewBag.CanCreateProfile =
                ProfileGateService.CanCreateAdditionalProfile(user, profiles.Count);

            // A background job from an earlier visit may still be running — tell
            // the user instead of letting them start a second one and get refused.
            ViewBag.HasRunningJob = _queue.IsUserBusy(user.Email);

            var defaultId = profiles.FirstOrDefault(p => p.IsDefault)?.Id
                         ?? profiles.FirstOrDefault()?.Id;

            return View(new InterpretationUploadViewModel
            {
                AvailableProfiles = profiles,
                ProfileId = defaultId
            });
        }

        /// <summary>
        /// Live progress of the upload the browser is currently waiting on.
        /// Polled every 1.5s by the upload screen so it can show the stages and,
        /// as soon as the extraction is done, the table of results itself.
        /// </summary>
        [HttpGet]
        public IActionResult Progress(string? token)
        {
            if (string.IsNullOrEmpty(CurrentEmail)) return Unauthorized();

            var state = _progress.Get(token);
            if (state == null) return Json(new { stage = "upload" });

            return Json(new
            {
                stage = state.Stage,
                error = state.Error,
                outOfRange = state.OutOfRangeCount,
                analytes = state.Table?.Count ?? 0,
                table = state.Table,
                redirectUrl = state.RedirectUrl,
                historyId = state.HistoryId
            });
        }

        /// <summary>
        /// Global, token-free status of the user's background interpretation.
        /// Polled by the top-right indicator on EVERY page, so the user sees
        /// "PDF în lucru" and then "Gata!" even after leaving the upload screen.
        /// Reads the archive row (the source of truth), not the in-memory
        /// progress tracker, so it also survives a browser restart.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> JobStatus()
        {
            if (string.IsNullOrEmpty(CurrentEmail)) return Unauthorized();

            var running = await _db.InterpretationHistories.AsNoTracking()
                .Where(h => h.UserEmail == CurrentEmail && h.Status == "processing")
                .OrderByDescending(h => h.Id)
                .Select(h => new { h.Id })
                .FirstOrDefaultAsync();

            var lastDone = await _db.InterpretationHistories.AsNoTracking()
                .Where(h => h.UserEmail == CurrentEmail && h.Status == "success")
                .OrderByDescending(h => h.Id)
                .Select(h => new { h.Id })
                .FirstOrDefaultAsync();

            var lastFailed = await _db.InterpretationHistories.AsNoTracking()
                .Where(h => h.UserEmail == CurrentEmail
                            && (h.Status == "error" || h.Status == "rejected"))
                .OrderByDescending(h => h.Id)
                .Select(h => new { h.Id })
                .FirstOrDefaultAsync();

            return Json(new
            {
                running = running != null,
                runningId = running?.Id,
                lastDoneId = lastDone?.Id,
                lastDoneUrl = lastDone == null ? null : $"/Profiles/ViewReport/{lastDone.Id}",
                lastFailedId = lastFailed?.Id
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(MaxFileSize)]
        public async Task<IActionResult> Upload(InterpretationUploadViewModel model, bool force = false,
                                                string? reuploadToken = null, string? progressToken = null)
        {
            _progress.SetStage(progressToken, "upload");

            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Home");
            }

            if (user.CreditRest <= 0 && user.BonusCreditsRemaining <= 0)
            {
                TempData["ErrorMessage"] = Loc.T("NoCreditsBody");
                return RedirectToAction("Buy", "Credits");
            }

            // Validate ProfileId - must exist and belong to the current user.
            var profile = model.ProfileId.HasValue
                ? await _db.Profiles.FirstOrDefaultAsync(p =>
                    p.Id == model.ProfileId.Value && p.UserEmail == user.Email)
                : null;

            if (profile == null)
            {
                ModelState.AddModelError(nameof(model.ProfileId),
                    Loc.T("ErrSelectValidProfile"));
                await RepopulateFormViewBags(user, model);
                return View(model);
            }

            // Obtain the PDF bytes. Two sources:
            //   1) Normal upload path: bytes come from model.PdfFile.
            //   2) "Force re-interpret" path (user clicked the button on the
            //      duplicate-detected page): bytes were cached under reuploadToken.
            byte[] pdfBytes;
            string originalFileName;

            if (!string.IsNullOrWhiteSpace(reuploadToken)
                && _cache.TryGetValue<CachedUpload>(DupCacheKeyPrefix + reuploadToken, out var cached)
                && cached != null
                && cached.UserEmail == user.Email
                && cached.ProfileId == profile.Id)
            {
                pdfBytes = cached.PdfBytes;
                originalFileName = cached.FileName;
                // One-shot: consume so the token cannot be reused.
                _cache.Remove(DupCacheKeyPrefix + reuploadToken);
            }
            else
            {
                if (model.PdfFile == null || model.PdfFile.Length == 0)
                {
                    ModelState.AddModelError(nameof(model.PdfFile), Loc.T("PdfFileRequired"));
                    await RepopulateFormViewBags(user, model);
                    return View(model);
                }

                if (model.PdfFile.Length > MaxFileSize)
                {
                    ModelState.AddModelError(nameof(model.PdfFile), Loc.T("FileTooLarge"));
                    await RepopulateFormViewBags(user, model);
                    return View(model);
                }

                if (!model.PdfFile.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                    && !model.PdfFile.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError(nameof(model.PdfFile), Loc.T("OnlyPdfAllowed"));
                    await RepopulateFormViewBags(user, model);
                    return View(model);
                }

                originalFileName = Path.GetFileName(model.PdfFile.FileName);

                try
                {
                    using var stream = model.PdfFile.OpenReadStream();
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    pdfBytes = ms.ToArray();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read uploaded PDF");
                    await SaveHistory(user.Email, null, null, "error", ex.Message, 0, null, null, profile.Id, null, null);
                    TempData["ErrorMessage"] = Loc.T("PdfExtractFailed");
                    return RedirectToAction(nameof(Upload));
                }
            }

            var languageCode = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

            // Compute SHA-256 hash of the uploaded PDF for duplicate detection.
            string pdfHash = ComputeSha256(pdfBytes);

            // If the user did not explicitly force a re-interpretation, check
            // whether the exact same PDF (by hash) was already interpreted
            // successfully for this SAME profile.
            if (!force)
            {
                var dup = await _db.InterpretationHistories
                    .AsNoTracking()
                    .Where(h => h.UserEmail == user.Email
                                && h.ProfileId == profile.Id
                                && h.Status == "success"
                                && h.PdfSha256 == pdfHash
                                && h.RawJsonResult != null)
                    .OrderByDescending(h => h.CreatedAt)
                    .Select(h => new { h.Id, h.CreatedAt, h.OriginalFileName })
                    .FirstOrDefaultAsync();

                if (dup != null)
                {
                    // Cache the PDF bytes under a short-lived token so the user can
                    // force a re-interpretation with a single click, without being
                    // asked to re-select the file.
                    var token = Guid.NewGuid().ToString("N");
                    _cache.Set(DupCacheKeyPrefix + token,
                        new CachedUpload(user.Email, profile.Id, pdfBytes, originalFileName),
                        DupCacheLifetime);

                    var dupVm = new DuplicateDetectedViewModel
                    {
                        ExistingHistoryId = dup.Id,
                        ExistingCreatedAt = dup.CreatedAt,
                        ExistingFileName = dup.OriginalFileName,
                        ProfileId = profile.Id,
                        ProfileName = profile.Name,
                        OriginalFileName = originalFileName,
                        ReuploadToken = token
                    };
                    return View("DuplicateDetected", dupVm);
                }
            }

            // =================================================================
            //  BACKGROUND EXECUTION (June 2026)
            //  Everything past this point used to run inside THIS request, so the
            //  user stared at a blocked screen for 2-4 minutes and closing the tab
            //  killed the Gemini call mid-flight. Now the request only queues the
            //  work; InterpretationQueueWorker runs it and the browser follows
            //  along through /Interpretation/Progress.
            // =================================================================

            // One interpretation at a time per user — keeps us far away from
            // Gemini's per-project rate limit and makes the credit math obvious.
            if (_queue.IsUserBusy(user.Email))
            {
                TempData["ErrorMessage"] = Loc.T("InterpretationAlreadyRunning");
                return RedirectToAction(nameof(Upload));
            }

            // The browser generates the token; if scripts are disabled we still
            // need one so the job can publish its progress somewhere.
            if (string.IsNullOrWhiteSpace(progressToken))
                progressToken = Guid.NewGuid().ToString("N");

            // Reserve the credit NOW. Refunded automatically on failure, on a
            // non-medical rejection, and by the startup recovery after a restart.
            CreditLedger.ReserveOne(user);

            var pending = new InterpretationHistory
            {
                UserEmail = user.Email,
                OriginalFileName = originalFileName,
                Language = languageCode,
                Status = "processing",
                CreditsConsumed = 1,
                ProfileId = profile.Id,
                PdfSha256 = pdfHash,
                CreatedAt = DateTime.UtcNow
            };
            _db.InterpretationHistories.Add(pending);
            await _db.SaveChangesAsync();

            var job = new InterpretationJob(
                HistoryId: pending.Id,
                UserEmail: user.Email,
                ProfileId: profile.Id,
                ProfileName: profile.Name,
                PdfBytes: pdfBytes,
                OriginalFileName: originalFileName,
                PdfHash: pdfHash,
                LanguageCode: languageCode,
                Force: force,
                ProgressToken: progressToken);

            if (!_queue.TryEnqueue(job))
            {
                // Lost a race with another tab of the same user — undo cleanly.
                CreditLedger.RefundOne(user);
                _db.InterpretationHistories.Remove(pending);
                await _db.SaveChangesAsync();
                TempData["ErrorMessage"] = Loc.T("InterpretationAlreadyRunning");
                return RedirectToAction(nameof(Upload));
            }

            _logger.LogInformation(
                "Interpretation queued: history={Id}, user={Email}, profile={Pid}, file={File}.",
                pending.Id, user.Email, profile.Id, originalFileName);

            // Re-render the upload page with the progress overlay already open and
            // bound to this job's token. The user can stay and watch, or leave —
            // the job finishes either way.
            ViewBag.ActiveProgressToken = progressToken;
            ViewBag.ActiveHistoryId = pending.Id;
            ViewBag.ActiveProfileId = profile.Id;
            await RepopulateFormViewBags(user, model);
            return View(model);
        }

        private async Task<int> SaveHistory(string email, string? file, string? lang, string status,
            string? errorMsg, int credits, int? inTok, int? outTok, int? profileId = null,
            string? rawJson = null, string? pdfSha256 = null, string? modelUsed = null,
            StageTimer? timer = null)
        {
            var entity = new InterpretationHistory
            {
                UserEmail = email,
                OriginalFileName = file,
                Language = lang,
                Status = status,
                ErrorMessage = errorMsg?.Length > 500 ? errorMsg[..500] : errorMsg,
                CreditsConsumed = credits,
                InputTokens = inTok,
                OutputTokens = outTok,
                ProfileId = profileId,
                RawJsonResult = rawJson,
                PdfSha256 = pdfSha256,
                ModelUsed = modelUsed,
                DurationMs = timer != null ? (int)Math.Min(int.MaxValue, timer.TotalMs) : null,
                StageTimingsJson = timer?.ToJson(),
                CreatedAt = DateTime.UtcNow
            };
            _db.InterpretationHistories.Add(entity);
            await _db.SaveChangesAsync();

            // Also record into the dedicated AiUsageLogs table so the Admin
            // dashboard widget counts every real Gemini call (success/error/
            // rejected) and NOT just the user-facing successful interpretations.
            // We skip rows where no Gemini call actually happened (e.g. "rejected"
            // for empty PDF before any AI invocation), detected by inTok == null.
            bool geminiWasCalled = inTok.HasValue || outTok.HasValue
                                   || !string.IsNullOrWhiteSpace(modelUsed);
            if (geminiWasCalled)
            {
                await _aiUsage.LogAsync(
                    source: "B2C",
                    userEmail: email,
                    clinicId: null,
                    modelUsed: modelUsed ?? _geminiSettings.Model ?? "(unknown)",
                    inputTokens: inTok ?? 0,
                    outputTokens: outTok ?? 0,
                    status: status,
                    errorMessage: errorMsg);
            }

            return entity.Id;
        }

        /// <summary>Returns the hex SHA-256 (64 lowercase chars) of the PDF bytes.</summary>
        private static string ComputeSha256(byte[] bytes)
        {
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Holds the uploaded PDF bytes in memory so the user can trigger a
        /// "force re-interpret" without being asked to re-select the file.
        /// Short-lived (see DupCacheLifetime) and one-shot.
        /// </summary>
        private sealed record CachedUpload(string UserEmail, int ProfileId, byte[] PdfBytes, string FileName);

        /// <summary>Reload dropdown profile list + credit ViewBags when returning View(model) after a validation error.</summary>
        private async Task RepopulateFormViewBags(User user, InterpretationUploadViewModel model)
        {
            ViewBag.CreditRest = user.CreditRest;
            ViewBag.BonusCreditsRemaining = user.BonusCreditsRemaining;
            ViewBag.TotalAvailableCredits = user.TotalAvailableCredits;
            model.AvailableProfiles = await _db.Profiles
                .AsNoTracking()
                .Where(p => p.UserEmail == user.Email)
                .OrderByDescending(p => p.IsDefault)
                .ThenBy(p => p.Name)
                .Select(p => new InterpretationUploadViewModel.ProfileOption
                {
                    Id = p.Id, Name = p.Name, IsDefault = p.IsDefault
                })
                .ToListAsync();
        }

    }
}
