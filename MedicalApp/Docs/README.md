# Documentatie MyMedicalApp

## Pentru utilizare
- `Ghid_Utilizare_RO.md` - ghidul de utilizare al aplicatiei.
- `Adding_New_Language.md` - cum se adauga o limba noua.

## Starea produsului
- `PRD.md` - cerintele, tot ce s-a implementat (cu date) si lista de prioritati ramase.
- `Plan_Cabinet_Medical.md` - planul tipului de cont Cabinet Medical (CM).

## Infrastructura si performanta
- `SQL_INDEXES.md` - auditul de indexuri SQL si notele de deploy.
- `QUOTA_AND_DURABLE_QUEUE.md` - cota Gemini si coada durabila de interpretari.
- `LOINC_SCALING.md` - cache-ul si scalarea microserviciului LOINC.
- `SCALE_OUT.md` / `AZURE_SCALING.md` - rularea pe mai multe instante in Azure.
- `AZURE_HOSTING.md` - checklist de configurare Azure: Always On, health check, cota Gemini pe instante, ordinea la deploy.
- `AZURE_APP_SETTINGS.md` - ce pun unde la deploy: `appsettings.Azure.json` vs. Application settings din portal, lista completa de chei.
- `CAM_BLOB_STORAGE.md` - stocarea fisierelor pentru clinici.
- `AUDIT.md` - auditul general al codului.

## Important
Aceste fisiere (mai putin cele doua ghiduri) sunt COPII. Originalele, care se
actualizeaza in continuare, sunt in `memory\` din radacina repository-ului,
langa folderul `MedicalApp`. Planul original este in `plan\plan.md`.

Fisierele .md nu sunt incluse in build si nu ajung pe server.
