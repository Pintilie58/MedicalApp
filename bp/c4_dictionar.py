from .helpers import add_heading, add_para, add_bullets, add_table, page_break


def build(doc):
    # ---------------- C.4.11 ----------------
    add_heading(doc, "C.4.11. Dicționarul de date (data dictionary)", 2)
    add_para(doc,
             "Pentru rigoare tehnică, se prezintă dicționarul de date al "
             "entităților principale proiectate, cu tipurile și rolul "
             "fiecărui atribut esențial. Schema completă va fi menținută "
             "prin migrări versionate (Code-First).")

    add_para(doc, "Entitatea Utilizator (Users):", bold=True)
    add_table(doc,
              headers=["Atribut", "Tip", "Descriere"],
              rows=[
                  ["Email (PK)", "nvarchar(256)", "Identificator unic; verificat prin e-mail"],
                  ["PasswordHash", "nvarchar(512)", "Hash Argon2id al parolei"],
                  ["EmailVerifiedAt", "datetime2", "Momentul confirmării adresei"],
                  ["CreditsAvailable", "int", "Credite de procesare disponibile"],
                  ["CreditsUsed", "int", "Credite consumate (istoric)"],
                  ["BonusCredits", "int", "Credite din programul de recomandare"],
                  ["PreferredLanguage", "nvarchar(8)", "Limba interfeței și a interpretărilor"],
                  ["PremiumUntil", "datetime2", "Valabilitatea abonamentului premium"],
                  ["CreatedAt / DeletedAt", "datetime2", "Audit + soft-delete GDPR (30 zile)"],
              ],
              col_widths_cm=[4.2, 3.2, 8.4], font_size=9)

    add_para(doc, "Entitatea Profil pacient (Profiles):", bold=True)
    add_table(doc,
              headers=["Atribut", "Tip", "Descriere"],
              rows=[
                  ["Id (PK)", "int identity", "Identificator profil"],
                  ["UserEmail (FK)", "nvarchar(256)", "Contul proprietar"],
                  ["DisplayName", "nvarchar(100)", "Numele afișat al profilului"],
                  ["BirthYear", "int", "Anul nașterii (intervale de referință)"],
                  ["Sex", "char(1)", "M/F — necesar intervalelelor diferențiate"],
                  ["IsDefault", "bit", "Profilul implicit al contului"],
              ],
              col_widths_cm=[4.2, 3.2, 8.4], font_size=9)

    add_para(doc, "Entitatea Interpretare (InterpretationHistories):", bold=True)
    add_table(doc,
              headers=["Atribut", "Tip", "Descriere"],
              rows=[
                  ["Id (PK)", "int identity", "Identificator interpretare"],
                  ["ProfileId (FK)", "int", "Profilul pacientului"],
                  ["PdfSha256", "char(64)", "Amprenta documentului (deduplicare)"],
                  ["RawJsonResult", "nvarchar(max)", "Rezultatul complet structurat (JSON)"],
                  ["ModelUsed", "nvarchar(64)", "Modelul AI folosit (economic/performant)"],
                  ["InputTokens / OutputTokens", "int", "Consum AI pentru controlul costului"],
                  ["Language", "nvarchar(8)", "Limba interpretării generate"],
                  ["CreatedAt", "datetime2", "Momentul procesării"],
              ],
              col_widths_cm=[4.2, 3.2, 8.4], font_size=9)

    add_para(doc, "Entitatea Rezultat-cheie (KeyResults, denormalizată):", bold=True)
    add_table(doc,
              headers=["Atribut", "Tip", "Descriere"],
              rows=[
                  ["Id (PK)", "bigint identity", "Identificator rând"],
                  ["InterpretationId (FK)", "int", "Interpretarea sursă"],
                  ["ParameterRaw", "nvarchar(256)", "Denumirea brută din buletin"],
                  ["ParameterNormalizedEn", "nvarchar(256)", "Denumirea canonică (terminologie LOINC)"],
                  ["PanelHeaderRaw", "nvarchar(512)", "Antetul ierarhic de panel (context sursă)"],
                  ["AnalyteLineRaw", "nvarchar(1024)", "Linia brută integrală a analitului"],
                  ["Value / Unit", "nvarchar", "Valoarea măsurată și unitatea"],
                  ["ReferenceRange", "nvarchar(128)", "Intervalul de referință raportat"],
                  ["Status", "nvarchar(16)", "Normal/High/Low/Borderline (determinist)"],
                  ["LoincCode", "nvarchar(16)", "Codul LOINC atribuit"],
                  ["LoincConfidence / LoincSource", "float / nvarchar", "Scorul și stratul care a decis (ancoră/semantic/fuzzy/reguli)"],
              ],
              col_widths_cm=[4.6, 3.2, 8.0], font_size=9)

    add_para(doc, "Entități B2B și de audit:", bold=True)
    add_table(doc,
              headers=["Entitate", "Atribute esențiale", "Rol"],
              rows=[
                  ["Clinics",
                   "Id, Name, Subdomain, Tier, CustomTariff, ActiveModules, "
                   "BrandingConfig",
                   "Configurația fiecărui tenant B2B"],
                  ["ClinicUsers",
                   "ClinicId, Email, Role (Admin/Operator)",
                   "Conturile organizaționale cu roluri"],
                  ["ClinicBatchRuns",
                   "Id, ClinicId, DocumentCount, Status, StartedAt, "
                   "CompletedAt, SummaryJson",
                   "Fiecare lot de procesare multi-pacient"],
                  ["AiUsageLogs",
                   "Id, InterpretationId, Model, InputTokens, OutputTokens, "
                   "LatencyMs, Status",
                   "Jurnalul fiecărui apel AI (cost și audit)"],
                  ["AuditLogs",
                   "Id, ActorEmail, Action, EntityRef, IpAddress, CreatedAt",
                   "Acțiunile sensibile; retenție 7 ani"],
                  ["Payments / Invoices",
                   "Id, UserEmail/ClinicId, Amount, Currency, Provider, "
                   "Status, InvoiceNumber",
                   "Tranzacțiile și facturarea automată"],
              ],
              col_widths_cm=[3.4, 7.4, 5.0], font_size=9)

    # ---------------- C.4.12 ----------------
    add_heading(doc, "C.4.12. Catalogul serviciilor API", 2)
    add_para(doc,
             "Principalele grupuri de servicii API care vor fi dezvoltate "
             "și documentate prin specificații OpenAPI:")
    add_table(doc,
              headers=["Grup / Endpoint reprezentativ", "Metodă", "Rol"],
              rows=[
                  ["/api/auth/register · /login · /verify-email · "
                   "/forgot-password", "POST",
                   "Fluxurile de înregistrare și autentificare (F3.2–F3.3)"],
                  ["/api/profiles", "GET/POST/PUT/DELETE",
                   "Gestiunea profilurilor de pacient (F3.4)"],
                  ["/api/interpretations/upload", "POST (multipart)",
                   "Încărcarea documentului și declanșarea procesării (F3.5)"],
                  ["/api/interpretations/{id} · /history", "GET",
                   "Raportul interpretat și istoricul (F3.6)"],
                  ["/api/analytics/evolution?loinc=…", "GET",
                   "Seriile temporale per parametru pentru evoluția grafică"],
                  ["/loinc/match (microserviciu intern)", "POST",
                   "Codificarea LOINC a unui analit cu context documentar (E5)"],
                  ["/api/clinic/batches · /batches/{id}", "POST/GET",
                   "Procesarea batch B2B și progresul loturilor (F8.2)"],
                  ["/api/clinic/reports/comparative", "GET",
                   "Rapoartele comparative multi-pacient (F8.3)"],
                  ["/fhir/Observation · /DiagnosticReport", "GET/POST",
                   "Exportul interoperabil HL7 FHIR (F6.3, F8.4)"],
                  ["/api/billing/subscribe · /webhooks/payments", "POST",
                   "Abonamente și confirmările gateway-ului de plăți (E9)"],
              ],
              col_widths_cm=[6.8, 3.0, 6.0], font_size=9)
    add_para(doc,
             "Toate API-urile vor fi versionate, protejate prin "
             "autentificare (sesiune sau cheie API pentru B2B), limitate "
             "ca rată și documentate public pentru integratori (zona B2B).",
             italic=True)
    page_break(doc)
