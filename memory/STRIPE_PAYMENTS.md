# Plăți cu Stripe Checkout (iunie 2026)

## Ce face
- Pachetele rămân definite pe server (`Services/CreditPackages.cs`, EUR). Browserul trimite
  doar cheia pachetului; suma nu poate fi manipulată.
- `Payments:Provider` = `Stripe` (implicit în `appsettings.json`) sau `Simulated` (formularul
  vechi de card fictiv, doar dezvoltare locală, creditează instant).
- Flux Stripe: `Credits/Checkout` → buton „Plătește în siguranță cu Stripe” → POST
  `Credits/StartStripe` (creează Checkout Session + rând `PaymentTransactions` = `initiated`)
  → pagina Stripe (card, 3-D Secure, Apple/Google Pay; noi nu atingem date de card)
  → întoarcere pe `Credits/StripeSuccess?session_id=…` → verificăm la Stripe → **o singură
  dată** (`TryMarkPaidAsync`, RowVersion) → `FulfillPurchaseAsync` (credite, `Purchases` cu
  `PaymentMethod = stripe` + `ProviderReference = pi_…`, foldere CAM la prima cumpărare,
  deblocare DEMO, notificare admin — același cod ca la plata simulată).
- Webhook `POST /Credits/StripeWebhook` (semnătură verificată): `checkout.session.completed`
  cu `payment_status = paid` → aceeași creditare idempotentă; `expired` / `async_payment_failed`
  → tranzacția marcată. Dacă webhook-ul și pagina de întoarcere ajung amândouă, doar prima
  creditează.
- Chitanța: Stripe o trimite automat pe emailul clientului (Dashboard → Settings → Emails →
  „Successful payments” trebuie bifat; în test mode se trimite doar dacă e activat explicit).
- Promo-codurile rămân ale aplicației (neatinse de Stripe).

## Chei și configurare
Cheile NU stau în fișiere.
- **Local (VS2026)**: click-dreapta pe proiect → *Manage User Secrets* → adaugă:
  ```json
  { "Payments": { "Stripe": { "SecretKey": "sk_test_…", "WebhookSecret": "" } } }
  ```
  WebhookSecret poate rămâne gol local (Stripe nu ajunge pe localhost; creditarea se face la
  întoarcere). Pentru a testa și webhook-ul local: `stripe listen --forward-to
  https://localhost:7229/Credits/StripeWebhook` (Stripe CLI) și pui `whsec_…` afișat.
- **Azure**: App Settings `Payments__Provider = Stripe`, `Payments__Stripe__SecretKey`,
  `Payments__Stripe__WebhookSecret`. Webhook-ul se înregistrează în Stripe Dashboard →
  Developers → Webhooks → `https://<domeniu>/Credits/StripeWebhook`, evenimente
  `checkout.session.completed`, `checkout.session.expired`, `checkout.session.async_payment_failed`.
- Cont: sandbox Stripe (țara RO) provizionat de platformă; se revendică din link-ul de
  onboarding (Manage → Payments). După revendicare + KYC, cheile live înlocuiesc cheile de test
  în App Settings. Moneda de încasare rămâne EUR (contul RO decontează în RON automat).

## Carduri de test
`4242 4242 4242 4242` succes · `4000 0025 0000 3155` cere 3-D Secure · `4000 0000 0000 9995`
fonduri insuficiente · `4000 0000 0000 0002` refuzat. Orice dată viitoare, orice CVC.

## Testat
`/app/probe_stripe` (copie `memory/probes/StripeCheckoutProbe.cs.txt`), 26 checkuri: sesiune
reală creată în sandbox, webhook semnat → creditare, retry idempotent, întoarcere după webhook
fără dublă creditare, semnătură falsă → 400, sesiune străină, `payment_status = unpaid` → 0
credite, `expired`, izolare pe utilizator/pachet, providerul Simulated neschimbat.
