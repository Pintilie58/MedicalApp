using System.Reflection;

// Verifies that EVERY constructor dependency of EVERY controller and hosted
// service is registered in the DI container — the check that would have caught
// the missing EvolutionPdfGenerator registration before the user saw it.

var asm = typeof(MedicalApp.Controllers.ProfilesController).Assembly;
var programText = File.ReadAllText("/app/MedicalApp/Program.cs");

// Types the framework provides or that are configuration wrappers.
bool IsFrameworkProvided(Type t) =>
    t.Namespace != null && !t.Namespace.StartsWith("MedicalApp")
    || t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("IOptions");

bool IsRegistered(Type t)
{
    var name = t.Name;
    return programText.Contains($"<{name}>")
        || programText.Contains($", {name}>")
        || programText.Contains($"<{name},")
        || programText.Contains($"AddScoped<{name}")
        || programText.Contains($"typeof({name})");
}

int problems = 0;
var controllers = asm.GetTypes()
    .Where(t => t.IsClass && !t.IsAbstract && typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
    .OrderBy(t => t.Name);

foreach (var c in controllers)
{
    var ctor = c.GetConstructors().OrderByDescending(x => x.GetParameters().Length).FirstOrDefault();
    if (ctor == null) continue;

    foreach (var p in ctor.GetParameters())
    {
        var t = p.ParameterType;
        if (IsFrameworkProvided(t)) continue;
        if (IsRegistered(t)) continue;

        Console.WriteLine($"FAIL  {c.Name} needs {t.Name} — NOT registered in Program.cs");
        problems++;
    }
}

Console.WriteLine(problems == 0
    ? $"PASS  every MedicalApp dependency of all {controllers.Count()} controllers is registered in Program.cs"
    : $"{problems} MISSING REGISTRATION(S)");
return problems == 0 ? 0 : 1;
