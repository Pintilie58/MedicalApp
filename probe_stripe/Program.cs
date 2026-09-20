using System.Text;
using System.Collections.Concurrent;
using MedicalApp.Controllers;
using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Probe for Stripe Checkout (June 2026): real sandbox session creation, webhook
// signature + idempotent fulfilment, return-page paths, simulated provider untouched.

int fails = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail is null ? "" : "  ->  " + detail)}");
    if (!ok) fails++;
}

var secretKey = Environment.GetEnvironmentVariable("PROBE_STRIPE_SK") ?? "";
var webhookSecret = "whsec_probe_secret_123";
var dbName = "stripe-" + Guid.NewGuid();
AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options);

PaymentSettings Settings(string provider) => new()
{
    Provider = provider,
    Stripe = new PaymentSettings.StripeOptions { SecretKey = secretKey, WebhookSecret = webhookSecret }
};

CreditsController Ctl(AppDbContext db, string provider, string? sessionEmail, FakeEmail email, string? body = null, string? sig = null)
{
    var settings = Options.Create(Settings(provider));
    var stripe = new StripePaymentService(db, settings, NullLogger<StripePaymentService>.Instance);
    var http = new DefaultHttpContext { Session = new FakeSession(sessionEmail) };
    http.Request.Scheme = "https"; http.Request.Host = new HostString("localhost:7229");
    if (body != null)
    {
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (sig != null) http.Request.Headers["Stripe-Signature"] = sig;
    }
    var c = new CreditsController(db, email, Options.Create(new AdminSettings { Emails = new List<string> { "admin@x.ro" } }),
        new MemoryCamFileStore(), new PdfReportGenerator(), stripe, settings, NullLogger<CreditsController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = http },
        TempData = new TempDataDictionary(http, new FakeTempDataProvider()),
        Url = new FakeUrlHelper()
    };
    return c;
}

const string buyer = "buyer@example.com";
using (var db = NewDb())
{
    db.Users.Add(new User { Email = buyer, Parola = "x", UserType = "Individual", Credite = 0, CreditRest = 0 });
    await db.SaveChangesAsync();
}

// ------------------------------------------------------------ 1. StartStripe → real Checkout Session
string sessionId;
{
    using var db = NewDb();
    var c = Ctl(db, "Stripe", buyer, new FakeEmail());
    var r = await c.StartStripe("standard");
    var redirect = r as RedirectResult;
    Check("1a redirects to Stripe hosted page", redirect != null && redirect.Url.StartsWith("https://checkout.stripe.com/"), (r as RedirectResult)?.Url ?? r.GetType().Name);
    var tx = await db.PaymentTransactions.SingleAsync();
    sessionId = tx.SessionId;
    Check("1b PaymentTransaction created as 'initiated' for 11 EUR / standard", tx.Status == "initiated" && tx.AmountEur == 11m && tx.PackageKey == "standard" && tx.UserEmail == buyer && tx.SessionId.StartsWith("cs_test_"), $"{tx.Status} {tx.AmountEur} {tx.SessionId[..12]}");
    var u = await db.Users.AsNoTracking().SingleAsync();
    Check("1c NO credits granted before payment", u.Credite == 0 && !await db.Purchases.AnyAsync());
}

// ------------------------------------------------------------ 2. Return page before paying → pending, still 0 credits
{
    using var db = NewDb();
    var c = Ctl(db, "Stripe", buyer, new FakeEmail());
    var r = await c.StripeSuccess(sessionId) as RedirectToActionResult;
    Check("2a unpaid session → back to Buy with 'pending' message", r != null && r.ActionName == "Buy" && (c.TempData["ErrorMessage"] as string) == Loc.T("PaymentStripePending"), r?.ActionName);
    Check("2b still 0 credits", (await db.Users.AsNoTracking().SingleAsync()).Credite == 0);
}

// ------------------------------------------------------------ 3. Webhook: signed checkout.session.completed → fulfil once
string EventJson(string type, string sid, string paymentStatus) =>
    "{\"id\":\"evt_probe_" + Guid.NewGuid().ToString("N") + "\",\"object\":\"event\",\"api_version\":\"2025-01-27.acacia\",\"created\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() +
    ",\"type\":\"" + type + "\",\"livemode\":false,\"pending_webhooks\":1,\"request\":{\"id\":null,\"idempotency_key\":null}," +
    "\"data\":{\"object\":{\"id\":\"" + sid + "\",\"object\":\"checkout.session\",\"payment_status\":\"" + paymentStatus + "\",\"status\":\"complete\",\"payment_intent\":\"pi_probe_123\",\"mode\":\"payment\"}}}";
{
    using var db = NewDb();
    var email = new FakeEmail();
    var body = EventJson("checkout.session.completed", sessionId, "paid");
    var sig = Stripe.EventUtility.GenerateSignatureHeader(body, webhookSecret);
    var c = Ctl(db, "Stripe", null, email, body, sig);
    var r = await c.StripeWebhook();
    Check("3a webhook returns 200", r is OkResult, r.GetType().Name);
    var u = await db.Users.AsNoTracking().SingleAsync();
    var p = await db.Purchases.AsNoTracking().ToListAsync();
    var tx = await db.PaymentTransactions.AsNoTracking().SingleAsync();
    Check("3b 4 credits granted, TotalPaid 11", u.Credite == 4 && u.CreditRest == 4 && u.TotalPaid == 11m, $"credite={u.Credite} paid={u.TotalPaid}");
    Check("3c Purchase row: stripe + PaymentIntent reference", p.Count == 1 && p[0].PaymentMethod == "stripe" && p[0].ProviderReference == "pi_probe_123" && p[0].CreditsAdded == 4, p.Count > 0 ? p[0].PaymentMethod + "/" + p[0].ProviderReference : "none");
    Check("3d transaction paid with PaidAt + pi", tx.Status == "paid" && tx.PaidAt != null && tx.PaymentIntentId == "pi_probe_123");
    Check("3e admin notified by email", email.Sent.Any(m => m.to == "admin@x.ro"), string.Join(",", email.Sent.Select(m => m.to)));
}

// ------------------------------------------------------------ 4. Same webhook delivered again (Stripe retries) → idempotent
{
    using var db = NewDb();
    var body = EventJson("checkout.session.completed", sessionId, "paid");
    var c = Ctl(db, "Stripe", null, new FakeEmail(), body, Stripe.EventUtility.GenerateSignatureHeader(body, webhookSecret));
    var r = await c.StripeWebhook();
    var u = await db.Users.AsNoTracking().SingleAsync();
    Check("4a retry → 200, still exactly 4 credits and 1 purchase", r is OkResult && u.Credite == 4 && await db.Purchases.CountAsync() == 1, $"credite={u.Credite}");
}

// ------------------------------------------------------------ 5. Return page after webhook already fulfilled → success redirect, no double credit
{
    using var db = NewDb();
    var c = Ctl(db, "Stripe", buyer, new FakeEmail());
    var r = await c.StripeSuccess(sessionId) as RedirectToActionResult;
    var u = await db.Users.AsNoTracking().SingleAsync();
    Check("5a redirect to Account/Dashboard with success message", r != null && r.ActionName == "Dashboard" && r.ControllerName == "Account" && (c.TempData["SuccessMessage"] as string)!.Contains("4"), r?.ActionName);
    Check("5b no double credit", u.Credite == 4 && await db.Purchases.CountAsync() == 1);
}

// ------------------------------------------------------------ 6. Bad signature / foreign session / unpaid status
{
    using var db = NewDb();
    var body = EventJson("checkout.session.completed", sessionId, "paid");
    var c = Ctl(db, "Stripe", null, new FakeEmail(), body, "t=1,v1=deadbeef");
    Check("6a tampered signature → 400", await c.StripeWebhook() is BadRequestResult);

    var body2 = EventJson("checkout.session.completed", "cs_test_unknown_session", "paid");
    var c2 = Ctl(db, "Stripe", null, new FakeEmail(), body2, Stripe.EventUtility.GenerateSignatureHeader(body2, webhookSecret));
    Check("6b unknown session → 200 and nothing changes", await c2.StripeWebhook() is OkResult && await db.Purchases.CountAsync() == 1);

    db.Users.Add(new User { Email = "second@example.com", Parola = "x", UserType = "Individual" });
    db.PaymentTransactions.Add(new PaymentTransaction { SessionId = "cs_test_second", UserEmail = "second@example.com", PackageKey = "normal", AmountEur = 6m });
    await db.SaveChangesAsync();
    var body3 = EventJson("checkout.session.completed", "cs_test_second", "unpaid");
    var c3 = Ctl(db, "Stripe", null, new FakeEmail(), body3, Stripe.EventUtility.GenerateSignatureHeader(body3, webhookSecret));
    await c3.StripeWebhook();
    var second = await db.Users.AsNoTracking().SingleAsync(x => x.Email == "second@example.com");
    Check("6c completed but payment_status=unpaid (e.g. SEPA pending) → no credits yet", second.Credite == 0);
    var body4 = EventJson("checkout.session.expired", "cs_test_second", "unpaid");
    var c4 = Ctl(db, "Stripe", null, new FakeEmail(), body4, Stripe.EventUtility.GenerateSignatureHeader(body4, webhookSecret));
    await c4.StripeWebhook();
    Check("6d expired → transaction marked expired", (await db.PaymentTransactions.AsNoTracking().SingleAsync(t => t.SessionId == "cs_test_second")).Status == "expired");
}

// ------------------------------------------------------------ 7. Security: another user's return URL, unknown session, wrong package audience
{
    using var db = NewDb();
    var c = Ctl(db, "Stripe", "second@example.com", new FakeEmail());
    var r = await c.StripeSuccess(sessionId) as RedirectToActionResult;
    Check("7a someone else's session_id → Buy, no fulfilment", r != null && r.ActionName == "Buy" && (await db.Users.AsNoTracking().SingleAsync(x => x.Email == "second@example.com")).Credite == 0);
    var r2 = await c.StripeSuccess("cs_test_does_not_exist") as RedirectToActionResult;
    Check("7b unknown session → Buy", r2 != null && r2.ActionName == "Buy");
    var r3 = await c.StartStripe("cam_starter") as RedirectToActionResult;
    Check("7c Individual cannot start a Clinic package", r3 != null && r3.ActionName == "Buy" && await db.PaymentTransactions.CountAsync() == 2);
    var r4 = await c.StartStripe("nope") as RedirectToActionResult;
    Check("7d unknown package → Buy", r4 != null && r4.ActionName == "Buy");
    var c5 = Ctl(db, "Stripe", null, new FakeEmail());
    Check("7e not logged in → Home", (await c5.StartStripe("normal") as RedirectToActionResult)?.ControllerName == "Home");
}

// ------------------------------------------------------------ 8. Simulated provider still works exactly as before; Stripe mode refuses the old form
{
    using var db = NewDb();
    var c = Ctl(db, "Simulated", "second@example.com", new FakeEmail());
    var r = await c.Checkout(new CheckoutViewModel { PackageKey = "normal", CardNumber = "4242424242424242", CardHolder = "X", Expiry = "12/30", Cvv = "123" }) as RedirectToActionResult;
    var u = await db.Users.AsNoTracking().SingleAsync(x => x.Email == "second@example.com");
    var p = await db.Purchases.AsNoTracking().Where(x => x.UserEmail == "second@example.com").ToListAsync();
    Check("8a simulated purchase grants 2 credits, PaymentMethod=simulated", r?.ActionName == "Dashboard" && u.Credite == 2 && p.Count == 1 && p[0].PaymentMethod == "simulated" && p[0].ProviderReference == null, $"credite={u.Credite}");
    var cs = Ctl(db, "Stripe", "second@example.com", new FakeEmail());
    var rs = await cs.Checkout(new CheckoutViewModel { PackageKey = "normal", CardNumber = "4242424242424242", CardHolder = "X", Expiry = "12/30", Cvv = "123" }) as RedirectToActionResult;
    var u2 = await db.Users.AsNoTracking().SingleAsync(x => x.Email == "second@example.com");
    Check("8b in Stripe mode the old card form POST grants nothing", rs?.ActionName == "Checkout" && u2.Credite == 2);
    var g = await cs.Checkout("normal") as ViewResult;
    Check("8c GET Checkout flags UseStripe for the view", g != null && (bool)g.ViewData["UseStripe"]! == true);
    var wc = Ctl(db, "Simulated", null, new FakeEmail(), "{}", "x");
    Check("8d webhook disabled when provider is Simulated → 404", await wc.StripeWebhook() is NotFoundResult);
}

Console.WriteLine();
Console.WriteLine(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;

sealed class FakeUrlHelper : IUrlHelper
{
    public ActionContext ActionContext => new();
    public string? Action(UrlActionContext ctx) => "/Credits/" + ctx.Action + (ctx.Values is { } v && v.GetType().GetProperty("package")?.GetValue(v) is string p ? "?package=" + p : "");
    public string? Content(string? contentPath) => contentPath;
    public bool IsLocalUrl(string? url) => true;
    public string? Link(string? routeName, object? values) => null;
    public string? RouteUrl(UrlRouteContext routeContext) => null;
}

sealed class FakeEmail : IEmailService
{
    public List<(string to, string subject)> Sent { get; } = new();
    public Task SendEmailAsync(string toEmail, string subject, string htmlBody) { Sent.Add((toEmail, subject)); return Task.CompletedTask; }
    public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string htmlBody, byte[] attachment, string fileName) { Sent.Add((toEmail, subject)); return Task.CompletedTask; }
    public Task SendEmailWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IEnumerable<(byte[] Bytes, string FileName, string MimeType)> attachments) { Sent.Add((toEmail, subject)); return Task.CompletedTask; }
}

sealed class MemoryCamFileStore : ICamFileStore
{
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();           // Original
    readonly ConcurrentDictionary<CamFolder, ConcurrentDictionary<string, byte[]>> _other = new();

    ConcurrentDictionary<string, byte[]> Of(CamFolder f) => f == CamFolder.Original ? Files : _other.GetOrAdd(f, _ => new());
    public List<string> Folder(CamFolder f) => Of(f).Keys.OrderBy(k => k).ToList();

    public string GetDisplayLocation(Clinic clinic, CamFolder? folder = null) => "memory";
    public Task<string> EnsureClinicFoldersAsync(Clinic clinic, CancellationToken ct = default) => Task.FromResult("memory");
    public Task<IReadOnlyList<CamFileEntry>> ListAsync(Clinic clinic, CamFolder folder, string? extension = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CamFileEntry>>(Of(folder).Select(kv => new CamFileEntry(kv.Key, kv.Value.Length, DateTime.UtcNow)).ToList());
    public Task<bool> ExistsAsync(Clinic clinic, CamFolder folder, string name, CancellationToken ct = default) => Task.FromResult(Of(folder).ContainsKey(name));
    public Task<byte[]?> ReadAsync(Clinic clinic, CamFolder folder, string name, CancellationToken ct = default)
        => Task.FromResult(Of(folder).TryGetValue(name, out var b) ? b : null);
    public Task<string> WriteAsync(Clinic clinic, CamFolder folder, string name, byte[] content, bool overwrite = false, CancellationToken ct = default)
    { Of(folder)[name] = content; return Task.FromResult(name); }
    public Task<string?> MoveAsync(Clinic clinic, CamFolder from, CamFolder to, string name, CancellationToken ct = default)
    {
        if (!Of(from).TryRemove(name, out var b)) return Task.FromResult<string?>(null);
        Of(to)[name] = b; return Task.FromResult<string?>(name);
    }
    public Task<bool> DeleteAsync(Clinic clinic, CamFolder folder, string name, CancellationToken ct = default) => Task.FromResult(Of(folder).TryRemove(name, out _));
}

sealed class FakeSession : ISession
{
    private readonly Dictionary<string, byte[]> _store = new();
    public FakeSession(string? email)
    {
        if (!string.IsNullOrEmpty(email)) _store["UserEmail"] = System.Text.Encoding.UTF8.GetBytes(email);
    }
    public bool IsAvailable => true;
    public string Id => "probe";
    public IEnumerable<string> Keys => _store.Keys;
    public void Clear() => _store.Clear();
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Remove(string key) => _store.Remove(key);
    public void Set(string key, byte[] value) => _store[key] = value;
    public bool TryGetValue(string key, out byte[] value) => _store.TryGetValue(key, out value!);
}

sealed class FakeTempDataProvider : ITempDataProvider
{
    public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
    public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
}
