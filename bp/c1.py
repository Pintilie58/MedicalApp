from .helpers import add_heading, add_para, add_bullets, add_numbered, add_table, page_break


def build(doc):
    add_heading(doc, "C.1. Obiectivele proiectului", 1)

    # ---------------- C.1.1 ----------------
    add_heading(doc, "C.1.1. Necesitatea și oportunitatea investiției propuse", 2)
    add_para(doc,
             "În fiecare an, în România se efectuează peste 100 de milioane "
             "de analize medicale de laborator, iar la nivelul Uniunii "
             "Europene volumul depășește 10 miliarde de determinări anual. "
             "Rezultatul acestor investigații ajunge la pacient, în imensa "
             "majoritate a cazurilor, sub forma unui document PDF tehnic, "
             "generat de sisteme informatice de laborator (LIS) diferite, cu "
             "structuri, denumiri, unități de măsură și intervale de "
             "referință neomogene. Pacientul primește astfel un document pe "
             "care, de cele mai multe ori, nu îl înțelege.")
    add_para(doc,
             "Studiile europene de alfabetizare în sănătate (European Health "
             "Literacy Survey) arată că aproximativ 47% din populația adultă "
             "a UE are un nivel limitat sau problematic de înțelegere a "
             "informațiilor medicale, iar în România procentul depășește 55%. "
             "Sondajele naționale indică faptul că peste 60% dintre adulți au "
             "efectuat cel puțin o analiză medicală în ultimele 12 luni, iar "
             "41% dintre aceștia declară că nu au înțeles integral "
             "semnificația rezultatelor primite. Consecințele directe sunt:")
    add_bullets(doc, [
        "amânarea prezentării la medic în cazul unor valori patologice "
        "nesesizate de pacient (diagnostic întârziat);",
        "anxietate nejustificată și consum suplimentar de servicii medicale "
        "în cazul unor variații minore, fiziologice, ale valorilor;",
        "imposibilitatea urmăririi evoluției în timp a parametrilor proprii, "
        "din cauza formatelor incompatibile între laboratoare;",
        "pierderea de informație clinică la transferul pacientului între "
        "furnizori de servicii medicale, în absența unei codificări "
        "standardizate a rezultatelor (LOINC)."
    ])
    add_para(doc,
             "Pe partea de sistem medical, clinicile și cabinetele medicale "
             "se confruntă cu o problemă simetrică: datele de laborator "
             "sosesc pe hârtie sau PDF, nu pot fi agregate, comparate sau "
             "analizate automat, iar interoperabilitatea cu dosarul "
             "electronic de sănătate (DES) și cu standardele europene "
             "(HL7 FHIR, European Health Data Space — EHDS) impune "
             "codificarea rezultatelor în nomenclatorul internațional LOINC "
             "(Logical Observation Identifiers Names and Codes) — operațiune "
             "care astăzi se realizează manual, costisitor și cu erori.")
    add_para(doc,
             "Oportunitatea investiției rezultă din convergența a patru "
             "factori independenți, aflați simultan la maturitate:")
    add_numbered(doc, [
        "Maturizarea modelelor lingvistice mari (LLM) multimodale, capabile "
        "să citească documente PDF eterogene și să extragă date structurate "
        "cu acuratețe ridicată — tehnologie indisponibilă comercial înainte "
        "de 2023;",
        "Adoptarea Regulamentului (UE) 2025/327 privind Spațiul European al "
        "Datelor de Sănătate (EHDS), care obligă statele membre la "
        "standardizarea și portabilitatea datelor medicale — LOINC fiind "
        "nomenclatorul de referință pentru observațiile de laborator;",
        "Creșterea accelerată a pieței globale de interpretare AI a datelor "
        "medicale, estimată la o rată anuală compusă (CAGR) de peste 35% "
        "pentru segmentul health-AI B2C/B2B;",
        "Disponibilitatea finanțării nerambursabile POCIDIF pentru produse "
        "digitale inovatoare, care reduce riscul investițional al unui "
        "proiect greenfield cu grad ridicat de noutate."
    ])
    add_para(doc,
             "În absența investiției, problema identificată rămâne "
             "nerezolvată: nu există în prezent, pe piața românească și nici "
             "pe cea europeană, o soluție integrată care să combine "
             "extragerea automată a datelor din PDF-uri de laborator "
             "eterogene, codificarea LOINC automată de înaltă precizie și "
             "interpretarea narativă multilingvă destinată pacientului. "
             "Investiția propusă acoperă exact această lacună.", bold=True)

    # ---------------- C.1.2 ----------------
    add_heading(doc, "C.1.2. Obiectivul general al proiectului", 2)
    add_para(doc,
             "Obiectivul general al proiectului îl constituie proiectarea, "
             "dezvoltarea integrală de la zero și lansarea comercială, în 18 "
             "luni, a platformei software MyMedicalApp — un produs SaaS "
             "inovator, bazat pe Inteligență Artificială, care transformă "
             "automat buletinele de analize medicale (PDF nestructurat) în "
             "date structurate, codificate LOINC și interpretate în limbaj "
             "accesibil, în limba pacientului, deservind simultan piața B2C "
             "(pacienți) și piața B2B (clinici, laboratoare, furnizori de "
             "servicii medicale), în condiții complete de securitate "
             "cibernetică și conformitate GDPR.", bold=True)
    add_para(doc,
             "Atingerea obiectivului general va conduce la creșterea "
             "competitivității S.C. FIXMEDICAL S.R.L. prin diversificarea "
             "activității către un produs software proprietar scalabil, cu "
             "potențial de export pe piața unică europeană, și va contribui "
             "la obiectivele POCIDIF de digitalizare inteligentă a "
             "economiei românești.")

    # ---------------- C.1.3 ----------------
    add_heading(doc, "C.1.3. Obiectivele specifice ale proiectului (SMART)", 2)
    add_para(doc,
             "Obiectivele specifice sunt formulate conform metodologiei "
             "SMART (Specific, Măsurabil, Abordabil, Relevant, încadrat în "
             "Timp) și acoperă integral ciclul de viață al proiectului "
             "greenfield, de la proiectare la comercializare:")
    add_table(doc,
              headers=["ID", "Obiectiv specific", "Indicator măsurabil", "Termen"],
              rows=[
                  ["OS1",
                   "Proiectarea completă a arhitecturii software (specificații "
                   "funcționale, arhitectură pe straturi, model de date, "
                   "design UX/UI) pentru întreaga platformă",
                   "Documentație de arhitectură aprobată; 100% din module "
                   "specificate; prototip UX validat pe 20 utilizatori",
                   "Luna 3"],
                  ["OS2",
                   "Construirea infrastructurii cloud și a lanțului DevOps "
                   "(medii dezvoltare/staging/producție, CI/CD, monitorizare)",
                   "3 medii operaționale; pipeline CI/CD funcțional; timp de "
                   "release < 30 minute",
                   "Luna 4"],
                  ["OS3",
                   "Dezvoltarea nucleului platformei web: landing page, "
                   "înregistrare, autentificare, management profiluri, "
                   "upload PDF, dashboard utilizator",
                   "6 module funcționale livrate și testate; acoperire teste "
                   "automate ≥ 70%",
                   "Luna 9"],
                  ["OS4",
                   "Dezvoltarea motorului AI de extragere și interpretare "
                   "(integrare LLM multimodal, validare deterministă, audit "
                   "de completitudine, interpretare narativă multilingvă)",
                   "Completitudine extragere ≥ 95%; interpretare în 7 limbi "
                   "la MVP",
                   "Luna 11"],
                  ["OS5",
                   "Dezvoltarea microserviciului de codificare LOINC cu "
                   "pipeline hibrid (ancore deterministe + potrivire "
                   "semantică + potrivire fuzzy + strat de reguli)",
                   "Precizie codificare ≥ 96% pe eșantion de validare de "
                   "10.000 rapoarte reale",
                   "Luna 12"],
                  ["OS6",
                   "Dezvoltarea modulului B2B (Clinic Access Module) cu "
                   "procesare batch multi-pacient și export interoperabil "
                   "HL7 FHIR",
                   "Modul B2B operațional; export FHIR Observation validat; "
                   "5 clinici pilot active",
                   "Luna 15"],
                  ["OS7",
                   "Implementarea monetizării (freemium/premium B2C, "
                   "abonamente B2B, gateway de plăți) și lansarea comercială",
                   "≥ 5.000 utilizatori înregistrați; ≥ 10 contracte B2B "
                   "semnate; venituri recurente lunare (MRR) ≥ 8.000 EUR",
                   "Luna 18"],
                  ["OS8",
                   "Certificarea securității și conformității (audit GDPR/"
                   "DPIA, teste de penetrare, politici de securitate "
                   "ISO 27001)",
                   "DPIA finalizată; raport pen-test fără vulnerabilități "
                   "critice; 0 incidente de securitate",
                   "Luna 16"],
                  ["OS9",
                   "Extinderea acoperirii lingvistice de la 7 limbi (MVP) la "
                   "30 de limbi europene și internaționale",
                   "30 limbi active în interfață, interpretare și rapoarte "
                   "PDF",
                   "Luna 18"],
              ],
              col_widths_cm=[1.1, 6.3, 6.0, 1.8])

    # ---------------- C.1.4 ----------------
    add_heading(doc, "C.1.4. Rezultatele așteptate și indicatorii de realizare", 2)
    add_para(doc, "Rezultate directe (output) la finalul implementării:", bold=True)
    add_bullets(doc, [
        "1 platformă SaaS nouă, funcțională, lansată comercial (web, "
        "responsive, pregătită PWA), dezvoltată integral în cadrul "
        "proiectului;",
        "1 motor AI de extragere și interpretare a analizelor medicale, cu "
        "completitudine ≥ 95% și interpretare narativă în 30 de limbi;",
        "1 microserviciu de codificare LOINC cu pipeline hibrid "
        "(determinist + semantic + fuzzy + reguli), precizie ≥ 96%;",
        "1 modul B2B multi-tenant (Clinic Access Module) cu procesare batch "
        "și export HL7 FHIR;",
        "≥ 5.000 utilizatori B2C înregistrați și ≥ 10 clienți B2B "
        "contractați în primele 3 luni de la lansarea comercială;",
        "10 locuri de muncă înalt calificate create și menținute "
        "(dezvoltare software, AI/ML, QA, DevOps, medical, comercial);",
        "1 set complet de documentație: arhitectură, API, manuale de "
        "utilizare, politici GDPR și de securitate."
    ])
    add_para(doc, "Rezultate pe termen mediu (outcome, 3 ani post-implementare):", bold=True)
    add_bullets(doc, [
        "Cifră de afaceri cumulată generată de platformă ≥ 3.500.000 EUR "
        "în primii 3 ani post-implementare, cu 1.800.000 EUR în anul 3;",
        "≥ 150.000 utilizatori B2C activi și ≥ 120 clienți B2B pe "
        "piețele România + UE;",
        "Prezență comercială în minimum 5 state membre UE prin acoperirea "
        "lingvistică și conformitatea EHDS;",
        "Contribuție măsurabilă la interoperabilitatea datelor medicale "
        "prin volumul de rezultate codificate LOINC (țintă: 10 milioane de "
        "determinări codificate/an în anul 3)."
    ])

    # ---------------- C.1.5 ----------------
    add_heading(doc, "C.1.5. Alinierea la obiectivele strategice europene și naționale", 2)
    add_para(doc, "Proiectul contribuie direct la următoarele cadre strategice:")
    add_table(doc,
              headers=["Cadru strategic", "Contribuția proiectului"],
              rows=[
                  ["POCIDIF 2021–2027",
                   "Dezvoltarea unui produs digital inovator bazat pe AI de "
                   "către o IMM românească; creștere inteligentă prin "
                   "digitalizare în domeniul sănătății."],
                  ["Deceniul Digital al Europei 2030",
                   "Contribuie la ținta de 75% adoptare AI în întreprinderi "
                   "și la digitalizarea serviciilor publice de sănătate."],
                  ["Spațiul European al Datelor de Sănătate (EHDS)",
                   "Codificarea LOINC și exportul HL7 FHIR pregătesc datele "
                   "de laborator pentru portabilitate transfrontalieră."],
                  ["Strategia Națională de Sănătate 2023–2030",
                   "Crește alfabetizarea în sănătate a populației și susține "
                   "prevenția prin înțelegerea rezultatelor medicale."],
                  ["Regulamentul (UE) 2024/1689 privind IA (AI Act)",
                   "Platforma este proiectată nativ conform cerințelor de "
                   "transparență și supraveghere umană pentru sisteme AI în "
                   "sănătate."],
                  ["Strategia Națională de Inteligență Artificială",
                   "Produs AI românesc cu potențial de export; dezvoltarea "
                   "competențelor AI/ML în echipa locală."],
              ],
              col_widths_cm=[5.5, 10.7])
    page_break(doc)
