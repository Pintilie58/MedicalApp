using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// Probe for appsettings.Azure.json (June 2026).
// Builds the configuration EXACTLY like the app does (WebApplication.CreateBuilder:
// appsettings.json -> appsettings.{Environment}.json -> environment variables) and
// verifies the claim we make in Docs/AZURE_APP_SETTINGS.md: the Azure file is an
// OVERLAY, not a replacement, and portal settings win over both.

int fails = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail is null ? "" : "  ->  " + detail)}");
    if (!ok) fails++;
}

const string contentRoot = "/app/MedicalApp";

IConfiguration Build(string environment, Dictionary<string, string?>? portalSettings = null)
{
    foreach (var key in new[] { "Gemini__RateLimit__InstanceCount", "ScaleOut__Enabled", "CamSettings__Blob__AccountUrl" })
        Environment.SetEnvironmentVariable(key, null);

    if (portalSettings != null)
        foreach (var kv in portalSettings)
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        ContentRootPath = contentRoot,
        EnvironmentName = environment
    });
    return builder.Configuration;
}

// =====================================================================
//  1. Environment = Azure -> the overlay is picked up
// =====================================================================
var azure = Build("Azure");

Check("1. the Azure file is loaded (CamSettings:Storage switched to Blob)",
    azure["CamSettings:Storage"] == "Blob", azure["CamSettings:Storage"]);
Check("1b. the LOINC auto-start (uvicorn on Windows) is OFF on Azure",
    azure.GetValue<bool>("LoincAutoStart:Enabled") == false);
Check("1c. the CAM debug JSON attachment is OFF on Azure",
    azure.GetValue<bool>("CamBatch:AttachDebugJson") == false);
Check("1d. the LOINC timeout is raised for a network call",
    azure.GetValue<int>("LoincMatcher:TimeoutSeconds") == 10,
    azure["LoincMatcher:TimeoutSeconds"]);

// =====================================================================
//  2. It is an OVERLAY: everything not repeated survives from appsettings.json
// =====================================================================
Check("2. models / prompts / prices from appsettings.json are NOT lost",
    azure["Gemini:Model"] == "gemini-2.5-flash"
    && azure["Gemini:ExtractorModel"] == "gemini-2.5-flash"
    && azure.GetValue<int>("Gemini:MaxOutputTokens") == 65000,
    azure["Gemini:Model"]);
Check("2b. the admin list survives (array untouched by the overlay)",
    azure.GetSection("AdminSettings:Emails").GetChildren().Any(), "");
Check("2c. keys the overlay does not mention keep the base value",
    azure["CamSettings:FilesRoot"] == @"C:\MedicalApp_files"
    && azure["EmailSettings:SmtpServer"] == "smtp-relay.brevo.com",
    azure["CamSettings:FilesRoot"]);
Check("2d. secrets stay EMPTY in the files (they belong in the portal)",
    azure["Gemini:ApiKey"] == "" && azure["EmailSettings:Password"] == ""
    && azure.GetConnectionString("DefaultConnection") == "");
Check("2e. the Azure overlay keeps single-instance defaults until you scale out",
    azure.GetValue<bool>("ScaleOut:Enabled") == false
    && azure.GetValue<int>("Gemini:RateLimit:InstanceCount") == 1);

// =====================================================================
//  3. Portal Application settings win over both files
// =====================================================================
var withPortal = Build("Azure", new Dictionary<string, string?>
{
    ["Gemini__RateLimit__InstanceCount"] = "3",
    ["ScaleOut__Enabled"] = "true",
    ["CamSettings__Blob__AccountUrl"] = "https://acme.blob.core.windows.net"
});
Check("3. Gemini__RateLimit__InstanceCount from the portal overrides the file",
    withPortal.GetValue<int>("Gemini:RateLimit:InstanceCount") == 3,
    withPortal["Gemini:RateLimit:InstanceCount"]);
Check("3b. ScaleOut__Enabled from the portal turns multi-instance mode on",
    withPortal.GetValue<bool>("ScaleOut:Enabled"));
Check("3c. the Blob account URL arrives from the portal (no secret in Git)",
    withPortal["CamSettings:Blob:AccountUrl"] == "https://acme.blob.core.windows.net"
    && withPortal["CamSettings:Blob:ConnectionString"] == "");

// The quota split the app applies with 3 instances.
var rate = new { Rpm = withPortal.GetValue<int>("Gemini:RateLimit:RequestsPerMinute"),
                 Concurrent = withPortal.GetValue<int>("Gemini:RateLimit:MaxConcurrentCalls"),
                 Instances = withPortal.GetValue<int>("Gemini:RateLimit:InstanceCount") };
Check("3d. with 3 instances each one gets a third of the quota",
    rate.Rpm / rate.Instances == 20 && rate.Concurrent / rate.Instances == 2,
    $"{rate.Rpm}/{rate.Instances} rpm, {rate.Concurrent}/{rate.Instances} concurrent");

// =====================================================================
//  4. Local development is not affected at all
// =====================================================================
var dev = Build("Development");
Check("4. on Development the Azure file is ignored (LocalDisk storage kept)",
    dev["CamSettings:Storage"] == "LocalDisk", dev["CamSettings:Storage"]);
Check("4b. the Development conveniences are still on locally",
    dev.GetValue<bool>("LoincAutoStart:Enabled")
    && dev.GetValue<bool>("CamBatch:AttachDebugJson"));
Check("4c. the local SQL Express connection string is untouched",
    dev.GetConnectionString("DefaultConnection")!.Contains("SQLEXPRESS"));

// =====================================================================
//  5. The Logging section of the overlay is valid for the logging system
//     (a stray key there used to be the classic way to break startup)
// =====================================================================
try
{
    using var factory = LoggerFactory.Create(b => b.AddConfiguration(azure.GetSection("Logging")));
    factory.CreateLogger("MedicalApp.Test").LogInformation("probe");
    Check("5. the Logging section binds without throwing", true);
}
catch (Exception ex)
{
    Check("5. the Logging section binds without throwing", false, ex.Message);
}

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;
