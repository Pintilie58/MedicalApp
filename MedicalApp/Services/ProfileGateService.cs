using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Central authority for the "extra profile" gate (Feb 2026 anti-abuse).
    ///
    /// Business rule (B2C only):
    ///   A regular user can freely use the auto-seeded "Eu" default profile
    ///   with the 1 bonus credit granted at registration. To add ANY additional
    ///   profile (family members: Mama, Tata, copil, …) they must have at
    ///   least 1 PAID credit currently in their balance. Bonus credits do NOT
    ///   count — otherwise a bad actor could keep re-registering to accumulate
    ///   free profiles for scraping / spam / PII collection.
    ///
    /// Applied at BOTH ends:
    ///   * Views hide/disable the "+ Profil nou" button via ViewBag.CanCreateProfile.
    ///   * ProfilesController.Create (GET and POST) enforces the same rule
    ///     server-side, so an attacker who bypasses the UI (e.g. crafts a
    ///     direct POST) still hits a 403-style redirect. The UI check alone
    ///     is cosmetic and must NEVER be trusted.
    ///
    /// Scope: Individual (B2C) users only. Clinic (CAM / B2B) accounts are
    /// unaffected — they run on a completely different credit + subscription
    /// model that has its own limits elsewhere.
    ///
    /// Grandfather clause (2a): existing users with multiple profiles created
    /// before this rule existed keep them. Only NEW profile creation is gated.
    ///
    /// Hard cap (June 2026): a B2C account may hold at most
    /// <see cref="MaxProfilesPerUser"/> profiles and a Cabinet Medical account
    /// at most <see cref="MaxProfilesPerCabinet"/> patients, paid credits or
    /// not. This is an operational safety limit (every profile multiplies the
    /// archive, charts and dossier queries), not a monetisation gate. Accounts
    /// that somehow already exceed it keep every profile they have — only
    /// creating a NEW one is refused.
    /// </summary>
    public static class ProfileGateService
    {
        /// <summary>Maximum number of profiles a B2C account may own.</summary>
        public const int MaxProfilesPerUser = 20;

        /// <summary>Maximum number of patients a Cabinet Medical account may own.</summary>
        public const int MaxProfilesPerCabinet = 2000;

        /// <summary>
        /// The cap that applies to this account, or <c>null</c> when there is
        /// none (clinics run on the CAM model and are not capped here).
        /// </summary>
        public static int? LimitFor(User? user)
        {
            if (user == null) return null;
            if (AccountTypes.IsClinic(user.UserType)) return null;
            return AccountTypes.IsCabinet(user.UserType)
                ? MaxProfilesPerCabinet
                : MaxProfilesPerUser;
        }

        /// <summary>
        /// True for the accounts a cap applies to (B2C and Cabinet Medical).
        /// Used by the views to decide whether the "X of N profiles used"
        /// counter makes any sense for this account.
        /// </summary>
        public static bool IsCapped(User? user) => LimitFor(user) != null;

        /// <summary>
        /// True when the account has reached (or passed) its cap, so the caller
        /// can show the "N profiles maximum" message instead of the "buy
        /// credits" one. Clinic accounts are never capped here.
        /// </summary>
        public static bool IsAtProfileLimit(User? user, int currentProfileCount)
            => LimitFor(user) is int max && currentProfileCount >= max;

        /// <summary>
        /// Returns <c>true</c> when the given user is allowed to create ANOTHER
        /// profile beyond the ones they already own.
        /// </summary>
        /// <param name="user">The user in question (must not be null).</param>
        /// <param name="currentProfileCount">Number of profiles the user already
        /// has in the database. Passed in explicitly so callers can reuse
        /// counts they already fetched instead of forcing another DB round-trip.</param>
        public static bool CanCreateAdditionalProfile(User? user, int currentProfileCount)
        {
            if (user == null) return false;

            // 3a — Clinic accounts have their own billing model and are out of
            // scope for this rule. Cabinet Medical accounts ARE gated: the paid
            // credit requirement applies to them exactly like to B2C
            // (user decision, June 2026).
            if (AccountTypes.IsClinic(user.UserType)) return true;

            // Hard cap first: it applies even to users with paid credits.
            if (IsAtProfileLimit(user, currentProfileCount)) return false;

            // Defensive: a B2C user with zero profiles shouldn't exist in
            // production (registration seeds "Eu"), but if the seed ever fails
            // we still want them to be able to bootstrap ONE profile so the
            // account is not stuck. This is not exploitable — the seed runs
            // exactly once per registration.
            if (currentProfileCount <= 0) return true;

            // 1b — Unlock while the user has PAID credits currently in the
            // balance. When they spend them all, the gate closes again. Bonus
            // credits are explicitly excluded so the free-trial flow cannot
            // grant unlimited profiles.
            return user.CreditRest > 0;
        }
    }
}
