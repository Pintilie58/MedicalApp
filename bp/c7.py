from .helpers import add_heading, add_para, add_bullets, add_numbered, add_table, page_break


def build(doc):
    add_heading(doc, "C.7. Strategia de marketing și de comercializare", 1)

    # ---------------- C.7.1 ----------------
    add_heading(doc, "C.7.1. Analiza pieței și a tendințelor", 2)
    add_para(doc,
             "Piața globală de sănătate digitală depășește 300 miliarde "
             "USD, cu segmentul AI-în-sănătate în creștere cu peste 35% "
             "anual. Tendințe relevante pentru poziționarea produsului:")
    add_bullets(doc, [
        "consumerizarea sănătății: pacientul devine cumpărător direct de "
        "servicii digitale de sănătate (abonamente wellness, telemedicină);",
        "presiunea reglementară pro-interoperabilitate (EHDS, FHIR) "
        "transformă codificarea LOINC dintr-un „nice-to-have\u201d într-o "
        "obligație pentru furnizorii medicali — vânt din spate pentru "
        "oferta B2B;",
        "încrederea în AI pentru informare medicală crește, dar publicul "
        "cere surse verificabile și garanții — poziționarea „AI verificat "
        "algoritmic, fără diagnostice\u201d răspunde exact acestei cereri;",
        "piața românească e-health este în fază incipientă, fără lider "
        "stabilit pe interpretarea analizelor — fereastră de oportunitate "
        "de tip first-mover."
    ])

    # ---------------- C.7.2 ----------------
    add_heading(doc, "C.7.2. Poziționare și diferențiere strategică", 2)
    add_para(doc,
             "Poziționare: „Analizele tale, explicate pe limba ta\u201d — "
             "singura platformă care citește orice buletin PDF, îl "
             "codifică la standard internațional și îl explică empatic, "
             "în 30 de limbi, cu verificare algoritmică a fiecărui status. "
             "Diferențiere susținută de barierele tehnologice construite "
             "în proiect (pipeline hibrid LOINC, validare deterministă, "
             "context ierarhic) — greu de replicat rapid de competitori.")

    # ---------------- C.7.3 ----------------
    add_heading(doc, "C.7.3. Obiective de marketing (SMART)", 2)
    add_table(doc,
              headers=["Obiectiv", "Indicator", "Termen"],
              rows=[
                  ["Notorietate pre-lansare", "≥ 3.000 înscrieri waitlist; "
                   "≥ 500.000 afișări campanii", "Luna 13"],
                  ["Adopție B2C la lansare", "≥ 5.000 utilizatori "
                   "înregistrați; CAC ≤ 6 EUR", "Luna 18"],
                  ["Conversie premium", "≥ 4% din utilizatorii activi", "Luna 18 + 3"],
                  ["Portofoliu B2B", "≥ 10 contracte semnate; pipeline ≥ 60 "
                   "clinici calificate", "Luna 18"],
                  ["Venit recurent", "MRR ≥ 8.000 EUR la lansare; ≥ 25.000 "
                   "EUR la 6 luni post-lansare", "Luna 18 / +6"],
                  ["Prezență UE", "Primele vânzări în ≥ 2 piețe externe", "Luna 18 + 6"],
              ],
              col_widths_cm=[5.2, 8.0, 3.0])

    # ---------------- C.7.4 ----------------
    add_heading(doc, "C.7.4. Mixul de marketing", 2)
    add_para(doc, "Produs:", bold=True)
    add_para(doc,
             "Freemium B2C cu upgrade natural către premium (istoric, "
             "familie, evoluție grafică); B2B pe trei tiere + enterprise "
             "on-premise. Produsul însuși este canal de marketing: fiecare "
             "raport PDF premium poartă branding și cod de recomandare.")
    add_para(doc, "Preț:", bold=True)
    add_para(doc,
             "B2C: 0 EUR (freemium, credite limitate) / 4,99 EUR pe lună "
             "premium. B2B: 199 / 499 / 999–2.499 EUR pe lună în funcție "
             "de volum și module; enterprise on-premise 150.000–500.000 "
             "EUR. Prețuri validate în faza de cercetare (C.3.3), sub "
             "pragurile psihologice identificate.")
    add_para(doc, "Plasare (canale):", bold=True)
    add_bullets(doc, [
        "vânzare directă online (self-service) pentru B2C și tierele B2B "
        "mici — onboarding < 1 zi;",
        "echipă de vânzare consultativă pentru clinici medii/mari și "
        "enterprise;",
        "parteneriate cu laboratoare și rețele de clinici (co-branding, "
        "revenue share);",
        "programe de afiliere cu influenceri medicali și platforme de "
        "telemedicină."
    ])
    add_para(doc, "Promovare:", bold=True)
    add_bullets(doc, [
        "SEO și marketing de conținut medical verificat (articole per "
        "analit, în 7→30 limbi) — activ organic durabil, construit din "
        "luna 5;",
        "campanii plătite (căutare + social) concentrate pe momente de "
        "intenție („ce înseamnă TSH mărit\u201d);",
        "campanii B2B pe canale profesionale (LinkedIn, conferințe "
        "medicale, asociații de clinici);",
        "PR de lansare: studiu național despre înțelegerea analizelor "
        "(datele din cercetarea C.3.3), cu potențial media ridicat;",
        "programul de recomandare cu credite bonus — creștere virală "
        "controlată."
    ])

    # ---------------- C.7.5 ----------------
    add_heading(doc, "C.7.5. Bugetul de marketing pe durata proiectului", 2)
    add_table(doc,
              headers=["Categorie", "Perioada", "Buget (EUR)"],
              rows=[
                  ["Cercetare și validare cerere (sondaje, interviuri)",
                   "Lunile 1–6", "12.000"],
                  ["Identitate vizuală și materiale", "Lunile 2–5", "6.000"],
                  ["Conținut SEO multilingv", "Lunile 5–18", "18.000"],
                  ["Campanii plătite pre-lansare (waitlist)", "Lunile 10–13",
                   "10.000"],
                  ["Campanii de lansare B2C", "Lunile 16–18", "22.000"],
                  ["Marketing și evenimente B2B", "Lunile 12–18", "14.000"],
                  ["PR și studiu național", "Lunile 15–17", "8.000"],
                  ["TOTAL", "", "90.000"],
              ],
              col_widths_cm=[8.6, 4.0, 3.6])

    # ---------------- C.7.6 ----------------
    add_heading(doc, "C.7.6. Pâlnia de conversie și parcursul clientului", 2)
    add_para(doc, "Parcursul B2C (self-service):", bold=True)
    add_numbered(doc, [
        "Descoperire: căutare organică („ce înseamnă…\u201d) sau campanie → "
        "articol SEO / landing page; țintă CTR ≥ 3%;",
        "Activare: înregistrare cu credite gratuite → primul document "
        "procesat în < 5 minute de la sosire; țintă activare ≥ 45%;",
        "Retenție: notificare la epuizarea creditelor, istoric care "
        "crește în valoare cu fiecare buletin; țintă revenire la 30 zile "
        "≥ 25%;",
        "Venit: conversie premium la momente de nevoie reală (al 2-lea "
        "profil, evoluție grafică); țintă ≥ 4%;",
        "Recomandare: credite bonus pentru invitații acceptate; țintă "
        "coeficient viral ≥ 0,3."
    ])
    add_para(doc, "Parcursul B2B (consultativ):", bold=True)
    add_numbered(doc, [
        "Generare de interes: conferințe, LinkedIn, recomandările "
        "medicilor care folosesc B2C; țintă 60 clinici calificate în "
        "pipeline;",
        "Demonstrație personalizată pe buletinele proprii ale clinicii "
        "(efect „wow\u201d controlat); țintă conversie demo → pilot ≥ 40%;",
        "Pilot 4–6 săptămâni cu indicatori de succes conveniți (timp "
        "economisit, satisfacția pacienților);",
        "Contractare pe tier potrivit volumului; onboarding < 1 zi;",
        "Expansiune: module suplimentare, upgrade de tier, extindere la "
        "alte locații ale rețelei."
    ])
    page_break(doc)
