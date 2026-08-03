from .helpers import add_heading, add_para, add_bullets, add_numbered, add_table, page_break


def build(doc):
    add_heading(doc, "C.3. Product-Market Fit", 1)

    # ---------------- C.3.1 ----------------
    add_heading(doc, "C.3.1. Piața țintă — segmentare și dimensionare", 2)
    add_para(doc,
             "Platforma adresează simultan două piețe complementare, cu "
             "cicluri de vânzare și economii diferite:")
    add_para(doc, "Segmentul B2C — pacienți și aparținători:", bold=True)
    add_bullets(doc, [
        "TAM (Total Addressable Market): populația adultă UE care "
        "efectuează analize anual — ≈ 250 milioane persoane;",
        "SAM (Serviceable Addressable Market): utilizatori digitali din "
        "România + primele 5 piețe UE de expansiune, cu interes activ "
        "pentru sănătate — ≈ 28 milioane persoane;",
        "SOM (Serviceable Obtainable Market, 3 ani): 150.000 utilizatori "
        "activi, dintre care 4–6% convertiți la abonament premium "
        "(4,99 EUR/lună)."
    ])
    add_para(doc, "Segmentul B2B — furnizori de servicii medicale:", bold=True)
    add_bullets(doc, [
        "România: ≈ 8.000 clinici și cabinete private, ≈ 400 laboratoare, "
        "≈ 500 spitale;",
        "UE: peste 60.000 de clinici private adresabile prin SaaS "
        "multilingv;",
        "Modelul de licențiere: abonament lunar per clinică pe trei tiere "
        "(199 / 499 / 999 EUR — în funcție de volumul de procesări și de "
        "modulele active), plus licențe enterprise on-premise "
        "(150.000–500.000 EUR) pentru rețele mari și instituții publice."
    ])

    # ---------------- C.3.2 ----------------
    add_heading(doc, "C.3.2. Problema abordată și dovada nevoii", 2)
    add_para(doc,
             "Problema-nucleu: rezultatele de laborator circulă ca documente "
             "PDF nestructurate, ilizibile pentru pacient și neexploatabile "
             "informatic pentru sistemul medical. Dovezile nevoii, "
             "colectate în faza de fundamentare a proiectului:")
    add_bullets(doc, [
        "peste 55% din populația României are alfabetizare în sănătate "
        "limitată (European Health Literacy Survey);",
        "41% dintre pacienții care au primit analize declară că nu au "
        "înțeles integral rezultatele;",
        "clinicile raportează costuri semnificative de timp medical "
        "consumat cu explicarea rezultatelor de rutină;",
        "obligațiile de interoperabilitate EHDS/FHIR intră progresiv în "
        "vigoare pentru statele membre, iar codificarea LOINC manuală "
        "este nescalabilă;",
        "cererile frecvente adresate motoarelor de căutare și asistenților "
        "AI de tip general („ce înseamnă TSH mărit\u201d) demonstrează cerere "
        "organică masivă, servită în prezent de surse nefiabile."
    ])

    # ---------------- C.3.3 ----------------
    add_heading(doc, "C.3.3. Validarea cererii — metodologie planificată", 2)
    add_para(doc,
             "Deoarece proiectul pornește de la zero, fără o bază "
             "cuantificabilă de utilizatori, planul include o componentă "
             "dedicată de validare a cererii, distribuită pe primele 6 luni "
             "ale implementării, cu buget alocat de 12.000 EUR:")
    add_numbered(doc, [
        "Cercetare cantitativă: sondaj online pe minimum 1.000 de "
        "respondenți (pacienți) privind disponibilitatea de utilizare și "
        "de plată; țintă de validare: ≥ 30% intenție de utilizare, ≥ 5% "
        "disponibilitate de plată pentru premium;",
        "Interviuri calitative structurate cu 30 de medici și 20 de "
        "manageri de clinici pentru validarea fluxurilor B2B și a "
        "nivelurilor de preț;",
        "Landing page de pre-lansare cu listă de așteptare (waitlist) și "
        "măsurarea ratei de conversie a campaniilor de testare a "
        "mesajelor; țintă: ≥ 3.000 înscrieri înainte de lansarea MVP;",
        "Program pilot: 5 clinici partenere vor primi acces gratuit 3 luni "
        "în schimbul feedback-ului structurat și al scrisorilor de "
        "intenție comercială."
    ])

    # ---------------- C.3.4 ----------------
    add_heading(doc, "C.3.4. Propunerea de valoare pe segmente", 2)
    add_table(doc,
              headers=["Segment", "Propunere de valoare", "Preț", "Țintă adopție"],
              rows=[
                  ["Pacient freemium",
                   "Înțelege gratuit analizele: extragere + status vizual + "
                   "interpretare de bază",
                   "0 EUR", "100% baseline"],
                  ["Pacient premium",
                   "Istoric nelimitat, 5 profiluri de familie, evoluție "
                   "grafică multi-vizită, rapoarte PDF complete, export",
                   "4,99 EUR/lună", "4–6% din baza B2C"],
                  ["Clinică mică (tier Start)",
                   "Digitalizarea rezultatelor + rapoarte interpretate cu "
                   "branding propriu",
                   "199 EUR/lună", "2% din SAM B2B"],
                  ["Clinică medie (tier Pro)",
                   "Procesare batch multi-pacient, dashboard, comparații, "
                   "suport prioritar",
                   "499 EUR/lună", "0,8% din SAM B2B"],
                  ["Rețea/laborator (tier Max)",
                   "Volum mare, API dedicat, export FHIR, SLA extins",
                   "999–2.499 EUR/lună", "0,15% din SAM B2B"],
                  ["Enterprise on-premise",
                   "Instalare în infrastructura proprie, suveranitate "
                   "completă a datelor, integrare HIS/LIS",
                   "150.000–500.000 EUR", "1–2 contracte/țară"],
              ],
              col_widths_cm=[3.4, 7.2, 3.0, 2.6])

    # ---------------- C.3.5 ----------------
    add_heading(doc, "C.3.5. Scalabilitatea produsului", 2)
    add_para(doc,
             "Modelul SaaS propus are cost marginal per utilizator "
             "apropiat de zero pe componentele software și strict "
             "proporțional cu volumul pe componenta AI (cost per document "
             "procesat, optimizat prin escaladarea adaptivă între modele). "
             "Scalarea geografică se face exclusiv prin software: adăugarea "
             "unei limbi noi nu necesită prezență fizică locală, iar "
             "arhitectura multi-tenant permite onboarding-ul unei clinici "
             "noi în mai puțin de o zi. Pragul de rentabilitate operațională "
             "(break-even lunar) este estimat la ≈ 21.000 EUR MRR, "
             "atingibil în anul 2 post-lansare conform proiecțiilor "
             "financiare din Partea a III-a.")

    # ---------------- C.3.6 ----------------
    add_heading(doc, "C.3.6. Profiluri de client (personas)", 2)
    add_para(doc,
             "Proiectarea produsului va fi ghidată de patru profiluri de "
             "client de referință, definite în faza de cercetare și "
             "rafinate pe parcursul validării:")

    add_para(doc, "Persona 1 — „Maria\u201d, 42 ani, mamă și îngrijitor de familie (B2C premium):", bold=True)
    add_para(doc,
             "Gestionează sănătatea a trei generații: copiii, propria "
             "persoană și părinții vârstnici. Primește buletine de la "
             "laboratoare diferite și nu le poate compara. Nevoia "
             "principală: un singur loc unde istoricul întregii familii "
             "este organizat, explicat și urmăribil în timp. Este "
             "dispusă să plătească un abonament lunar mic pentru "
             "liniștea de a înțelege — profilul țintă al tierului "
             "premium (5 profiluri de familie).")

    add_para(doc, "Persona 2 — „Andrei\u201d, 29 ani, profesionist urban preventiv (B2C freemium):", bold=True)
    add_para(doc,
             "Își face analize anuale de rutină din proprie inițiativă. "
             "Caută online semnificația valorilor și ajunge pe surse "
             "nefiabile. Nevoia principală: răspuns rapid, gratuit și de "
             "încredere la întrebarea „e ceva în neregulă?\u201d. Intră prin "
             "căutare organică (SEO), folosește creditele gratuite și "
             "este canal de recomandare virală; o parte convertesc la "
             "premium odată cu acumularea istoricului.")

    add_para(doc, "Persona 3 — „Dr. Ionescu\u201d, medic și manager de clinică medie (B2B tier Pro):", bold=True)
    add_para(doc,
             "Conduce o clinică cu 12 medici care pierde zilnic timp "
             "clinic explicând rezultate de rutină și retranscriind date "
             "din PDF-uri. Nevoia principală: rapoarte interpretate cu "
             "branding propriu, procesare batch și date structurate "
             "exportabile. Decident pragmatic: cumpără după un pilot "
             "reușit și o demonstrație de economie de timp cuantificată.")

    add_para(doc, "Persona 4 — „LabCorp Regional\u201d, rețea de laboratoare (enterprise on-premise):", bold=True)
    add_para(doc,
             "Procesează sute de mii de determinări lunar și are "
             "obligații emergente de interoperabilitate (LOINC, FHIR, "
             "EHDS). Politica internă interzice trimiterea datelor în "
             "cloud extern. Nevoia principală: instalare on-premise a "
             "motorului de codificare și interpretare, cu SLA și suport "
             "dedicat — profilul licențelor enterprise de "
             "150.000–500.000 EUR.")
    page_break(doc)
