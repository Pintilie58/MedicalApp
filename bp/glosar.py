from .helpers import add_heading, add_para, add_table, page_break


def build(doc):
    page_break(doc)
    add_heading(doc, "Glosar de termeni tehnici (anexă la Partea a II-a)", 1)
    add_para(doc,
             "Pentru facilitarea evaluării, se definesc termenii tehnici "
             "utilizați în descrierea proiectului:")
    add_table(doc,
              headers=["Termen", "Definiție"],
              rows=[
                  ["LOINC", "Logical Observation Identifiers Names and Codes — "
                   "nomenclator internațional (Regenstrief Institute) de "
                   "codificare a observațiilor de laborator; ~97.000 de "
                   "coduri; standardul de referință pentru "
                   "interoperabilitatea rezultatelor medicale."],
                  ["HL7 FHIR", "Fast Healthcare Interoperability Resources — "
                   "standardul modern de schimb de date medicale între "
                   "sisteme informatice, adoptat de Spațiul European al "
                   "Datelor de Sănătate."],
                  ["EHDS", "European Health Data Space — cadrul european "
                   "(Regulamentul (UE) 2025/327) pentru portabilitatea și "
                   "utilizarea secundară a datelor de sănătate."],
                  ["LLM multimodal", "Model lingvistic mare capabil să "
                   "proceseze simultan text și imagini/documente (ex. "
                   "familia Google Gemini) — folosit pentru citirea "
                   "buletinelor PDF eterogene."],
                  ["Embedding semantic", "Reprezentare vectorială numerică a "
                   "unui text (aici: 384 dimensiuni), care permite "
                   "compararea matematică a semnificațiilor prin "
                   "similaritate cosinus."],
                  ["Potrivire fuzzy", "Tehnică de comparare tolerantă la "
                   "diferențe de scriere (abrevieri, greșeli de tipar), "
                   "bazată pe distanțe de editare între șiruri."],
                  ["Pipeline hibrid", "Lanț de procesare care combină mai "
                   "multe tehnici (deterministe + statistice + semantice) "
                   "cu un model de ponderare, pentru precizie superioară "
                   "oricărei tehnici individuale."],
                  ["Structured output", "Constrângerea răspunsului unui model "
                   "AI la o schemă strictă (JSON), eliminând răspunsurile "
                   "malformate."],
                  ["Tier promotion", "Escaladarea automată de la un model AI "
                   "economic la unul performant, exclusiv pentru cazurile "
                   "care eșuează validările."],
                  ["Freemium", "Model comercial cu nivel de bază gratuit și "
                   "funcționalități avansate contra abonament."],
                  ["Multi-tenant", "Arhitectură în care mai mulți clienți "
                   "(clinici) folosesc aceeași instalare, cu izolare "
                   "strictă a datelor între ei."],
                  ["CI/CD", "Integrare și livrare continuă — automatizarea "
                   "build-ului, testării și publicării fiecărei versiuni."],
                  ["IaC", "Infrastructure as Code — definirea infrastructurii "
                   "cloud în fișiere de cod versionate, reproductibile."],
                  ["DPIA", "Data Protection Impact Assessment — evaluarea de "
                   "impact asupra protecției datelor, obligatorie GDPR "
                   "pentru prelucrarea datelor de sănătate la scară largă."],
                  ["SCA / 3-D Secure", "Autentificarea strictă a clienților "
                   "la plățile online, obligatorie în UE (PSD2)."],
                  ["RPO / RTO", "Recovery Point/Time Objective — pierderea "
                   "maximă de date, respectiv durata maximă de "
                   "indisponibilitate acceptate la un dezastru."],
                  ["TRL", "Technology Readiness Level — scara 1–9 de "
                   "maturitate tehnologică (de la concept la produs "
                   "operațional pe piață)."],
                  ["MRR / CAC", "Monthly Recurring Revenue — venitul lunar "
                   "recurent; Customer Acquisition Cost — costul mediu de "
                   "achiziție al unui client."],
                  ["WCAG 2.1 AA", "Standardul de accesibilitate web pentru "
                   "persoane cu dizabilități, nivelul de conformitate AA."],
                  ["SLA", "Service Level Agreement — angajament contractual "
                   "de disponibilitate și timp de răspuns."],
              ],
              col_widths_cm=[3.6, 12.6], font_size=9)
