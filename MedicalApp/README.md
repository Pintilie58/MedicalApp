# MedicalApp — ASP.NET Core 9 MVC + SQL Server

Aplicație web cu:
- Ecran **Home** cu selector de limbă (EN / RO / FR / ES / DE)
- Imagine Welcome + mesaj localizat ("Welcome in this world")
- **Înregistrare** și **Login** cu parole criptate BCrypt
- Bază de date **Microsoft SQL Server** via Entity Framework Core 9
- Structura tabelei `Users`: **Email (PK)**, **Parola**, **Credite**, **DataC**, **CreditConsum**, **CreditRest**

---

## 1. Deschiderea proiectului în Visual Studio 2022

1. Deschide `MedicalApp.sln` în VS2022 (minim versiunea cu suport .NET 9 SDK — VS 17.12+).
2. La prima deschidere, VS va restaura automat pachetele NuGet. Dacă nu o face: click-dreapta pe soluție → **Restore NuGet Packages**.

---

## 2. Configurarea conexiunii la SQL Server

Deschide fișierul `appsettings.json` și modifică connection string-ul după serverul tău:

```json
"ConnectionStrings": {
  "DefaultConnection": "Server=localhost;Database=MedicalAppDB;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
}
```

Exemple:
- **SQL Server local (Windows Authentication)**: `Server=localhost;Database=MedicalAppDB;Trusted_Connection=True;TrustServerCertificate=True`
- **SQL Server Express**: `Server=localhost\SQLEXPRESS;Database=MedicalAppDB;Trusted_Connection=True;TrustServerCertificate=True`
- **SQL Server cu user/parolă**: `Server=localhost;Database=MedicalAppDB;User Id=sa;Password=YourPass;TrustServerCertificate=True`

---

## 3. Crearea bazei de date prin Migrations

În VS2022 deschide **Package Manager Console** (`Tools → NuGet Package Manager → Package Manager Console`) și rulează:

```powershell
Add-Migration InitialCreate
Update-Database
```

Asta va:
1. Crea migrarea inițială (folderul `Migrations/`)
2. Crea baza de date `MedicalAppDB` și tabelul `Users` cu schema:

| Coloană       | Tip          | Cheie |
|---------------|--------------|-------|
| Email         | nvarchar(200)| PK    |
| Parola        | nvarchar(255)|       |
| Credite       | int          |       |
| DataC         | datetime2    |       |
| CreditConsum  | int          |       |
| CreditRest    | int          |       |

> Alternativ din CLI: `dotnet ef migrations add InitialCreate` apoi `dotnet ef database update`.

---

## 4. Adăugarea imaginii de welcome

Pune imaginea ta cu numele **`welcome.jpg`** în folderul:
```
wwwroot/images/welcome.jpg
```

Dacă nu adaugi imagine, pagina va afișa automat un placeholder online.

---

## 5. Rularea aplicației

- Apasă **F5** (cu debugger) sau **Ctrl+F5** (fără debugger) în VS2022.
- Browserul se va deschide la `https://localhost:5001` (sau `http://localhost:5000`).

---

## 6. Structura proiectului

```
MedicalApp/
├── Controllers/
│   ├── HomeController.cs        # Index + schimbare limbă
│   └── AccountController.cs     # Login, Register, Logout, Dashboard
├── Data/
│   └── AppDbContext.cs          # EF Core context
├── Models/
│   ├── User.cs                  # Entitate DB
│   └── AuthViewModels.cs        # LoginViewModel, RegisterViewModel
├── Services/
│   └── Loc.cs                   # Helper de localizare (5 limbi)
├── Views/
│   ├── Home/Index.cshtml        # Ecranul Home cu limbă + auth
│   ├── Account/Dashboard.cshtml # După login
│   └── Shared/_Layout.cshtml
├── wwwroot/
│   ├── css/site.css
│   └── images/welcome.jpg       # pui tu imaginea aici
├── appsettings.json             # connection string
├── Program.cs                   # configurare servicii + pipeline
└── MedicalApp.csproj
```

---

## 7. Note de securitate

- Parolele sunt hash-uite cu **BCrypt.Net-Next** (cost factor default = 11).
- Sesiunea este păstrată server-side cu `IdleTimeout` de 60 minute.
- Toate formularele POST au `ValidateAntiForgeryToken` (protecție CSRF).

---

## 8. Pașii următori (pentru iterațiile viitoare)

- Adăugare `[Authorize]` custom attribute
- Ecran de modificare a profilului
- Logică de credite (acordare/consum)
- Password reset prin email
- Integrare API-uri medicale

Spor la treabă! 🚀
