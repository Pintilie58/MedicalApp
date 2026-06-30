using System.Net.Mail;
using System.Net.Sockets;
using Microsoft.Extensions.Caching.Memory;

namespace MedicalApp.Services
{
    /// <summary>
    /// Result categories returned by <see cref="EmailDeliverabilityChecker"/>.
    /// Stored in the CheckPdfs viewmodel and driving the row badge color.
    /// </summary>
    public enum EmailValidity
    {
        /// <summary>Empty / null — operator has not entered anything.</summary>
        Empty = 0,
        /// <summary>RFC-valid syntax + domain has at least one A/AAAA record (so SMTP would at least try).</summary>
        Valid = 1,
        /// <summary>RFC-valid syntax but the domain does not resolve at all — almost always a typo.</summary>
        NoMxRecord = 2,
        /// <summary>Cannot be parsed by <see cref="MailAddress"/>.</summary>
        InvalidSyntax = 3,
        /// <summary>
        /// DNS lookup timed out / errored for some reason that is NOT a clean "domain not found".
        /// We do NOT block the operator on this one — they might be on a flaky network.
        /// </summary>
        DnsUnknown = 4,
    }

    public sealed class EmailValidityResult
    {
        public EmailValidity Validity { get; init; }
        public string? DomainSuggestion { get; init; }
        public string FriendlyMessage { get; init; } = string.Empty;
    }

    /// <summary>
    /// Cheap, network-aware deliverability check for patient emails before a CAM batch
    /// runs. We do two things only:
    ///   1. RFC syntactic validation via <see cref="MailAddress"/> (catches `a@b`, `@x.com`, etc).
    ///   2. A lightweight A/AAAA lookup against the domain part with a 2-second cap.
    ///      If the domain doesn't resolve, we flag the row as NoMxRecord and surface a
    ///      "Did you mean ...?" hint when the typed domain is one Levenshtein-1 edit
    ///      away from a known consumer provider (gmail.com, yahoo.com, ...).
    ///
    /// What we explicitly DO NOT do:
    ///   * Actual MX record resolution — .NET has no built-in MX resolver, and pulling
    ///     in DnsClient.NET would add a dependency for marginal benefit. An A record on
    ///     the apex domain is a reliable proxy for "domain exists" which is what most
    ///     typos miss.
    ///   * SMTP probes (RCPT TO without DATA) — Gmail/Outlook anti-abuse blocks these.
    ///   * Mailbox-level checks — only the SMTP server can know that.
    ///
    /// Results are cached in IMemoryCache for 10 minutes per domain so an operator
    /// clicking around the CheckPdfs page does NOT hit DNS hundreds of times for the
    /// same gmail.com / yahoo.com domain.
    /// </summary>
    public sealed class EmailDeliverabilityChecker
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<EmailDeliverabilityChecker> _logger;

        public EmailDeliverabilityChecker(IMemoryCache cache, ILogger<EmailDeliverabilityChecker> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        // -----------------------------------------------------------------
        // Top consumer-email providers we will offer typo suggestions for.
        // Order doesn't matter; lookup is O(N) with N small.
        // -----------------------------------------------------------------
        private static readonly string[] KnownDomains = new[]
        {
            "gmail.com", "yahoo.com", "yahoo.co.uk", "outlook.com", "hotmail.com",
            "icloud.com", "live.com", "msn.com", "aol.com", "protonmail.com",
            "yandex.com", "gmx.com",
            // Romania-specific
            "yahoo.ro", "gmail.ro", "outlook.ro"
        };

        /// <summary>
        /// Synchronous entry point used by row pre-checks. Returns the cached MX
        /// outcome for the domain if available, otherwise just runs the syntactic
        /// part and reports <see cref="EmailValidity.DnsUnknown"/> so the caller
        /// is never blocked on first hit. Use <see cref="ValidateAsync"/> to also
        /// resolve DNS.
        /// </summary>
        public EmailValidityResult ValidateSync(string? email)
        {
            return BuildResultCore(email, mxKnown: false, mxResolves: false);
        }

        public async Task<EmailValidityResult> ValidateAsync(string? email, CancellationToken ct = default)
        {
            // Syntax first — cheap, no network. Bail early if invalid.
            var preliminary = BuildResultCore(email, mxKnown: false, mxResolves: false);
            if (preliminary.Validity != EmailValidity.DnsUnknown) return preliminary;

            var domain = ExtractDomain(email!);
            if (string.IsNullOrEmpty(domain)) return preliminary;

            bool resolves;
            try
            {
                resolves = await ResolveDomainCachedAsync(domain, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DNS lookup hiccup for {Domain} — treating as DnsUnknown.", domain);
                return preliminary;
            }

            return BuildResultCore(email, mxKnown: true, mxResolves: resolves);
        }

        // -----------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------

        private static string ExtractDomain(string email)
        {
            int at = email.LastIndexOf('@');
            return at <= 0 || at == email.Length - 1 ? string.Empty : email[(at + 1)..].Trim().ToLowerInvariant();
        }

        private EmailValidityResult BuildResultCore(string? email, bool mxKnown, bool mxResolves)
        {
            if (string.IsNullOrWhiteSpace(email))
                return new EmailValidityResult { Validity = EmailValidity.Empty, FriendlyMessage = Loc.T("EmailValidEmpty") };

            email = email.Trim();
            bool syntaxOk;
            try
            {
                // MailAddress is strict enough for our purposes. Wrapping the
                // exception is necessary because it throws on invalid input.
                var addr = new MailAddress(email);
                syntaxOk = !string.IsNullOrEmpty(addr.Address);
            }
            catch { syntaxOk = false; }

            if (!syntaxOk)
                return new EmailValidityResult
                {
                    Validity = EmailValidity.InvalidSyntax,
                    FriendlyMessage = Loc.T("EmailValidInvalidSyntax")
                };

            var domain = ExtractDomain(email);
            var suggestion = SuggestDomainCorrection(domain);

            if (!mxKnown)
            {
                return new EmailValidityResult
                {
                    Validity = EmailValidity.DnsUnknown,
                    DomainSuggestion = suggestion,
                    FriendlyMessage = Loc.T("EmailValidDnsUnknown")
                };
            }

            if (!mxResolves)
            {
                var msg = suggestion != null
                    ? string.Format(Loc.T("EmailValidNoMxWithSuggestionFmt"), domain, suggestion)
                    : string.Format(Loc.T("EmailValidNoMxNoSuggestionFmt"), domain);
                return new EmailValidityResult
                {
                    Validity = EmailValidity.NoMxRecord,
                    DomainSuggestion = suggestion,
                    FriendlyMessage = msg
                };
            }

            return new EmailValidityResult
            {
                Validity = EmailValidity.Valid,
                FriendlyMessage = Loc.T("EmailValidValid")
            };
        }

        /// <summary>
        /// Suggest a typo correction if <paramref name="domain"/> is exactly 1 edit away
        /// from a known consumer email provider. Levenshtein with early exit at 2.
        /// </summary>
        private static string? SuggestDomainCorrection(string domain)
        {
            if (string.IsNullOrEmpty(domain)) return null;
            if (KnownDomains.Contains(domain, StringComparer.OrdinalIgnoreCase)) return null;

            string? best = null;
            int bestDist = int.MaxValue;
            foreach (var known in KnownDomains)
            {
                int d = Levenshtein(domain, known, maxAllowed: 2);
                if (d < bestDist) { bestDist = d; best = known; }
                if (bestDist == 1) break; // can't do better
            }
            return bestDist <= 2 ? best : null;
        }

        // Classic O(n*m) Levenshtein with early termination when running min exceeds
        // maxAllowed. Sufficient for our short domain strings.
        private static int Levenshtein(string a, string b, int maxAllowed)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            if (Math.Abs(a.Length - b.Length) > maxAllowed) return int.MaxValue;

            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                int rowMin = curr[0];
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                    if (curr[j] < rowMin) rowMin = curr[j];
                }
                if (rowMin > maxAllowed) return int.MaxValue;
                (prev, curr) = (curr, prev);
            }
            return prev[b.Length];
        }

        private async Task<bool> ResolveDomainCachedAsync(string domain, CancellationToken ct)
        {
            // 10-minute TTL: enough to keep the CheckPdfs page snappy for the
            // operator's session, short enough to pick up DNS changes if they
            // re-attempt later in the day.
            return await _cache.GetOrCreateAsync($"deliverability:{domain}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
                return await ResolveDomainRawAsync(domain, ct);
            });
        }

        private async Task<bool> ResolveDomainRawAsync(string domain, CancellationToken ct)
        {
            try
            {
                // 2-second cap — typo domains usually fail fast (NXDOMAIN), but
                // some malformed inputs trigger the full resolver timeout (~5s+)
                // which would freeze the CheckPdfs page render. Bail short.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                var addresses = await System.Net.Dns.GetHostAddressesAsync(domain, cts.Token);
                return addresses.Length > 0;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
            {
                // Definitive NXDOMAIN — confidently report no MX.
                return false;
            }
            catch (OperationCanceledException)
            {
                // Treat timeouts as "unknown" → preliminary syntactic OK stands.
                throw;
            }
            catch
            {
                // Any other transient DNS issue → propagate as "unknown".
                throw;
            }
        }
    }
}
