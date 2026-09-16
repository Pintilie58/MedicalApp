using MedicalApp.Areas.CAM.Controllers;
using MedicalApp.Areas.CAM.Models;
using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO.Compression;

// Probe: /CAM/Files controller over LocalDiskCamFileStore + InMemory DB.
int failed = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail != null ? "  ->  " + detail : "")}");
    if (!ok) failed++;
}

var root = Path.Combine(Path.GetTempPath(), "cam_files_probe_" + Guid.NewGuid().ToString("N"));
var store = new LocalDiskCamFileStore(
    Options.Create(new CamSettings { FilesRoot = root, Storage = "LocalDisk" }),
    NullLogger<LocalDiskCamFileStore>.Instance);

var opts = new DbContextOptionsBuilder<AppDbContext>()
    .UseInMemoryDatabase("files_probe_" + Guid.NewGuid()).Options;
var db = new AppDbContext(opts);

var clinicA = new Clinic { UserEmail = "clinica.a@test.ro", Name = "Clinica A", City = "X", Address = "Y", FoldersCreatedAt = DateTime.UtcNow };
var clinicB = new Clinic { UserEmail = "clinica.b@test.ro", Name = "Clinica B", City = "X", Address = "Y", FoldersCreatedAt = DateTime.UtcNow };
db.Clinics.AddRange(clinicA, clinicB);
await db.SaveChangesAsync();

await store.EnsureClinicFoldersAsync(clinicA);
await store.EnsureClinicFoldersAsync(clinicB);
byte[] Pdf(string tag) => System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 " + tag + new string('x', 500));

await store.WriteAsync(clinicA, CamFolder.Original, "a1.pdf", Pdf("a1"));
await store.WriteAsync(clinicA, CamFolder.Original, "a2.pdf", Pdf("a2"));
await store.WriteAsync(clinicA, CamFolder.Sends, "sent1.pdf", Pdf("s1"));
await store.WriteAsync(clinicA, CamFolder.Sends, "sent2.pdf", Pdf("s2"));
await store.WriteAsync(clinicA, CamFolder.Sends, "sent3.pdf", Pdf("s3"));
await store.WriteAsync(clinicA, CamFolder.Sumar, "sumar_1.txt", System.Text.Encoding.UTF8.GetBytes("sumar"));
await store.WriteAsync(clinicA, CamFolder.Errors, "20260614_101010_bad.pdf", Pdf("bad"));
await store.WriteAsync(clinicA, CamFolder.Errors, "20260614_101010_bad.pdf.reasons.txt",
    System.Text.Encoding.UTF8.GetBytes("Header\n  • Email lipsă (din fișier)"));
await store.WriteAsync(clinicA, CamFolder.Errors, "orphan.pdf", Pdf("orphan"));
await store.WriteAsync(clinicA, CamFolder.Errors, "orphan.pdf.reasons.txt",
    System.Text.Encoding.UTF8.GetBytes("Header\n  • Motiv din txt"));
await store.WriteAsync(clinicB, CamFolder.Original, "b1.pdf", Pdf("b1"));

db.ClinicPdfOverrides.Add(new ClinicPdfOverride { ClinicId = clinicA.Id, FileName = "a1.pdf", OverrideName = "P", OverrideEmail = "p@x.ro" });
var run = new ClinicBatchRun { ClinicId = clinicA.Id, Status = "Completed", StartedAt = DateTime.UtcNow, TotalFiles = 1 };
db.ClinicBatchRuns.Add(run);
await db.SaveChangesAsync();
db.ClinicBatchErrors.Add(new ClinicBatchError { BatchRunId = run.Id, FileName = "bad.pdf", Reason = "Email lipsă (din DB)", OccurredAt = DateTime.UtcNow });
await db.SaveChangesAsync();

FilesController Ctl(string email, MemoryStream? body = null)
{
    var http = new DefaultHttpContext { Session = new FakeSession(email) };
    if (body != null) http.Response.Body = body;
    var workbench = new CamCheckPdfsBuilder(db, store,
        new CamPdfMetadataExtractor(NullLogger<CamPdfMetadataExtractor>.Instance),
        new EmailDeliverabilityChecker(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            NullLogger<EmailDeliverabilityChecker>.Instance),
        NullLogger<CamCheckPdfsBuilder>.Instance);
    var c = new FilesController(db, store, workbench, NullLogger<FilesController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = http },
        TempData = new TempDataDictionary(http, new FakeTempDataProvider())
    };
    return c;
}

// ---- 1. Index per folder ----
Console.WriteLine("=== 1. Index ===");
var vmO = (await Ctl("clinica.a@test.ro").Index("Original") as ViewResult)!.Model as CamFilesViewModel;
Check("1a Original lists 2 files", vmO!.Items.Count == 2 && vmO.Folder == CamFolder.Original);
Check("1b counts: Original=2 Sends=3 Sumar=1 Errors=2 (.reasons.txt hidden)",
    vmO.Counts[CamFolder.Original] == 2 && vmO.Counts[CamFolder.Sends] == 3 && vmO.Counts[CamFolder.Sumar] == 1 && vmO.Counts[CamFolder.Errors] == 2,
    string.Join(",", vmO.Counts.Select(k => $"{k.Key}={k.Value}")));
Check("1c CanUpload/CanDelete on Original, no restore", vmO.CanUpload && vmO.CanDelete && !vmO.CanRestore);
Check("1c2 workbench built for Original with 2 rows", vmO.Workbench != null && vmO.Workbench.Items.Count == 2);
var a1 = vmO.Workbench!.Items.First(r => r.FileName == "a1.pdf");
var a2 = vmO.Workbench.Items.First(r => r.FileName == "a2.pdf");
Check("1c3 a1 valid via manual override", a1.IsValid && a1.IsManualOverride && a1.PatientEmail == "p@x.ro");
Check("1c4 a2 (fake pdf) blocked", !a2.IsValid);
var legacy = new CheckPdfsController(db, store, NullLogger<CheckPdfsController>.Instance)
{
    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { Session = new FakeSession("clinica.a@test.ro") } },
    TempData = new TempDataDictionary(new DefaultHttpContext(), new FakeTempDataProvider())
};
var lg = legacy.Index() as RedirectToActionResult;
Check("1c5 old /CAM/CheckPdfs redirects to Files/Original", lg != null && lg.ControllerName == "Files" && Equals(lg.RouteValues!["folder"], "Original"));
await legacy.SaveOverride("a2.pdf", "Ion", "ion@gmail.com");
var lg2 = await legacy.ClearOverride("a2.pdf") as RedirectToActionResult;
Check("1c6 POST actions redirect back to Files/Original", lg2 != null && lg2.ControllerName == "Files");

var vmS = (await Ctl("clinica.a@test.ro").Index("sends") as ViewResult)!.Model as CamFilesViewModel;
Check("1d 'sends' (lowercase) parses, 3 rows, read-only", vmS!.Items.Count == 3 && !vmS.CanUpload && !vmS.CanDelete);
Check("1d2 no workbench outside Original", vmS.Workbench == null);

var vmE = (await Ctl("clinica.a@test.ro").Index("Errors") as ViewResult)!.Model as CamFilesViewModel;
var bad = vmE!.Items.First(r => r.FileName.EndsWith("bad.pdf"));
var orphan = vmE.Items.First(r => r.FileName == "orphan.pdf");
Check("1e Errors: 2 rows, .reasons.txt not listed", vmE.Items.Count == 2 && vmE.Items.All(r => !r.FileName.EndsWith(".reasons.txt")));
Check("1f reason from DB matched on stamped name", bad.Reason == "Email lipsă (din DB)", bad.Reason);
Check("1g reason fallback from .reasons.txt", orphan.Reason == "Motiv din txt", orphan.Reason);
Check("1h Errors: CanRestore + CanDelete, no upload", vmE.CanRestore && vmE.CanDelete && !vmE.CanUpload);

var vmBad = (await Ctl("clinica.a@test.ro").Index("nope") as ViewResult)!.Model as CamFilesViewModel;
Check("1i unknown folder falls back to Original", vmBad!.Folder == CamFolder.Original);

var vmB = (await Ctl("clinica.b@test.ro").Index("Original") as ViewResult)!.Model as CamFilesViewModel;
Check("1j clinic B sees only its own file", vmB!.Items.Count == 1 && vmB.Items[0].FileName == "b1.pdf");

var anon = await Ctl("").Index("Original");
Check("1k anonymous -> redirect Home", anon is RedirectToActionResult r0 && r0.ControllerName == "Home");

// ---- 2. Download ----
Console.WriteLine("=== 2. Download ===");
var dl = await Ctl("clinica.a@test.ro").Download("Sends", "sent1.pdf") as FileContentResult;
Check("2a pdf bytes + content type + name", dl != null && dl.ContentType == "application/pdf" && dl.FileDownloadName == "sent1.pdf" && dl.FileContents.SequenceEqual(Pdf("s1")));
var dlTxt = await Ctl("clinica.a@test.ro").Download("Sumar", "sumar_1.txt") as FileContentResult;
Check("2b txt content type", dlTxt != null && dlTxt.ContentType.StartsWith("text/plain"));
var dlB = await Ctl("clinica.b@test.ro").Download("Sends", "sent1.pdf");
Check("2c clinic B cannot download A's file (redirect, not file)", dlB is RedirectToActionResult);
var trav = await Ctl("clinica.a@test.ro").Download("Original", "../Sends/sent1.pdf");
Check("2d path traversal rejected (400)", trav is BadRequestResult);
var missing = await Ctl("clinica.a@test.ro").Download("Original", "ghost.pdf");
Check("2e missing file -> redirect with error", missing is RedirectToActionResult);

// ---- 3. ZIP ----
Console.WriteLine("=== 3. DownloadZip ===");
var zipBody = new MemoryStream();
var zipCtl = Ctl("clinica.a@test.ro", zipBody);
var zr = await zipCtl.DownloadZip("Sends");
zipBody.Position = 0;
using (var za = new ZipArchive(zipBody, ZipArchiveMode.Read))
{
    var names = za.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
    Check("3a ZIP has the 3 Sends files", names.SequenceEqual(new[] { "sent1.pdf", "sent2.pdf", "sent3.pdf" }), string.Join(",", names));
    using var es = za.GetEntry("sent2.pdf")!.Open();
    using var ms = new MemoryStream(); es.CopyTo(ms);
    Check("3b entry content intact", ms.ToArray().SequenceEqual(Pdf("s2")));
}
Console.WriteLine("   ct=" + zipCtl.Response.ContentType + " cd=" + zipCtl.Response.Headers.ContentDisposition + " r=" + zr.GetType().Name);
Check("3c zip content type + attachment header", zipCtl.Response.ContentType == "application/zip"
    && zipCtl.Response.Headers.ContentDisposition.ToString().Contains("Clinica_A_Sends") && zr is EmptyResult);
var emptyZip = await Ctl("clinica.b@test.ro", new MemoryStream()).DownloadZip("Errors");
Check("3d empty folder -> redirect (no empty zip)", emptyZip is RedirectToActionResult);

// ---- 4. Upload ----
Console.WriteLine("=== 4. Upload ===");
IFormFile Form(string name, byte[] bytes)
{
    var ms = new MemoryStream(bytes);
    return new FormFile(ms, 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = "application/pdf" };
}
var up1 = await Ctl("clinica.a@test.ro").Upload(Form("Popescu Ion.pdf", Pdf("new"))) as JsonResult;
var up1Name = (string)up1!.Value!.GetType().GetProperty("name")!.GetValue(up1.Value)!;
Check("4a upload ok, stored under its name", up1Name == "Popescu Ion.pdf" && await store.ExistsAsync(clinicA, CamFolder.Original, "Popescu Ion.pdf"));
var up2 = await Ctl("clinica.a@test.ro").Upload(Form("Popescu Ion.pdf", Pdf("dup"))) as JsonResult;
var up2Name = (string)up2!.Value!.GetType().GetProperty("name")!.GetValue(up2.Value)!;
Check("4b duplicate name -> stamped, original untouched", up2Name != "Popescu Ion.pdf" && up2Name.EndsWith("Popescu Ion.pdf")
    && (await store.ReadAsync(clinicA, CamFolder.Original, "Popescu Ion.pdf"))!.SequenceEqual(Pdf("new")), up2Name);
var up3 = await Ctl("clinica.a@test.ro").Upload(Form("notes.docx", Pdf("x")));
Check("4c non-pdf rejected 400", up3 is BadRequestObjectResult);
var up4 = await Ctl("clinica.a@test.ro").Upload(Form("../../evil.pdf", Pdf("e")))  as JsonResult;
var up4Name = (string)up4!.Value!.GetType().GetProperty("name")!.GetValue(up4.Value)!;
Check("4d path components stripped", up4Name == "evil.pdf" && await store.ExistsAsync(clinicA, CamFolder.Original, "evil.pdf"), up4Name);
var up5 = await Ctl("clinica.a@test.ro").Upload(null);
Check("4e empty -> 400", up5 is BadRequestObjectResult);
var up6 = await Ctl("").Upload(Form("x.pdf", Pdf("x")));
Check("4f anonymous -> 401", up6 is UnauthorizedResult);
Check("4g Original now has 5 files", (await store.ListAsync(clinicA, CamFolder.Original)).Count == 5);

// ---- 5. Delete ----
Console.WriteLine("=== 5. Delete ===");
await Ctl("clinica.a@test.ro").Delete("Original", "a1.pdf");
Check("5a deleted from Original", !await store.ExistsAsync(clinicA, CamFolder.Original, "a1.pdf"));
Check("5b override row removed with it", !await db.ClinicPdfOverrides.AnyAsync(o => o.FileName == "a1.pdf"));
var fdel = await Ctl("clinica.a@test.ro").Delete("Sends", "sent1.pdf");
Check("5c Sends is read-only (403), file kept", fdel is ForbidResult && await store.ExistsAsync(clinicA, CamFolder.Sends, "sent1.pdf"));
await Ctl("clinica.a@test.ro").Delete("Errors", "orphan.pdf");
Check("5d Errors delete removes file + .reasons.txt", !await store.ExistsAsync(clinicA, CamFolder.Errors, "orphan.pdf")
    && !await store.ExistsAsync(clinicA, CamFolder.Errors, "orphan.pdf.reasons.txt"));
await Ctl("clinica.b@test.ro").Delete("Original", "a2.pdf");
Check("5e clinic B cannot delete A's file", await store.ExistsAsync(clinicA, CamFolder.Original, "a2.pdf"));
await Ctl("clinica.a@test.ro").Delete("Original", "..\\a2.pdf");
Check("5f traversal name ignored", await store.ExistsAsync(clinicA, CamFolder.Original, "a2.pdf"));

// ---- 6. Restore ----
Console.WriteLine("=== 6. Restore ===");
var rs = await Ctl("clinica.a@test.ro").Restore("20260614_101010_bad.pdf") as RedirectToActionResult;
Check("6a moved Errors -> Original", await store.ExistsAsync(clinicA, CamFolder.Original, "20260614_101010_bad.pdf")
    && !await store.ExistsAsync(clinicA, CamFolder.Errors, "20260614_101010_bad.pdf"));
Check("6b .reasons.txt cleaned up", !await store.ExistsAsync(clinicA, CamFolder.Errors, "20260614_101010_bad.pdf.reasons.txt"));
Check("6c redirects to Original tab", rs != null && Equals(rs.RouteValues!["folder"], CamFolder.Original));
Check("6d Errors now empty", (await store.ListAsync(clinicA, CamFolder.Errors)).Count == 0);
var rs2 = await Ctl("clinica.a@test.ro").Restore("ghost.pdf");
Check("6e missing -> redirect Errors with message", rs2 is RedirectToActionResult r2 && Equals(r2.RouteValues!["folder"], CamFolder.Errors));

// ---- 7. Regression: CheckPdfs still lists the same Original files ----
Console.WriteLine("=== 7. Regression ===");
var orig = await store.ListAsync(clinicA, CamFolder.Original, ".pdf");
Check("7a Original = a2 + Popescu + stamped dup + evil + restored bad (5)", orig.Count == 5, string.Join(",", orig.Select(o => o.Name)));

try { Directory.Delete(root, true); } catch { }
Console.WriteLine();
Console.WriteLine(failed == 0 ? "ALL CHECKS PASSED" : $"{failed} CHECK(S) FAILED");
return failed == 0 ? 0 : 1;

sealed class FakeTempDataProvider : ITempDataProvider
{
    public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
    public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
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
