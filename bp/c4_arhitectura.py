from .helpers import add_heading, add_para, add_bullets, add_numbered, add_table, page_break


def build(doc):
    add_heading(doc, "C.4. Descrierea tehnică a proiectului", 1)
    add_para(doc,
             "Prezentul capitol descrie soluția tehnică ce va fi proiectată "
             "și construită integral în cadrul proiectului: arhitectura "
             "generală, componentele funcționale, modelul de date, precum și "
             "enumerarea completă a etapelor și fazelor de dezvoltare — de "
             "la landing page, înregistrare și autentificare până la motorul "
             "AI, microserviciul de codificare LOINC, modulul B2B, "
             "monetizare și lansarea comercială.")

    # ---------------- C.4.1 ----------------
    add_heading(doc, "C.4.1. Arhitectura logică a sistemului", 2)
    add_para(doc,
             "Platforma va fi construită pe o arhitectură distribuită pe "
             "patru straturi ortogonale, conformă cu principiile Clean "
             "Architecture și Domain-Driven Design, aleasă pentru "
             "mentenabilitate pe termen lung, testabilitate și posibilitatea "
             "livrării atât în cloud (SaaS), cât și on-premise:")
    add_bullets(doc, [
        "Stratul de prezentare (Presentation Layer): aplicație web "
        "responsive dezvoltată în ASP.NET Core MVC (Razor), cu Bootstrap 5 "
        "și JavaScript standard — decizie arhitecturală care minimizează "
        "suprafața de atac de securitate, asigură compatibilitate largă cu "
        "dispozitivele utilizatorilor și permite ulterior ambalarea PWA;",
        "Stratul de aplicație (Application Layer): controlere și servicii "
        "orchestratoare în C# / .NET (LTS curent), care vor coordona "
        "fluxurile de înregistrare, autentificare, upload, interpretare AI, "
        "codificare LOINC, generare rapoarte și notificare; injecție de "
        "dependențe nativă și politici de reziliență (retry, timeout, "
        "circuit-breaker) pentru toate apelurile externe;",
        "Stratul de domeniu (Domain Layer): entitățile de business "
        "(Utilizator, Profil pacient, Interpretare, Rezultat-cheie, "
        "Clinică, Rulare batch, Jurnal consum AI), regulile de validare a "
        "creditelor, regulile de calcul al statusului clinic și "
        "evenimentele de domeniu;",
        "Stratul de infrastructură (Infrastructure Layer): persistență "
        "prin Entity Framework Core pe Microsoft SQL Server / Azure SQL "
        "Database, microserviciu Python (FastAPI) pentru codificarea "
        "LOINC, integrări externe (API LLM multimodal, relay SMTP "
        "tranzacțional, generare PDF, seif de secrete)."
    ])
    add_para(doc,
             "Separarea microserviciului LOINC (Python) de nucleul platformei "
             "(.NET) este o decizie deliberată: ecosistemul Python oferă "
             "bibliotecile de referință pentru embeddings semantice și "
             "potrivire fuzzy, iar izolarea într-un serviciu propriu permite "
             "scalarea și versionarea independentă a componentei de AI "
             "algoritmic față de aplicația web.")

    # ---------------- C.4.2 ----------------
    add_heading(doc, "C.4.2. Componentele funcționale majore care vor fi dezvoltate", 2)

    add_para(doc, "(A) Serviciul de interpretare AI:", bold=True)
    add_para(doc,
             "Componenta centrală a platformei. Va orchestra apelul către "
             "modelul LLM multimodal cu un prompt ierarhic pe patru "
             "niveluri: (1) rolul de sistem — interpret de laborator "
             "medical, cu limite etice stricte și refuzul diagnosticelor; "
             "(2) regulile de extragere — structura JSON obligatorie și "
             "câmpurile per analit; (3) regulile de normalizare — denumiri "
             "canonice în terminologie LOINC, specimen explicit; "
             "(4) capturarea contextului sursă — antetul de panel și linia "
             "brută a analitului, cu eliminarea semantică a adnotărilor "
             "administrative. Va include logică de reîncercare cu escaladare "
             "adaptivă între modelul economic și cel performant, precum și "
             "jurnalizarea completă a consumului de tokeni pentru controlul "
             "costului per interpretare.")

    add_para(doc, "(B) Microserviciul de codificare LOINC:", bold=True)
    add_para(doc,
             "Serviciu independent (Python, FastAPI) expus printr-un API "
             "REST intern. Va primi denumirea canonică a analizei, unitatea, "
             "denumirea brută și contextul documentar și va returna codul "
             "LOINC optim, împreună cu scorul de încredere și sursa "
             "deciziei. Pipeline-ul intern în cinci pași:")
    add_numbered(doc, [
        "Căutare în ancore: potrivire determinstă exactă (după normalizare) "
        "într-un dicționar curat de termeni canonici validați medical → "
        "scor 1,0;",
        "Potrivire semantică: encoding vectorial (SentenceTransformer, 384 "
        "dimensiuni) și similaritate cosinus față de embeddings "
        "pre-calculate pentru ~97.000 de denumiri LOINC; extragerea celor "
        "mai buni 25 de candidați;",
        "Potrivire fuzzy multi-sursă: scoruri RapidFuzz calculate "
        "încrucișat între denumirea canonică / denumirea brută și "
        "denumirea lungă / componenta LOINC; se reține maximul;",
        "Strat de reguli contextuale: extragerea cuvintelor-cheie de "
        "metodă și specimen din contextul documentar, cu prioritate pentru "
        "contextul sursă (antet + linie brută) față de textul generat de "
        "AI;",
        "Corecție finală în funcție de unitate: comutarea automată între "
        "perechea masică/molară a aceluiași analit atunci când unitatea "
        "raportată contrazice proprietatea candidatului câștigător."
    ])

    add_para(doc, "(C) Validatorul clinic determinist:", bold=True)
    add_para(doc,
             "Componentă algoritmică (non-AI) care va recalcula statusul "
             "clinic al fiecărui parametru din combinația valoare + interval "
             "de referință, cu suport pentru peste 20 de formate de interval "
             "(3,5–5,0; <100; ≤5; ≥40; intervale diferențiate pe sex; "
             "rezultate calitative de tip negativ/absent/urme). Când "
             "parsarea reușește, statusul determinist prevalează asupra "
             "celui emis de AI; când eșuează, se păstrează varianta AI și "
             "se jurnalizează cazul pentru analiză.")

    add_para(doc, "(D) Auditorul de completitudine:", bold=True)
    add_para(doc,
             "Mecanism de auto-verificare: modelul AI va declara numărul de "
             "analize identificate în document, iar auditorul îl va compara "
             "cu numărul efectiv extras. Sub pragul de completitudine de "
             "95%, procesarea se reia automat. Pragul și activarea vor fi "
             "configurabile fără redistribuire de cod.")

    add_para(doc, "(E) Clientul rezilient de codificare:", bold=True)
    add_para(doc,
             "Client HTTP tipizat (.NET) care va apela microserviciul LOINC "
             "pentru fiecare analit, cu timeout de 5 secunde, maximum 3 "
             "reîncercări cu backoff exponențial și degradare elegantă "
             "(interpretarea se livrează și în absența codului LOINC, "
             "marcată corespunzător). Codurile cu scor sub 0,55 vor fi "
             "respinse ca nesigure.")

    add_para(doc, "(F) Generatorul de rapoarte PDF:", bold=True)
    add_para(doc,
             "Generator determinist de rapoarte PDF (bibliotecă QuestPDF) "
             "cu aspect profesional: gruparea parametrilor pe antetele de "
             "panel reconstruite din contextul ierarhic, metadate inline "
             "sub denumirea fiecărui parametru, cod LOINC afișat per "
             "analit, iar în varianta freemium — mascarea secțiunilor "
             "premium fără compromiterea confidențialității.")

    add_para(doc, "(G) Modulul B2B — Clinic Access Module (CAM):", bold=True)
    add_para(doc,
             "Portal multi-tenant pentru clinici: conturi de organizație cu "
             "roluri, procesare batch multi-pacient, dashboard cu statusuri "
             "agregate, rapoarte comparative, tarifare per volum și export "
             "interoperabil HL7 FHIR (resursele Observation, "
             "DiagnosticReport, Patient).")

    # ---------------- C.4.3 ----------------
    add_heading(doc, "C.4.3. Modelul de date și persistența", 2)
    add_para(doc,
             "Persistența va folosi Microsoft SQL Server (dezvoltare) și "
             "Azure SQL Database (producție), prin Entity Framework Core cu "
             "migrări Code-First. Modelul de date principal proiectat:")
    add_bullets(doc, [
        "Tabela Utilizatori (Users): conturi B2C, credite disponibile și "
        "consumate, credite bonus (program de recomandare), stare "
        "abonament premium;",
        "Tabela Profiluri (Profiles): profiluri de pacient asociate "
        "contului (maximum 5 în tierul premium — familie), cu date "
        "demografice minime necesare interpretării (vârstă, sex);",
        "Tabela Interpretări (InterpretationHistories): câte o înregistrare "
        "per document procesat — JSON-ul complet al rezultatului "
        "(NVARCHAR(MAX)), amprenta SHA-256 a PDF-ului pentru deduplicare, "
        "modelul AI folosit, tokenii consumați, momentul procesării;",
        "Tabela Rezultate-cheie (KeyResults, denormalizată): câte un rând "
        "per analit, cu valoare, unitate, interval, status, cod LOINC, "
        "scor de încredere — fundamentul interogărilor rapide pentru "
        "evoluția grafică multi-vizită;",
        "Tabela Clinici (Clinics): configurația fiecărui tenant B2B — "
        "tarife, module active, branding, subdomeniu;",
        "Tabela Rulări batch (ClinicBatchRuns): fiecare lot de procesare "
        "B2B, cu progres și rezultate agregate;",
        "Tabela Jurnal AI (AiUsageLogs): fiecare apel către modelul AI — "
        "tokeni de intrare/ieșire, model, latență, status — pentru "
        "controlul costurilor și audit;",
        "Tabela Jurnal de audit (AuditLogs): acțiunile sensibile "
        "(autentificare, upload, ștergere, operațiuni administrative), cu "
        "retenție de 7 ani conform legislației de arhivare medicală."
    ])
    add_para(doc,
             "Principii de proiectare a datelor: chei surogat, indecși pe "
             "coloanele de interogare frecventă (utilizator + profil + cod "
             "LOINC + dată), soft-delete cu fereastră de 30 de zile pentru "
             "dreptul la ștergere GDPR, criptare transparentă la repaus "
             "(TDE) și separare strictă a datelor între tenanții B2B.")
    page_break(doc)
