# Tip de cont nou: Cabinet Medical (CM)

## Ce se adaugă

Un al treilea tip de cont, alături de „Persoană fizică” (B2C) și „Clinică” (CAM):
**Cabinet Medical**. Este gândit pentru un medic de familie / cabinet mic, care interpretează
buletine pentru pacienții săi, unul câte unul — nu în loturi.

## Traseul utilizatorului

1. **Înregistrare**: în formularul de înregistrare apare o a treia opțiune de tip de cont,
   „Cabinet Medical”. Când e selectată, se cere obligatoriu **Numele cabinetului**
   (ex. „DR. Ionescu Felicia — Medic de familie”), maxim 150 de caractere. Restul pașilor
   (email, parolă, cod de verificare pe email) rămân identici cu cei de azi.
2. **Imediat după confirmare**, contul primește **1 credit bonus** și este trimis direct în
   ecranul de interpretare (încărcare PDF), nu în dashboard.
3. **Interpretarea gratuită** (cea pe creditul bonus) se afișează **blurată/parțială**, exact
   mecanismul de „gustare” folosit azi pentru persoanele fizice: se vede structura raportului,
   dar conținutul complet e ascuns până la prima achiziție.
4. **Dacă cabinetul cumpără credite înainte de a lansa interpretarea**: raportul apare
   **complet, neblurat**, se consumă un credit plătit, iar **creditul bonus rămâne neatins** și
   disponibil mai departe.
5. **Deblurare retroactivă**: după prima achiziție, raportul gratuit făcut anterior devine
   vizibil integral (nu rămâne blurat pe veci și nu se cere reinterpretare, care ar costa
   încă un credit). Aceasta este o decizie de discutat — vezi „Decizii de confirmat”, pct. 1.

## Prețuri

Cabinetul vede **un singur pachet: 89 € = 45 credite**. Nu vede pachetele de persoană fizică
(6 €, 11 €, 39 €, 89 €) și nu vede pachetele de clinică. Pachetul are aceeași valoare ca
pachetul „Premium” B2C, dar este o ofertă separată, ca să poată fi schimbată ulterior
independent (preț, număr de credite, denumire) fără să afecteze persoanele fizice.

## Limite

- **2000 de profile (pacienți)** per cabinet, față de 20 la persoană fizică.
- Regula existentă „profile suplimentare doar dacă ai credite plătite” **se aplică și
  cabinetului**: cu creditul bonus lucrează pe primul profil; pentru a adăuga pacienți trebuie
  să fi cumpărat pachetul. Este a doua decizie de confirmat — vezi pct. 2.
- Contorul din interfață devine „X profile create — max 2000” pentru cabinet, cu aceeași
  avertizare colorată la apropierea de limită.

## Ce implică 2000 de profile pentru interfață

Ecranele actuale de profile sunt gândite pentru câteva profile de familie: o pagină cu
cartonașe și un simplu selector la încărcarea PDF-ului. La 2000 de pacienți devin
nefolosibile. Prin urmare, pentru conturile de tip cabinet:

- pagina de profile primește **căutare după nume** și **paginare**;
- selectorul de pacient din ecranul de interpretare devine un câmp cu **căutare** (scrii
  numele, alegi din sugestii), în loc de listă derulantă cu 2000 de intrări;
- listele se încarcă pe bucăți, ca să nu crească timpul de răspuns pe măsură ce aplicația
  ajunge la volumul țintă de 200.000 de conturi.

Pentru persoanele fizice, ecranele rămân exact cum sunt azi (sub 20 de profile, cartonașe).

## Ce NU se schimbă

- Comportamentul conturilor de persoană fizică: pachete, plafonul de 20 de profile, regula
  creditelor plătite, blurarea, mesajele.
- Comportamentul conturilor de clinică (CAM): loturi, foldere, pachetele proprii, plafonul lor.
- Prețurile și creditele existente, istoricul achizițiilor, rapoartele deja generate.

## Administrare

- Panoul de admin arată tipul „Cabinet Medical” ca etichetă distinctă în lista de utilizatori
  și în raportările de venit, cu numele cabinetului lângă email (la fel cum apare azi numele
  clinicii).
- Filtrarea utilizatorilor pe tip de cont include noul tip.

## Limbi

Toate textele noi (opțiunea de la înregistrare, eticheta și explicația „Nume cabinet”,
mesajele de limită, denumirea pachetului, mesajele de blurare și de deblurare) se adaugă în
**toate cele 7 limbi**: engleză, română, franceză, spaniolă, germană, italiană, portugheză.

## Decizii de confirmat

1. **Deblurare retroactivă după prima achiziție** — planul presupune că raportul gratuit
   devine vizibil integral după ce cabinetul cumpără pachetul. Alternativa: raportul gratuit
   rămâne blurat definitiv, iar conținutul complet se obține doar la o interpretare nouă
   (consumă un credit).
2. **Adăugarea de pacienți înainte de achiziție** — planul păstrează regula actuală: fără
   credite plătite nu se pot adăuga profile suplimentare. Alternativa, mai permisivă pentru un
   cabinet: îi permitem să își introducă pacienții de la început (de ex. până la 5 profile) și
   abia interpretarea consumă credite.
3. **Creditul bonus folosit mai târziu** — planul presupune că blurarea depinde de „contul nu
   a cumpărat niciodată”, nu de tipul creditului. Deci, după o achiziție, dacă se ajunge la
   consumarea creditului bonus, raportul respectiv NU va fi blurat. Alternativa: orice raport
   plătit din creditul bonus este blurat, indiferent de istoricul achizițiilor.
4. **Conturile existente** — nu se migrează nimic automat. Un cont de persoană fizică sau de
   clinică nu devine cabinet; trecerea unui cont existent pe noul tip se face, dacă e nevoie,
   manual din admin (se poate include în această livrare la cerere).

## Presupuneri

- Cabinetul folosește fluxul de interpretare individual (un PDF per pacient), nu modulul de
  loturi al clinicilor.
- Numele cabinetului este obligatoriu la înregistrare și poate fi modificat ulterior din
  setările contului.
- Cabinetul nu are nevoie de pagini separate de tip „dashboard de clinică”; folosește
  ecranele B2C existente (profile, arhivă, grafice, comparații, dosar medical, rapoarte PDF).
- Un singur pachet activ pentru cabinet acum; structura permite adăugarea altora ulterior.
