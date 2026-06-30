# Ghid de utilizare — MedicalApp+

> Versiune: februarie 2026 · Limba: română
> Aplicația rulează în 5 limbi (RO / EN / FR / ES / DE). Selectorul de limbă apare în colțul dreapta-sus al paginii de pornire.

---

## Cuprins

- [Prezentare generală](#prezentare-generală)
- [B2C — Persoane fizice & cabinete medicale individuale](#b2c--persoane-fizice--cabinete-medicale-individuale)
  - 1. [Înregistrare cont](#1-înregistrare-cont)
  - 2. [Verificarea emailului](#2-verificarea-emailului)
  - 3. [Autentificare & resetare parolă](#3-autentificare--resetare-parolă)
  - 4. [Dashboard](#4-dashboard)
  - 5. [Profile (membri familie / pacienți)](#5-profile-membri-familie--pacienți)
  - 6. [Încărcarea unui PDF pentru interpretare](#6-încărcarea-unui-pdf-pentru-interpretare)
  - 7. [Detectarea duplicatelor](#7-detectarea-duplicatelor)
  - 8. [Credite — pachete, cumpărare, consum](#8-credite--pachete-cumpărare-consum)
  - 9. [Istoric per profil & descărcare raport PDF](#9-istoric-per-profil--descărcare-raport-pdf)
  - 10. [Comparare rezultate (între analize)](#10-comparare-rezultate-între-analize)
  - 11. [Grafic de evoluție (per LOINC)](#11-grafic-de-evoluție-per-loinc)
  - 12. [Schimbare limbă · preferințe email](#12-schimbare-limbă--preferințe-email)
- [B2B — Cabinete de Analize Medicale (CAM)](#b2b--cabinete-de-analize-medicale-cam)
  - 1. [Cont Clinic — înregistrare](#1-cont-clinic--înregistrare)
  - 2. [Configurarea cabinetului (foldere watched)](#2-configurarea-cabinetului-foldere-watched)
  - 3. [Dashboard CAM](#3-dashboard-cam)
  - 4. [Selectare PDF-uri (fost "Verificare PDF-uri")](#4-selectare-pdf-uri-fost-verificare-pdf-uri)
  - 5. [Override pacient](#5-override-pacient)
  - 6. [Email blacklist](#6-email-blacklist)
  - 7. [Pornire batch · monitorizare · anulare](#7-pornire-batch--monitorizare--anulare)
  - 8. [NotSends · motive · re-procesare](#8-notsends--motive--re-procesare)
  - 9. [Sumar lot — PDF & TXT](#9-sumar-lot--pdf--txt)
  - 10. [Lista pacienților clinicii](#10-lista-pacienților-clinicii)
  - 11. [Cleanup fișiere vechi](#11-cleanup-fișiere-vechi)
  - 12. [Cumpărare credite CAM](#12-cumpărare-credite-cam)
- [Întrebări frecvente](#întrebări-frecvente)

---

## Prezentare generală

**MedicalApp+** transformă buletinele PDF de analize medicale în interpretări detaliate, prietenoase și complete. Aplicația:

- 📄 Citește PDF-ul (text-layer direct via PdfPig + fallback OCR Vision pentru scanate)
- 🧠 Folosește Gemini (2.5 Flash → 2.5 Pro → 3.1 Pro Preview, 3 tier-uri de fallback automat) pentru interpretare
- 🏷️ Mapează parametrii pe coduri LOINC standardizate prin microserviciul Python local
- 📑 Generează un raport PDF formatat profesional (QuestPDF) — alb-roșu (out-of-range) clar vizibil
- 📧 Trimite raportul pe email (B2B) sau permite descărcarea (B2C)
- 📈 Salvează rezultatele numeric (LOINC + valoare + interval) pentru istoric și grafice de evoluție

**Două moduri de utilizare:**

| Mod | Cine | Cum funcționează | Cost |
|---|---|---|---|
| **B2C** | Persoană fizică sau cabinet medical mic | Încarci tu manual PDF-ul, citești pe ecran sau descarci raport | Plătești per interpretare (credit) |
| **B2B (CAM)** | Cabinete de Analize Medicale | Tu lași PDF-urile într-un folder, aplicația le procesează automat și trimite pacienților pe email | Plătești pachet lunar (credite) |

---

# B2C — Persoane fizice & cabinete medicale individuale

## 1. Înregistrare cont

1. Mergi la pagina principală (ex: `https://medicalapp.ro/`)
2. Apasă **"Sign In / Register"** din header sau scrollează la secțiunea "Crează cont"
3. Completează:
   - **Email** (va fi username-ul tău — verifică ortografia!)
   - **Parolă** (minimum 8 caractere, recomandăm 12+)
   - **Confirmare parolă**
   - **Cod promoțional** (opțional — verifică automat dacă e valid)
   - Bifează acordul GDPR / Termeni
4. Apasă **"Crează cont"**

🎁 **Bonus:** După înregistrare primești automat **credite gratuite de bun venit** (configurabil de admin — default 2 credite). Acestea se consumă PRIMELE, înainte de cele plătite.

---

## 2. Verificarea emailului

Imediat după înregistrare:

1. Vei fi redirecționat la pagina **"Verificare email"**
2. Verifică inbox-ul pentru un email cu **codul de 6 cifre** (subject: *"Cod de verificare MedicalApp+"*)
3. Dacă nu vezi email-ul, verifică folderul **Spam / Promoții**
4. Introdu codul și apasă **"Verifică"**
5. Dacă codul a expirat (după 15 minute), apasă **"Retrimite codul"**

⚠️ **Contul rămâne blocat până confirmi emailul.** Nu poți încărca PDF-uri sau cumpăra credite înainte.

---

## 3. Autentificare & resetare parolă

**Login obișnuit:**
1. Apasă **"Sign In"**
2. Introdu email + parolă
3. Bifează *"Ține-mă conectat"* dacă vrei sesiune lungă (30 zile)
4. Apasă **"Autentificare"**

**Am uitat parola:**
1. Apasă link-ul **"Am uitat parola"** sub formular
2. Introdu emailul contului
3. Primești pe email un **link de resetare** (valabil 1 oră)
4. Click pe link → introdu noua parolă (de două ori) → confirm

🔒 **Securitate:** După 5 încercări greșite de login pe același cont în 15 minute, contul este blocat temporar 30 min (anti-brute-force).

---

## 4. Dashboard

După login aterizezi pe **Dashboard**. Aici vezi:

- 💳 **Credite disponibile** — defalcate pe **gratuite (bonus) / plătite**
  - Bonus-urile se consumă PRIMELE
  - Tooltip explicativ pe contoare
- 📁 **Profilele tale** — listă scurtă cu acces rapid la istoric
- 🚀 **Acțiuni rapide:**
  - "Interpretează o analiză nouă" — buton principal
  - "Cumpără credite"
  - "Gestionează profile"
- 📊 **Ultimele 5 interpretări** (cross-profil) cu mini-preview și buton "Vezi raport"

⚠️ **Alerte automate:**
- Sub 3 credite rămase → bandă galbenă "Credite scăzute"
- 0 credite → bandă roșie "Reînnoiește pentru a continua"

---

## 5. Profile (membri familie / pacienți)

Sistemul de **Profile** îți permite să gestionezi analizele pentru mai mulți pacienți pe același cont — soț/soție, copii, părinți, sau (pentru cabinetul medical mic) toți pacienții tăi.

### Creare profil nou

1. Mergi la **Profile** din meniu
2. Apasă **"+ Profil nou"**
3. Completează:
   - **Nume complet** (ex: *Maria Popescu*)
   - **Data nașterii** (folosită pentru vârstă în raport)
   - **Gen** (M/F)
   - **Grupă sanguină** (opțional)
   - **Notă privată** (opțional — alergii, condiții cronice, etc.)
4. Apasă **"Salvează"**

### Editare / Ștergere

- Apasă **🖉 (creion)** lângă profil → modifică câmpurile → "Salvează"
- Apasă **🗑️ (coș)** → confirmă ștergerea
  - ⚠️ **Atenție:** Ștergerea profilului șterge și TOT istoricul de analize asociat.

### Profilul "Eu" (implicit)

La crearea contului, se generează automat un profil "Tu" pe baza datelor contului. Îl poți edita ca pe oricare altul, dar nu îl poți șterge complet (rămâne ascuns dar contul funcționează).

---

## 6. Încărcarea unui PDF pentru interpretare

1. Apasă **"Interpretează analiză nouă"** (Dashboard) sau **"Upload"** din meniu
2. **Selectează profilul** căruia îi aparține analiza (dropdown)
3. **Trage și plasează fișierul PDF** în zona indicată, sau apasă **"Alege fișier"**
   - Format acceptat: **PDF (text-layer sau scanat)**
   - Mărime maximă: **15 MB**
   - Limba PDF-ului: orice — aplicația interpretează în limba UI selectată
4. Apasă **"Interpretează"**

### Ce se întâmplă apoi:

```
[Tu uploadezi PDF] 
       ↓
[Extragere text PdfPig — 80% din cazuri]
       ↓ (dacă nu reușește)
[OCR Vision Gemini — pentru PDF-uri scanate]
       ↓
[Trimitere la Gemini cu prompt medical strict]
       ↓
[Mapare LOINC prin microserviciul local Python]
       ↓
[Generare raport PDF profesional via QuestPDF]
       ↓
[Salvare în istoric + afișare pe ecran]
       ↓
[Consum 1 credit]
```

Timp tipic: **15–45 secunde** (depinde de mărimea PDF-ului și de tier-ul Gemini).

### Raportul generat conține:

- 📋 **Date pacient** (nume, vârstă extrasă DIN PDF, gen, dată recoltare)
- 🧪 **Tabel analize** cu valoarea, intervalul de referință, săgeată ↑ / ↓ / ✓
- 🩺 **Interpretare medicală** structurată pe sisteme (hematologie, biochimie, hormoni, etc.)
- 💡 **Recomandări generale** (NU sunt diagnostic — sunt informaționale)
- ⚠️ **Disclaimer** clar că nu înlocuiește consultul medical

---

## 7. Detectarea duplicatelor

Dacă încarci un PDF identic cu unul procesat anterior pe ACELAȘI profil (același hash SHA-256 al conținutului), aplicația te oprește:

> **"Acest fișier a fost deja interpretat pe 12 ian 2026. Vrei să-l procesezi din nou?"**

Opțiuni:
- **"Vezi raportul existent"** — NU consumă credit
- **"Procesează din nou (re-interpretare)"** — CONSUMĂ un credit nou
- **"Anulează"**

Util când ai upload accidental același PDF de 2 ori.

---

## 8. Credite — pachete, cumpărare, consum

### Pachete B2C disponibile

| Pachet | Preț | Credite | Cost/credit |
|---|---|---|---|
| **Normal** | 6 EUR | 2 | ~3.00 EUR |
| **Standard** | 11 EUR | 4 | ~2.75 EUR |
| **Super** | 50 EUR | 18 | ~2.78 EUR |
| **Premium** | 100 EUR | 38 | ~2.63 EUR |

### Cum cumperi

1. Apasă **"Cumpără credite"** (Dashboard sau buton din header când ai puține)
2. Selectează pachetul → apasă **"Continuă la plată"**
3. Pe pagina de checkout completezi datele cardului
   > ⚠️ În acest moment, plata este **SIMULATĂ** (mock) pentru dezvoltare locală. Integrarea reală cu Stripe/Netopia este pe roadmap P3.
4. Confirmi → creditele se adaugă instant + primești email de confirmare

### Cum se consumă

- **1 interpretare = 1 credit**
- Bonus-urile (cele gratuite la înregistrare) se consumă PRIMELE
- Apoi cele plătite, de la pachetul cel mai vechi
- Un raport blurat (vezi mai jos) NU consumă credit suplimentar la re-vizualizare

### Modelul Freemium — raport "blurat"

Dacă ai **0 credite** și încarci totuși un PDF, raportul se generează DAR cu **valorile parțial cenzurate** (blurred). Vezi structura și verdictul general, dar valorile numerice precise rămân ascunse până cumperi credite și apeși **"Deblochează raport"**.

---

## 9. Istoric per profil & descărcare raport PDF

1. Mergi la **Profile** → click pe profilul dorit
2. Vezi lista cronologică a TUTUROR analizelor procesate pentru acel profil
3. Pentru fiecare:
   - **📅 Data + ora** procesării
   - **📄 Numele fișierului** original
   - **🏷️ Tag-uri** (Hematologie, Biochimie, etc.) detectate automat
   - **▶️ "Vezi raport"** — afișează raportul pe ecran
   - **⬇️ "Descarcă PDF"** — descarcă raportul PDF formatat
   - **🗑️ "Șterge"** — elimină din istoric (irreversibil!)

---

## 10. Comparare rezultate (între analize)

Pentru a vedea cum au evoluat parametrii între 2+ analize:

1. Pe pagina profilului, **bifează minimum 2 analize** din istoric
2. Apasă **"Compară selectate"**
3. Se deschide o pagină **"Compare"** cu:
   - **Coloane cronologice** (cea mai veche stânga, cea mai recentă dreapta)
   - **Rânduri grupate** pe categorie LOINC (Hematologie, Biochimie serică, Coagulare, etc.) — în limba UI
   - **Coduri colorate**:
     - 🟢 verde = în interval
     - 🔴 roșu = în afara intervalului
     - ⬜ gri = lipsă din acea analiză
   - **Săgeți ↑ / ↓** între coloane când valoarea s-a schimbat semnificativ
4. Apasă **"Export PDF"** ca să salvezi comparația ca raport descărcabil

---

## 11. Grafic de evoluție (per LOINC)

Pentru a vedea evoluția UNUI SINGUR parametru în timp:

1. Pe pagina **Compare** sau **History**, apasă pe orice valoare numerică (ex: "Glucoză")
2. Se deschide pagina **"Evolution"** cu:
   - **Grafic linie/area** Chart.js — axa X = timp, axa Y = valoare
   - **Bandă verde** = intervalul de referință (varia în funcție de vârstă/gen)
   - **Puncte interactive** — hover arată data exactă + valoarea + linkul la raport
   - **Selector multi-LOINC** — poți adăuga 2-5 parametri pe același grafic (ex: HDL + LDL + Total cholesterol pe același chart)
3. Apasă **"Export PDF"** pentru un raport cu grafic + comentariu Gemini

---

## 12. Schimbare limbă · preferințe email

**Limba interfeței:**
- Click pe **🇷🇴 Română ▾** (colț dreapta-sus) → alege limba dorită
- Schimbarea e instantanee + se păstrează în cookie (1 an)
- Toate paginile, butoanele și rapoartele generate VOR FI în noua limbă

**Detectare automată:**
- La prima vizită fără cookie setat, aplicația detectează automat limba browser-ului tău (`Accept-Language` server-side + fallback `navigator.language` client-side) și o setează implicit

**Limbă pentru rapoartele generate:**
- Raportul PDF este în limba UI-ului ACTIV la momentul interpretării
- Vrei un raport în engleză? Schimbă temporar limba pe EN, încarcă PDF-ul, apoi revii la RO

---

# B2B — Cabinete de Analize Medicale (CAM)

## 1. Cont Clinic — înregistrare

Pasul de înregistrare este IDENTIC cu B2C (vezi [B2C #1](#1-înregistrare-cont)), DAR la creare bifezi **"Sunt cabinet de analize medicale"**.

Asta activează:
- ✅ Acces la modul **CAM (/CAM/Dashboard)**
- ✅ Pachete de credite specifice **Clinic** (50/500/1000 EUR — vezi mai jos)
- ✅ Foldere monitorizate automat (Original / Sent / NotSends / Sumar)
- ✅ Procesare batch pe loturi

> 💡 **Recomandare:** Dacă ai și nevoie de utilizare personală + CAM, creează **două conturi separate** cu emailuri diferite. Tabela `Users` are PK pe email.

După înregistrare, contactează admin-ul aplicației pentru activarea finală a profilului Clinic (asocierea cu o intrare în tabela `Clinics`).

---

## 2. Configurarea cabinetului (foldere watched)

Pe mașina cabinetului (Windows / Linux), aplicația monitorizează 4 foldere standard:

```
C:\MedicalApp\Clinics\<NumeCabinet>\
    ├─ Original\      ← AICI pui PDF-urile noi
    ├─ Sent\          ← Aplicația mută aici PDF-urile procesate cu succes
    ├─ NotSends\      ← PDF-urile care au eșuat sunt mutate aici
    └─ Sumar\         ← Rapoartele de lot (.txt + .pdf) se generează aici
```

Aplicația citește calea bazei de la `Clinic.FolderPath` (configurat de admin). Tu doar pui PDF-uri în `Original\` și aplicația preia restul.

---

## 3. Dashboard CAM

Mergi la **/CAM/Dashboard** (link din meniu sau direct URL). Vezi:

### KPI cards (sus):
- 📊 **PDF-uri procesate luna curentă**
- 📧 **Emailuri trimise pacienților**
- ⚠️ **NotSends (erori)** rămase nerezolvate
- 💳 **Credite rămase**

### Grafic istoric:
- Bar chart Chart.js cu procesări/zi din luna selectată
- Selector lună/an în dreapta sus

### Loturi recente:
- Tabel cu ultimele 10 batch-uri:
  - **#ID** · Pornit · Finalizat · Durată · Status (RUNNING / COMPLETED / FAILED / CANCELLED)
  - **Total fișiere** · **Procesate cu succes** · **Emailuri trimise** · **NotSends**
  - Acțiuni: **"Vezi sumar"** · **"Descarcă sumar PDF"**

### Cleanup section:
- Buton **"Cleanup fișiere vechi > N zile"** (vezi §11)

---

## 4. Selectare PDF-uri (fost "Verificare PDF-uri")

> *Numele a fost actualizat în februarie 2026 pentru claritate.*

Mergi la **/CAM/CheckPdfs** (link "Selectare PDF-uri" din Dashboard).

Această pagină îți permite să **inspectezi și să cureți** lista de PDF-uri din `Original\` ÎNAINTE de a porni batch-ul. Așa eviți să trimiți emailuri greșite.

### Ce vezi pe ecran:

Pentru fiecare PDF din `Original\`:

| Coloană | Conține |
|---|---|
| **📄 Fișier** | Numele PDF-ului |
| **👤 Pacient detectat** | Numele extras automat din PDF |
| **📧 Email detectat** | Adresa de email găsită în PDF |
| **⚠️ Validitate email** | OK / Sintaxă invalidă / Domeniu inexistent / Sugestii (ex: "Probabil ai vrut să spui gmail.com") |
| **🔧 Acțiuni** | Override · Șterge |

### Ce poți face aici:

- **Sortare** pe orice coloană (apasă header-ul)
- **Filtru** după pacient sau status email
- **Selecție** PDF-uri pentru upload manual (vezi §4.1)

### 4.1 Upload manual de PDF-uri în Original\

Dacă PDF-urile nu sunt deja în folder:

1. Apasă **"Upload PDF-uri"**
2. Selectează unul sau mai multe fișiere (sau drag-and-drop)
3. Apasă **"Încarcă"** → fișierele sunt copiate în `Original\` și apar instant în tabel

---

## 5. Override pacient

Uneori Gemini detectează greșit numele/emailul (ex: la PDF-uri scanate prost). Pentru a corecta manual:

1. În pagina **Selectare PDF-uri**, găsește rândul cu PDF-ul problematic
2. Apasă **"Override"** (creion lângă coloanele Pacient/Email)
3. Introdu:
   - **Nume pacient corect**
   - **Email corect**
4. Apasă **"Salvează"**

Override-ul se salvează asociat cu **HASH-ul SHA-256 al fișierului** — așa că dacă mai apare exact același PDF în viitor, override-ul se aplică automat.

Pentru a anula override-ul: apasă **"Clear override"** pe rând.

---

## 6. Email blacklist

Anumite domenii nu acceptă emailuri (ex: typo-uri masive, domenii defuncte, domenii care marchează ca spam). Le poți **bloca preventiv**.

1. În **Selectare PDF-uri**, scrolează jos la secțiunea **"Email Blacklist"**
2. Adaugă domenii separate prin virgulă: `temp-mail.com, fake.com, throwaway.org`
3. Apasă **"Salvează blacklist"**

Orice PDF al cărui email aparține unui domeniu blacklistat va fi automat catalogat ca **NotSend** cu motivul "Domeniu blacklistat" — fără să consume credit Gemini.

---

## 7. Pornire batch · monitorizare · anulare

### Pornire batch

1. Asigură-te că **Original\** conține PDF-urile pe care vrei să le procesezi
2. Asigură-te că ai credite suficiente (1 credit/PDF)
3. Mergi la **/CAM/Batch/Start**
4. Aplicația îți arată un **preview**: 
   - Câte PDF-uri vor fi procesate
   - Câte credite vor fi consumate
   - Avertisment dacă ai mai puține credite decât PDF-uri
5. Apasă **"Confirmă & pornește"**

### Monitorizare

După pornire, ești redirecționat la **/CAM/Batch/Progress/{id}**:

- **Bară de progres** % completă
- **Live stats**: procesate / total · emailuri trimise · erori
- **Log live** scroll automat cu ultimele acțiuni (ex: *"15:32:18 — Maria Popescu — sent"*)
- **Polling automat** la 2 secunde (nu trebuie să refresh-uezi)

### Anulare

Dacă vrei să oprești batch-ul în desfășurare:

1. Apasă butonul roșu **"Anulează batch"**
2. Confirmă
3. PDF-urile deja procesate rămân procesate
4. Cele neîncepute rămân în `Original\` pentru un batch viitor
5. Batch-ul primește status **CANCELLED**

---

## 8. NotSends · motive · re-procesare

Un PDF ajunge în **`NotSends\`** dacă:

| Motiv | Cauză | Cum rezolvi |
|---|---|---|
| **No email** | Aplicația nu a detectat un email în PDF | Override manual (vezi §5) + reîncarcă |
| **Invalid email** | Sintaxă greșită (ex: "ana@.ro") | Override + reîncarcă |
| **Domain doesn't exist** | DNS lookup eșuat | Verifică cu pacientul + override |
| **Email blacklisted** | Domeniul e pe lista ta de blacklist | Scoate de pe blacklist SAU contactează altfel pacientul |
| **Gemini timeout** | Toate 3 tier-urile au eșuat | Reîncearcă mai târziu (probleme cu API-ul Google) |
| **PDF corrupt** | Fișier deteriorat | Refă PDF-ul |
| **Already sent (duplicate hash)** | Același PDF a mai fost procesat | Verifică Sent\ |

Lângă fiecare fișier din `NotSends\` apare un sibling `.reasons.txt` cu detalii.

### Re-procesare:

Mută manual fișierul din `NotSends\` înapoi în `Original\` (după ce ai rezolvat cauza), apoi pornește un nou batch.

> ⚠️ După **3 eșecuri consecutive ale aceluiași PDF**, aplicația marchează fișierul ca DEFINITIV NotSend și nu îl mai re-procesează automat — așa eviți buclele infinite.

---

## 9. Sumar lot — PDF & TXT

Pentru fiecare batch finalizat (succes sau eșec), aplicația generează **DOUĂ fișiere** în `Sumar\`:

### `Sum_yyyyMMdd_HHmm.txt` — text simplu

```
=========================================
  Sumar Lot — Procesare Buletine — Cabinet X
=========================================

Pornit: 12 feb 2026 14:30:15
Finalizat: 12 feb 2026 14:42:08
Status: completed
Durată: 00:11:53

--- Statistici ---
  Procesate cu succes: 24
  Emailuri trimise: 22
  Comparații atașate: 5
  NotSends (erori): 2
  Total fișiere în lot: 26

--- NotSends (motive) ---
  • [14:38:22] BUL_2026_002441.pdf  →  pacient: Ion Marinescu
      Motiv: Email blacklisted (temp-mail.com)
      Încercări: 1
  ...
```

### `Sum_yyyyMMdd_HHmm.pdf` — PDF profesional

- Header cu logo cabinet
- 4 KPI cards (procesate / trimise / comparații / nesendate)
- Tabel detaliat erori
- Footer cu data generării
- **Localizat** în limba UI a operatorului (5 limbi)

Le poți descărca direct din Dashboard CAM → tabelul "Loturi recente" → apasă **"Descarcă sumar PDF"** sau **".txt"**.

---

## 10. Lista pacienților clinicii

Mergi la **/CAM/Patients**.

Vezi un tabel cu TOȚI pacienții văzuți vreodată de cabinet:

- **Nume**
- **Email** (cel din override sau cel detectat automat)
- **Număr analize procesate**
- **Ultima analiză** (data)
- **Acțiuni**: **Vezi istoric** · **Șterge**

### Căutare:
Câmp de search în top-right — caută parțial în nume sau email.

### Ștergere pacient:
Apasă **🗑️** → confirmă. Asta șterge înregistrarea din tabela `ClinicPatients`, dar **NU șterge fișierele PDF din Sent\\** (acolo rămân ca arhivă).

---

## 11. Cleanup fișiere vechi

Pentru a economisi spațiu pe disc, poți curăța periodic fișierele vechi:

1. Pe **Dashboard CAM**, secțiunea jos
2. Introdu numărul de zile (ex: **365** = un an)
3. Apasă **"Cleanup"**
4. Aplicația șterge fizic:
   - PDF-urile din `Sent\` mai vechi de N zile
   - Rapoartele generate vechi
   - Sumare TXT/PDF vechi
   - **NU șterge** datele numerice din baza de date (acelea rămân pentru istoric & evoluție)

⚠️ **Acțiune ireversibilă!** Recomandăm backup înainte.

---

## 12. Cumpărare credite CAM

### Pachete B2B disponibile

| Pachet | Preț | Credite | Cost/credit |
|---|---|---|---|
| **Starter (CAM)** | 50 EUR | 17 | ~2.94 EUR |
| **Business (CAM)** | 500 EUR | 183 | ~2.73 EUR |
| **Enterprise (CAM)** | 1000 EUR | 390 | ~2.56 EUR |

Procedura este aceeași ca pentru B2C (vezi [B2C #8](#8-credite--pachete-cumpărare-consum)), DAR vei vedea **doar pachetele Clinic** în UI (pachetele individuale sunt ascunse).

### Bonus volum:
- Pentru **Enterprise**, contactează direct admin-ul aplicației — putem oferi discount custom + factură pe firmă.

---

# Întrebări frecvente

**Î: Pot să procesez PDF-uri în engleză cu UI-ul setat pe română?**
R: Da. PDF-ul poate fi în orice limbă (Gemini înțelege). Raportul generat va fi în limba UI-ului tău.

**Î: Cât de precise sunt interpretările?**
R: Foarte precise pe parametrii STANDARD (hematologie, biochimie). Modelul a fost ajustat să NU omită niciun rând din tabel (reguli FIRST ROW / LAST ROW / POST-LONG-COMMENT în prompt). Totuși, **nu este diagnostic medical** — e un ajutor educațional. Întotdeauna consultă medicul tău.

**Î: Aplicația trimite date la Google?**
R: Da, PDF-ul este trimis la Gemini API (Google) pentru interpretare. Datele NU sunt folosite pentru antrenamentul modelelor (Google Cloud paid tier). Datele numerice sunt stocate doar local în SQL Server.

**Î: Cât trăiesc fișierele în Sent\\?**
R: Indefinit, până când le ștergi manual sau rulezi Cleanup.

**Î: Pot să import istoric vechi (analize din 2020)?**
R: Nu există încă import în masă. Poți încărca PDF-urile vechi unul câte unul prin Upload. Roadmap: import CSV LOINC în viitor.

**Î: Cum schimb limba unui PACIENT specific (în B2B)?**
R: În tabela `ClinicPatients` se reține preferința de limbă. Update direct prin SQL sau prin pagina de override. Roadmap: UI pentru editare per pacient.

**Î: Ce se întâmplă dacă cad creditele la 0 în mijlocul unui batch B2B?**
R: Batch-ul se oprește gracios, PDF-urile neprocesate rămân în Original\, primești email de alertă. Cumperi credite și pornești un batch nou.

**Î: Pot să schimb modelul Gemini implicit?**
R: Da, prin `appsettings.json` → `Gemini.Model`. Schimbarea afectează atât B2C cât și B2B simultan. Vezi documentația tehnică internă pentru detalii.

---

📞 **Suport:** Pentru întrebări care nu sunt acoperite aici, contactează echipa MedicalApp+ la adresa de email din pagina principală.

🎯 **Versiunea acestui ghid:** februarie 2026 · *Acest document se actualizează la fiecare release major.*
